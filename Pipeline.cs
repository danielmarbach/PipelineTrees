using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static System.Linq.Expressions.Expression;
using FastExpressionCompiler;

namespace PipelineTrees
{
    public interface IBehavior<in TInContext, out TOutContext> : IBehavior
        where TInContext : IBehaviorContext
        where TOutContext : IBehaviorContext
    {
        Task Invoke(TInContext context, Func<TOutContext, CancellationToken, Task> next, CancellationToken cancellationToken);
    }

    public interface IBehavior
    {
    }

    public interface IBehaviorContext
    {
    }



    public abstract class Behavior<TContext> : IBehavior<TContext, TContext> where TContext : IBehaviorContext
    {
        public Task Invoke(TContext context, Func<TContext, CancellationToken, Task> next, CancellationToken cancellationToken)
        {
            return Invoke(context, () => next(context, cancellationToken), cancellationToken);
        }

        public abstract Task Invoke(TContext context, Func<Task> next, CancellationToken cancellationToken);
    }

    class Behavior1BeforeOptimization : Behavior<IBehaviorContext>
    {
        public override Task Invoke(IBehaviorContext context, Func<Task> next, CancellationToken cancellationToken)
        {
            return next();
        }
    }

    class Behavior1AfterOptimization : IBehavior<IBehaviorContext, IBehaviorContext>
    {
        public Task Invoke(IBehaviorContext context, Func<IBehaviorContext, CancellationToken, Task> next, CancellationToken cancellationToken)
        {
            return next(context, cancellationToken);
        }
    }

    public class PipelineModifications
    {
        public List<RegisterStep> Additions = new List<RegisterStep>();
        public List<RemoveStep> Removals = new List<RemoveStep>();
        public List<ReplaceStep> Replacements = new List<ReplaceStep>();
    }

    static class BehaviorExtensions
    {
        // ReSharper disable once SuggestBaseTypeForParameter
        public static Func<TRootContext, CancellationToken, Task> CreatePipelineExecutionFuncFor<TRootContext>(this IBehavior[] behaviors)
            where TRootContext : IBehaviorContext
        {
            return (Func<TRootContext, CancellationToken, Task>)behaviors.CreatePipelineExecutionExpression();
        }

        /// <code>
        /// (rootContext, rootToken)
        ///    => behavior1.Invoke(rootContext,
        ///       (context1, token1) => behavior2.Invoke(context1,
        ///        ..., token1
        ///          (context{N}, token{N}) => behavior{N}.Invoke(context{N},
        ///             (context{N+1}, token{N+1}) => TaskEx.Completed), token{N})
        /// </code>
        public static Delegate CreatePipelineExecutionExpression(this IBehavior[] behaviors, List<Expression> expressions = null)
        {
            Delegate lambdaExpression = null;
            var length = behaviors.Length - 1;
            // We start from the end of the list know the lambda expressions deeper in the call stack in advance
            for (var i = length; i >= 0; i--)
            {
                var currentBehavior = behaviors[i];
                var behaviorInterfaceType = currentBehavior.GetType().GetInterfaces().FirstOrDefault(t => t.GetGenericArguments().Length == 2 && t.FullName.StartsWith("PipelineTrees.IBehavior"));
                if (behaviorInterfaceType == null)
                {
                    throw new InvalidOperationException("Behaviors must implement IBehavior<TInContext, TOutContext>");
                }
                var methodInfo = behaviorInterfaceType.GetMethods().FirstOrDefault();
                if (methodInfo == null)
                {
                    throw new InvalidOperationException("Behaviors must implement IBehavior<TInContext, TOutContext> and provide an invocation method.");
                }

                var genericArguments = behaviorInterfaceType.GetGenericArguments();
                var inContextType = genericArguments[0];

                var inContextParameter = Parameter(inContextType, $"context{i}");
                var inCancellationTokenParameter = Parameter(typeof(CancellationToken), $"token{i}");

                if (i == length)
                {
                    if (currentBehavior is IPipelineTerminator)
                    {
                        inContextType = typeof(PipelineTerminator<>.ITerminatingContext).MakeGenericType(inContextType);
                    }
                    var doneDelegate = CreateDoneDelegate(inContextType, i);
                    lambdaExpression = CreateBehaviorCallDelegate(currentBehavior, methodInfo, inContextParameter, inCancellationTokenParameter, doneDelegate, expressions);
                    continue;
                }

                lambdaExpression = CreateBehaviorCallDelegate(currentBehavior, methodInfo, inContextParameter, inCancellationTokenParameter, lambdaExpression, expressions);
            }

            return lambdaExpression;
        }

        /// <code>
        /// (context{i}, token{i}) => behavior.Invoke(context{i}, (context{i+1}, token{i+1}) => previous, token{i})
        /// </code>>
        static Delegate CreateBehaviorCallDelegate(IBehavior currentBehavior, MethodInfo methodInfo, ParameterExpression outerContextParam, ParameterExpression outCancellationTokenParamter, Delegate previous, List<Expression> expressions = null)
        {
            Expression body = Call(Constant(currentBehavior), methodInfo, outerContextParam, Constant(previous), outCancellationTokenParamter);
            var lambdaExpression = Lambda(body, outerContextParam, outCancellationTokenParamter);
            expressions?.Add(lambdaExpression);
            return lambdaExpression.CompileFast();
        }

        /// <code>
        /// (context{i}, token{i}) => return Task.CompletedTask;
        /// </code>>
        static Delegate CreateDoneDelegate(Type inContextType, int i)
        {
            var innerContextParam = Parameter(inContextType, $"context{i + 1}");
            var innerCancellationTokenParam = Parameter(typeof(CancellationToken), $"token{i + 1}");
            return Lambda(typeof(Func<,,>).MakeGenericType(inContextType, typeof(CancellationToken), typeof(Task)), Constant(Task.CompletedTask), innerContextParam, innerCancellationTokenParam).CompileFast();
        }
    }

    public abstract class StageConnector<TFromContext, TToContext> : IBehavior<TFromContext, TToContext>,
            IStageConnector
        where TFromContext : IBehaviorContext
        where TToContext : IBehaviorContext
    {
        public abstract Task Invoke(TFromContext context, Func<TToContext, CancellationToken, Task> stage, CancellationToken cancellationToken);
    }

    public abstract class PipelineTerminator<T> : StageConnector<T, PipelineTerminator<T>.ITerminatingContext>,
        IPipelineTerminator where T : IBehaviorContext
    {

        protected abstract Task Terminate(T context);

        public sealed override Task Invoke(T context, Func<ITerminatingContext, CancellationToken, Task> next, CancellationToken cancellationToken)
        {
            return Terminate(context);
        }

        public interface ITerminatingContext : IBehaviorContext
        {
        }
    }

    public class SettingsHolder : ReadOnlySettings
    {
        public T Get<T>(string key)
        {
            return (T)Get(key);
        }

        public bool TryGet<T>(out T val)
        {
            return TryGet(typeof(T).FullName, out val);
        }

        public bool TryGet<T>(string key, out T val)
        {
            val = default(T);

            object tmp;
            if (!Overrides.TryGetValue(key, out tmp))
            {
                if (!Defaults.TryGetValue(key, out tmp))
                {
                    return false;
                }
            }

            if (!(tmp is T))
            {
                return false;
            }

            val = (T)tmp;
            return true;
        }

        public T Get<T>()
        {
            return (T)Get(typeof(T).FullName);
        }

        public object Get(string key)
        {
            object result;
            if (Overrides.TryGetValue(key, out result))
            {
                return result;
            }

            if (Defaults.TryGetValue(key, out result))
            {
                return result;
            }

            throw new KeyNotFoundException($"The given key ({key}) was not present in the dictionary.");
        }

        public T GetOrDefault<T>()
        {
            return GetOrDefault<T>(typeof(T).FullName);
        }

        public T GetOrDefault<T>(string key)
        {
            object result;
            if (Overrides.TryGetValue(key, out result))
            {
                return (T)result;
            }

            if (Defaults.TryGetValue(key, out result))
            {
                return (T)result;
            }

            return default(T);
        }

        public bool HasSetting(string key)
        {
            return Overrides.ContainsKey(key) || Defaults.ContainsKey(key);
        }

        public bool HasSetting<T>()
        {
            var key = typeof(T).FullName;

            return HasSetting(key);
        }

        public bool HasExplicitValue(string key)
        {
            return Overrides.ContainsKey(key);
        }

        public bool HasExplicitValue<T>()
        {
            var key = typeof(T).FullName;

            return HasExplicitValue(key);
        }

        public T GetOrCreate<T>()
            where T : class, new()
        {
            T value;
            if (!TryGet(out value))
            {
                value = new T();
                Set<T>(value);
            }
            return value;
        }

        public void Set(string key, object value)
        {
            EnsureWriteEnabled(key);

            Overrides[key] = value;
        }

        public void Set<T>(object value)
        {
            Set(typeof(T).FullName, value);
        }

        public void Set<T>(Action value)
        {
            Set(typeof(T).FullName, value);
        }

        public void SetDefault<T>(object value)
        {
            SetDefault(typeof(T).FullName, value);
        }

        public void SetDefault<T>(Action value)
        {
            SetDefault(typeof(T).FullName, value);
        }

        public void SetDefault(string key, object value)
        {
            EnsureWriteEnabled(key);

            Defaults[key] = value;
        }

        internal void PreventChanges()
        {
            locked = true;
        }

        internal void Merge(ReadOnlySettings settings)
        {
            EnsureMergingIsPossible();

            var holder = settings as SettingsHolder ?? new SettingsHolder();

            foreach (var @default in holder.Defaults)
            {
                Defaults[@default.Key] = @default.Value;
            }

            foreach (var @override in holder.Overrides)
            {
                Overrides[@override.Key] = @override.Value;
            }
        }

        void EnsureMergingIsPossible()
        {
            if (locked)
            {
                throw new Exception(
                    "Unable to merge settings. The settings has been locked for modifications. Move any configuration code earlier in the configuration pipeline");
            }
        }

        void EnsureWriteEnabled(string key)
        {
            if (locked)
            {
                throw new Exception(
                    $"Unable to set the value for key: {key}. The settings has been locked for modifications. Move any configuration code earlier in the configuration pipeline");
            }
        }

        public void Clear()
        {
            foreach (var item in Defaults)
            {
                (item.Value as IDisposable)?.Dispose();
            }

            Defaults.Clear();

            foreach (var item in Overrides)
            {
                (item.Value as IDisposable)?.Dispose();
            }

            Overrides.Clear();
        }

        ConcurrentDictionary<string, object> Defaults =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        bool locked;

        ConcurrentDictionary<string, object> Overrides =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    public interface ReadOnlySettings
    {
        /// <summary>
        /// Gets the setting value.
        /// </summary>
        /// <typeparam name="T">The <typeparamref name="T" /> to locate in the <see cref="ReadOnlySettings" />.</typeparam>
        /// <returns>The setting value.</returns>
        T Get<T>();

        /// <summary>
        /// Gets the setting value.
        /// </summary>
        /// <typeparam name="T">The type of the setting.</typeparam>
        /// <param name="key">The key of the setting to get.</param>
        /// <returns>The setting value.</returns>
        T Get<T>(string key);

        /// <summary>
        /// Safely get the settings value, returning false if the settings key was not found.
        /// </summary>
        /// <typeparam name="T">The type to get, fullname will be used as key.</typeparam>
        /// <param name="val">The value if present.</param>
        bool TryGet<T>(out T val);

        /// <summary>
        /// Safely get the settings value, returning false if the settings key was not found.
        /// </summary>
        /// <typeparam name="T">The type of the setting.</typeparam>
        /// <param name="key">The key of the setting to get.</param>
        /// <param name="val">The setting value.</param>
        /// <returns>True if found, false otherwise</returns>
        bool TryGet<T>(string key, out T val);

        /// <summary>
        /// Gets the setting value.
        /// </summary>
        object Get(string key);

        /// <summary>
        /// Gets the setting or default based on the typename.
        /// </summary>
        /// <typeparam name="T">The setting to get.</typeparam>
        /// <returns>The actual value.</returns>
        T GetOrDefault<T>();

        /// <summary>
        /// Gets the setting value or the <code>default(T).</code>.
        /// </summary>
        /// <typeparam name="T">The value of the setting.</typeparam>
        /// <param name="key">The key of the setting to get.</param>
        /// <returns>The setting value.</returns>
        T GetOrDefault<T>(string key);

        /// <summary>
        /// Determines whether the <see cref="ReadOnlySettings" /> contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the <see cref="ReadOnlySettings" />.</param>
        /// <returns>true if the <see cref="ReadOnlySettings" /> contains an element with the specified key; otherwise, false.</returns>
        bool HasSetting(string key);

        /// <summary>
        /// Determines whether the <see cref="ReadOnlySettings" /> contains the specified <typeparamref name="T" />.
        /// </summary>
        /// <typeparam name="T">The <typeparamref name="T" /> to locate in the <see cref="ReadOnlySettings" />.</typeparam>
        /// <returns>true if the <see cref="ReadOnlySettings" /> contains an element with the specified key; otherwise, false.</returns>
        bool HasSetting<T>();

        /// <summary>
        /// Determines whether the <see cref="ReadOnlySettings" /> contains a specific value for the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the <see cref="ReadOnlySettings" />.</param>
        /// <returns>
        /// true if the <see cref="ReadOnlySettings" /> contains an explicit value with the specified key; otherwise,
        /// false.
        /// </returns>
        bool HasExplicitValue(string key);

        /// <summary>
        /// Determines whether the <see cref="ReadOnlySettings" /> contains a specific value for the specified
        /// <typeparamref name="T" />.
        /// </summary>
        /// <typeparam name="T">The <typeparamref name="T" /> to locate in the <see cref="ReadOnlySettings" />.</typeparam>
        /// <returns>true if the <see cref="ReadOnlySettings" /> contains an element with the specified key; otherwise, false.</returns>
        bool HasExplicitValue<T>();
    }

    class StepRegistrationsCoordinator
    {
        public StepRegistrationsCoordinator(List<RemoveStep> removals, List<ReplaceStep> replacements)
        {
            this.removals = removals;
            this.replacements = replacements;
        }

        public void Register(string pipelineStep, Type behavior, string description)
        {
            additions.Add(RegisterStep.Create(pipelineStep, behavior, description));
        }

        public void Register(RegisterStep rego)
        {
            additions.Add(rego);
        }

        public List<RegisterStep> BuildPipelineModelFor<TRootContext>() where TRootContext : IBehaviorContext
        {
            var relevantRemovals = removals.Where(removal => additions.Any(a => a.StepId == removal.RemoveId)).ToList();
            var relevantReplacements =
                replacements.Where(removal => additions.Any(a => a.StepId == removal.ReplaceId)).ToList();

            var piplineModelBuilder = new PipelineModelBuilder(typeof(TRootContext), additions, relevantRemovals,
                relevantReplacements);

            return piplineModelBuilder.Build();
        }

        List<RegisterStep> additions = new List<RegisterStep>();
        List<RemoveStep> removals;
        List<ReplaceStep> replacements;
    }

    public class RemoveStep
    {
        public RemoveStep(string removeId)
        {
            RemoveId = removeId;
        }

        public string RemoveId { get; private set; }
    }

    public class ReplaceStep
    {
        public ReplaceStep(string idToReplace, Type behavior, string description = null,
            Func<IBuilder, IBehavior> factoryMethod = null)
        {
            ReplaceId = idToReplace;
            Description = description;
            BehaviorType = behavior;
            FactoryMethod = factoryMethod;
        }

        public string ReplaceId { get; }
        public string Description { get; }
        public Type BehaviorType { get; }
        public Func<IBuilder, IBehavior> FactoryMethod { get; }
    }

    [DebuggerDisplay("{StepId}({BehaviorType.FullName}) - {Description}")]
    public abstract class RegisterStep
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RegisterStep" /> class.
        /// </summary>
        /// <param name="stepId">The unique identifier for this steps.</param>
        /// <param name="behavior">The type of <see cref="Behavior{TContext}" /> to register.</param>
        /// <param name="description">A brief description of what this step does.</param>
        /// <param name="factoryMethod">A factory method for creating the behavior.</param>
        protected RegisterStep(string stepId, Type behavior, string description,
            Func<IBuilder, IBehavior> factoryMethod = null)
        {
            this.factoryMethod = factoryMethod;

            BehaviorType = behavior;
            StepId = stepId;
            Description = description;
        }

        /// <summary>
        /// Gets the unique identifier for this step.
        /// </summary>
        public string StepId { get; }

        /// <summary>
        /// Gets the description for this registration.
        /// </summary>
        public string Description { get; private set; }

        internal List<Dependency> Befores { get; private set; }
        internal List<Dependency> Afters { get; private set; }

        /// <summary>
        /// Gets the type of <see cref="Behavior{TContext}" /> that is being registered.
        /// </summary>
        public Type BehaviorType { get; private set; }

        /// <summary>
        /// Checks if this behavior is enabled.
        /// </summary>
        public virtual bool IsEnabled(ReadOnlySettings settings)
        {
            return true;
        }

        /// <summary>
        /// Instructs the pipeline to register this step before the <paramref name="id" /> one. If the <paramref name="id" /> does
        /// not exist, this condition is ignored.
        /// </summary>
        /// <param name="id">The unique identifier of the step that we want to insert before.</param>
        public void InsertBeforeIfExists(string id)
        {

            if (Befores == null)
            {
                Befores = new List<Dependency>();
            }

            Befores.Add(new Dependency(StepId, id, Dependency.DependencyDirection.Before, false));
        }

        /// <summary>
        /// Instructs the pipeline to register this step before the <paramref name="id" /> one.
        /// </summary>
        public void InsertBefore(string id)
        {
            if (Befores == null)
            {
                Befores = new List<Dependency>();
            }

            Befores.Add(new Dependency(StepId, id, Dependency.DependencyDirection.Before, true));
        }

        /// <summary>
        /// Instructs the pipeline to register this step after the <paramref name="id" /> one. If the <paramref name="id" /> does
        /// not exist, this condition is ignored.
        /// </summary>
        /// <param name="id">The unique identifier of the step that we want to insert after.</param>
        public void InsertAfterIfExists(string id)
        {
            if (Afters == null)
            {
                Afters = new List<Dependency>();
            }

            Afters.Add(new Dependency(StepId, id, Dependency.DependencyDirection.After, false));
        }

        /// <summary>
        /// Instructs the pipeline to register this step after the <paramref name="id" /> one.
        /// </summary>
        public void InsertAfter(string id)
        {
            if (Afters == null)
            {
                Afters = new List<Dependency>();
            }

            Afters.Add(new Dependency(StepId, id, Dependency.DependencyDirection.After, true));
        }

        internal void Replace(ReplaceStep replacement)
        {
            if (StepId != replacement.ReplaceId)
            {
                throw new InvalidOperationException(
                    $"Cannot replace step '{StepId}' with '{replacement.ReplaceId}'. The ID of the replacement must match the replaced step.");
            }

            BehaviorType = replacement.BehaviorType;
            factoryMethod = replacement.FactoryMethod;

            if (!string.IsNullOrWhiteSpace(replacement.Description))
            {
                Description = replacement.Description;
            }
        }

        internal IBehavior CreateBehaviorNew(IBuilder defaultBuilder)
        {
            var behavior = factoryMethod != null
                ? factoryMethod(defaultBuilder)
                : (IBehavior)defaultBuilder.Build(BehaviorType);

            return behavior;
        }

        internal static RegisterStep Create(string pipelineStep, Type behavior, string description,
            Func<IBuilder, IBehavior> factoryMethod = null)
        {
            return new DefaultRegisterStep(behavior, pipelineStep, description, factoryMethod);
        }

        Func<IBuilder, IBehavior> factoryMethod;

        class DefaultRegisterStep : RegisterStep
        {
            public DefaultRegisterStep(Type behavior, string stepId, string description,
                Func<IBuilder, IBehavior> factoryMethod)
                : base(stepId, behavior, description, factoryMethod)
            {
            }
        }
    }

    class Dependency
    {
        public enum DependencyDirection
        {
            Before = 1,
            After = 2
        }

        public Dependency(string dependantId, string dependsOnId, DependencyDirection direction, bool enforce)
        {
            DependantId = dependantId;
            DependsOnId = dependsOnId;
            Direction = direction;
            Enforce = enforce;
        }

        public string DependantId { get; private set; }
        public string DependsOnId { get; private set; }
        public bool Enforce { get; private set; }

        public DependencyDirection Direction { get; private set; }
    }

    public interface IBuilder : IDisposable
    {
        object Build(Type typeToBuild);

        IBuilder CreateChildBuilder();

        T Build<T>();

        IEnumerable<T> BuildAll<T>();

        IEnumerable<object> BuildAll(Type typeToBuild);

        void Release(object instance);

        void BuildAndDispatch(Type typeToBuild, Action<object> action);
    }

    class PipelineModelBuilder
    {
        public PipelineModelBuilder(Type rootContextType, List<RegisterStep> additions, List<RemoveStep> removals,
            List<ReplaceStep> replacements)
        {
            this.rootContextType = rootContextType;
            this.additions = additions;
            this.removals = removals;
            this.replacements = replacements;
        }

        public List<RegisterStep> Build()
        {
            var registrations = new Dictionary<string, RegisterStep>(StringComparer.CurrentCultureIgnoreCase);
            var listOfBeforeAndAfterIds = new List<string>();

            // Let's do some validation too

            //Step 1: validate that additions are unique
            foreach (var metadata in additions)
            {
                if (!registrations.ContainsKey(metadata.StepId))
                {
                    registrations.Add(metadata.StepId, metadata);
                    if (metadata.Afters != null)
                    {
                        listOfBeforeAndAfterIds.AddRange(metadata.Afters.Select(a => a.DependsOnId));
                    }
                    if (metadata.Befores != null)
                    {
                        listOfBeforeAndAfterIds.AddRange(metadata.Befores.Select(b => b.DependsOnId));
                    }

                    continue;
                }

                var message =
                    $"Step registration with id '{metadata.StepId}' is already registered for '{registrations[metadata.StepId].BehaviorType}'.";
                throw new Exception(message);
            }

            //  Step 2: do replacements
            foreach (var metadata in replacements)
            {
                if (!registrations.ContainsKey(metadata.ReplaceId))
                {
                    var message =
                        $"You can only replace an existing step registration, '{metadata.ReplaceId}' registration does not exist.";
                    throw new Exception(message);
                }

                var registerStep = registrations[metadata.ReplaceId];
                registerStep.Replace(metadata);
            }

            // Step 3: validate the removals
            foreach (var metadata in removals.Distinct(idComparer))
            {
                if (!registrations.ContainsKey(metadata.RemoveId))
                {
                    var message =
                        $"You cannot remove step registration with id '{metadata.RemoveId}', registration does not exist.";
                    throw new Exception(message);
                }

                if (listOfBeforeAndAfterIds.Contains(metadata.RemoveId, StringComparer.CurrentCultureIgnoreCase))
                {
                    var add =
                        additions.First(
                            mr =>
                                (mr.Befores != null &&
                                 mr.Befores.Select(b => b.DependsOnId)
                                     .Contains(metadata.RemoveId, StringComparer.CurrentCultureIgnoreCase)) ||
                                (mr.Afters != null &&
                                 mr.Afters.Select(b => b.DependsOnId)
                                     .Contains(metadata.RemoveId, StringComparer.CurrentCultureIgnoreCase)));

                    var message =
                        $"You cannot remove step registration with id '{metadata.RemoveId}', registration with id '{add.StepId}' depends on it.";
                    throw new Exception(message);
                }

                registrations.Remove(metadata.RemoveId);
            }

            var stages = registrations.Values.GroupBy(r => r.GetInputContext())
                .ToList();

            var finalOrder = new List<RegisterStep>();

            if (registrations.Count == 0)
            {
                return finalOrder;
            }

            var currentStage = stages.SingleOrDefault(stage => stage.Key == rootContextType);

            if (currentStage == null)
            {
                throw new Exception(
                    $"Can't find any behaviors/connectors for the root context ({rootContextType.FullName})");
            }

            var stageNumber = 1;

            while (currentStage != null)
            {
                var stageSteps = currentStage.Where(stageStep => !IsStageConnector(stageStep)).ToList();

                //add the stage connector
                finalOrder.AddRange(Sort(stageSteps));

                var stageConnectors = currentStage.Where(IsStageConnector).ToList();

                if (stageConnectors.Count > 1)
                {
                    var connectors = $"'{string.Join("', '", stageConnectors.Select(sc => sc.BehaviorType.FullName))}'";
                    throw new Exception(
                        $"Multiple stage connectors found for stage '{currentStage.Key.FullName}'. Remove one of: {connectors}");
                }

                var stageConnector = stageConnectors.FirstOrDefault();

                if (stageConnector == null)
                {
                    if (stageNumber < stages.Count)
                    {
                        throw new Exception($"No stage connector found for stage {currentStage.Key.FullName}");
                    }

                    currentStage = null;
                }
                else
                {
                    finalOrder.Add(stageConnector);

                    if (typeof(IPipelineTerminator).IsAssignableFrom(stageConnector.BehaviorType))
                    {
                        currentStage = null;
                    }
                    else
                    {
                        var args = stageConnector.BehaviorType.BaseType.GetGenericArguments();
                        var stageEndType = args[1];
                        currentStage = stages.SingleOrDefault(stage => stage.Key == stageEndType);
                    }
                }

                stageNumber++;
            }

            return finalOrder;
        }

        static bool IsStageConnector(RegisterStep stageStep)
        {
            return typeof(IStageConnector).IsAssignableFrom(stageStep.BehaviorType);
        }

        static IEnumerable<RegisterStep> Sort(List<RegisterStep> registrations)
        {
            if (registrations.Count == 0)
            {
                return registrations;
            }

            // Step 1: create nodes for graph
            var nameToNode = new Dictionary<string, Node>();
            var allNodes = new List<Node>();
            foreach (var rego in registrations)
            {
                // create entries to preserve order within
                var node = new Node(rego);
                nameToNode[rego.StepId] = node;
                allNodes.Add(node);
            }

            // Step 2: create edges from InsertBefore/InsertAfter values
            foreach (var node in allNodes)
            {
                ProcessBefores(node, nameToNode);
                ProcessAfters(node, nameToNode);
            }

            // Step 3: Perform Topological Sort
            var output = new List<RegisterStep>();
            foreach (var node in allNodes)
            {
                node.Visit(output);
            }

            return output;
        }

        static void ProcessBefores(Node node, Dictionary<string, Node> nameToNode)
        {
            if (node.Befores == null)
            {
                return;
            }
            foreach (var beforeReference in node.Befores)
            {
                Node referencedNode;
                if (nameToNode.TryGetValue(beforeReference.DependsOnId, out referencedNode))
                {
                    referencedNode.previous.Add(node);
                    continue;
                }
                var currentStepIds = GetCurrentIds(nameToNode);
                var message =
                    $"Registration '{beforeReference.DependsOnId}' specified in the insertbefore of the '{node.StepId}' step does not exist. Current StepIds: {currentStepIds}";

                if (!beforeReference.Enforce)
                {
                }
                else
                {
                    throw new Exception(message);
                }
            }
        }

        static void ProcessAfters(Node node, Dictionary<string, Node> nameToNode)
        {
            if (node.Afters == null)
            {
                return;
            }
            foreach (var afterReference in node.Afters)
            {
                Node referencedNode;
                if (nameToNode.TryGetValue(afterReference.DependsOnId, out referencedNode))
                {
                    node.previous.Add(referencedNode);
                    continue;
                }
                var currentStepIds = GetCurrentIds(nameToNode);
                var message =
                    $"Registration '{afterReference.DependsOnId}' specified in the insertafter of the '{node.StepId}' step does not exist. Current StepIds: {currentStepIds}";

                if (!afterReference.Enforce)
                {
                }
                else
                {
                    throw new Exception(message);
                }
            }
        }

        static string GetCurrentIds(Dictionary<string, Node> nameToNodeDict)
        {
            return $"'{string.Join("', '", nameToNodeDict.Keys)}'";
        }

        List<RegisterStep> additions;
        List<RemoveStep> removals;
        List<ReplaceStep> replacements;

        Type rootContextType;
        static CaseInsensitiveIdComparer idComparer = new CaseInsensitiveIdComparer();

        class Node
        {
            public Node(RegisterStep registerStep)
            {
                rego = registerStep;
                Befores = registerStep.Befores;
                Afters = registerStep.Afters;
                StepId = registerStep.StepId;

                OutputContext = registerStep.GetOutputContext();
            }

            public Type OutputContext { get; private set; }

            internal void Visit(List<RegisterStep> output)
            {
                if (visited)
                {
                    return;
                }
                visited = true;
                foreach (var n in previous)
                {
                    n.Visit(output);
                }
                if (rego != null)
                {
                    output.Add(rego);
                }
            }

            public List<Dependency> Afters;
            public List<Dependency> Befores;

            public string StepId;
            internal List<Node> previous = new List<Node>();
            RegisterStep rego;
            bool visited;
        }

        class CaseInsensitiveIdComparer : IEqualityComparer<RemoveStep>
        {
            public bool Equals(RemoveStep x, RemoveStep y)
            {
                return x.RemoveId.Equals(y.RemoveId, StringComparison.CurrentCultureIgnoreCase);
            }

            public int GetHashCode(RemoveStep obj)
            {
                return obj.RemoveId.ToLower().GetHashCode();
            }
        }
    }

    interface IStageConnector
    {
    }

    interface IPipelineTerminator
    {
    }

    static class RegisterStepExtensions
    {
        public static bool IsStageConnector(this RegisterStep step)
        {
            return typeof(IStageConnector).IsAssignableFrom(step.BehaviorType);
        }

        public static Type GetContextType(this Type behaviorType)
        {
            var behaviorInterface = behaviorType.GetBehaviorInterface();
            return behaviorInterface.GetGenericArguments()[0];
        }

        public static bool IsBehavior(this Type behaviorType)
        {
            return behaviorType.GetInterfaces()
                .Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == BehaviorInterfaceType);
        }

        static Type GetBehaviorInterface(this Type behaviorType)
        {
            return behaviorType.GetInterfaces()
                .First(x => x.IsGenericType && x.GetGenericTypeDefinition() == BehaviorInterfaceType);
        }

        public static Type GetOutputContext(this RegisterStep step)
        {
            return step.BehaviorType.GetOutputContext();
        }

        public static Type GetOutputContext(this Type behaviorType)
        {
            var behaviorInterface = GetBehaviorInterface(behaviorType);
            return behaviorInterface.GetGenericArguments()[1];
        }

        public static Type GetInputContext(this RegisterStep step)
        {
            return step.BehaviorType.GetInputContext();
        }

        public static Type GetInputContext(this Type behaviorType)
        {
            var behaviorInterface = GetBehaviorInterface(behaviorType);
            return behaviorInterface.GetGenericArguments()[0];
        }

        static Type BehaviorInterfaceType = typeof(IBehavior<,>);
    }
}
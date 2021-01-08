using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PipelineTrees
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var behaviorContext = new BehaviorContext();

            var behaviors = new IBehavior[] {
                new MyBehavior1(),
                new MyBehavior2(),
                new CancelBehavior(),
            };

            var pipeline = behaviors.CreatePipelineExecutionFuncFor<BehaviorContext>();
            Console.WriteLine("Execute 1");
            await pipeline(behaviorContext, new CancellationToken(false));

            try
            {
                Console.WriteLine("Execute 2 with Cancellation");
                await pipeline(behaviorContext, new CancellationToken(true));
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Canceled");
            }
            try
            {
                Console.WriteLine();
                Console.WriteLine("Execute 2 with Cancellation");
                await pipeline(behaviorContext, new CancellationToken(true));
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Canceled");
            }

            Console.WriteLine();
            Console.WriteLine("Execute 4");
            await pipeline(behaviorContext, new CancellationToken(false));

            Console.ReadLine();
        }
    }

    class MyBehavior1 : Behavior<BehaviorContext>
    {
        public MyBehavior1()
        {
        }

        public override Task Invoke(BehaviorContext context, Func<Task> next, CancellationToken cancellationToken)
        {
            Console.WriteLine(typeof(MyBehavior1));
            return next();
        }
    }

    class MyBehavior2 : Behavior<BehaviorContext>
    {
        public MyBehavior2()
        {
        }

        public override Task Invoke(BehaviorContext context, Func<Task> next, CancellationToken cancellationToken)
        {
            Console.WriteLine(typeof(MyBehavior2));
            return next();
        }
    }

    class CancelBehavior : Behavior<BehaviorContext>
    {
        public CancelBehavior()
        {
        }

        public override Task Invoke(BehaviorContext context, Func<Task> next, CancellationToken cancellationToken)
        {
            Console.WriteLine(typeof(CancelBehavior));
            cancellationToken.ThrowIfCancellationRequested();
            return next();
        }
    }

    class BehaviorContext : IBehaviorContext {}
}

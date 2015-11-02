using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Transactions;

namespace TransactionCompletedException
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine($"Running on Thread {Thread.CurrentThread.ManagedThreadId}");

            Console.WriteLine("Complete the transaction scope? [Y|N] ");
            string completeKey = Console.ReadKey().KeyChar.ToString().ToUpperInvariant();
            Console.WriteLine("Behavior?");
            Console.WriteLine("Throw inside TransactionCompleted event [A]");
            Console.WriteLine("Throw inside Prepare phase [P]");
            Console.WriteLine("Throw inside Commit phase [C]");
            Console.WriteLine("Throw inside Rollback phase [R]");
            string behaviorKey = Console.ReadKey().KeyChar.ToString().ToUpperInvariant();
            Console.WriteLine();

            Console.WriteLine("Attach UnhandledException handler? [U]");
            string globalCatch = Console.ReadKey().KeyChar.ToString().ToUpperInvariant();
            if (globalCatch == "U")
            {
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            }

            using (var scope = new TransactionScope())
            {
                Transaction.Current.EnlistDurable(Guid.NewGuid(), new FakeResourceManager(behaviorKey),
                    EnlistmentOptions.None);

                Transaction.Current.TransactionCompleted += (sender, e) =>
                {
                    Console.WriteLine("A transaction has completed:");
                    Console.WriteLine("ID:             {0}", e.Transaction.TransactionInformation.LocalIdentifier);
                    Console.WriteLine("Distributed ID: {0}", e.Transaction.TransactionInformation.DistributedIdentifier);
                    Console.WriteLine("Status:         {0}", e.Transaction.TransactionInformation.Status);
                    Console.WriteLine("IsolationLevel: {0}", e.Transaction.IsolationLevel);
                    Console.WriteLine("Thread: {0}", Thread.CurrentThread.ManagedThreadId);

                    if (behaviorKey == "A")
                    {
                        throw new InvalidOperationException();
                    }
                };

                switch (completeKey)
                {
                    case "Y":
                        scope.Complete();
                        break;
                    case "N":
                        break;
                }
            }

            Console.ReadLine();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine($"Caught exception {e.ExceptionObject} which terminates {e.IsTerminating}");
        }

        class FakeResourceManager : IEnlistmentNotification
        {
            private readonly string key;

            public FakeResourceManager(string key)
            {
                this.key = key;
            }

            public void Prepare(PreparingEnlistment preparingEnlistment)
            {
                preparingEnlistment.Prepared();
                if (key == "P")
                {
                    throw new InvalidOperationException();
                }
                Console.WriteLine("Prepared");
            }

            public void Commit(Enlistment enlistment)
            {
                enlistment.Done();
                Console.WriteLine("Committed");
            }

            public void Rollback(Enlistment enlistment)
            {
                enlistment.Done();
                Console.WriteLine("Rolled back");
            }

            public void InDoubt(Enlistment enlistment)
            {
                enlistment.Done();
                Console.WriteLine("In doubt");
            }
        }
    }
}

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
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            using (var txScope = new TransactionScope())
            {
                Transaction.Current.EnlistDurable(Guid.NewGuid(), new FakeResourceManager(),
                    EnlistmentOptions.None);

                Transaction.Current.TransactionCompleted += (sender, eventArgs) =>
                {
                    Console.WriteLine(
                        $"Status {eventArgs.Transaction.TransactionInformation.Status} Thread {Thread.CurrentThread.ManagedThreadId}");

                    // throw inside Complete
                    throw new InvalidOperationException();
                };
                //txScope.Complete();
            }

            Console.ReadLine();
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine($"Caught exception {e.ExceptionObject} which terminates {e.IsTerminating}");
        }

        class FakeResourceManager : IEnlistmentNotification
        {
            public void Prepare(PreparingEnlistment preparingEnlistment)
            {
                preparingEnlistment.Prepared();
                Console.WriteLine("Prepared");
            }

            public void Commit(Enlistment enlistment)
            {
                //throw new InvalidOperationException();
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

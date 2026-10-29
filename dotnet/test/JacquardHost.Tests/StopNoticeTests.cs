using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JacquardLang.Host.Carrier;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// The worker gives its stop notice (through which the host starts the
/// cancellation of its handlers' token) before every kill, including a kill
/// by disposal: one from another thread while an operation waits, and one
/// with no operation in progress. Within <see cref="Host"/> the worker is
/// disposed only after the invocation has ended, which starts the
/// cancellation itself; these tests cover the worker on its own.
/// </summary>
[TestFixture]
public sealed class StopNoticeTests
{
    [TearDown]
    public void ClearSeams()
    {
        Worker.ClockHook = null;
        Worker.ExpireHook = null;
    }

    private static void AssertNoticeBeforeEveryKill(ConcurrentQueue<string> events)
    {
        string[] order = events.ToArray();
        Assert.That(order, Does.Contain("kill"), "the worker was not killed");
        int notices = 0;
        foreach (string step in order)
        {
            if (step == "notice")
            {
                notices++;
            }
            else
            {
                Assert.That(notices, Is.GreaterThan(0), "a kill came before any stop notice: " + string.Join(", ", order));
            }
        }
    }

    [Test]
    public void DisposalWithNoOperationGivesTheNoticeBeforeTheKill()
    {
        var events = new ConcurrentQueue<string>();
        Worker worker = Worker.Start("unused", Standin("silent", string.Empty, TimeSpan.FromSeconds(30)), CancellationToken.None);
        worker.StopNotice = () => events.Enqueue("notice");
        Worker.ExpireHook = () => events.Enqueue("kill");
        worker.Dispose();
        AssertNoticeBeforeEveryKill(events);
    }

    [Test]
    public void DisposalFromAnotherThreadBetweenTwoChecksGivesTheNoticeBeforeTheKill()
    {
        // The waiting thread's first clock read comes from its stopping
        // check, after that check has read the disposal flag. The clock seam
        // then starts the disposal on another thread and returns only once
        // the flag is set, so the next check, the fault check, sees it.
        var events = new ConcurrentQueue<string>();
        Worker worker = Worker.Start("unused", Standin("silent", string.Empty, TimeSpan.FromSeconds(30)), CancellationToken.None);
        worker.StopNotice = () => events.Enqueue("notice");
        Worker.ExpireHook = () => events.Enqueue("kill");
        int waiter = -1;
        int reads = 0;
        Thread? disposer = null;
        Worker.ClockHook = () =>
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref waiter) && Interlocked.Increment(ref reads) == 1)
            {
                disposer = new Thread(worker.Dispose) { IsBackground = true, Name = "test-disposer" };
                disposer.Start();
                var watch = Stopwatch.StartNew();
                while (!worker.Stopping && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Thread.Yield();
                }
            }

            return Stopwatch.GetTimestamp();
        };

        CarrierException? thrown = Within(TimeSpan.FromSeconds(20), () =>
        {
            Volatile.Write(ref waiter, Environment.CurrentManagedThreadId);
            try
            {
                worker.Await(() => worker.Stopping);
                return null;
            }
            catch (CarrierException error)
            {
                return error;
            }
        });
        Assert.That(disposer, Is.Not.Null, "the clock seam did not start the disposal");
        Assert.That(disposer!.Join(TimeSpan.FromSeconds(10)), Is.True, "the disposal did not finish");
        Assert.That(thrown, Is.Not.Null, "the wait ended on its predicate, before the race");
        Assert.That(thrown!.IsAborted, Is.True, thrown.Message);
        AssertNoticeBeforeEveryKill(events);
    }
}

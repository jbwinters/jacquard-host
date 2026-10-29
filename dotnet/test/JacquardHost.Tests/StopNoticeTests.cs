using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using JacquardLang.Host.Carrier;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// The worker gives its stop notice (through which the host starts the
/// cancellation of its handlers' token) before every kill, one notice per
/// kill on the killing thread: a kill by disposal, from the disposer's own
/// thread or from a waiting operation's thread, and the kill when setting up
/// the spawned worker's pipes fails. Within <see cref="Host"/> the worker is
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
        Worker.SpawnedHook = null;
    }

    /// <summary>Records each notice and kill with the thread it ran on.</summary>
    private sealed class Events
    {
        private readonly ConcurrentQueue<(string Step, int Thread)> steps = new();

        internal Events()
        {
            Worker.ExpireHook = () => steps.Enqueue(("kill", Environment.CurrentManagedThreadId));
        }

        internal void Notice() => steps.Enqueue(("notice", Environment.CurrentManagedThreadId));

        /// <summary>
        /// Asserts that every kill used a notice of its own, given on the
        /// killing thread before it (so <c>notice, kill, kill</c> fails), and
        /// that there was a kill. Returns the killing threads, in order.
        /// </summary>
        internal int[] AssertOneNoticePerKill()
        {
            (string Step, int Thread)[] order = steps.ToArray();
            string shown = string.Join(", ", order.Select(step => step.Step + "@" + step.Thread));
            var unused = new Dictionary<int, int>();
            var killers = new List<int>();
            foreach ((string step, int thread) in order)
            {
                unused.TryGetValue(thread, out int count);
                if (step == "notice")
                {
                    unused[thread] = count + 1;
                    continue;
                }

                Assert.That(count, Is.GreaterThan(0), "a kill had no stop notice of its own on its thread: " + shown);
                unused[thread] = count - 1;
                killers.Add(thread);
            }

            Assert.That(killers, Is.Not.Empty, "the worker was not killed: " + shown);
            return killers.ToArray();
        }
    }

    private static WorkerOptions Silent() => Standin("silent", string.Empty, TimeSpan.FromSeconds(30));

    [Test]
    public void TheCheckRefusesAKillWithoutANoticeOfItsOwn()
    {
        // The assertion itself: two kills after one notice fail it.
        var events = new Events();
        Worker.ExpireHook!();
        events.Notice();
        Assert.Throws<AssertionException>((Action)(() => events.AssertOneNoticePerKill()));
        var paired = new Events();
        paired.Notice();
        Worker.ExpireHook!();
        Worker.ExpireHook!();
        Assert.Throws<AssertionException>((Action)(() => paired.AssertOneNoticePerKill()));
    }

    [Test]
    public void DisposalWithNoOperationGivesTheNoticeBeforeTheKill()
    {
        var events = new Events();
        Worker worker = Worker.Start("unused", Silent(), events.Notice, CancellationToken.None);
        worker.Dispose();
        Assert.That(events.AssertOneNoticePerKill(), Is.EqualTo(new[] { Environment.CurrentManagedThreadId }));
    }

    [Test]
    public void DisposalOnAnotherThreadKillsOnTheDisposersThreadAfterTheNotice()
    {
        var events = new Events();
        Worker worker = Worker.Start("unused", Silent(), events.Notice, CancellationToken.None);
        int disposerId = -1;
        var disposer = new Thread(() =>
        {
            Volatile.Write(ref disposerId, Environment.CurrentManagedThreadId);
            worker.Dispose();
        })
        {
            IsBackground = true,
            Name = "test-disposer",
        };
        disposer.Start();
        Assert.That(disposer.Join(TimeSpan.FromSeconds(10)), Is.True, "the disposal did not finish");
        Assert.That(events.AssertOneNoticePerKill(), Is.EqualTo(new[] { Volatile.Read(ref disposerId) }));
        Assert.That(worker.Signal, Is.EqualTo("SIGKILL"));
    }

    [Test]
    public void DisposalFromAnotherThreadBetweenTwoChecksGivesTheNoticeBeforeTheKill()
    {
        // The waiting thread's first clock read comes from its stopping
        // check, after that check has read the disposal flag. The clock seam
        // then starts the disposal on another thread and returns only once
        // the flag is set, so the next check, the fault check, sees it and
        // kills the worker on the waiting thread. The disposer kills again
        // only if the killed worker has not exited by the time it holds the
        // worker; that kill too must have its own notice.
        var events = new Events();
        Worker worker = Worker.Start("unused", Silent(), events.Notice, CancellationToken.None);
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
        int[] killers = events.AssertOneNoticePerKill();
        Assert.That(killers[0], Is.EqualTo(Volatile.Read(ref waiter)), "the waiting thread did not kill the worker");
        Assert.That(killers.Skip(1), Is.All.EqualTo(disposer.ManagedThreadId), "a later kill was not the disposer's");
    }

    [Test]
    public void AFailureToSetUpTheSpawnedWorkersPipesGivesTheNoticeBeforeTheKill()
    {
        var events = new Events();
        int pid = -1;
        Worker.SpawnedHook = process =>
        {
            pid = process.Id;
            throw new NotSupportedException("seam: worker pipes are not pipe streams");
        };
        Worker worker = Worker.Start("unused", Silent(), events.Notice, CancellationToken.None);
        try
        {
            Assert.That(pid, Is.GreaterThan(0), "the seam did not run");
            Assert.That(events.AssertOneNoticePerKill(), Is.EqualTo(new[] { Environment.CurrentManagedThreadId }));
            Assert.That(Alive(pid), Is.False, "the worker was still running when Start returned");
            Assert.That(worker.Signal, Is.EqualTo("SIGKILL"), "the killed worker's exit was not recorded");
            Assert.That(worker.SpawnError, Is.EqualTo("seam: worker pipes are not pipe streams"));
            CarrierException? fault = worker.Fault();
            Assert.That(fault, Is.Not.Null);
            Assert.That(fault!.IsLoss && !fault.IsTimeout && !fault.IsAborted, Is.True, fault.Message);
            Assert.That(fault.Message, Is.EqualTo("could not poll the worker's pipes (seam: worker pipes are not pipe streams)"));
            Assert.Throws<CarrierException>((Action)(() => worker.ReadFrame(JacquardLang.Host.Protocol.Limits.Hard)));
        }
        finally
        {
            worker.Dispose();
            if (pid > 0 && Alive(pid))
            {
                Kill(pid);
            }
        }
    }
}

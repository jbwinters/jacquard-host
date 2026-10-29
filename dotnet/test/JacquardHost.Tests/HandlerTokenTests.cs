using System;
using System.Diagnostics;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// Callbacks a handler registers on <see cref="HandlerContext.CancellationToken"/>
/// are handler code. They must never run on, or hold, the caller's thread or
/// the thread that cancels the caller's token, and whatever they throw must
/// neither escape <see cref="Host.Invoke"/> nor crash the process. The token
/// is cancelled at the deadline and not before, however long the lifetime,
/// and its cancellation does not depend on the shared thread pool.
/// </summary>
[TestFixture]
public sealed class HandlerTokenTests
{
    [TearDown]
    public void ClearSeams() => Worker.ClockHook = null;

    private static InvocationResult InvokeEcho(Host host, Handler handler, CancellationToken cancellationToken = default) =>
        host.Invoke(
            Pinned.Identity("kit-echo"),
            Echo(),
            new Value[] { new TextValue("ping") },
            new OperationRegistry().Bind(Pinned.Identity("kit-world"), Pinned.Identity("send"), handler),
            cancellationToken);

    /// <summary>Cancels <paramref name="source"/> on its own thread after a delay, recording when Cancel returned and what it threw.</summary>
    private sealed class Canceller
    {
        private readonly ManualResetEventSlim returned = new();
        private Exception? thrown;

        internal Canceller(CancellationTokenSource source, TimeSpan delay)
        {
            var thread = new Thread(() =>
            {
                Thread.Sleep(delay);
                try
                {
                    source.Cancel();
                }
                catch (Exception error)
                {
                    thrown = error;
                }

                returned.Set();
            })
            {
                IsBackground = true,
                Name = "test-canceller",
            };
            thread.Start();
        }

        internal bool Returned(TimeSpan limit) => returned.Wait(limit);

        internal Exception? Thrown => Volatile.Read(ref thrown);
    }

    [Test]
    public void AThrowingCallbackDoesNotEscapeASuccessfulInvoke()
    {
        using var ran = new ManualResetEventSlim();
        InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success"), (context, args) =>
        {
            context.CancellationToken.Register(() =>
            {
                ran.Set();
                throw new InvalidOperationException("callback");
            });
            return new TextValue("pong");
        }));
        Assert.That(result.Ok, Is.True, result.ToString());
        Assert.That(ran.Wait(TimeSpan.FromSeconds(5)), Is.True, "the token was not cancelled when the invocation ended");
    }

    [Test]
    public void ABlockingCallbackDoesNotHoldASuccessfulInvoke()
    {
        // The callback blocks until the test releases it; the caller must not
        // wait for it, whatever the deadline.
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        try
        {
            InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(2)), (context, args) =>
            {
                context.CancellationToken.Register(() =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(60));
                });
                return new TextValue("pong");
            }));
            Assert.That(result.Ok, Is.True, result.ToString());
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the callback never ran");
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public void ABlockingCallbackHoldsNeitherTheCancellerNorInvoke()
    {
        using var release = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        Canceller? canceller = null;
        var watch = Stopwatch.StartNew();
        try
        {
            InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(30)), (context, args) =>
            {
                context.CancellationToken.Register(() => release.Wait(TimeSpan.FromSeconds(60)));
                canceller = new Canceller(cancel, TimeSpan.FromMilliseconds(300));
                context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
                return new TextValue("late");
            }, cancel.Token));
            Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
            Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a late response was sent");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "Invoke waited for the callback");
            Assert.That(canceller!.Returned(TimeSpan.FromSeconds(5)), Is.True, "the canceller's thread ran the callback");
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public void AThrowingCallbackDoesNotEscapeToTheCanceller()
    {
        using var cancel = new CancellationTokenSource();
        using var ran = new ManualResetEventSlim();
        Canceller? canceller = null;
        InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(30)), (context, args) =>
        {
            context.CancellationToken.Register(() =>
            {
                ran.Set();
                throw new InvalidOperationException("callback");
            });
            canceller = new Canceller(cancel, TimeSpan.FromMilliseconds(300));
            context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
            return new TextValue("late");
        }, cancel.Token));
        Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
        Assert.That(ran.Wait(TimeSpan.FromSeconds(5)), Is.True, "the callback never ran");
        Assert.That(canceller!.Returned(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(canceller.Thrown, Is.Null, "the handler's callback threw into the caller's Cancel");
    }

    [Test]
    public void ABlockingCallbackAtTheDeadlineDoesNotHoldInvoke()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var watch = Stopwatch.StartNew();
        try
        {
            InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(1)), (context, args) =>
            {
                context.CancellationToken.Register(() =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(60));
                });

                // Ignores cancellation until its callback has run.
                entered.Wait(TimeSpan.FromSeconds(10));
                return new TextValue("late");
            }));
            Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
            Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a late response was sent");
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3.5)), "Invoke waited for the callback");
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True, "the token was not cancelled at the deadline");
        }
        finally
        {
            release.Set();
        }
    }

    [Test]
    public void AThrowingCallbackAtTheDeadlineDoesNotCrashTheProcess()
    {
        // Callbacks run last-registered first, and Cancel throws only after
        // all have run, so "after" signals just before the exception leaves
        // Cancel. Unhandled on a timer thread, it would end this process.
        using var after = new ManualResetEventSlim();
        InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(1)), (context, args) =>
        {
            context.CancellationToken.Register(after.Set);
            context.CancellationToken.Register(() => throw new InvalidOperationException("timer callback"));
            after.Wait(TimeSpan.FromSeconds(10));
            return new TextValue("late");
        }));
        Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
        Assert.That(after.Wait(TimeSpan.FromSeconds(5)), Is.True, "the token was not cancelled at the deadline");

        // An unhandled exception ends the process within this pause.
        Thread.Sleep(1000);
        Assert.Pass("the process survived the callback's exception");
    }

    [Test]
    public void AHandlerTokenUnderAYearLongDeadlineIsCancelledByTheCaller()
    {
        // A lifetime longer than any single timer wait: the token still
        // works, and the caller's cancellation ends the call promptly.
        using var cancel = new CancellationTokenSource();
        bool sawCancelled = false;
        var watch = Stopwatch.StartNew();
        InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromDays(365)), (context, args) =>
        {
            cancel.CancelAfter(300);
            sawCancelled = context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            return new TextValue("late");
        }, cancel.Token));
        Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(Eventually(() => Volatile.Read(ref sawCancelled), TimeSpan.FromSeconds(5)), Is.True, "the handler's token was not cancelled");
    }

    [TestCase(36)]
    [TestCase(48)]
    public void ATokenUnderALifetimeOverADayIsCancelledAtTheDeadlineAndNotBefore(int hours)
    {
        // The seam moves the lifetime clock: first a day and a minute in
        // (where a one-day timer would have fired with hours left), then to a
        // second before the deadline, then to the deadline itself.
        long offset = 0;
        Worker.ClockHook = () => Stopwatch.GetTimestamp() + Interlocked.Read(ref offset);
        TimeSpan lifetime = TimeSpan.FromHours(hours);
        bool cancelledAfterADay = true;
        bool cancelledJustBefore = true;
        bool flaggedJustBefore = true;
        bool cancelledAtTheDeadline = false;
        InvocationResult result = Within(TimeSpan.FromSeconds(20), () => InvokeEcho(Replaying("one-once-effect-success", lifetime), (context, args) =>
        {
            Interlocked.Exchange(ref offset, Ticks(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1)));
            cancelledAfterADay = context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
            Interlocked.Exchange(ref offset, Ticks(lifetime - TimeSpan.FromSeconds(1)));
            cancelledJustBefore = context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(300));
            flaggedJustBefore = context.IsCancelled;
            Interlocked.Exchange(ref offset, Ticks(lifetime));
            cancelledAtTheDeadline = context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            return new TextValue("late");
        }));
        Assert.That(Volatile.Read(ref cancelledAfterADay), Is.False, "the token was cancelled a day into the lifetime");
        Assert.That(Volatile.Read(ref cancelledJustBefore), Is.False, "the token was cancelled before the deadline");
        Assert.That(Volatile.Read(ref flaggedJustBefore), Is.False, "IsCancelled was true before the deadline");
        Assert.That(Volatile.Read(ref cancelledAtTheDeadline), Is.True, "the token was not cancelled at the deadline");
        Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
        Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a late response was sent");
    }

    private static long Ticks(TimeSpan span) => (long)(span.TotalSeconds * Stopwatch.Frequency);

    /// <summary>
    /// Fills every thread-pool thread, and 40 more that the pool would have
    /// to inject, with work items blocked on a gate, then queues a probe
    /// behind them. While the probe has not run, any work item queued later
    /// has not run either. Disposal opens the gate and waits for every item.
    /// </summary>
    private sealed class Starvation : IDisposable
    {
        private readonly ManualResetEventSlim gate = new();
        private readonly ManualResetEventSlim probe = new();
        private readonly CountdownEvent done;

        internal Starvation()
        {
            ThreadPool.GetMinThreads(out int workers, out _);
            int blockers = Math.Max(workers, ThreadPool.ThreadCount) + 40;
            done = new CountdownEvent(blockers + 1);
            for (int i = 0; i < blockers; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(
                    static s =>
                    {
                        s.gate.Wait(TimeSpan.FromSeconds(60));
                        s.done.Signal();
                    },
                    this,
                    preferLocal: false);
            }

            ThreadPool.UnsafeQueueUserWorkItem(
                static s =>
                {
                    s.probe.Set();
                    s.done.Signal();
                },
                this,
                preferLocal: false);
            Thread.Sleep(200);
            Assert.That(probe.IsSet, Is.False, "the thread pool was not saturated");
        }

        /// <summary>Whether the probe, and so any work item queued after it, has run.</summary>
        internal bool PoolRan => probe.IsSet;

        public void Dispose()
        {
            gate.Set();
            Assert.That(done.Wait(TimeSpan.FromSeconds(30)), Is.True, "the blocked work items did not finish");
            gate.Dispose();
            probe.Dispose();
            done.Dispose();
        }
    }

    /// <summary>Records, from the token's callback, whether the starved pool had run by then.</summary>
    private sealed class CancelWitness
    {
        private readonly ManualResetEventSlim cancelled = new();
        private bool poolRanFirst;

        internal void Watch(CancellationToken token, Starvation starvation) =>
            token.Register(() =>
            {
                Volatile.Write(ref poolRanFirst, starvation.PoolRan);
                cancelled.Set();
            });

        internal bool Cancelled(TimeSpan limit) => cancelled.Wait(limit);

        internal bool PoolRanFirst => Volatile.Read(ref poolRanFirst);
    }

    [Test]
    public void UnderAStarvedThreadPoolTheTokenIsCancelledAtTheDeadline()
    {
        var witness = new CancelWitness();
        using (var starvation = new Starvation())
        {
            InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(1)), (context, args) =>
            {
                witness.Watch(context.CancellationToken, starvation);
                context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                return new TextValue("late");
            }));
            Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
            Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a late response was sent");
            Assert.That(witness.Cancelled(TimeSpan.FromSeconds(10)), Is.True, "the token was not cancelled");
            Assert.That(witness.PoolRanFirst, Is.False, "the token was cancelled only after the starved pool ran");
        }
    }

    [Test]
    public void UnderAStarvedThreadPoolTheTokenIsCancelledWhenTheCallerCancels()
    {
        var witness = new CancelWitness();
        using var cancel = new CancellationTokenSource();
        using (var starvation = new Starvation())
        {
            InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success", TimeSpan.FromSeconds(30)), (context, args) =>
            {
                witness.Watch(context.CancellationToken, starvation);
                _ = new Canceller(cancel, TimeSpan.FromMilliseconds(300));
                context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                return new TextValue("late");
            }, cancel.Token));
            Assert.That(Reason(result), Is.EqualTo("aborted"), result.ToString());
            Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a late response was sent");
            Assert.That(witness.Cancelled(TimeSpan.FromSeconds(10)), Is.True, "the token was not cancelled");
            Assert.That(witness.PoolRanFirst, Is.False, "the token was cancelled only after the starved pool ran");
        }
    }

    [Test]
    public void UnderAStarvedThreadPoolTheTokenIsCancelledWhenTheInvocationEnds()
    {
        var witness = new CancelWitness();
        using (var starvation = new Starvation())
        {
            InvocationResult result = Within(TimeSpan.FromSeconds(15), () => InvokeEcho(Replaying("one-once-effect-success"), (context, args) =>
            {
                witness.Watch(context.CancellationToken, starvation);
                return new TextValue("pong");
            }));
            Assert.That(result.Ok, Is.True, result.ToString());
            Assert.That(witness.Cancelled(TimeSpan.FromSeconds(10)), Is.True, "the token was not cancelled");
            Assert.That(witness.PoolRanFirst, Is.False, "the token was cancelled only after the starved pool ran");
        }
    }
}

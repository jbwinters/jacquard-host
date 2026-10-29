using System;
using System.Diagnostics;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// Every kill of a worker while a handler may be running comes after the
/// cancellation of the handler's token has started, whichever check sees the
/// stop first. The wait for a handler reads the stop condition twice per
/// slice (its own predicate, then the fault check), and the call checks again
/// after the handler returns; these tests use the clock seam to land the
/// stop exactly between two such reads, so the outcome does not depend on
/// timing.
/// </summary>
[TestFixture]
public sealed class KillOrderTests
{
    /// <summary>How long the kill seam waits for the token's callback; the old order never runs it before the kill.</summary>
    private static readonly TimeSpan CallbackWait = TimeSpan.FromSeconds(3);

    [TearDown]
    public void ClearSeams()
    {
        Worker.ClockHook = null;
        Worker.ExpireHook = null;
        Host.HandlerThreadStartedHook = null;
    }

    /// <summary>What the stop is, and when it lands.</summary>
    public enum Stop
    {
        /// <summary>The deadline passes between the wait's predicate and its fault check.</summary>
        DeadlineInTheWait,

        /// <summary>The caller's token is cancelled between the wait's predicate and its fault check.</summary>
        CallerCancelInTheWait,

        /// <summary>The handler has returned; the deadline passes between the call's stopping check and its fault check.</summary>
        DeadlineAfterTheHandler,
    }

    /// <summary>
    /// A lifetime clock that, once armed on the caller's thread, answers that
    /// caller's first read with the real time and every later read (on any
    /// thread) with a time two hours on, past the one-hour deadline. With the
    /// cancel action set, the first read cancels the caller's token instead,
    /// after the stopping check has read it and before the fault check does.
    /// </summary>
    private sealed class SplitClock
    {
        private static readonly long Jump = (long)(2 * 3600.0 * Stopwatch.Frequency);
        private readonly Action? cancelAtFirstRead;
        private int caller = -1;
        private int reads;
        private readonly bool jumpAfterFirstRead;

        internal SplitClock(Action? cancelAtFirstRead, bool jumpAfterFirstRead)
        {
            this.cancelAtFirstRead = cancelAtFirstRead;
            this.jumpAfterFirstRead = jumpAfterFirstRead;
        }

        /// <summary>Arms the clock; called on the caller's thread.</summary>
        internal void Arm() => Volatile.Write(ref caller, Environment.CurrentManagedThreadId);

        internal long Now()
        {
            long real = Stopwatch.GetTimestamp();
            int armed = Volatile.Read(ref caller);
            if (armed < 0)
            {
                return real;
            }

            if (Environment.CurrentManagedThreadId == armed && Interlocked.Increment(ref reads) == 1)
            {
                cancelAtFirstRead?.Invoke();
                return real;
            }

            return jumpAfterFirstRead && Volatile.Read(ref reads) >= 1 ? real + Jump : real;
        }
    }

    [TestCase(Stop.DeadlineInTheWait)]
    [TestCase(Stop.CallerCancelInTheWait)]
    [TestCase(Stop.DeadlineAfterTheHandler)]
    public void TheTokenIsCancelledBeforeAKillThatLandsBetweenTwoChecks(Stop stop)
    {
        using var cancel = new CancellationTokenSource();
        using var registered = new ManualResetEventSlim();
        using var callback = new ManualResetEventSlim();
        bool returnsAtOnce = stop == Stop.DeadlineAfterTheHandler;
        var clock = new SplitClock(stop == Stop.CallerCancelInTheWait ? cancel.Cancel : null, stop != Stop.CallerCancelInTheWait);
        int kills = 0;
        bool callbackBeforeKill = false;
        Worker.ExpireHook = () =>
        {
            if (Interlocked.Increment(ref kills) == 1)
            {
                Volatile.Write(ref callbackBeforeKill, callback.Wait(CallbackWait));
            }
        };

        // The caller's thread arms the clock only once the handler has
        // registered its callback (and, for the post-handler case, returned),
        // so the caller's next clock read is the stopping check.
        Host.HandlerThreadStartedHook = () =>
        {
            Assert.That(registered.Wait(TimeSpan.FromSeconds(10)), Is.True, "the handler did not start");
            if (returnsAtOnce)
            {
                Thread.Sleep(100);
            }

            clock.Arm();
        };
        Worker.ClockHook = clock.Now;
        InvocationResult result = Within(TimeSpan.FromSeconds(30), () => Replaying("one-once-effect-success", TimeSpan.FromHours(1)).Invoke(
            Pinned.Identity("kit-echo"),
            Echo(),
            new Value[] { new TextValue("ping") },
            new OperationRegistry().Bind(Pinned.Identity("kit-world"), Pinned.Identity("send"), (context, args) =>
            {
                context.CancellationToken.Register(callback.Set);
                registered.Set();
                if (!returnsAtOnce)
                {
                    context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                }

                return new TextValue("pong");
            }),
            cancel.Token));
        Assert.That(Reason(result), Is.EqualTo(stop == Stop.CallerCancelInTheWait ? "aborted" : "deadline"), result.ToString());
        Assert.That(result.HostFrames, Has.Count.EqualTo(2), "a response was sent after the stop");
        Assert.That(Volatile.Read(ref kills), Is.GreaterThanOrEqualTo(1), "the worker was not killed");
        Assert.That(Volatile.Read(ref callbackBeforeKill), Is.True, "the worker was killed before the token's cancellation started");
    }
}

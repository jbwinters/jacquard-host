using System;
using System.Diagnostics;
using System.Threading;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// Callbacks a handler registers on <see cref="HandlerContext.CancellationToken"/>
/// are handler code. They must never run on, or hold, the caller's thread,
/// the thread that cancels the caller's token, or the deadline timer, and
/// whatever they throw must neither escape <see cref="Host.Invoke"/> nor
/// crash the process.
/// </summary>
[TestFixture]
public sealed class HandlerTokenTests
{
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
}

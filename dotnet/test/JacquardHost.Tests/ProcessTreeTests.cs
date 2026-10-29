using System;
using System.Diagnostics;
using System.Threading;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// The worker's process tree at expiry and after exit. The stand-in's
/// descendants use <c>sh</c> and <c>sleep</c>, so these run on Unix only.
/// </summary>
[TestFixture]
[Platform("Linux,MacOsX")]
public sealed class ProcessTreeTests
{
    [Test]
    public void TheDeadlineStopsTheWorkersProcessTree()
    {
        // A child, and a grandchild under a waiting shell, hold the pipes
        // while the worker stays silent.
        foreach (string mode in new[] { "child-silent", "grandchild-silent" })
        {
            var watch = Stopwatch.StartNew();
            InvocationResult result = InvokeReady(Faulty(mode, TimeSpan.FromSeconds(2)));
            Assert.That(watch.Elapsed < TimeSpan.FromSeconds(5) && Reason(result) == "deadline", Is.True, mode + ": " + watch.Elapsed + " " + result.HostEvidence);
            Dies(mode, Descendant(mode, result.Stderr));
        }
    }

    [Test]
    public void CancellationStopsAWorkerAndTheChildHoldingABlockedWrite()
    {
        // The worker stays alive and its child holds stdin without reading,
        // so the invoke frame cannot be written. Expiry, or cancellation,
        // returns promptly and kills both.
        foreach (bool cancelled in new[] { false, true })
        {
            using var cancel = new CancellationTokenSource();
            if (cancelled)
            {
                After(TimeSpan.FromMilliseconds(500), cancel.Cancel);
            }

            var watch = Stopwatch.StartNew();
            InvocationResult result = Within(TimeSpan.FromSeconds(15), () => Faulty("stdin-child-alive", TimeSpan.FromSeconds(cancelled ? 20 : 1))
                .Invoke(Pinned.Identity("kit-ready"), Echo(withWorld: false), new Value[] { new TextValue(new string('x', 220_000)) }, new OperationRegistry(), cancel.Token));
            int pid = Descendant("stdin-child-alive", result.Stderr);
            try
            {
                Assert.That(Reason(result), Is.EqualTo(cancelled ? "aborted" : "deadline"), result.ToString());
                Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
                Dies("stdin-child-alive", pid);
            }
            finally
            {
                Kill(pid);
            }
        }
    }

    [Test]
    public void DescendantsReparentedBeforeTheKillAreNotFound()
    {
        // A descendant that daemonized while the worker ran is outside the
        // tree: the host still returns at the deadline, but it survives.
        var watch = Stopwatch.StartNew();
        InvocationResult result = InvokeReady(Faulty("daemon-silent", TimeSpan.FromSeconds(2)));
        int pid = Descendant("daemon-silent", result.Stderr);
        try
        {
            Assert.That(watch.Elapsed < TimeSpan.FromSeconds(5) && Reason(result) == "deadline", Is.True, result.ToString());
            Assert.That(Alive(pid), Is.True, "the daemonized descendant was found after all; update LIMITS.md");
        }
        finally
        {
            Kill(pid);
        }

        // A launcher that exits at once, leaving a descendant holding stdout:
        // the host sees the pipe still held after the exit and reports a loss
        // after one second; the descendant survives.
        watch.Restart();
        result = InvokeReady(Faulty("descendant", TimeSpan.FromSeconds(10)));
        pid = Descendant("descendant", result.Stderr);
        try
        {
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(Reason(result), Is.EqualTo("carrier_loss"), result.ToString());
            Assert.That(result.Message, Does.Contain("worker descendants kept its pipes open after exit"));
            Assert.That(Alive(pid), Is.True, "the reparented descendant was found after all; update LIMITS.md");
        }
        finally
        {
            Kill(pid);
        }
    }

    [Test]
    public void AWriteBlockedByAnEscapedDescendantEndsAtTheDeadline()
    {
        // The worker exits after the selection while its child, now
        // reparented, holds stdin and never reads, so the invoke frame
        // cannot be written. No thread is left blocked on the pipe.
        var watch = Stopwatch.StartNew();
        InvocationResult result = Within(TimeSpan.FromSeconds(15), () => Faulty("stdin-descendant", TimeSpan.FromSeconds(2))
            .Invoke(Pinned.Identity("kit-ready"), Echo(withWorld: false), new Value[] { new TextValue(new string('x', 220_000)) }, new OperationRegistry()));
        int pid = Descendant("stdin-descendant", result.Stderr);
        try
        {
            Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
            Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
            Assert.That(result.ExitStatus, Is.EqualTo(0));
            Assert.That(Alive(pid), Is.True, "the reparented descendant was found after all; update LIMITS.md");
        }
        finally
        {
            Kill(pid);
        }
    }

    [Test]
    public void TheExitWaitObeysTheDeadline()
    {
        InvocationResult ok = InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10)));
        Assert.That(ok.Ok && ok.ExitStatus == 0 && ok.Signal is null, Is.True, ok.ToString());

        // The worker sends its terminal, closes stdout, and never exits.
        var watch = Stopwatch.StartNew();
        InvocationResult result = InvokeReady(Faulty("hang-after-close", TimeSpan.FromSeconds(2)));
        Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        Assert.That(result.Signal, Is.EqualTo("SIGKILL"));
        Assert.That(result.ExitStatus, Is.Null);
    }

    [Test]
    public void ASlowCleanExitSucceeds()
    {
        var watch = Stopwatch.StartNew();
        InvocationResult result = InvokeReady(Faulty("slow-exit", TimeSpan.FromSeconds(10)));
        Assert.That(result.Ok, Is.True, result.ToString());
        Assert.That(watch.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1500)));
    }

    [Test]
    public void CleanupAfterAnEarlyErrorIsNotHeldByAnEscapedDescendant()
    {
        // The worker sends a malformed hello and exits; its daemonized
        // descendant keeps stderr open, which disposal does not wait for.
        var watch = Stopwatch.StartNew();
        InvocationResult result = InvokeReady(Faulty("escaped-descendant-unknown-field", TimeSpan.FromSeconds(10)));
        TimeSpan elapsed = watch.Elapsed;
        Kill(Descendant("escaped-descendant-unknown-field", result.Stderr));
        Assert.That(result.Kind == ResultKind.ProtocolError && result.Code == "E1601", Is.True, result.ToString());
        Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
    }

    [Test]
    public void ADescendantHoldingStderrAfterTheExitIsALoss()
    {
        // As in the Go and Rust hosts, and unlike the Java host: once the
        // worker has exited, stderr must close within one second. The
        // descendant keeps running; the host cannot find it.
        foreach (string mode in new[] { "stderr-descendant", "escaped-descendant" })
        {
            var watch = Stopwatch.StartNew();
            InvocationResult result = InvokeReady(Faulty(mode, TimeSpan.FromSeconds(10)));
            int pid = Descendant(mode, result.Stderr);
            try
            {
                Assert.That(result.Kind == ResultKind.CarrierLoss && result.Code == "E1611" && Reason(result) == "carrier_loss", Is.True, mode + ": " + result);
                Assert.That(result.Message, Does.Contain("worker descendants kept its pipes open after exit"));
                Assert.That(result.ExitStatus, Is.EqualTo(0), mode);
                Assert.That(result.CoreFrames, Has.Count.EqualTo(2), mode + ": the outcome was read");
                Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), mode);
                Assert.That(Alive(pid), Is.True, mode + ": the descendant was stopped after all; update LIMITS.md");
            }
            finally
            {
                Kill(pid);
            }
        }

        InvocationResult failed = InvokeReady(Faulty("stderr-descendant-nonzero", TimeSpan.FromSeconds(10)));
        Kill(Descendant("stderr-descendant-nonzero", failed.Stderr));
        Assert.That(failed.Kind == ResultKind.CarrierLoss && failed.Code == "E1611", Is.True, failed.ToString());
        Assert.That(failed.ExitStatus, Is.EqualTo(74));
    }
}

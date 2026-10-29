using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// Open descriptors, adapter threads, and the heap across invocations and
/// failures, read from <c>/proc</c>. The adapter starts no thread of its own
/// except one per handler call, named <c>jacquard-handler</c>, and one short-lived
/// <c>jacquard-handler-cancel</c> per invocation that gave a handler a token.
/// Both names read as <c>jacquard-handle</c> in <c>/proc</c> (15 bytes), so
/// the counts include both.
/// </summary>
[TestFixture]
[Platform("Linux")]
public sealed class DescriptorTests
{
    private static bool DescriptorsBackTo(int before) => Eventually(() => Descriptors() <= before);

    [TearDown]
    public void ClearSeams()
    {
        Worker.SpawnedHook = null;
        Worker.DescriptorHook = null;
        Worker.ExpireHook = null;
    }

    [Test]
    public void ADescriptorOnADllOutsideTheRuntimeAndTestDirectoriesIsCounted()
    {
        // Only assembly files the runtime loads from its own directory or the
        // test output are left out of the count; any other .dll is counted.
        string dir = Directory.CreateTempSubdirectory("jacquard-dll-").FullName;
        string path = Path.Combine(dir, "Leak.dll");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0 });
            int before = Descriptors();
            using (File.OpenRead(path))
            {
                Assert.That(Descriptors(), Is.EqualTo(before + 1), "a descriptor on " + path + " was not counted");
            }

            Assert.That(Descriptors(), Is.EqualTo(before));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(dir);
        }
    }

    /// <summary>Where the seam fails the worker's setup after <c>Process.Start</c> succeeded.</summary>
    public enum SetupFailure
    {
        /// <summary>Before the carrier obtains any redirected stream.</summary>
        BeforeThePipes,

        /// <summary>While the carrier checks the streams it obtained: stderr is not a pipe stream.</summary>
        WhileCheckingThePipes,
    }

    [TestCase(SetupFailure.BeforeThePipes)]
    [TestCase(SetupFailure.WhileCheckingThePipes)]
    public void AFailureAfterTheSpawnKillsTheWorkerThroughTheKillPathAndReleasesIt(SetupFailure failure)
    {
        // A seam fails the worker's setup after Process.Start succeeded, as
        // redirected streams that are not pipe streams would. In the second
        // case all three streams were obtained, and stdin and stdout were
        // checked, before the stderr check fails.
        Assert.That(InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10))).Ok, Is.True); // warm up
        int before = Descriptors();
        var pids = new List<int>();
        int kills = 0;
        int checkedStreams = 0;
        Worker.SpawnedHook = process =>
        {
            pids.Add(process.Id);
            if (failure == SetupFailure.BeforeThePipes)
            {
                throw new NotSupportedException("seam: worker pipes are not pipe streams");
            }
        };
        if (failure == SetupFailure.WhileCheckingThePipes)
        {
            Worker.DescriptorHook = _ =>
            {
                if (Interlocked.Increment(ref checkedStreams) % 3 == 0)
                {
                    throw new NotSupportedException("seam: worker pipes are not pipe streams");
                }
            };
        }

        Worker.ExpireHook = () => Interlocked.Increment(ref kills);
        try
        {
            for (int i = 0; i < 3; i++)
            {
                InvocationResult result = Within(TimeSpan.FromSeconds(20), () => InvokeReady(Faulty("silent", TimeSpan.FromSeconds(30))));

                // Release is synchronous here: nothing outlives Invoke that
                // holds a host pipe, so the count is read at once, with no
                // window in which a collection could finalize a lost stream.
                int after = Descriptors();
                Assert.That(after, Is.LessThanOrEqualTo(before), "descriptors grew from " + before + " to " + after + " after invocation " + (i + 1));
                Assert.That(pids, Has.Count.EqualTo(i + 1), "the seam did not run");
                Assert.That(Volatile.Read(ref checkedStreams), Is.EqualTo(failure == SetupFailure.BeforeThePipes ? 0 : 3 * (i + 1)), "the streams were not checked as the seam expects");
                Assert.That(Alive(pids[i]), Is.False, "the spawned worker is still running after Invoke returned");
                Assert.That(result.Kind, Is.EqualTo(ResultKind.CarrierLoss), result.ToString());
                Assert.That(Reason(result), Is.EqualTo("worker_spawn_failed"), result.ToString());
                Assert.That(result.Code, Is.EqualTo("E1611"));
                Assert.That(result.HostEvidence.Get("detail")?.AsString(), Is.EqualTo("could not poll the worker's pipes (seam: worker pipes are not pipe streams)"));
                Assert.That(result.Signal, Is.EqualTo("SIGKILL"), "the killed worker's exit was not waited for and recorded");
                Assert.That(result.CoreFrames, Is.Empty);
                Assert.That(result.HostFrames, Is.Empty);
            }

            Assert.That(Volatile.Read(ref kills), Is.EqualTo(3), "the kill did not go through the common kill path once per worker");
            Assert.That(Eventually(() => AdapterThreads() == 0), Is.True, "adapter threads remain");
        }
        finally
        {
            Worker.SpawnedHook = null;
            Worker.DescriptorHook = null;
            foreach (int pid in pids)
            {
                if (Alive(pid))
                {
                    Kill(pid);
                }
            }
        }
    }

    [Test]
    public void InvocationsReleaseTheirPipesAndThreads()
    {
        Assert.That(InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10))).Ok, Is.True); // warm up lazily opened files
        int before = Descriptors();
        Assert.That(AdapterThreads(), Is.EqualTo(0));
        for (int i = 0; i < 5; i++)
        {
            InvocationResult result = InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10)));
            Assert.That(result.Ok, Is.True, result.ToString());
        }

        Assert.That(DescriptorsBackTo(before), Is.True, "descriptors grew from " + before + " to " + Descriptors());
        Assert.That(AdapterThreads(), Is.EqualTo(0), "adapter threads outlived their invocations");

        // A daemonized descendant keeps holding stderr after the invocation
        // returns; the host does not wait for it, and its pipes are closed.
        InvocationResult escaped = InvokeReady(Faulty("escaped-descendant", TimeSpan.FromSeconds(10)));
        int pid = Descendant("escaped-descendant", escaped.Stderr);
        bool released = DescriptorsBackTo(before);
        int threads = AdapterThreads();
        Kill(pid);
        Assert.That(released, Is.True, "escaped descendant: descriptors grew from " + before + " to " + Descriptors());
        Assert.That(threads, Is.EqualTo(0), "escaped descendant: adapter threads remain");

        // Handlers run on their own threads, which end with the handler; the
        // count sees a handler thread while one runs. Each invocation then
        // starts a cancel thread as it ends; the loop waits for it to finish
        // before the next invocation, so the handler counts only its own
        // thread.
        int during = -1;
        OperationRegistry registry = new OperationRegistry().Bind(Pinned.Identity("kit-world"), Pinned.Identity("send"), (context, args) =>
        {
            during = AdapterThreads();
            return new TextValue("pong");
        });
        Host replaying = Replaying("one-once-effect-success");
        for (int i = 0; i < 3; i++)
        {
            Assert.That(Eventually(() => AdapterThreads() == 0), Is.True, "an earlier invocation's threads remain");
            InvocationResult result = replaying.Invoke(Pinned.Identity("kit-echo"), Echo(), new Value[] { new TextValue("ping") }, registry);
            Assert.That(result.Ok, Is.True, result.ToString());
        }

        Assert.That(during, Is.EqualTo(1), "the handler thread was not seen while it ran");
        Assert.That(Eventually(() => AdapterThreads() == 0), Is.True, "handler threads remain");
        Assert.That(DescriptorsBackTo(before), Is.True, "handlers: descriptors grew from " + before + " to " + Descriptors());

        // Failure paths release the same resources: expiry, cancellation, a
        // truncated frame, and a spawn failure.
        for (int i = 0; i < 3; i++)
        {
            Assert.That(Reason(InvokeReady(Faulty("silent", TimeSpan.FromMilliseconds(500)))), Is.EqualTo("deadline"));
            using var cancelled = new CancellationTokenSource();
            After(TimeSpan.FromMilliseconds(200), cancelled.Cancel);
            Assert.That(Reason(InvokeReady(Faulty("silent", TimeSpan.FromSeconds(10)), cancelled.Token)), Is.EqualTo("aborted"));
            Assert.That(InvokeReady(Faulty("truncated", TimeSpan.FromSeconds(10))).Ok, Is.False);
            Assert.That(Reason(InvokeReady(new Host("unused", new HostOptions { Carrier = new WorkerOptions { Command = new[] { "/no/such/executable" } } }))), Is.EqualTo("worker_spawn_failed"));
        }

        Assert.That(Eventually(() => AdapterThreads() == 0), Is.True, "failure paths: adapter threads remain");
        Assert.That(DescriptorsBackTo(before), Is.True, "failure paths: descriptors grew from " + before + " to " + Descriptors());
    }

    [Test]
    public void AnEscapedDescendantHoldingStdinLeavesNoThreadOrDescriptor()
    {
        // The worker exits after the selection; its reparented child holds
        // stdin and never reads, so the invoke frame cannot be written. The
        // call ends at the deadline with the descendant still alive, and
        // nothing of the host's stays behind with it.
        Assert.That(InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10))).Ok, Is.True); // warm up
        int before = Descriptors();
        InvocationResult result = Within(TimeSpan.FromSeconds(15), () => Faulty("stdin-descendant", TimeSpan.FromSeconds(2))
            .Invoke(Pinned.Identity("kit-ready"), Echo(withWorld: false), new Value[] { new TextValue(new string('x', 220_000)) }, new OperationRegistry()));
        int pid = Descendant("stdin-descendant", result.Stderr);
        try
        {
            Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
            Assert.That(Alive(pid), Is.True);
            Assert.That(AdapterThreads(), Is.EqualTo(0));
            Assert.That(DescriptorsBackTo(before), Is.True, "descriptors grew from " + before + " to " + Descriptors());
        }
        finally
        {
            Kill(pid);
        }
    }

    [Test]
    public void AStderrFloodAfterTheWorkerExitsDoesNotHoldTheCaller() =>
        AssertFloodDoesNotHold("(yes jacquard-flood >&2 & echo $! > \"$PIDFILE\"); sleep 0.3; exit 0", TimeSpan.FromSeconds(10), null);

    [Test]
    public void AStderrFloodDoesNotHoldExpiry() =>
        AssertFloodDoesNotHold("(yes jacquard-flood >&2 & echo $! > \"$PIDFILE\"); exec sleep 30", TimeSpan.FromMilliseconds(500), null);

    [Test]
    public void AStderrFloodDoesNotHoldCancellation() =>
        AssertFloodDoesNotHold("(yes jacquard-flood >&2 & echo $! > \"$PIDFILE\"); exec sleep 30", TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(500));

    [Test]
    public void AStdoutFloodDoesNotHoldTheCaller() =>
        AssertFloodDoesNotHold("(yes jacquard-flood & echo $! > \"$PIDFILE\"); exec sleep 30", TimeSpan.FromSeconds(10), null);

    [Test]
    public void ASilentDescendantHoldingStdoutAndStderrDoesNotHoldTheCaller() =>
        AssertFloodDoesNotHold("(sleep 30 & echo $! > \"$PIDFILE\"); exit 0", TimeSpan.FromSeconds(10), null);

    /// <summary>
    /// The worker leaves a reparented descendant writing to its stdout or
    /// stderr as fast as it can (or holding them silently); the kill cannot
    /// find it. The invocation must return within its bound, keep no flood
    /// in the heap, and leave the writer without a reader once it returns.
    /// </summary>
    private static void AssertFloodDoesNotHold(string script, TimeSpan deadline, TimeSpan? cancelAfter)
    {
        Assert.That(InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10))).Ok, Is.True); // warm up
        int before = Descriptors();
        string dir = Directory.CreateTempSubdirectory("jacquard-flood-").FullName;
        string pidFile = Path.Combine(dir, "pid");
        var env = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["PIDFILE"] = pidFile,
        };
        var options = new WorkerOptions { Command = new[] { "sh", "-c", script }, Environment = env, Deadline = deadline };
        long heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        using var cancel = new CancellationTokenSource();
        if (cancelAfter is { } delay)
        {
            After(delay, cancel.Cancel);
        }

        var watch = Stopwatch.StartNew();
        InvocationResult result = Within(TimeSpan.FromSeconds(30), () => new Host("unused", new HostOptions { Carrier = options }).ShutdownProbe(cancel.Token));
        TimeSpan elapsed = watch.Elapsed;
        long heapAfter = GC.GetTotalMemory(forceFullCollection: false);
        Assert.That(Eventually(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0), Is.True, "the descendant never started");
        int pid = int.Parse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture);
        try
        {
            Assert.That(result.Ok, Is.False, result.ToString());
            TimeSpan bound = (cancelAfter ?? (deadline < TimeSpan.FromSeconds(1) ? deadline : TimeSpan.FromSeconds(1))) + TimeSpan.FromSeconds(3);
            Assert.That(elapsed, Is.LessThan(bound), "returned after " + elapsed);
            Assert.That(heapAfter - heapBefore, Is.LessThan(64L << 20), "the heap grew by " + ((heapAfter - heapBefore) >> 20) + " MiB");
            if (script.Contains("yes", StringComparison.Ordinal))
            {
                // The host closed its end, so the writer's next write fails.
                Assert.That(Eventually(() => !Alive(pid)), Is.True, "the flood still has a reader");
            }
        }
        finally
        {
            Kill(pid);
            File.Delete(pidFile);
            Directory.Delete(dir);
        }

        Assert.That(Eventually(() => AdapterThreads() == 0), Is.True, "adapter threads remain");
        Assert.That(DescriptorsBackTo(before), Is.True, "descriptors grew from " + before + " to " + Descriptors());
    }
}

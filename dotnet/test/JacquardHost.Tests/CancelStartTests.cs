using System;
using System.Globalization;
using System.IO;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>
/// When the host cannot start a thread to cancel the handlers' token it falls
/// back to a thread-pool work item; when that fails too it records the
/// failure and gives up. Either way <see cref="Host.Invoke"/> returns its
/// result, nothing runs a callback on the caller's thread, and the worker is
/// disposed. A seam stands for the failures, as an out-of-memory process
/// would produce them.
/// </summary>
[TestFixture]
[Platform("Linux")]
public sealed class CancelStartTests
{
    [TearDown]
    public void ClearSeams() => Host.CancelStartHook = null;

    /// <summary>Live child processes of this process, from <c>/proc</c>.</summary>
    private static int Children()
    {
        string self = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        int count = 0;
        foreach (string dir in Directory.GetDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            try
            {
                string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                string[] fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (fields[1] == self && fields[0] != "Z")
                {
                    count++;
                }
            }
            catch (IOException)
            {
                // The process ended while it was listed.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours.
            }
        }

        return count;
    }

    private static void Fail(bool pool, string where)
    {
        if (where == "thread")
        {
            throw new OutOfMemoryException("seam: no thread");
        }

        if (pool)
        {
            throw new NotSupportedException("seam: no pool work item");
        }
    }

    private sealed class Observed
    {
        internal readonly ManualResetEventSlim Done = new();
        internal int CallerThread = -1;
        internal int CallbackThread = -1;
        internal bool CallbackOnPool;
        internal bool TokenCancelled = true;
        internal bool FlaggedCancelled;
    }

    private static InvocationResult InvokeWith(Observed seen, TimeSpan deadline, bool waits, out Exception? thrown)
    {
        InvocationResult? result = null;
        Exception? failure = null;
        Host host = Replaying("one-once-effect-success", deadline);
        var registry = new OperationRegistry().Bind(Pinned.Identity("kit-world"), Pinned.Identity("send"), (context, args) =>
        {
            context.CancellationToken.Register(() =>
            {
                Volatile.Write(ref seen.CallbackThread, Environment.CurrentManagedThreadId);
                Volatile.Write(ref seen.CallbackOnPool, Thread.CurrentThread.IsThreadPoolThread);
            });
            if (waits)
            {
                Volatile.Write(ref seen.TokenCancelled, context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(3)));
                Volatile.Write(ref seen.FlaggedCancelled, context.IsCancelled);
                seen.Done.Set();
            }

            return new TextValue("pong");
        });
        Within(TimeSpan.FromSeconds(20), () =>
        {
            Volatile.Write(ref seen.CallerThread, Environment.CurrentManagedThreadId);
            try
            {
                result = host.Invoke(Pinned.Identity("kit-echo"), Echo(), new Value[] { new TextValue("ping") }, registry);
            }
            catch (Exception error)
            {
                failure = error;
            }

            return 0;
        });
        thrown = failure;
        return result!;
    }

    [Test]
    public void WithoutAThreadTheTokenIsCancelledOnAPoolThread()
    {
        Host.CancelStartHook = where => Fail(pool: false, where);
        var seen = new Observed();
        InvocationResult result = InvokeWith(seen, TimeSpan.FromSeconds(1), waits: true, out Exception? thrown);
        Assert.That(thrown, Is.Null, "Invoke threw");
        Assert.That(seen.Done.Wait(TimeSpan.FromSeconds(10)), Is.True, "the handler did not return");
        Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
        Assert.That(result.HandlerTokenNotCancelled, Is.False);
        Assert.That(Volatile.Read(ref seen.TokenCancelled), Is.True, "the token was not cancelled");
        Assert.That(Volatile.Read(ref seen.CallbackOnPool), Is.True, "the callback did not run on a pool thread");
        Assert.That(Volatile.Read(ref seen.CallbackThread), Is.Not.EqualTo(Volatile.Read(ref seen.CallerThread)));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void WithNeitherAThreadNorAPoolWorkItemInvokeReturnsAndDisposesTheWorker(bool atTheDeadline)
    {
        Assert.That(InvokeReady(Faulty("ok", TimeSpan.FromSeconds(10))).Ok, Is.True); // warm up lazily opened files
        int before = Descriptors();
        Assert.That(Eventually(() => Children() == 0), Is.True, "a worker from an earlier test is still running");
        int attempts = 0;
        Host.CancelStartHook = where =>
        {
            Interlocked.Increment(ref attempts);
            Fail(pool: true, where);
        };
        var seen = new Observed();
        InvocationResult result = InvokeWith(seen, atTheDeadline ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(20), waits: atTheDeadline, out Exception? thrown);
        Assert.Multiple((Action)(() =>
        {
            Assert.That(thrown, Is.Null, "Invoke threw: " + thrown);
            Assert.That(Eventually(() => Children() == 0), Is.True, "the worker was not disposed: it is still running");
            Assert.That(Eventually(() => Descriptors() <= before), Is.True, "the worker was not disposed: descriptors grew from " + before + " to " + Descriptors());
        }));
        Assert.That(Volatile.Read(ref attempts), Is.EqualTo(2), "the host did not try a thread and then the pool, once");
        Assert.That(result.HandlerTokenNotCancelled, Is.True, "the failure was not recorded");
        if (atTheDeadline)
        {
            Assert.That(Reason(result), Is.EqualTo("deadline"), result.ToString());
            Assert.That(seen.Done.Wait(TimeSpan.FromSeconds(10)), Is.True, "the handler did not return");
            Assert.That(Volatile.Read(ref seen.TokenCancelled), Is.False, "the token was cancelled");
            Assert.That(Volatile.Read(ref seen.FlaggedCancelled), Is.True, "IsCancelled did not report the stop");
        }
        else
        {
            Assert.That(result.Ok, Is.True, result.ToString());
        }

        Assert.That(Volatile.Read(ref seen.CallbackThread), Is.EqualTo(-1), "a callback ran");
    }
}

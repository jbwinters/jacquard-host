using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Conformance;
using JacquardLang.Host.Protocol;
using NUnit.Framework;

// Host's test seams (RequestDecodedHook and the others) are process-global
// statics, so no two tests may run at once. No fixture is Parallelizable, and
// this keeps the worker count at one if one becomes so.
[assembly: LevelOfParallelism(1)]

namespace JacquardLang.Host.Tests;

/// <summary>Shared helpers for the tests.</summary>
internal static class Support
{
    private static readonly Lazy<Kit> LoadedKit = new(Kit.Load);

    internal static Kit Pinned => LoadedKit.Value;

    /// <summary>Runs code expected to fail with a protocol refusal carrying <paramref name="expected"/>.</summary>
    internal static ProtocolException Code(string expected, string context, Action body)
    {
        try
        {
            body();
        }
        catch (ProtocolException error)
        {
            Assert.That(error.Code, Is.EqualTo(expected), context + ": " + error.Message);
            return error;
        }

        Assert.Fail(context + ": expected " + expected + ", got success");
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Asserts that <paramref name="body"/> throws <typeparamref name="T"/>, and returns it.</summary>
    internal static T Throws<T>(Action body, string message = "")
        where T : Exception => Assert.Throws<T>(body, message)!;

    internal static Json Parse(string text) => JsonCodec.Decode(Encoding.UTF8.GetBytes(text), 64);

    internal static Hash HashOf(char c) => new(new string(c, 64));

    internal static CallInterface Unit() => new(Array.Empty<BoundaryType>(), new TupleType(), Array.Empty<Hash>());

    internal static CallInterface Echo(bool withWorld = true)
    {
        var text = new NominalType(Pinned.Identity("text"));
        return new CallInterface(new[] { text }, text, withWorld ? new[] { Pinned.Identity("kit-world") } : Array.Empty<Hash>());
    }

    /// <summary>The dotnet executable that runs the stand-in.</summary>
    private static string Muxer()
    {
        string? host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host))
        {
            return host;
        }

        string? process = Environment.ProcessPath;
        if (process is not null && Path.GetFileNameWithoutExtension(process) == "dotnet")
        {
            return process;
        }

        string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        return root is not null && File.Exists(Path.Combine(root, "dotnet")) ? Path.Combine(root, "dotnet") : "dotnet";
    }

    /// <summary>The stand-in worker command: this runtime running the stand-in assembly.</summary>
    internal static IReadOnlyList<string> StandinCommand() => new[]
    {
        Muxer(),
        "exec",
        "--runtimeconfig",
        Path.Combine(AppContext.BaseDirectory, "JacquardLang.Host.Tests.runtimeconfig.json"),
        Path.Combine(AppContext.BaseDirectory, "JacquardLang.Host.StandIn.dll"),
    };

    internal static Dictionary<string, string> Environment0()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            env[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        return env;
    }

    internal static WorkerOptions Standin(string mode, string transcript, TimeSpan deadline)
    {
        Dictionary<string, string> env = Environment0();
        env[StandIn.Program.ModeVariable] = mode;
        env[StandIn.Program.CaseVariable] = transcript;

        // Keep each stand-in small.
        env["DOTNET_GCHeapHardLimit"] = "0x8000000";
        env["DOTNET_TieredPGO"] = "0";
        return new WorkerOptions { Command = StandinCommand(), Environment = env, Deadline = deadline };
    }

    internal static Host Faulty(string mode, TimeSpan deadline, Limits? limits = null) =>
        new("unused", new HostOptions { Carrier = Standin(mode, string.Empty, deadline), Limits = limits });

    internal static Host Replaying(string transcript, TimeSpan? deadline = null) =>
        new("unused", new HostOptions { Carrier = Standin("replay", transcript, deadline ?? TimeSpan.FromSeconds(20)) });

    internal static InvocationResult InvokeReady(Host host, CancellationToken cancellationToken = default) =>
        host.Invoke(Pinned.Identity("kit-ready"), Unit(), Array.Empty<Value>(), new OperationRegistry(), cancellationToken);

    internal static string? Reason(InvocationResult result) => result.HostEvidence.Get("reason")?.AsString();

    /// <summary>The pid a stand-in printed on stderr.</summary>
    internal static int Descendant(string mode, IReadOnlyList<byte> stderr)
    {
        string text = Encoding.UTF8.GetString(stderr.ToArray()).Trim();
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pid))
        {
            Assert.Fail(mode + ": stand-in stderr " + text);
        }

        return pid;
    }

    /// <summary>Whether <paramref name="pid"/> is a live process (not a zombie).</summary>
    internal static bool Alive(int pid)
    {
        string stat = "/proc/" + pid.ToString(CultureInfo.InvariantCulture) + "/stat";
        if (Directory.Exists("/proc/self"))
        {
            try
            {
                string text = File.ReadAllText(stat);
                return text[(text.LastIndexOf(')') + 2)..][0] != 'Z';
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static void Kill(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    /// <summary>Waits up to two seconds for <paramref name="pid"/> to exit, killing it and failing otherwise.</summary>
    internal static void Dies(string mode, int pid)
    {
        if (!Eventually(() => !Alive(pid)))
        {
            Kill(pid);
            Assert.Fail(mode + ": descendant survived the worker");
        }
    }

    /// <summary>Waits up to <paramref name="limit"/> (two seconds by default) for a condition.</summary>
    internal static bool Eventually(Func<bool> condition, TimeSpan? limit = null)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > (limit ?? TimeSpan.FromSeconds(2)))
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on another thread and fails if it has not
    /// returned within <paramref name="limit"/>, so a call that hangs fails
    /// the test instead of holding the run.
    /// </summary>
    internal static T Within<T>(TimeSpan limit, Func<T> body)
    {
        T? value = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                value = body();
            }
            catch (Exception error)
            {
                failure = error;
            }
        })
        {
            IsBackground = true,
            Name = "test-bounded",
        };
        thread.Start();
        if (!thread.Join(limit))
        {
            Assert.Fail("the call did not return within " + limit);
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("the call failed", failure);
        }

        return value!;
    }

    /// <summary>
    /// Open descriptors of this process (Linux), except those on assembly
    /// files: the runtime opens an assembly when it first loads it, which
    /// can happen in the middle of a test (for example when tiered
    /// compilation recompiles a method), and keeps it open. The adapter opens
    /// no assembly files.
    /// </summary>
    internal static int Descriptors()
    {
        int count = 0;
        foreach (string fd in Directory.GetFiles("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(fd).LinkTarget;
            }
            catch (IOException)
            {
                // Closed while it was listed.
                continue;
            }

            if (target is null || !target.EndsWith(".dll", StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Live threads of this process named by the adapter (Linux, names truncated to 15 bytes).</summary>
    internal static int AdapterThreads()
    {
        int count = 0;
        foreach (string task in Directory.GetDirectories("/proc/self/task"))
        {
            try
            {
                if (File.ReadAllText(Path.Combine(task, "comm")).StartsWith("jacquard-", StringComparison.Ordinal))
                {
                    count++;
                }
            }
            catch (IOException)
            {
                // The thread ended while it was listed.
            }
        }

        return count;
    }

    /// <summary>Calls <paramref name="action"/> after <paramref name="delay"/> on a background thread.</summary>
    internal static Thread After(TimeSpan delay, Action action)
    {
        var thread = new Thread(() =>
        {
            Thread.Sleep(delay);
            try
            {
                action();
            }
            catch (ObjectDisposedException)
            {
                // The test already finished and disposed what it would stop.
            }
        })
        {
            IsBackground = true,
            Name = "test-trigger",
        };
        thread.Start();
        return thread;
    }
}

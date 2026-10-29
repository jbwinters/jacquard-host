using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using JacquardLang.Host.Conformance;
using JacquardLang.Host.Protocol;
using Microsoft.Win32.SafeHandles;

namespace JacquardLang.Host.StandIn;

/// <summary>
/// A recorded or faulty worker for the offline tests. It implements no Core
/// semantics and is not part of the adapter's interface. The descendant modes
/// use <c>sh</c> and <c>sleep</c>, so they are Unix-only.
/// </summary>
public static partial class Program
{
    /// <summary>Names the stand-in behaviour.</summary>
    public const string ModeVariable = "JACQUARD_DOTNET_STANDIN";

    /// <summary>Names the transcript replayed in <c>replay</c> mode.</summary>
    public const string CaseVariable = "JACQUARD_DOTNET_STANDIN_CASE";

    // Descriptors 0, 1, and 2 themselves, so closing stdout really closes it.
    private static readonly FileStream Out = new(new SafeFileHandle(1, ownsHandle: true), FileAccess.Write, 1);
    private static readonly FileStream Err = new(new SafeFileHandle(2, ownsHandle: false), FileAccess.Write, 1);
    private static readonly FileStream In = new(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read, 1);

    /// <summary>Runs the mode named by <see cref="ModeVariable"/>.</summary>
    public static int Main()
    {
        string mode = Env(ModeVariable);
        switch (mode)
        {
            case "exit":
                return 0;
            case "silent":
                return Block();
            case "truncated":
                Out.Write(new byte[] { 0, 0, 0 });
                Out.Flush();
                return 0;
            case "descendant":
                // Leave a descendant holding stdout, like a launcher that does not exec.
                Shell("sleep 30 </dev/null 2>/dev/null & echo $! >&2", wait: true);
                return 0;
            case "child-silent":
                // A child holding stdout and stderr while the worker stays alive.
                Report(Shell("exec sleep 30 </dev/null", wait: false));
                return Block();
            case "grandchild-silent":
                // A grandchild holding the pipes under a shell that waits for it.
                Shell("sleep 30 </dev/null & echo $! >&2; wait", wait: false);
                return Block();
            case "daemon-silent":
                // A descendant reparented while the worker is alive: its shell exits at once.
                Shell("sleep 30 </dev/null >/dev/null & echo $! >&2", wait: true);
                return Block();
            case "stderr-descendant":
            case "stderr-descendant-nonzero":
                // Answer normally, but leave a child holding only stderr.
                Report(Shell("exec sleep 30 </dev/null >/dev/null", wait: false));
                return Fault(Kit.Load(), mode == "stderr-descendant" ? "ok" : "nonzero");
            case "escaped-descendant":
            case "escaped-descendant-unknown-field":
                // A daemonized descendant holds stderr; it is not the worker's
                // descendant by the time the host could look for it.
                Shell("sleep 30 </dev/null >/dev/null & echo $! >&2", wait: true);
                return Fault(Kit.Load(), mode == "escaped-descendant" ? "ok" : "unknown-field");
            case "stdin-descendant":
            case "stdin-child-alive":
            {
                // A child holds stdin and never reads. The worker reads the
                // selection, then exits (or stays alive without reading), so
                // the invoke write can only wait.
                Report(Shell("exec sleep 30 >/dev/null 2>/dev/null", wait: false));
                Send(Kit.Load().Transcripts[0].CoreOutput[0]);
                ReadFrame();
                return mode == "stdin-descendant" ? 0 : Block();
            }

            case "replay":
                return Replay(Kit.Load(), Env(CaseVariable));
            case "large-request":
            {
                // An effect request whose one argument is a text at the text
                // ceiling, so the host spends a while decoding it.
                Transcript effect = Kit.Load().Find("one-once-effect-success")!;
                Send(effect.CoreOutput[0]);
                ReadFrame();
                ReadFrame();
                Json text = Json.Object(("kind", Json.Of("text")), ("value", Json.Of(new string('x', 262_144))));
                Send(effect.CoreOutput[1].With("arguments", Json.Of(new[] { text })));
                return Block();
            }

            case "flood-after-terminal":
            {
                // The pure recording, then valid frames of about 600 KB each.
                Kit kit = Kit.Load();
                Replay(kit, kit.Transcripts[0].Name);
                JsonObject padding = Json.Object(("padding", Json.Of(Enumerable.Range(0, 100_000).Select(i => (Json)Json.Of(i)))));
                for (int i = 0; i < 300; i++)
                {
                    Send(padding);
                }

                return 0;
            }

            case "":
                return 2;
            default:
                return Fault(Kit.Load(), mode);
        }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    /// <summary>Starts <c>sh -c script</c> sharing this process's descriptors; returns its pid.</summary>
    private static int Shell(string script, bool wait)
    {
        var info = new ProcessStartInfo("sh") { UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        using Process shell = Process.Start(info) ?? throw new InvalidOperationException("sh did not start");
        if (wait)
        {
            shell.WaitForExit();
        }

        return shell.Id;
    }

    private static void Report(int pid)
    {
        Err.Write(Encoding.ASCII.GetBytes(pid.ToString(CultureInfo.InvariantCulture)));
        Err.Flush();
    }

    private static void Send(JsonObject frame)
    {
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(JsonCodec.Stringify(frame, 256));
            byte[] prefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payload.Length);
            Out.Write(prefix);
            Out.Write(payload);
            Out.Flush();
        }
        catch (IOException)
        {
            // The host may have closed the pipe.
        }
    }

    private static bool Discard(long count)
    {
        byte[] chunk = new byte[8192];
        try
        {
            for (long left = count; left > 0;)
            {
                int n = In.Read(chunk, 0, (int)Math.Min(chunk.Length, left));
                if (n <= 0)
                {
                    return false;
                }

                left -= n;
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool ReadFrame()
    {
        byte[] prefix = new byte[4];
        try
        {
            for (int filled = 0; filled < 4;)
            {
                int n = In.Read(prefix, filled, 4 - filled);
                if (n <= 0)
                {
                    return false;
                }

                filled += n;
            }
        }
        catch (IOException)
        {
            return false;
        }

        return Discard(BinaryPrimitives.ReadUInt32BigEndian(prefix));
    }

    private static int Replay(Kit kit, string name)
    {
        Transcript? transcript = kit.Find(name);
        if (transcript is null)
        {
            return 3;
        }

        int index = 0;
        bool raw = false;
        foreach (TranscriptStep step in transcript.HostInput)
        {
            if (step.Direction == "core_to_host")
            {
                JsonObject frame = transcript.CoreOutput[index++];
                Send(frame);
                if (frame.Get("kind")?.AsString() is "outcome" or "fatal" or "shutdown_ack")
                {
                    return (int)transcript.ExitStatus;
                }
            }
            else if (step.RawHex is not null)
            {
                Discard(step.RawHex.Length / 2);
                raw = true;
            }
            else if (!step.AfterTerminal && !raw)
            {
                ReadFrame();
            }
        }

        return (int)transcript.ExitStatus;
    }

    /// <summary>
    /// Waits without reading or writing. A minute bounds it, so a stand-in
    /// left behind by a crashed test run still ends on its own.
    /// </summary>
    private static int Block()
    {
        Thread.Sleep(TimeSpan.FromMinutes(1));
        return 0;
    }

    private static int Fault(Kit kit, string mode)
    {
        Transcript pure = kit.Transcripts[0];
        JsonObject hello = pure.CoreOutput[0];
        JsonObject outcome = pure.CoreOutput[1];
        if (mode == "stderr")
        {
            byte[] noise = new byte[2 * 1024 * 1024];
            Array.Fill(noise, (byte)'x');
            Err.Write(noise);
            Err.Flush();
        }

        if (mode == "unknown-field")
        {
            hello = hello.With("extra", Json.Of(1));
        }

        Send(hello);
        if (mode == "oversized-early")
        {
            // Without waiting for host_select, send a frame larger than the
            // selected max_frame_bytes but within the hard limit.
            Send(Json.Object(("padding", Json.Of(new string('x', 4096)))));
        }

        if (mode == "no-read")
        {
            return Block();
        }

        int count = 1;
        while (ReadFrame())
        {
            if (count == 2)
            {
                if (mode == "invalid-hash")
                {
                    outcome = WithCallable(outcome, new string('A', 64));
                }

                Send(outcome);
                if (mode == "extra")
                {
                    Send(outcome);
                }

                if (mode == "stall")
                {
                    return Block();
                }

                // Close stdout so the host sees end of stream before the exit.
                CloseStdout();
                if (mode == "slow-exit")
                {
                    Thread.Sleep(1500);
                }

                if (mode == "hang-after-close")
                {
                    return Block();
                }

                return mode == "nonzero" ? 74 : 0;
            }

            count++;
        }

        return 0;
    }

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    /// <summary>
    /// Closes descriptor 1 and every other descriptor on the same pipe: the
    /// .NET runtime keeps a duplicate of stdout, which would hold the pipe
    /// open after descriptor 1 is closed.
    /// </summary>
    private static void CloseStdout()
    {
        Out.Flush();
        if (Directory.Exists("/proc/self/fd"))
        {
            string pipe = new FileInfo("/proc/self/fd/1").LinkTarget ?? string.Empty;
            foreach (string entry in Directory.GetFiles("/proc/self/fd"))
            {
                if (int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out int fd) && fd != 1
                    && new FileInfo(entry).LinkTarget == pipe)
                {
                    Close(fd);
                }
            }
        }

        Out.Dispose();
    }

    private static JsonObject WithCallable(JsonObject outcome, string callable)
    {
        JsonObject evidence = (JsonObject)outcome["evidence"];
        JsonObject core = (JsonObject)evidence["core"];
        JsonObject target = (JsonObject)core["target"];
        return outcome.With("evidence", evidence.With("core", core.With("target", target.With("callable", Json.Of(callable)))));
    }
}

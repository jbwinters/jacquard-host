using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Carrier;

/// <summary>
/// One bounded <c>stdio-u32-json-v0</c> worker lifetime.
/// </summary>
/// <remarks>
/// <para>
/// .NET cannot start a process in its own process group, and its pipe
/// streams have no read or write that a timeout or a cancellation can
/// interrupt (on Unix an anonymous pipe's asynchronous read is a blocking
/// read on a pool thread). So the carrier never calls a read or write that
/// can block. It asks the C library's <c>poll(2)</c> whether a pipe is ready,
/// on the caller's thread, in waits of at most 5 ms, and only then reads the
/// bytes already in the pipe or writes at most <c>PIPE_BUF</c> bytes, which
/// a writable pipe always accepts at once. Every wait, whether for a frame, a
/// write, a handler, or the exit, rechecks the single lifetime deadline, the
/// <see cref="CancellationToken"/>, and disposal. No thread is ever left
/// blocked on a worker pipe.
/// </para>
/// <para>
/// .NET's <see cref="Process"/> does not read redirected output on its own
/// unless asked to (<c>BeginOutputReadLine</c>, which this carrier never
/// calls), so nothing else holds or drains the pipes.
/// </para>
/// <para>
/// At expiry, cancellation, or disposal while the worker runs, the worker's
/// process tree is killed with <see cref="Process.Kill(bool)"/>: .NET stops
/// the worker, lists its children from the process table, kills it, and
/// does the same for each child. A descendant that has already been
/// reparented (because its parent exited or it daemonized) is not found and
/// keeps running.
/// </para>
/// <para>
/// The runtime reaps the worker itself when it exits. After the exit, a
/// descendant that still holds stdout or stderr is detected (the pipe has a
/// writer but the worker has gone): the carrier waits up to one second, then
/// reports a loss. It cannot stop that descendant.
/// </para>
/// <para>A worker is used from one thread at a time; <see cref="Dispose"/> may come from any thread.</para>
/// </remarks>
public sealed class Worker : IDisposable
{
    /// <summary>How long disposal waits for a killed worker to exit, and how long a pipe may stay held after the exit.</summary>
    private const int ExitGraceMs = 1000;

    /// <summary>The longest single wait between checks.</summary>
    private const int PollSliceMs = 5;

    /// <summary>The most stderr one drain reads, so a fast writer cannot hold the caller.</summary>
    private const int StderrSlice = 1 << 20;

    /// <summary>The exit status .NET reports for a process ended by SIGKILL.</summary>
    private const int SigkillExit = 128 + 9;

    private const short Ready = Posix.PollIn | Posix.PollHup | Posix.PollErr | Posix.PollNval;

    private readonly object gate = new();
    private readonly Process? process;
    private readonly StreamWriter? stdinWriter;
    private readonly Stream? stdin;
    private readonly Stream? stdout;
    private readonly Stream? stderr;
    private readonly int stdinFd = -1;
    private readonly int stdoutFd = -1;
    private readonly int stderrFd = -1;
    private readonly long expires;
    private readonly CancellationToken cancel;
    private readonly byte[] chunk = new byte[8192];
    private volatile bool expired;
    private volatile bool disposeRequested;
    private byte[] stderrBytes = Array.Empty<byte>();
    private int stderrLength;
    private int stderrLimit = Limits.Hard.MaxStderrBytes;
    private bool stderrEnded;
    private CarrierException? fault;
    private bool inputClosed;
    private bool killed;
    private bool exitRecorded;
    private bool disposed;

    private Worker(long expires, string? spawnError, CancellationToken cancel)
    {
        this.expires = expires;
        this.cancel = cancel;
        SpawnError = spawnError;
    }

    private Worker(Process process, long expires, CancellationToken cancel)
        : this(expires, null, cancel)
    {
        this.process = process;
        stdinWriter = process.StandardInput;
        stdin = stdinWriter.BaseStream;
        stdout = process.StandardOutput.BaseStream;
        stderr = process.StandardError.BaseStream;
        stdinFd = Descriptor(stdin);
        stdoutFd = Descriptor(stdout);
        stderrFd = Descriptor(stderr);
    }

    /// <summary>
    /// The longest lifetime: a longer <see cref="WorkerOptions.Deadline"/>,
    /// up to <see cref="TimeSpan.MaxValue"/>, is clamped to it (about 73 years).
    /// </summary>
    public static TimeSpan MaxDeadline { get; } = TimeSpan.FromDays(73 * 365);

    /// <summary>The worker lifetime when <see cref="WorkerOptions.Deadline"/> is null.</summary>
    public static TimeSpan DefaultDeadline { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Test seam: the lifetime clock, in <see cref="Stopwatch"/> timestamp
    /// ticks, so a test can move a long lifetime to its deadline without
    /// waiting. It sets the deadline and decides expiry; the one-second exit
    /// graces keep the real clock. Process-global; null outside tests.
    /// </summary>
    internal static volatile Func<long>? ClockHook;

    /// <summary>The lifetime clock: <see cref="Stopwatch.GetTimestamp"/> outside tests.</summary>
    private static long Now() => ClockHook is { } clock ? clock() : Stopwatch.GetTimestamp();

    /// <summary>The spawn error, when the worker never started.</summary>
    public string? SpawnError { get; }

    /// <summary>The exit status once the worker has exited, unless the host's SIGKILL ended it.</summary>
    public int? ExitStatus { get; private set; }

    /// <summary>
    /// <c>"SIGKILL"</c> when the host killed the worker and .NET reported exit
    /// status 137 (128 + 9); otherwise null. .NET reports any other signal as
    /// exit status 128 + its number.
    /// </summary>
    public string? Signal { get; private set; }

    /// <summary>Whether the worker's lifetime ended by expiry, cancellation, or disposal.</summary>
    public bool IsExpired => expired;

    /// <summary>
    /// Whether the call is cancelled, the worker expired or is being disposed,
    /// or its lifetime passed. Safe from any thread, and exact: it does not
    /// wait for the caller's next poll to notice the deadline.
    /// </summary>
    public bool Stopping => expired || disposeRequested || cancel.IsCancellationRequested || Now() >= expires;

    /// <summary>The retained stderr prefix.</summary>
    public byte[] Stderr
    {
        get
        {
            lock (gate)
            {
                return stderrBytes.AsSpan(0, stderrLength).ToArray();
            }
        }
    }

    /// <summary>
    /// Spawns <c>COMMAND host worker --store STORE</c>. A spawn failure is
    /// reported by the first operation.
    /// </summary>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static Worker Start(string store, WorkerOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        TimeSpan deadline = options.Deadline ?? DefaultDeadline;

        long expires = Expires(Now(), deadline);
        if (!Posix.Supported)
        {
            return Failed(expires, "unsupported platform", "the .NET host runs workers on Linux and macOS only", cancellationToken);
        }

        if (Stopped(expires, cancellationToken))
        {
            return StoppedBeforeSpawn(expires, cancellationToken);
        }

        IReadOnlyList<string> command = options.ResolvedCommand();
        var info = new ProcessStartInfo(command[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        for (int i = 1; i < command.Count; i++)
        {
            info.ArgumentList.Add(command[i]);
        }

        foreach (string argument in new[] { "host", "worker", "--store", store })
        {
            info.ArgumentList.Add(argument);
        }

        if (options.Environment is { } environment)
        {
            info.Environment.Clear();
            foreach (KeyValuePair<string, string> variable in environment)
            {
                info.Environment[variable.Key] = variable.Value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return Failed(
                expires,
                error.Message,
                "could not start " + command[0] + " (" + error.Message + "); check the configured Core installation",
                cancellationToken);
        }

        try
        {
            return new Worker(process, expires, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            // The streams are not the pipes this carrier polls.
            KillQuietly(process);
            process.Dispose();
            return Failed(expires, error.Message, "could not poll the worker's pipes (" + error.Message + ")", cancellationToken);
        }
    }

    /// <summary>
    /// The timestamp <paramref name="deadline"/> after <paramref name="now"/>.
    /// Any positive <see cref="TimeSpan"/> is accepted; the lifetime is
    /// clamped to <see cref="MaxDeadline"/>, so the sum cannot overflow.
    /// </summary>
    internal static long Expires(long now, TimeSpan deadline)
    {
        double ticks = Math.Min(deadline.TotalSeconds, MaxDeadline.TotalSeconds) * Stopwatch.Frequency;
        return ticks >= long.MaxValue - now ? long.MaxValue : now + (long)ticks;
    }

    private static int Descriptor(Stream stream) =>
        stream is PipeStream pipe
            ? (int)pipe.SafePipeHandle.DangerousGetHandle()
            : throw new NotSupportedException("worker pipes are " + stream.GetType().Name + ", not pipe streams");

    private static bool Stopped(long expires, CancellationToken cancel) => cancel.IsCancellationRequested || Now() >= expires;

    private static Worker Failed(long expires, string spawnError, string message, CancellationToken cancel) =>
        new(expires, spawnError, cancel) { fault = CarrierException.Loss(message), exitRecorded = true };

    /// <summary>
    /// A worker that was never spawned because the call was cancelled, or its
    /// lifetime ended, first. Its first operation reports the abort or the
    /// deadline, as for a running worker.
    /// </summary>
    private static Worker StoppedBeforeSpawn(long expires, CancellationToken cancel)
    {
        var stopped = new Worker(expires, null, cancel) { exitRecorded = true };
        stopped.Fault();
        return stopped;
    }

    /// <summary>The carrier failure that ended the worker, if any; null otherwise.</summary>
    public CarrierException? Fault()
    {
        lock (gate)
        {
            return FaultCore();
        }
    }

    private CarrierException? FaultCore()
    {
        if (fault is null)
        {
            if (cancel.IsCancellationRequested || disposeRequested)
            {
                Expire();
                fault = CarrierException.Aborted("invocation aborted; outside completion is unknown");
            }
            else if (Now() >= expires)
            {
                Expire();
                fault = CarrierException.Timeout("worker lifetime deadline exceeded");
            }
        }

        return fault;
    }

    private void ThrowIfFault()
    {
        CarrierException? current = FaultCore();
        if (current is not null)
        {
            throw current;
        }
    }

    private CarrierException FaultOr(CarrierException fallback) => FaultCore() ?? fallback;

    /// <summary>Kills the worker's process tree and closes its input.</summary>
    private void Expire()
    {
        expired = true;
        KillTree();
        CloseInputCore();
    }

    private bool HasExited()
    {
        try
        {
            return process is null || process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Kills the worker and the descendants .NET finds, while the worker is alive.</summary>
    private void KillTree()
    {
        if (process is null || HasExited())
        {
            return;
        }

        killed = true;
        KillQuietly(process);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            // Already gone, or a descendant could not be signalled.
        }
    }

    /// <summary>
    /// The poll timeout: whole milliseconds left before the deadline, rounded
    /// down so a poll never ends after it, and at most one slice. It is
    /// capped in timestamp ticks before any multiplication, so a long
    /// deadline cannot overflow into a negative (infinite) poll timeout.
    /// </summary>
    internal static int RemainingMs(long expires, long now)
    {
        long left = expires - now;
        if (left <= 0)
        {
            return 0;
        }

        if (left >= PollSliceMs * Stopwatch.Frequency / 1000)
        {
            return PollSliceMs;
        }

        return (int)(left * 1000 / Stopwatch.Frequency);
    }

    /// <summary>
    /// Waits up to one slice, never past the deadline, for <paramref name="fd"/>
    /// to report <paramref name="events"/> (or for nothing when it is -1),
    /// reading whatever stderr arrives meanwhile.
    /// </summary>
    private void Pause(int fd, short events)
    {
        Span<Posix.PollFd> fds = stackalloc Posix.PollFd[2];
        int count = 0;
        if (fd >= 0)
        {
            fds[count++] = new Posix.PollFd { Fd = fd, Events = events };
        }

        bool watchStderr = stderr is not null && !stderrEnded;
        if (watchStderr)
        {
            fds[count++] = new Posix.PollFd { Fd = stderrFd, Events = Posix.PollIn };
        }

        int timeout = RemainingMs(expires, Now());
        if (count == 0)
        {
            Thread.Sleep(timeout);
            return;
        }

        Posix.Poll(fds[..count], timeout);
        if (watchStderr && (fds[count - 1].Revents & Ready) != 0)
        {
            DrainStderr();
        }
    }

    private static short Events(int fd, short events)
    {
        Span<Posix.PollFd> fds = stackalloc Posix.PollFd[1];
        fds[0] = new Posix.PollFd { Fd = fd, Events = events };
        Posix.Poll(fds, 0);
        return fds[0].Revents;
    }

    /// <summary>Selects how much stderr is retained.</summary>
    public void SetStderrLimit(int limit)
    {
        lock (gate)
        {
            stderrLimit = Math.Max(0, limit);
            stderrLength = Math.Min(stderrLength, stderrLimit);
        }
    }

    /// <summary>Reads whatever stderr is in the pipe, keeping a bounded prefix. Never blocks.</summary>
    private void DrainStderr()
    {
        if (stderr is null || stderrEnded)
        {
            return;
        }

        try
        {
            for (int budget = StderrSlice; budget > 0;)
            {
                if ((Events(stderrFd, Posix.PollIn) & Ready) == 0)
                {
                    return;
                }

                int n = stderr.Read(chunk, 0, chunk.Length);
                if (n <= 0)
                {
                    stderrEnded = true;
                    return;
                }

                int keep = Math.Min(n, stderrLimit - stderrLength);
                if (keep > 0)
                {
                    if (stderrLength + keep > stderrBytes.Length)
                    {
                        Array.Resize(ref stderrBytes, Math.Min(stderrLimit, Math.Max(stderrLength + keep, stderrBytes.Length * 2)));
                    }

                    Array.Copy(chunk, 0, stderrBytes, stderrLength, keep);
                    stderrLength += keep;
                }

                budget -= n;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or CarrierException)
        {
            stderrEnded = true;
        }
    }

    /// <summary>
    /// One non-blocking step on stdout: bytes read, 0 when none are ready yet,
    /// -1 at end of stream (every writer closed it), or -2 when the worker has
    /// exited but another process still holds stdout. The exit is observed
    /// before the pipe is checked, so all of the worker's own output has been
    /// read when -2 is returned.
    /// </summary>
    private int ReadStep(byte[] buffer, int offset, int length)
    {
        bool exited = HasExited();
        try
        {
            if ((Events(stdoutFd, Posix.PollIn) & Ready) != 0)
            {
                int n = stdout!.Read(buffer, offset, length);
                return n <= 0 ? -1 : n;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return -1;
        }

        return exited ? -2 : 0;
    }

    /// <summary>Fills <paramref name="buffer"/>; returns its length, or <c>~filled</c> at end of stream.</summary>
    private int Fill(byte[] buffer)
    {
        int filled = 0;
        long heldSince = 0;
        while (filled < buffer.Length)
        {
            ThrowIfFault();
            int n = ReadStep(buffer, filled, buffer.Length - filled);
            if (n > 0)
            {
                filled += n;
                continue;
            }

            if (n == -1)
            {
                return ~filled;
            }

            if (n == -2)
            {
                long now = Stopwatch.GetTimestamp();
                if (heldSince == 0)
                {
                    heldSince = now;
                }
                else if (now - heldSince >= Stopwatch.Frequency * ExitGraceMs / 1000)
                {
                    fault ??= CarrierException.Loss("worker descendants kept its pipes open after exit");
                    CloseInputCore();
                    throw fault;
                }
            }

            Pause(stdoutFd, Posix.PollIn);
        }

        return filled;
    }

    /// <summary>Reads one frame under <paramref name="limits"/>. Returns null at a clean end of stream.</summary>
    /// <exception cref="CarrierException">A loss, or a frame that violates the protocol.</exception>
    public JsonObject? ReadFrame(Limits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        lock (gate)
        {
            ThrowIfFault();
            if (stdout is null)
            {
                return null;
            }

            byte[] prefix = new byte[4];
            int got = Fill(prefix);
            if (got < 0)
            {
                if (~got == 0)
                {
                    return null;
                }

                throw CarrierException.Loss("worker closed stdout during a frame");
            }

            uint length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
            if (length == 0 || length > limits.MaxFrameBytes)
            {
                throw CarrierException.FromProtocol(ProtocolException.Exceeded("frame length exceeds max_frame_bytes"));
            }

            byte[] payload = new byte[length];
            if (Fill(payload) < 0)
            {
                throw CarrierException.Loss("worker closed stdout during a frame");
            }

            // A frame that arrives in the final poll slice is not acted on.
            ThrowIfFault();
            try
            {
                return JsonCodec.DecodeObject(payload, limits.MaxJsonDepth);
            }
            catch (ProtocolException error)
            {
                throw CarrierException.FromProtocol(error);
            }
        }
    }

    /// <summary>
    /// Writes raw carrier bytes. Each write carries at most <c>PIPE_BUF</c>
    /// bytes and starts only once poll reports the pipe writable, so it never
    /// blocks; between writes the caller waits under the lifetime.
    /// </summary>
    /// <exception cref="CarrierException">A loss: the input is closed, the write failed, or the lifetime ended.</exception>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            ThrowIfFault();
            if (stdin is null || inputClosed)
            {
                throw CarrierException.Loss("worker write failed (worker input is closed)");
            }

            int chunkSize = Posix.WriteChunk;
            int offset = 0;
            while (offset < bytes.Length)
            {
                ThrowIfFault();
                if ((Events(stdinFd, Posix.PollOut) & (Posix.PollOut | Posix.PollErr | Posix.PollHup | Posix.PollNval)) == 0)
                {
                    Pause(stdinFd, Posix.PollOut);
                    continue;
                }

                int n = Math.Min(chunkSize, bytes.Length - offset);
                try
                {
                    stdin.Write(bytes.Slice(offset, n));
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    throw FaultOr(CarrierException.Loss("worker write failed (" + error.Message + ")"));
                }

                offset += n;
            }

            ThrowIfFault();
        }
    }

    /// <summary>Encodes and writes one frame under <paramref name="limits"/>.</summary>
    /// <exception cref="CarrierException">A frame that cannot be encoded within the limits, or a write failure.</exception>
    public void WriteFrame(IReadOnlyDictionary<string, Json> frame, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(limits);
        lock (gate)
        {
            ThrowIfFault();
            byte[] bytes;
            try
            {
                bytes = JsonCodec.FrameBytes(frame, limits.MaxFrameBytes, limits.MaxJsonDepth);
            }
            catch (ProtocolException error)
            {
                throw CarrierException.FromProtocol(error);
            }

            WriteBytes(bytes);
        }
    }

    /// <summary>
    /// Waits until <paramref name="isDone"/> returns true, the deadline
    /// passes, the call is cancelled, or the worker is disposed, reading
    /// stderr meanwhile.
    /// </summary>
    /// <exception cref="CarrierException">The lifetime ended first.</exception>
    public void Await(Func<bool> isDone)
    {
        ArgumentNullException.ThrowIfNull(isDone);
        lock (gate)
        {
            while (true)
            {
                ThrowIfFault();
                if (isDone())
                {
                    return;
                }

                Pause(-1, 0);
            }
        }
    }

    /// <summary>Closes the worker's stdin. Nothing is writing, so this never blocks.</summary>
    public void CloseInput()
    {
        lock (gate)
        {
            CloseInputCore();
        }
    }

    private void CloseInputCore()
    {
        if (inputClosed)
        {
            return;
        }

        inputClosed = true;
        try
        {
            stdinWriter?.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The worker may already have closed its end.
        }
    }

    /// <summary>
    /// Closes stdin and waits for the worker to exit, bounded by the deadline.
    /// Call only after stdout reached end of stream. After the exit, stderr
    /// must close within one second: a descendant that keeps holding it is
    /// reported as a loss.
    /// </summary>
    /// <exception cref="CarrierException">The lifetime ended first, or a descendant kept stderr open.</exception>
    public void AwaitExit()
    {
        lock (gate)
        {
            CloseInputCore();
            if (process is not null)
            {
                while (!HasExited() && FaultCore() is null)
                {
                    Pause(-1, 0);
                }

                if (fault is not null)
                {
                    // FaultCore killed the worker; wait briefly so the exit is recorded.
                    WaitForExit(ExitGraceMs);
                }

                RecordExit();
                DrainStderr();
                AwaitStderrClose();
            }

            ThrowIfFault();
        }
    }

    /// <summary>Waits up to one second, within the lifetime, for every writer to close stderr.</summary>
    private void AwaitStderrClose()
    {
        long until = Stopwatch.GetTimestamp() + (Stopwatch.Frequency * ExitGraceMs / 1000);
        while (fault is null && stderr is not null && !stderrEnded)
        {
            if (FaultCore() is not null)
            {
                return;
            }

            if (Stopwatch.GetTimestamp() >= until)
            {
                fault = CarrierException.Loss("worker descendants kept its pipes open after exit");
                return;
            }

            Pause(-1, 0);
            DrainStderr();
        }
    }

    /// <summary>Waits up to <paramref name="milliseconds"/> for the worker to exit, regardless of the deadline.</summary>
    private void WaitForExit(int milliseconds)
    {
        try
        {
            process?.WaitForExit(milliseconds);
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            // No process to wait for.
        }
    }

    private void RecordExit()
    {
        if (exitRecorded || process is null || !HasExited())
        {
            return;
        }

        exitRecorded = true;
        int value = process.ExitCode;
        if (killed && value == SigkillExit)
        {
            Signal = "SIGKILL";
        }
        else
        {
            ExitStatus = value;
        }
    }

    /// <summary>
    /// Kills a live worker's process tree, waits up to one second for its
    /// exit, reads the stderr left in the pipe, and closes the pipes. Safe
    /// from any thread: an operation in progress on another thread sees the
    /// disposal within one poll slice and ends first.
    /// </summary>
    public void Dispose()
    {
        disposeRequested = true;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CloseInputCore();
            if (process is null)
            {
                return;
            }

            if (!HasExited())
            {
                KillTree();
                WaitForExit(ExitGraceMs);
            }

            RecordExit();
            DrainStderr();

            // Closing the host's ends leaves a descendant still writing with
            // no reader, so its next write fails.
            stdout?.Dispose();
            stderr?.Dispose();
            stderrEnded = true;
            process.Dispose();
        }
    }
}

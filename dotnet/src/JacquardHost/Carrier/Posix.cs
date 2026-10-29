using System;
using System.Runtime.InteropServices;

namespace JacquardLang.Host.Carrier;

/// <summary>
/// The C library's <c>poll(2)</c>, the one native call the carrier makes. It
/// lets the caller's thread wait for a pipe to become readable or writable
/// with a timeout, so no read or write on a worker pipe ever blocks. The
/// <c>pollfd</c> layout and the event bits used here are the same on Linux
/// and macOS.
/// </summary>
internal static partial class Posix
{
    internal const short PollIn = 0x0001;
    internal const short PollOut = 0x0004;
    internal const short PollErr = 0x0008;
    internal const short PollHup = 0x0010;
    internal const short PollNval = 0x0020;

    private const int Eintr = 4;

    /// <summary>Whether the carrier can run here: Linux or macOS.</summary>
    internal static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>
    /// The most bytes one write may carry so that it cannot block once poll
    /// reports the pipe writable: <c>PIPE_BUF</c>, the space POLLOUT
    /// guarantees (one page on Linux, 512 bytes on macOS).
    /// </summary>
    internal static int WriteChunk => OperatingSystem.IsMacOS() ? 512 : 4096;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int NativePoll(PollFd* fds, nuint count, int timeout);

    /// <summary>
    /// Polls <paramref name="fds"/> for up to <paramref name="timeoutMs"/>
    /// milliseconds (0 returns at once). A signal interrupting the wait counts
    /// as a timeout; the caller rechecks its state and polls again.
    /// </summary>
    /// <returns>The number of descriptors with events.</returns>
    /// <exception cref="CarrierException">poll failed for another reason; the worker is lost.</exception>
    internal static unsafe int Poll(Span<PollFd> fds, int timeoutMs)
    {
        for (int i = 0; i < fds.Length; i++)
        {
            fds[i].Revents = 0;
        }

        int result;
        fixed (PollFd* pointer = fds)
        {
            result = NativePoll(pointer, (nuint)fds.Length, timeoutMs);
        }

        if (result >= 0)
        {
            return result;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == Eintr)
        {
            return 0;
        }

        throw CarrierException.Loss("could not wait on the worker's pipes (poll failed with errno " + error.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }
}

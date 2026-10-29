using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// The C library calls the SDK makes beyond the carrier's <c>poll(2)</c>:
/// <c>kill(2)</c> to stop a Core command's process group (or freeze and
/// kill the command where there is none), <c>geteuid(2)</c>
/// and a status call (<c>statx(2)</c> on Linux, <c>stat(2)</c> and
/// <c>lstat(2)</c> on macOS) for the cache's owner check, for file kinds and
/// sizes, and for the identities that detect a replaced file,
/// <c>access(2)</c> for the execute permission, and <c>mkdir(2)</c> to
/// create a staging directory exclusively.
/// </summary>
/// <remarks>
/// The macOS calls use the 64-bit-inode <c>struct stat</c> layout; they are
/// compiled and selected at run time but, like the rest of the .NET host on
/// macOS, not exercised by the tests or CI.
/// </remarks>
internal static partial class Unix
{
    internal const int Sigkill = 9;

    /// <summary>SIGSTOP on Linux (macOS numbers it 17; the SDK sends it only on Linux).</summary>
    internal const int Sigstop = 19;

    private const int Enoent = 2;
    private const int Enotdir = 20;
    private const int Eexist = 17;
    private const int AtFdcwd = -100;
    private const int AtSymlinkNofollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int FileTypeMask = 0xF000;
    private const int RegularType = 0x8000;
    private const int DirectoryType = 0x4000;
    private const int LinkType = 0xA000;

    /// <summary>A file's kind.</summary>
    internal enum Kind
    {
        Missing,
        Regular,
        Directory,
        Link,
        Other,
    }

    /// <summary>
    /// What the status call reports: the kind, size, owner, and permission
    /// bits, and the file's identity: its device and inode, and its
    /// modification and status-change times in nanoseconds.
    /// </summary>
    internal readonly record struct Status(Kind Kind, long Size, uint Owner, int Mode, ulong Device = 0, ulong Inode = 0, long Modified = 0, long Changed = 0)
    {
        /// <summary>
        /// Whether <paramref name="other"/> describes the same file in the
        /// same state: kind, device, inode, size, mode, owner, and both times.
        /// Replacing the file changes its inode; writing it, renaming it, or
        /// changing its mode or owner changes its status-change time.
        /// </summary>
        internal bool SameFile(Status other) =>
            Kind == other.Kind && Device == other.Device && Inode == other.Inode && Size == other.Size
            && Mode == other.Mode && Owner == other.Owner && Modified == other.Modified && Changed == other.Changed;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int NativeKill(int pid, int signal);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint NativeGeteuid();

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeAccess(string path, int mode);

    [LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int NativeMkdir(string path, uint mode);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int NativeStatx(int dirfd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "stat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacStatArm(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacLstatArm(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "stat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacStatIntel(string path, byte* buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int MacLstatIntel(string path, byte* buffer);

    /// <summary>Whether the SDK's file and process checks can run here: Linux or macOS.</summary>
    internal static bool Supported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>This process's effective user id.</summary>
    internal static uint EffectiveUser() => NativeGeteuid();

    /// <summary>
    /// Sends SIGKILL to every process in group <paramref name="group"/>.
    /// A group id of one or less is never signalled (<c>kill(-1)</c> and
    /// <c>kill(0)</c> mean every process and the caller's own group).
    /// </summary>
    internal static void KillGroup(int group)
    {
        if (group > 1)
        {
            _ = NativeKill(-group, Sigkill);
        }
    }

    /// <summary>Sends SIGKILL to <paramref name="pid"/>, never to a pid of one or less.</summary>
    internal static void Kill(int pid)
    {
        if (pid > 1)
        {
            _ = NativeKill(pid, Sigkill);
        }
    }

    /// <summary>Sends <paramref name="signal"/> to <paramref name="pid"/>, never to a pid of one or less.</summary>
    internal static void Signal(int pid, int signal)
    {
        if (pid > 1)
        {
            _ = NativeKill(pid, signal);
        }
    }

    /// <summary>Whether this process may execute <paramref name="path"/>, as <c>access(path, X_OK)</c> reports.</summary>
    internal static bool MayExecute(string path) => NativeAccess(path, 1) == 0;

    /// <summary>Creates <paramref name="path"/> with <paramref name="mode"/>; false when it already exists.</summary>
    /// <exception cref="IOException">The directory cannot be created for another reason.</exception>
    internal static bool MakeDirectory(string path, int mode)
    {
        if (NativeMkdir(path, (uint)mode) == 0)
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == Eexist)
        {
            return false;
        }

        throw new IOException("mkdir failed with errno " + error.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The status of <paramref name="path"/>, following a final symbolic link
    /// when <paramref name="follow"/> is set; <see cref="Kind.Missing"/> when
    /// it does not exist.
    /// </summary>
    /// <exception cref="IOException">The status cannot be read.</exception>
    internal static unsafe Status Stat(string path, bool follow)
    {
        byte* buffer = stackalloc byte[256];
        new Span<byte>(buffer, 256).Clear();
        int result;
        int modeOffset;
        int ownerOffset;
        int sizeOffset;
        ulong device;
        ulong inode;
        long modified;
        long changed;
        if (OperatingSystem.IsMacOS())
        {
            bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            result = (follow, arm) switch
            {
                (true, true) => MacStatArm(path, buffer),
                (false, true) => MacLstatArm(path, buffer),
                (true, false) => MacStatIntel(path, buffer),
                _ => MacLstatIntel(path, buffer),
            };
            modeOffset = 4;
            ownerOffset = 16;
            sizeOffset = 96;

            // st_dev (int32) at 0, st_ino at 8, st_mtimespec at 48, st_ctimespec at 64.
            device = *(uint*)buffer;
            inode = *(ulong*)(buffer + 8);
            modified = Nanoseconds(*(long*)(buffer + 48), *(long*)(buffer + 56));
            changed = Nanoseconds(*(long*)(buffer + 64), *(long*)(buffer + 72));
        }
        else
        {
            result = NativeStatx(AtFdcwd, path, follow ? 0 : AtSymlinkNofollow, StatxBasicStats, buffer);
            modeOffset = 28;
            ownerOffset = 20;
            sizeOffset = 40;

            // stx_ino at 32, stx_ctime at 96, stx_mtime at 112 (each a
            // 64-bit second and a 32-bit nanosecond), stx_dev_major and
            // stx_dev_minor at 136 and 140.
            inode = *(ulong*)(buffer + 32);
            changed = Nanoseconds(*(long*)(buffer + 96), *(uint*)(buffer + 104));
            modified = Nanoseconds(*(long*)(buffer + 112), *(uint*)(buffer + 120));
            device = ((ulong)*(uint*)(buffer + 136) << 32) | *(uint*)(buffer + 140);
        }

        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == Enoent || error == Enotdir)
            {
                return new Status(Kind.Missing, 0, 0, 0);
            }

            throw new IOException("cannot read its status (errno " + error.ToString(CultureInfo.InvariantCulture) + ")");
        }

        int mode = *(ushort*)(buffer + modeOffset);
        uint owner = *(uint*)(buffer + ownerOffset);
        long size = *(long*)(buffer + sizeOffset);
        Kind kind = (mode & FileTypeMask) switch
        {
            RegularType => Kind.Regular,
            DirectoryType => Kind.Directory,
            LinkType => Kind.Link,
            _ => Kind.Other,
        };
        return new Status(kind, size, owner, mode & 0xFFF, device, inode, modified, changed);
    }

    private static long Nanoseconds(long seconds, long nanoseconds) => unchecked((seconds * 1_000_000_000L) + nanoseconds);
}

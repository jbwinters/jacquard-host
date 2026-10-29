using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>Helpers for the SDK tests.</summary>
internal static class SdkSupport
{
    /// <summary>Waits up to a minute for <paramref name="path"/> to hold a complete pid line and returns it.</summary>
    internal static int PidFile(string path)
    {
        DateTime deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            string text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            if (text.EndsWith('\n') && text.Trim().Length > 0)
            {
                return int.Parse(text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0], CultureInfo.InvariantCulture);
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(path + " was never written");
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Waits up to ten seconds for <paramref name="pid"/> to exit, killing it and failing otherwise; the tests' children live for five minutes.</summary>
    internal static void Dies(string what, int pid)
    {
        if (!Support.Eventually(() => !Support.Alive(pid), TimeSpan.FromSeconds(10)))
        {
            Support.Kill(pid);
            Assert.Fail(what + " is still running");
        }
    }

    /// <summary>A shell command line.</summary>
    internal static string[] Sh(string script) => new[] { "/bin/sh", "-c", script };

    /// <summary>
    /// A fresh owner-only scratch directory, under the temporary directory
    /// when no other user may write it or a directory above it, and
    /// otherwise under <c>/tmp</c> when that is so, so the SDK's directory
    /// rule finds nothing unsafe above the scratch directory and the tests
    /// that expect its exact refusals see only the levels they make. Fails
    /// naming the requirement when neither is.
    /// </summary>
    internal static string Scratch(string prefix)
    {
        string name = "jacquard-dotnet-sdk-" + prefix + "-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        if (!Unix.Supported)
        {
            return Directory.CreateTempSubdirectory("jacquard-dotnet-sdk-" + prefix + "-").FullName;
        }

        foreach (string candidate in new[] { Path.GetTempPath(), "/tmp" })
        {
            if (Directory.Exists(candidate) && SafeAncestry(Unix.RealPath(candidate)))
            {
                return Directory.CreateDirectory(Path.Combine(Unix.RealPath(candidate), name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
            }
        }

        throw new AssertionException("the SDK tests need a temporary directory that no other user, nor any group, may write, nor any directory above it (unless sticky); set TMPDIR to one");
    }

    /// <summary>
    /// Whether <paramref name="directory"/>, a real path, and every directory
    /// above it is owned by this user or root and neither its group nor
    /// others may write it unless it is sticky (without the private-group
    /// exception, which tests answer through seams).
    /// </summary>
    internal static bool SafeAncestry(string directory)
    {
        for (string? level = directory; level is not null; level = Path.GetDirectoryName(level))
        {
            Unix.Status status = Unix.Stat(level, follow: false);
            if (status.Kind != Unix.Kind.Directory || (status.Owner != Unix.EffectiveUser() && status.Owner != 0) || ((status.Mode & 0x12) != 0 && (status.Mode & 0x200) == 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Answers the private-group lookups as a user "me" whose primary group
    /// is <paramref name="directory"/>'s group, named
    /// <paramref name="name"/> with <paramref name="members"/>, and a
    /// password database listing "me" with that primary group followed by
    /// <paramref name="others"/>.
    /// </summary>
    internal static void Accounts(string directory, string name, string[] members, params Unix.PasswdEntry?[] others)
    {
        uint group = Unix.Stat(directory, follow: false).Group;
        uint me = Unix.EffectiveUser();
        Core.UserHook = uid => uid == me ? new Unix.Account("me", group) : null;
        Core.GroupHook = gid => gid == group ? new Unix.GroupEntry(name, members) : null;
        Core.PasswdHook = () => new Unix.PasswdEntry?[] { new Unix.PasswdEntry("me", me, group) }.Concat(others);
    }

    /// <summary>Clears the private-group seams.</summary>
    internal static void NoAccounts()
    {
        Core.UserHook = null;
        Core.GroupHook = null;
        Core.PasswdHook = null;
    }

    /// <summary>
    /// Removes a scratch directory, first restoring owner permissions on
    /// everything in it (tests make some files unreadable).
    /// </summary>
    internal static void RemoveScratch(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (new FileInfo(entry).LinkTarget is null)
                {
                    File.SetUnixFileMode(entry, File.GetUnixFileMode(entry) | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            catch (IOException)
            {
                // Gone already.
            }
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>A file of <paramref name="length"/> bytes that takes no space (sparse), with <paramref name="mode"/>.</summary>
    internal static string Sparse(string path, long length, UnixFileMode mode = UnixFileMode.None)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew))
        {
            stream.SetLength(length);
        }

        File.SetUnixFileMode(path, mode);
        return path;
    }

    internal static void MakeFifo(string path)
    {
        using var mkfifo = System.Diagnostics.Process.Start("mkfifo", path) ?? throw new InvalidOperationException("mkfifo");
        Assert.That(mkfifo.WaitForExit(30_000) && mkfifo.ExitCode == 0, "mkfifo failed");
    }

    /// <summary>The text of <paramref name="bytes"/>.</summary>
    internal static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}

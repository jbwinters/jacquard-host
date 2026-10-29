using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
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

    /// <summary>A fresh scratch directory.</summary>
    internal static string Scratch(string prefix) => Directory.CreateTempSubdirectory("jacquard-dotnet-sdk-" + prefix + "-").FullName;

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

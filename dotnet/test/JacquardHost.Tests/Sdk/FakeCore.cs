using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>
/// A fake Core: a shell script that logs its first argument, answers
/// <c>--version</c>, and runs a case for each other command. Every process a
/// case leaves behind records its pid in a <c>*.pid</c> or <c>pids</c> file;
/// <see cref="Dispose"/> kills them.
/// </summary>
internal sealed class FakeCore : IDisposable
{
    internal static readonly string First = new('a', 64);
    internal static readonly string Second = new('b', 64);

    private FakeCore(string root, Runtime runtime)
    {
        Root = root;
        Runtime = runtime;
    }

    internal string Root { get; }

    internal Runtime Runtime { get; }

    internal string Binary => Path.Combine(Root, "jacquard");

    internal PrepareOptions Options => new() { CacheDir = Path.Combine(Root, "cache") };

    /// <summary>
    /// A fake whose <c>--version</c> prints <paramref name="version"/> (unless
    /// <paramref name="versionCase"/> replaces it), and whose other commands
    /// run <paramref name="cases"/> (shell <c>case</c> arms). <c>$ROOT</c>
    /// names the fake's directory.
    /// </summary>
    internal static FakeCore Create(string root, string version, string? versionCase, string cases)
    {
        // Owner-only whatever the umask: the cache under it must have no parent others may write.
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(root, "prelude"));
        File.WriteAllText(Path.Combine(root, "prelude", "01-prim.jqd"), "prelude\n");
        File.WriteAllText(Path.Combine(root, "rules.jac"), "rules\n");
        string script = "#!/bin/sh\nROOT='" + root + "'\necho \"$1\" >> \"$ROOT/log\"\ncase \"$1\" in\n--version) "
            + (versionCase ?? "echo 'jacquard " + version + "'") + " ;;\n" + cases + "\nesac\n";
        string binary = Path.Combine(root, "jacquard");
        File.WriteAllText(binary, script);
        File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var runtime = new Runtime(new RuntimeOptions { Binary = binary, Prelude = Path.Combine(root, "prelude") });
        return new FakeCore(root, runtime);
    }

    /// <summary>A fake whose <c>check</c> fails with <c>boom</c>.</summary>
    internal static FakeCore Failing(string root, string version) => Create(root, version, null, "check) echo 'boom' >&2; exit 3 ;;");

    /// <summary>A fake whose <c>check</c> and <c>hash</c> succeed and whose <c>run</c> fails.</summary>
    internal static FakeCore Hashing(string root) =>
        Create(root, Compat.CoreVersion, null, "check) ;;\nhash) echo '0:x " + new string('0', 64) + "' ;;\nrun) exit 4 ;;");

    /// <summary>
    /// A fake that prepares every source as two functions, <c>first</c> and
    /// <c>second</c>, with identities <see cref="First"/> and
    /// <see cref="Second"/>, over a one-type prelude.
    /// </summary>
    internal static FakeCore Programs(string root) => Create(root, Compat.CoreVersion, null,
        "check) echo 'first : () ->{} Int'; echo 'second : () ->{} Int' ;;\n"
        + "hash) case \"$2\" in */prelude/*) echo '0:int " + new string('1', 64) + "' ;; *) echo '0:first " + First + "'; echo '1:second "
        + Second + "' ;; esac ;;\n"
        + "run) mkdir -p \"$4\" && echo objects > \"$4/objects\" ;;");

    /// <summary>
    /// A fake whose one function, <c>first</c>, uses the effect
    /// <c>Inventory</c>, whose one operation is <c>lookup</c>.
    /// </summary>
    internal static FakeCore Effects(string root) => Create(root, Compat.CoreVersion, null,
        "check) echo 'first : () ->{Inventory} Int' ;;\n"
        + "hash) case \"$2\" in */prelude/*) echo '0:int " + new string('1', 64) + "' ;; *) echo '0:first " + First + "'; echo '1:inventory "
        + new string('c', 64) + "'; echo '1:lookup " + new string('d', 64) + "' ;; esac ;;\n"
        + "run) mkdir -p \"$4\" && echo objects > \"$4/objects\" ;;");

    internal string Source(string name)
    {
        string source = Path.Combine(Root, name);
        if (!File.Exists(source))
        {
            File.WriteAllText(source, name + "\n");
        }

        return source;
    }

    /// <summary>Prepares <paramref name="name"/>, which must fail.</summary>
    internal JacquardException PrepareFails(string name = "rules.jac", CancellationToken cancellationToken = default) =>
        Support.Throws<JacquardException>(() => Runtime.Prepare(new[] { Source(name) }, Options, cancellationToken))!;

    internal long Calls(string command)
    {
        string log = Path.Combine(Root, "log");
        return File.Exists(log) ? File.ReadAllLines(log).Count(line => line == command) : 0;
    }

    internal bool StagingLeft()
    {
        string cache = Path.Combine(Root, "cache");
        return Directory.Exists(cache) && Directory.EnumerateDirectories(cache).Any(entry => Path.GetFileName(entry).StartsWith("prepare-", StringComparison.Ordinal));
    }

    /// <summary>The cache's entries, each a staging directory or a published entry, sorted.</summary>
    internal List<string> CacheEntries() =>
        Directory.EnumerateFileSystemEntries(Path.Combine(Root, "cache")).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList()!;

    /// <summary>Waits for <paramref name="name"/> to hold a complete pid line and returns it.</summary>
    internal int Pid(string name) => SdkSupport.PidFile(Path.Combine(Root, name));

    public void Dispose()
    {
        foreach (string file in Directory.EnumerateFiles(Root).Where(path => path.EndsWith("pids", StringComparison.Ordinal) || path.EndsWith(".pid", StringComparison.Ordinal)))
        {
            foreach (string pid in File.ReadAllText(file).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                Support.Kill(int.Parse(pid, CultureInfo.InvariantCulture));
            }
        }
    }
}

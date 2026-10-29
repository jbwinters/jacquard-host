using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>
/// Runtime options, and preparation against a fake Core: one version check
/// and prelude discovery per key across threads and runtimes, a child
/// holding a command's output killed, every Core command bounded by its
/// timeout, its output limit, and the token, bounded regular-file reads and
/// walks, a verified owner-only cache, and a bounded failure cleanup that
/// removes only what the preparation made.
/// </summary>
[TestFixture]
[Platform("Linux")]
public sealed class RuntimeTests
{
    private const string SlowCheck = "check) sleep 300 & echo $! > \"$ROOT/child.pid\"; wait ;;";

    private string scratch = string.Empty;

    [SetUp]
    public void Prepare() => scratch = SdkSupport.Scratch("runtime");

    [TearDown]
    public void CleanUp()
    {
        Runtime.FlightWaitHook = null;
        Runtime.SourceReadHook = null;
        Runtime.LoadedHook = null;
        Runtime.PublishedHook = null;
        Runtime.CleanupHook = null;
        Runtime.CommandHook = null;
        Runtime.RootHook = null;
        Core.DigestChunkHook = null;
        Core.TreeFileHook = null;
        Core.TreeEntryCap = Core.TreeEntries;
        SdkSupport.NoAccounts();
        SdkSupport.RemoveScratch(scratch);
    }

    private string At(string name) => Path.Combine(scratch, name);

    private static string Excerpt(char c, int length) => new string(c, 64) + "... (" + length.ToString(CultureInfo.InvariantCulture) + " chars)";

    [Test]
    public void RuntimeOptionsAreValidatedBeforeCoreRuns()
    {
        var baseOptions = new RuntimeOptions { Prelude = scratch };
        Assert.That(Support.Throws<JacquardException>(() => _ = new Runtime(baseOptions with { Deadline = TimeSpan.Zero })).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(
            Support.Throws<JacquardException>(() => _ = new Runtime(baseOptions with { Limits = Limits.Hard.With("max_frame_bytes", 0) })).Message,
            Does.StartWith("invalid runtime limits"));
        var runtime = new Runtime(baseOptions with { Binary = "/no/such/jacquard" });
        foreach (string[] sources in new[] { new[] { "rules.txt" }, Array.Empty<string>(), new[] { "a.jac", "b.jqd" }, new[] { ".jac" } })
        {
            JacquardException error = Support.Throws<JacquardException>(() => runtime.Prepare(sources));
            Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Preparation), string.Join(",", sources));
            Assert.That(error.Message, Is.EqualTo("pass .jac files or .jqd files in dependency order"));
        }

        Assert.That(Support.Throws<JacquardException>(() => runtime.Prepare(new[] { "rules.jac" })).Message, Does.Contain("cannot find Core executable /no/such/jacquard"));
        Assert.That(runtime.CoreCommands, Is.EqualTo(0));

        // A name longer than any path is refused with an excerpt, never used or copied whole.
        var huge = new Runtime(baseOptions with { Binary = "/" + new string('x', 1_000_000) });
        Assert.That(Support.Throws<JacquardException>(() => huge.Prepare(new[] { "rules.jac" })).Message, Does.StartWith("cannot find Core executable /" + new string('x', 63) + "... (1000001 chars)"));
        string longer = Support.Throws<JacquardException>(() => new Runtime(baseOptions with { Binary = "/no/such/" + new string('y', 1000) }).Prepare(new[] { "rules.jac" })).Message;
        Assert.That(longer, Has.Length.LessThan(200));
        Assert.That(longer, Does.Contain("... (1009 chars)"));

        // Paths longer than any path are refused with an excerpt before the file system sees them.
        string source = Support.Throws<JacquardException>(() => runtime.Prepare(new[] { "/" + new string('s', 1_000_000) + ".jac" })).Message;
        Assert.That(source, Does.StartWith("source /sss").And.EndWith("(1000005 chars) is longer than any path"));
        string cache = Support.Throws<JacquardException>(() => runtime.Prepare(new[] { "rules.jac" }, new PrepareOptions { CacheDir = "/" + new string('c', 1_000_000) })).Message;
        Assert.That(cache, Does.StartWith("cache directory /ccc"));
        Assert.That(cache, Has.Length.LessThan(200));

        // A relative path is measured as the file system will see it: 4,080 chars grow past the limit.
        string relative = Support.Throws<JacquardException>(() => runtime.Prepare(new[] { "rules.jac" }, new PrepareOptions { CacheDir = new string('c', 4080) })).Message;
        Assert.That(relative, Does.StartWith("cache directory ccc").And.EndWith("is longer than any path"));
        Assert.That(Directory.Exists(new string('c', 200)), Is.False, "a directory was made");
        JacquardException prelude = Support.Throws<JacquardException>(() => _ = new Runtime(baseOptions with { Prelude = "/" + new string('p', 1_000_000) }));
        Assert.That(prelude.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(prelude.Message, Has.Length.LessThan(200));
        foreach (string nul in new[] { "jac\0quard", "/tmp/jac\0quard" })
        {
            JacquardException error = Support.Throws<JacquardException>(() => new Runtime(baseOptions with { Binary = nul }).Prepare(new[] { "rules.jac" }));
            Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Preparation));
            Assert.That(error.Message, Does.StartWith("cannot find Core executable"));
        }

        Assert.That(Support.Throws<JacquardException>(() => runtime.Prepare(new[] { "ru\0les.jac" })).Message, Does.StartWith("source ru").And.EndWith("is not a path"));
        Support.Throws<ArgumentNullException>(() => runtime.Prepare(new string[] { null! }));
    }

    [Test]
    public void EmptyRuntimeOptionsMeanUnset()
    {
        var runtime = new Runtime(new RuntimeOptions { Binary = string.Empty, Prelude = scratch });
        string? bin = Environment.GetEnvironmentVariable("JACQUARD_BIN");
        Assert.That(runtime.Binary, Is.EqualTo(string.IsNullOrEmpty(bin) ? "jacquard" : bin));
        Assert.That(runtime.Prelude, Is.EqualTo(Path.GetFullPath(scratch)));
    }

    [Test]
    public void VersionIsCheckedOnceAcrossThreadsAndRuntimes()
    {
        using FakeCore fake = FakeCore.Failing(At("once"), Compat.CoreVersion);
        JacquardException[] errors = Enumerable.Range(0, 4).AsParallel().WithDegreeOfParallelism(4).Select(_ => fake.PrepareFails()).ToArray();
        foreach (JacquardException error in errors)
        {
            Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Preparation));
            Assert.That(error.Message, Is.EqualTo("Core check failed: boom"));
        }

        Assert.That(fake.Calls("--version"), Is.EqualTo(1));
        Assert.That(fake.Calls("check"), Is.EqualTo(4));
        Assert.That(fake.StagingLeft(), Is.False, "a staging directory was left behind");

        // Another runtime for the same Core shares the check.
        var other = new Runtime(new RuntimeOptions { Binary = fake.Binary, Prelude = Path.Combine(fake.Root, "prelude") });
        Support.Throws<JacquardException>(() => other.Prepare(new[] { fake.Source("rules.jac") }, fake.Options));
        Assert.That(fake.Calls("--version"), Is.EqualTo(1));
        Assert.That(other.CoreCommands, Is.EqualTo(1));
    }

    /// <summary>
    /// A Core command whose child keeps its output open does not hold the
    /// preparation: the command's group is killed when it exits, so the child
    /// dies and preparation goes on (and here fails at <c>run</c>).
    /// </summary>
    [Test]
    public void ACoreCommandsChildHoldingItsOutputIsKilledAtTheExit()
    {
        Assume.That(CoreCommand.Setsid, Is.Not.Null, "no setsid at /usr/bin or /bin");
        using FakeCore fake = FakeCore.Create(At("holder"), Compat.CoreVersion, null,
            "check) sleep 300 & echo $! > \"$ROOT/holder.pid\" ;;\nhash) echo '0:x " + new string('0', 64) + "' ;;\nrun) exit 4 ;;");
        Assert.That(fake.PrepareFails().Message, Is.EqualTo("Core run failed: exit status 4"));
        SdkSupport.Dies("the command's child", fake.Pid("holder.pid"));
        Assert.That(fake.StagingLeft(), Is.False);
    }

    [Test]
    public void AVersionMismatchIsRefusedAndCheckedAgain()
    {
        using FakeCore fake = FakeCore.Failing(At("mismatch"), "9.9.9");
        for (int i = 0; i < 2; i++)
        {
            string message = fake.PrepareFails().Message;
            Assert.That(message, Does.Contain("expected pinned Core version").And.Contain("jacquard 9.9.9"));
        }

        Assert.That(fake.Calls("--version"), Is.EqualTo(2));
        Assert.That(fake.Calls("check"), Is.EqualTo(0));
    }

    [Test]
    public void PreludeDiscoveryRunsOncePerCoreAndPrelude()
    {
        using FakeCore fake = FakeCore.Hashing(At("prelude"));
        foreach (string source in new[] { "rules.jac", "other.jac" })
        {
            Assert.That(fake.PrepareFails(source).Message, Is.EqualTo("Core run failed: exit status 4"));
        }

        // Three prelude files once, then one local hash per source.
        Assert.That(fake.Calls("hash"), Is.EqualTo(5));
        Assert.That(fake.Calls("--version"), Is.EqualTo(1));
        Assert.That(fake.Calls("run"), Is.EqualTo(2));
        Assert.That(fake.StagingLeft(), Is.False);
    }

    [Test]
    public void CoreOutputBeyondItsLimitIsRefused()
    {
        using FakeCore fake = FakeCore.Create(At("overflow"), Compat.CoreVersion, null, "check) head -c 9000000 /dev/zero ;;");
        Assert.That(fake.PrepareFails().Message, Is.EqualTo("Core check output exceeds 8 MiB"));
    }

    [Test]
    public void ACoreCommandThatOutlivesItsTimeoutIsKilledWithItsChild()
    {
        using FakeCore fake = FakeCore.Create(At("timeout"), Compat.CoreVersion, null, SlowCheck);
        fake.Runtime.CommandTimeout = TimeSpan.FromMilliseconds(500);
        Assert.That(fake.PrepareFails().Message, Is.EqualTo("Core check failed: no exit within 500 ms"));
        SdkSupport.Dies("the timed-out command's child", fake.Pid("child.pid"));
        Assert.That(fake.StagingLeft(), Is.False);
    }

    [Test]
    public void CancellationStopsTheRunningCoreCommand()
    {
        using FakeCore fake = FakeCore.Create(At("cancel"), Compat.CoreVersion, null, SlowCheck);
        using var cancel = new CancellationTokenSource();
        Thread canceller = Support.After(TimeSpan.Zero, () =>
        {
            fake.Pid("child.pid");
            cancel.Cancel();
        });
        Assert.That(fake.PrepareFails("rules.jac", cancel.Token).Message, Is.EqualTo("Core check failed: preparation was cancelled"));
        SdkSupport.Dies("the cancelled command's child", fake.Pid("child.pid"));
        canceller.Join();

        // The cleanup observes the stop too, so the unpublished staging directory stays.
        Assert.That(fake.StagingLeft(), Is.True);
    }

    /// <summary>
    /// One preparation runs a slow <c>--version</c>; another waits for that
    /// check. The waiter observes its own cancellation while the first still
    /// runs, and never runs the command.
    /// </summary>
    [Test]
    public void APreparationWaitingForAnotherObservesItsCancellation()
    {
        string slowVersion = "sleep 300 & echo $! > \"$ROOT/version.pid\"; wait; echo 'jacquard " + Compat.CoreVersion + "'";
        using FakeCore fake = FakeCore.Create(At("waiter"), Compat.CoreVersion, slowVersion, "check) exit 3 ;;");
        using var first = new CancellationTokenSource();
        Task<JacquardException> owner = Task.Factory.StartNew(() => fake.PrepareFails("rules.jac", first.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        int sleeper = fake.Pid("version.pid");

        // The seam cancels each waiter only once it waits, so the wait itself must notice.
        using var waiter = new CancellationTokenSource();
        Runtime.FlightWaitHook = waiter.Cancel;
        Assert.That(fake.PrepareFails("rules.jac", waiter.Token).Message, Is.EqualTo("preparation was cancelled"));
        Runtime.FlightWaitHook = null;
        Assert.That(Support.Alive(sleeper), Is.True, "the first preparation's command stopped");
        Assert.That(fake.Calls("--version"), Is.EqualTo(1));
        first.Cancel();
        Assert.That(owner.Wait(TimeSpan.FromMinutes(1)), Is.True);
        Assert.That(owner.Result.Message, Is.EqualTo("Core --version failed: preparation was cancelled"));
        SdkSupport.Dies("the first preparation's command", sleeper);
    }

    /// <summary>
    /// Prepares on another thread, which must finish within a minute. If it
    /// is blocked opening <paramref name="fifo"/>, a writer is opened to
    /// release it and the test fails.
    /// </summary>
    private static JacquardException PreparesWithoutBlocking(FakeCore fake, string[] sources, string fifo)
    {
        Task<JacquardException?> preparing = Task.Factory.StartNew<JacquardException?>(
            () =>
            {
                try
                {
                    fake.Runtime.Prepare(sources, fake.Options);
                    return null;
                }
                catch (JacquardException error)
                {
                    return error;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        if (!preparing.Wait(TimeSpan.FromMinutes(1)))
        {
            using (var writer = new FileStream(fifo, FileMode.Open, FileAccess.Write))
            {
                writer.WriteByte((byte)'\n');
            }

            preparing.Wait(TimeSpan.FromMinutes(1));
            Assert.Fail("the preparation blocked on " + fifo);
        }

        Assert.That(preparing.Result, Is.Not.Null, "the preparation succeeded");
        return preparing.Result!;
    }

    [Test]
    public void ASourceThatIsNotARegularFileIsRefusedWithoutBlocking()
    {
        using FakeCore fake = FakeCore.Failing(At("kinds"), Compat.CoreVersion);
        string fifo = Path.Combine(fake.Root, "fifo.jac");
        SdkSupport.MakeFifo(fifo);
        Assert.That(PreparesWithoutBlocking(fake, new[] { fifo }, fifo).Message, Is.EqualTo("cannot read " + fifo + ": not a regular file"));
        string directory = Directory.CreateDirectory(Path.Combine(fake.Root, "directory.jac")).FullName;
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { directory }, fake.Options)).Message, Is.EqualTo("cannot read " + directory + ": not a regular file"));
        Assert.That(fake.Calls("check"), Is.EqualTo(0));

        // A symbolic link to a regular file is read.
        string link = Path.Combine(fake.Root, "link.jac");
        File.CreateSymbolicLink(link, fake.Source("rules.jac"));
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { link }, fake.Options)).Message, Is.EqualTo("Core check failed: boom"));
    }

    [Test]
    public void SourcesOverTheCeilingAreRefusedBeforeTheyAreRead()
    {
        using FakeCore fake = FakeCore.Failing(At("ceiling"), Compat.CoreVersion);
        var read = new List<string>();
        Runtime.SourceReadHook = path =>
        {
            lock (read)
            {
                read.Add(path);
            }
        };

        // Sparse and unreadable, so a refusal that is not "cannot read" was made from the size alone.
        string one = SdkSupport.Sparse(Path.Combine(fake.Root, "one.jac"), Runtime.SourceLimit + 1);
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { one }, fake.Options)).Message, Is.EqualTo("sources exceed 16 MiB in total"));
        string half = SdkSupport.Sparse(Path.Combine(fake.Root, "half.jac"), Runtime.SourceLimit / 2, UnixFileMode.UserRead);
        string other = SdkSupport.Sparse(Path.Combine(fake.Root, "other.jac"), Runtime.SourceLimit / 2);
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { half, other }, fake.Options)).Message, Is.EqualTo("sources exceed 16 MiB in total"));
        Assert.That(read, Is.EqualTo(new[] { half }), "a source past the ceiling was read");
        Assert.That(fake.Calls("check"), Is.EqualTo(0));
    }

    [Test]
    public void ACancellationIsObservedBetweenSourceFiles()
    {
        using FakeCore fake = FakeCore.Failing(At("between"), Compat.CoreVersion);
        string[] sources = { fake.Source("a.jac"), fake.Source("b.jac") };
        var read = new List<string>();
        using var cancel = new CancellationTokenSource();
        Runtime.SourceReadHook = path =>
        {
            read.Add(path);
            cancel.Cancel();
        };
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(sources, fake.Options, cancel.Token)).Message, Is.EqualTo(Core.Aborted));
        Assert.That(read, Is.EqualTo(new[] { sources[0] }));
        Assert.That(fake.Calls("check"), Is.EqualTo(0));
    }

    [Test]
    public void ThePreludeWalkReadsOnlyBoundedRegularFiles()
    {
        using FakeCore fake = FakeCore.Failing(At("walk"), Compat.CoreVersion);
        string big = SdkSupport.Sparse(Path.Combine(fake.Root, "prelude", "zz-big.jqd"), Core.TreeLimit + 1);
        Assert.That(fake.PrepareFails().Message, Does.StartWith("cannot read prelude").And.EndWith("exceed 256 MiB"));
        File.Delete(big);
        string fifo = Path.Combine(fake.Root, "prelude", "zz-fifo.jqd");
        SdkSupport.MakeFifo(fifo);
        Assert.That(PreparesWithoutBlocking(fake, new[] { fake.Source("rules.jac") }, fifo).Message, Does.EndWith("unsupported filesystem entry in prepared artifacts: zz-fifo.jqd"));
        File.Delete(fifo);
        File.WriteAllText(Path.Combine(fake.Root, "prelude", "02-more.jqd"), "more\n");
        var digested = new List<string>();
        using var cancel = new CancellationTokenSource();
        Core.TreeFileHook = path =>
        {
            digested.Add(path);
            cancel.Cancel();
        };
        Assert.That(fake.PrepareFails("rules.jac", cancel.Token).Message, Does.EndWith(Core.Aborted));

        // The walk streams each directory, so either file may come first.
        Assert.That(digested, Has.Count.EqualTo(1));
        Assert.That(fake.Calls("check"), Is.EqualTo(0));
    }

    /// <summary>A walk thousands of directories deep keeps an explicit stack, so it is refused by the entry cap or read, never by the call stack.</summary>
    [Test]
    public void ADeepPreludeTreeIsWalkedWithoutRecursion()
    {
        using FakeCore fake = FakeCore.Failing(At("deep"), Compat.CoreVersion);
        string directory = Path.Combine(fake.Root, "prelude");
        for (int i = 0; i < 1500; i++)
        {
            directory = Path.Combine(directory, "d");
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "leaf.jqd"), "leaf\n");
        Assert.That(fake.PrepareFails().Message, Is.EqualTo("Core check failed: boom"));
    }

    [Test]
    public void AChangedCacheEntryIsRefusedIncludingItsMetadata()
    {
        using FakeCore fake = FakeCore.Programs(At("verified"));
        string[] sources = { fake.Source("rules.jac") };
        PreparedProgram program = fake.Runtime.Prepare(sources, fake.Options);
        Assert.That(program.Data.Identity("first").Hex, Is.EqualTo(FakeCore.First));
        Assert.That(File.Exists(Path.Combine(program.Directory, Runtime.HashOutput)), Is.True);
        int commands = fake.Runtime.CoreCommands;
        Assert.That(fake.Runtime.Prepare(sources, fake.Options).Directory, Is.EqualTo(program.Directory));
        Assert.That(fake.Runtime.CoreCommands, Is.EqualTo(commands), "a warm preparation ran Core");
        string metadata = Path.Combine(program.Directory, "program.json");
        string original = File.ReadAllText(metadata);
        var changes = new[]
        {
            ("[\"first\",\"" + FakeCore.First + "\"]", "[\"first\",\"" + FakeCore.Second + "\"]"),
            ("{\"hash\":\"" + FakeCore.First + "\",\"name\":\"first\"}", "{\"hash\":\"" + FakeCore.Second + "\",\"name\":\"first\"}"),
            ("\"first\":\"() ->{} Int\"", "\"first\":\"() ->{} Text\""),
        };
        foreach ((string from, string to) in changes)
        {
            Replace(metadata, from, to);
            JacquardException error = Support.Throws<JacquardException>(() => fake.Runtime.Prepare(sources, fake.Options), "a changed program.json was accepted: " + to);
            Assert.That(error.Message, Does.Contain("program.json bindings differ from Core's cached output"));
            File.WriteAllText(metadata, original);
        }

        Assert.That(fake.Runtime.Prepare(sources, fake.Options).Directory, Is.EqualTo(program.Directory));
        string hash = Path.Combine(program.Directory, Runtime.HashOutput);
        string output = File.ReadAllText(hash);
        Replace(hash, "0:first " + FakeCore.First, "0:first " + FakeCore.Second);
        string changed = File.ReadAllText(hash);
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(sources, fake.Options)).Message, Does.Contain("cached artifact digests do not match"));
        Replace(metadata, Core.Digest(Encoding.UTF8.GetBytes(output)), Core.Digest(Encoding.UTF8.GetBytes(changed)));
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(sources, fake.Options)).Message, Does.Contain("program.json bindings differ"));

        // A file replaced by a symbolic link is refused, not followed.
        File.Delete(metadata);
        File.CreateSymbolicLink(metadata, hash);
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(sources, fake.Options)).Message, Does.Contain("unsupported filesystem entry in prepared artifacts: program.json"));
        Assert.That(fake.Runtime.CoreCommands, Is.EqualTo(commands));
    }

    private static void Replace(string file, string from, string to)
    {
        string text = File.ReadAllText(file);
        Assert.That(text, Does.Contain(from), from + " is not in " + file);
        File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal));
    }

    /// <summary>
    /// A new cache entry is owner-only throughout (Core's store included),
    /// and the cache root, the entry, or any file or directory in it that its
    /// group may write is refused on load; restoring the permission makes the
    /// entry load again.
    /// </summary>
    [Test]
    public void AGroupWritableCacheEntryIsRefused()
    {
        using FakeCore fake = FakeCore.Programs(At("owner"));
        string[] sources = { fake.Source("rules.jac") };
        PreparedProgram program = fake.Runtime.Prepare(sources, fake.Options);
        string entry = program.Directory;
        var targets = Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.AllDirectories).Append(entry).ToList();
        Assert.That(targets, Does.Contain(Path.Combine(entry, "store", "objects")));
        foreach (string path in targets)
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            Assert.That(mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), Is.EqualTo(UnixFileMode.None), path + " is not owner-only: " + mode);
        }

        string[] checkedPaths =
        {
            Path.GetFullPath(fake.Options.CacheDir!), entry, Path.Combine(entry, "program.json"), Path.Combine(entry, Runtime.HashOutput), Path.Combine(entry, "core"),
            Path.Combine(entry, "store"), Path.Combine(entry, "store", "objects"),
        };
        foreach (string path in checkedPaths)
        {
            UnixFileMode original = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, original | UnixFileMode.GroupWrite);
            JacquardException error = Support.Throws<JacquardException>(() => fake.Runtime.Prepare(sources, fake.Options), "a group-writable " + path + " was accepted");
            Assert.That(error.Message, Does.Contain(path + " is group- or world-writable"));
            File.SetUnixFileMode(path, original);
        }

        Assert.That(fake.Runtime.Prepare(sources, fake.Options).Directory, Is.EqualTo(entry));
    }

    /// <summary>
    /// A cache root this user does not own is refused before anything is
    /// written to it. The root directory stands in for a foreign owner; a
    /// foreign-owned entry inside an owned root would need another account,
    /// so it is not tested.
    /// </summary>
    [Test]
    public void AForeignOwnedCacheRootIsRefused()
    {
        Assume.That(Unix.EffectiveUser(), Is.Not.EqualTo(0u), "running as root");
        using FakeCore fake = FakeCore.Programs(At("foreign"));
        JacquardException error = Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = "/" }));
        Assert.That(error.Message, Does.Contain("cache path / is not owned by the current user"));
        Assert.That(fake.Calls("check"), Is.EqualTo(0));
    }

    private static Runtime RuntimeFor(FakeCore fake, string binary) => new(new RuntimeOptions { Binary = binary, Prelude = Path.Combine(fake.Root, "prelude") });

    /// <summary>An executable over the 512 MiB fingerprint ceiling is refused from its size: it is sparse and unreadable, so a read would fail with "cannot read" instead.</summary>
    [Test]
    public void AnExecutableOverTheFingerprintCeilingIsRefusedBeforeItIsRead()
    {
        using FakeCore fake = FakeCore.Failing(At("huge"), Compat.CoreVersion);
        string huge = SdkSupport.Sparse(Path.Combine(fake.Root, "huge-jacquard"), Core.ExecutableLimit + 1, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string message = Support.Throws<JacquardException>(() => RuntimeFor(fake, huge).Prepare(new[] { fake.Source("rules.jac") }, fake.Options)).Message;
        Assert.That(message, Does.StartWith("Core executable ").And.EndWith(" exceeds 512 MiB"));
    }

    /// <summary>A cancellation during fingerprinting stops it after the piece being read (a test seam counts the pieces of a 16 MiB executable).</summary>
    [Test]
    public void AStopDuringFingerprintingIsObserved()
    {
        using FakeCore fake = FakeCore.Failing(At("digest"), Compat.CoreVersion);
        string big = SdkSupport.Sparse(Path.Combine(fake.Root, "big-jacquard"), 16L << 20, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        int pieces = 0;
        using var cancel = new CancellationTokenSource();
        Core.DigestChunkHook = () =>
        {
            pieces++;
            cancel.Cancel();
        };
        Assert.That(Support.Throws<JacquardException>(() => RuntimeFor(fake, big).Prepare(new[] { fake.Source("rules.jac") }, fake.Options, cancel.Token)).Message, Is.EqualTo(Core.Aborted));
        Assert.That(pieces, Is.EqualTo(1));
        Assert.That(fake.Calls("--version"), Is.EqualTo(0));
    }

    /// <summary>
    /// A cancellation after the first of two empty prelude files stops the
    /// walk before the second, whichever comes first: an empty file has no
    /// piece to read, so only the check before each entry can see the stop.
    /// </summary>
    [Test]
    public void AStopBetweenEmptyFilesIsObserved()
    {
        using FakeCore fake = FakeCore.Failing(At("empty"), Compat.CoreVersion);
        File.WriteAllText(Path.Combine(fake.Root, "prelude", "01-prim.jqd"), string.Empty);
        File.WriteAllText(Path.Combine(fake.Root, "prelude", "02-empty.jqd"), string.Empty);
        var digested = new List<string>();
        using var cancel = new CancellationTokenSource();
        Core.TreeFileHook = path =>
        {
            digested.Add(path);
            cancel.Cancel();
        };
        Assert.That(fake.PrepareFails("rules.jac", cancel.Token).Message, Does.EndWith(Core.Aborted));
        Assert.That(digested, Has.Count.EqualTo(1));
        Assert.That(fake.Calls("check"), Is.EqualTo(0));
    }

    /// <summary>A tree over the entry cap is refused, zero-byte files counted (a test seam lowers the cap to 3).</summary>
    [Test]
    public void ATreeOverTheEntryCapIsRefused()
    {
        Assert.That(Core.TreeEntries, Is.EqualTo(100_000));
        using FakeCore fake = FakeCore.Failing(At("entries"), Compat.CoreVersion);
        foreach (string name in new[] { "a.jqd", "b.jqd", "c.jqd" })
        {
            File.WriteAllText(Path.Combine(fake.Root, "prelude", name), string.Empty);
        }

        Core.TreeEntryCap = 3;
        string prelude = Path.Combine(fake.Root, "prelude");
        Assert.That(fake.PrepareFails().Message, Does.StartWith("cannot read prelude").And.EndWith("more than 3 entries under " + prelude));
        Assert.That(fake.Calls("check"), Is.EqualTo(0));
    }

    /// <summary>A source list that counts the elements read, and fails a read past <c>limit</c>.</summary>
    private sealed class CountingList : IReadOnlyList<string>
    {
        private readonly string element;
        private readonly int limit;
        private readonly Action<int> onRead;

        internal CountingList(int count, string element, int limit, Action<int> onRead)
        {
            Count = count;
            this.element = element;
            this.limit = limit;
            this.onRead = onRead;
        }

        internal List<int> Indices { get; } = new();

        public int Count { get; }

        public string this[int index]
        {
            get
            {
                if (Indices.Count >= limit)
                {
                    throw new InvalidOperationException("read past the source cap");
                }

                Indices.Add(index);
                onRead(index);
                return element;
            }
        }

        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException("the list was enumerated");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A source list over 1,024 is refused from its count, before any element is read. A list of exactly 1,024 passes that check.</summary>
    [Test]
    public void AHugeSourceListIsRefusedBeforeAnyElementIsRead()
    {
        Assert.That(Runtime.SourceFiles, Is.EqualTo(1024));
        using FakeCore fake = FakeCore.Programs(At("count"));
        string source = fake.Source("rules.jac");
        var huge = new CountingList(int.MaxValue, source, 2048, _ => { });
        JacquardException error = Support.Throws<JacquardException>(() => fake.Runtime.Prepare(huge, fake.Options));
        Assert.That(error.Message, Is.EqualTo("more than 1024 sources"));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Preparation));
        Assert.That(huge.Indices, Is.Empty);
        var over = new CountingList(1025, source, 2048, _ => { });
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(over, fake.Options)).Message, Is.EqualTo("more than 1024 sources"));
        Assert.That(over.Indices, Is.Empty);
        var most = new CountingList(1024, source, 2048, _ => { });
        fake.Runtime.Prepare(most, fake.Options);
        Assert.That(most.Indices, Has.Count.EqualTo(1024));
    }

    /// <summary>A cancellation while the list is validated stops it before the next element is read.</summary>
    [Test]
    public void AStopDuringSourceValidationIsObserved()
    {
        using FakeCore fake = FakeCore.Programs(At("validate"));
        string source = fake.Source("rules.jac");
        using var cancel = new CancellationTokenSource();
        var cancelled = new CountingList(3, source, 3, index =>
        {
            if (index == 1)
            {
                cancel.Cancel();
            }
        });
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(cancelled, fake.Options, cancel.Token)).Message, Is.EqualTo(Core.Aborted));
        Assert.That(cancelled.Indices, Is.EqualTo(new[] { 0, 1 }));
        Assert.That(fake.Calls("--version"), Is.EqualTo(0));
    }

    /// <summary>A fake whose <c>run</c> leaves ten files in its store and fails.</summary>
    private static FakeCore FailingStore(string root) => FakeCore.Create(root, Compat.CoreVersion, null,
        "check) echo 'first : () ->{} Int' ;;\nhash) echo '0:first " + FakeCore.First + "' ;;\n"
        + "run) mkdir -p \"$4\" && for i in 0 1 2 3 4 5 6 7 8 9; do : > \"$4/f$i\"; done; exit 4 ;;");

    /// <summary>
    /// A failed preparation's cleanup removes its staging directory, but a
    /// cancellation during the cleanup ends it at once (a test seam sets the
    /// stop after the second removal), leaving the unpublished staging
    /// directory; nothing is published either way.
    /// </summary>
    [Test]
    public void AStopDuringTheFailureCleanupEndsIt()
    {
        using FakeCore fake = FailingStore(At("cleanup"));
        var removed = new List<string>();
        Runtime.CleanupHook = removed.Add;
        Assert.That(fake.PrepareFails().Message, Does.EndWith("Core run failed: exit status 4"));
        Assert.That(removed, Has.Count.EqualTo(13));
        Assert.That(fake.CacheEntries(), Is.Empty);
        removed.Clear();
        using var cancel = new CancellationTokenSource();
        Runtime.CleanupHook = path =>
        {
            removed.Add(path);
            if (removed.Count == 2)
            {
                cancel.Cancel();
            }
        };
        Assert.That(fake.PrepareFails("rules.jac", cancel.Token).Message, Is.EqualTo(Core.Aborted));
        Assert.That(removed, Has.Count.EqualTo(2));
        List<string> left = fake.CacheEntries();
        Assert.That(left, Has.Count.EqualTo(1));
        Assert.That(left[0], Does.StartWith("prepare-"));
        Assert.That(Directory.Exists(Path.Combine(fake.Root, "cache", left[0], "store")), Is.True);
    }

    /// <summary>The failure cleanup removes at most the tree cap's entries (a test seam lowers it to 3), leaving the rest unpublished.</summary>
    [Test]
    public void TheFailureCleanupStopsAtTheCap()
    {
        using FakeCore fake = FailingStore(At("capped"));
        var removed = new List<string>();
        Runtime.CleanupHook = removed.Add;
        Core.TreeEntryCap = 3;
        Assert.That(fake.PrepareFails().Message, Does.EndWith("Core run failed: exit status 4"));
        Assert.That(removed, Has.Count.EqualTo(3));
        List<string> left = fake.CacheEntries();
        Assert.That(left, Has.Count.EqualTo(1));
        Assert.That(left[0], Does.StartWith("prepare-"));
    }

    /// <summary>
    /// A cache root reached through a symbolic link is checked as the
    /// directory it names (the link's own mode reads 0777), and its entries
    /// live there; a group-writable real root is refused with a hint.
    /// </summary>
    [Test]
    public void ASymlinkedCacheRootIsFollowedToItsRealDirectory()
    {
        using FakeCore fake = FakeCore.Programs(At("link"));
        string real = Directory.CreateDirectory(Path.Combine(fake.Root, "real-cache"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        string link = Path.Combine(fake.Root, "linked-cache");
        Directory.CreateSymbolicLink(link, real);
        var options = new PrepareOptions { CacheDir = link };
        PreparedProgram program = fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options);
        Assert.That(Path.GetDirectoryName(program.Directory), Is.EqualTo(real));
        Assert.That(Directory.EnumerateFileSystemEntries(real).Count(), Is.EqualTo(1));
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options).Directory, Is.EqualTo(program.Directory));
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute);
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options)).Message,
            Is.EqualTo("cache path " + real + " is group- or world-writable; only its owner may write the cache (make it owner-only, for example chmod 700 " + real + ")"));
    }

    /// <summary>A preparation removes only its own staging directory: directories it did not make, whatever their names or ages, are left alone.</summary>
    [Test]
    public void APreparationLeavesDirectoriesItDidNotMake()
    {
        using FakeCore fake = FakeCore.Programs(At("leaves"));
        string cache = Directory.CreateDirectory(Path.Combine(fake.Root, "cache"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        string notes = Directory.CreateDirectory(Path.Combine(cache, "prepare-release-notes")).FullName;
        File.WriteAllText(Path.Combine(notes, "draft.txt"), "keep");
        string left = Directory.CreateDirectory(Path.Combine(cache, "prepare-1-00000000-0")).FullName;
        Directory.SetLastWriteTimeUtc(notes, DateTime.UtcNow.AddDays(-1));
        Directory.SetLastWriteTimeUtc(left, DateTime.UtcNow.AddDays(-1));
        string entry = fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options).Directory;
        Assert.That(File.ReadAllText(Path.Combine(notes, "draft.txt")), Is.EqualTo("keep"));
        Assert.That(Directory.Exists(left), Is.True, "a directory the preparation did not make was removed");
        Assert.That(fake.CacheEntries(), Is.EqualTo(new[] { Path.GetFileName(entry), Path.GetFileName(left), Path.GetFileName(notes) }.OrderBy(name => name, StringComparer.Ordinal)));
    }

    /// <summary>Once an entry is published its staging path is vacated, and whatever appears there afterwards is not the preparation's: it is left alone.</summary>
    [Test]
    public void ADirectoryAtTheVacatedStagingPathIsLeftAlone()
    {
        using FakeCore fake = FakeCore.Programs(At("vacated"));
        string? replacement = null;
        Runtime.PublishedHook = staging =>
        {
            replacement = Directory.CreateDirectory(staging).FullName;
            File.WriteAllText(Path.Combine(staging, "unrelated.txt"), "keep");
        };
        fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options);
        Assert.That(File.ReadAllText(Path.Combine(replacement!, "unrelated.txt")), Is.EqualTo("keep"), "the preparation removed a directory it did not make");
    }

    /// <summary>A cancellation that lands as a warm load finishes fails the preparation instead of returning a program.</summary>
    [Test]
    public void AStopAsAWarmLoadFinishesIsReported()
    {
        using FakeCore fake = FakeCore.Programs(At("loaded"));
        string[] sources = { fake.Source("rules.jac") };
        fake.Runtime.Prepare(sources, fake.Options);
        using var cancel = new CancellationTokenSource();
        Runtime.LoadedHook = cancel.Cancel;
        Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(sources, fake.Options, cancel.Token)).Message, Is.EqualTo(Core.Aborted));
        Runtime.LoadedHook = null;

        // Without a stop the same entry loads.
        fake.Runtime.Prepare(sources, fake.Options);
    }

    /// <summary>Replaces <paramref name="path"/> with a new executable file holding <paramref name="text"/>, as an atomic rename does.</summary>
    private static void Replace(string path, string text)
    {
        string next = path + ".next";
        File.WriteAllText(next, text);
        File.SetUnixFileMode(next, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(next, path, overwrite: true);
    }

    private static string ChangedMessage(string what, string path) => what + " " + path + " changed during preparation; prepare again once it no longer changes";

    /// <summary>
    /// The cache root is checked once and used by its path, so its identity
    /// is checked again before each use. A test seam renames the checked
    /// root away and puts a new directory in its place (as another user who
    /// may write its parent could) just before the staging directory is
    /// made, before publishing, before a load, and once a load has read the
    /// entry, on a cold and on a warm preparation; each is refused.
    /// </summary>
    [Test]
    public void ACacheRootReplacedAfterItsCheckIsRefused()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string cache = Path.GetFullPath(fake.Options.CacheDir!);
        string Swap(string use, string source)
        {
            bool swapped = false;
            Runtime.RootHook = at =>
            {
                if (at == use && !swapped)
                {
                    swapped = true;
                    Directory.Move(cache, cache + "-" + use + "-" + Path.GetFileNameWithoutExtension(source));
                    Directory.CreateDirectory(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            };
            string message = fake.PrepareFails(source).Message;
            Runtime.RootHook = null;
            Assert.That(swapped, Is.True, use + " was never reached");
            return message;
        }

        string refused = "cache root " + cache + " was replaced after it was checked; prepare again";
        foreach (string use in new[] { "stage", "publish", "load", "loaded" })
        {
            Assert.That(Swap(use, "cold-" + use + ".jac"), Is.EqualTo(refused), "cold " + use);
        }

        foreach (string use in new[] { "load", "loaded" })
        {
            string source = "warm-" + use + ".jac";
            fake.Runtime.Prepare(new[] { fake.Source(source) }, fake.Options);
            Assert.That(Swap(use, source), Is.EqualTo(refused), "warm " + use);
        }

        // Unswapped, the same root is used.
        fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options);
    }

    /// <summary>
    /// No other user may write a directory above the cache root, so none can
    /// rename the root away and put another directory at its path: a parent
    /// that its group or others may write is refused, naming it, unless it
    /// has the sticky bit (in which only an entry's owner may rename it).
    /// Links in the root's path are resolved once, so the mode of a link
    /// itself (always 0777) does not matter and its target is checked.
    /// </summary>
    [Test]
    public void ACacheParentOthersMayWriteIsRefusedUnlessItIsSticky()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        NotPrivate(parent);
        var options = new PrepareOptions { CacheDir = Path.Combine(parent, "cache") };
        string Refused(UnixFileMode mode)
        {
            File.SetUnixFileMode(parent, mode);
            return Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options))!.Message;
        }

        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        string cache = Path.Combine(parent, "cache");
        Assert.That(Refused(Owner | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute), Is.EqualTo(Unsafe(cache, parent + " (writable by others)")), "others may write");
        Assert.That(Refused(Owner | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute), Is.EqualTo(Unsafe(cache, parent + " (" + NotPrivateGroup + ")")), "the group may write");

        // A grandparent is checked as well.
        string nested = Directory.CreateDirectory(Path.Combine(parent, "nested"), Owner).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1FF);
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = Path.Combine(nested, "cache") }))!.Message,
            Is.EqualTo(Unsafe(Path.Combine(nested, "cache"), parent + " (writable by others)")));

        // Sticky and world-writable, as /tmp is: accepted.
        File.SetUnixFileMode(parent, (UnixFileMode)0x3FF);
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options).Directory, Does.StartWith(Path.Combine(parent, "cache") + "/"));
        File.SetUnixFileMode(parent, Owner | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options);

        // Through a link (mode 0777) to the parent: the real path is used.
        string link = At("link");
        File.CreateSymbolicLink(link, parent);
        PreparedProgram linked = fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = Path.Combine(link, "cache") });
        Assert.That(linked.Directory, Does.StartWith(Path.Combine(parent, "cache") + "/"));
    }

    private const string NotPrivateGroup = "writable by its group, which is not your private group";

    /// <summary>The refusal of a cache at <paramref name="cache"/> listing <paramref name="levels"/>.</summary>
    private static string Unsafe(string cache, params string[] levels) =>
        "cache directory " + cache + " could be replaced by another user through the directories above it."
        + " Make each of these writable only by you (for example chmod go-w DIR), or use a location under directories only you or root may write: "
        + string.Join("; ", levels);

    /// <summary>
    /// Answers the private-group lookups as a user "me" whose primary group
    /// <paramref name="directory"/>'s group is, named <paramref name="name"/>
    /// with <paramref name="members"/>, and the only account with it.
    /// </summary>
    private static void Accounts(string directory, string name, params string[] members) => SdkSupport.Accounts(directory, name, members);

    /// <summary>Answers the lookups so that <paramref name="directory"/>'s group is not the user's private group.</summary>
    private static void NotPrivate(string directory) => Accounts(directory, "staff");

    /// <summary>
    /// A group-writable directory above the cache is accepted when its group
    /// is the user's private group (Debian OpenSSH's rule): the directory's
    /// group is the user's primary group, the group's name is the user's
    /// name, and it lists no members or only the user. The password and
    /// group answers come from a seam; the directory is 0770.
    /// </summary>
    [Test]
    public void ACacheParentWritableByThePrivateGroupIsAccepted()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent"), (UnixFileMode)0x1F8).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1F8);
        var options = new PrepareOptions { CacheDir = Path.Combine(parent, "cache") };
        Accounts(parent, "me");
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options).Directory, Does.StartWith(Path.Combine(parent, "cache") + "/"), "no members");
        Accounts(parent, "me", "me");
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options).Directory, Does.StartWith(Path.Combine(parent, "cache") + "/"), "the user alone");

        // The private group does not excuse others' write.
        File.SetUnixFileMode(parent, (UnixFileMode)0x1FA);
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options))!.Message,
            Is.EqualTo(Unsafe(Path.Combine(parent, "cache"), parent + " (writable by others)")));
    }

    /// <summary>
    /// Any other group-writable directory above the cache is refused: a
    /// group that is not the user's primary group, a group named otherwise,
    /// a group with another member (alone or beside the user), and a user or
    /// group the databases do not know.
    /// </summary>
    [Test]
    public void ACacheParentWritableByAnyOtherGroupIsRefused()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent"), (UnixFileMode)0x1F8).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1F8);
        var options = new PrepareOptions { CacheDir = Path.Combine(parent, "cache") };
        uint group = Unix.Stat(parent, follow: false).Group;
        string expected = Unsafe(Path.Combine(parent, "cache"), parent + " (" + NotPrivateGroup + ")");
        void Refused(string what) =>
            Assert.That(Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options))!.Message, Is.EqualTo(expected), what);

        Core.UserHook = uid => new Unix.Account("me", group + 1);
        Core.GroupHook = gid => new Unix.GroupEntry("me", Array.Empty<string>());
        Refused("another primary group");
        Accounts(parent, "staff");
        Refused("a group named otherwise");
        Accounts(parent, "me", "other");
        Refused("another member");
        Accounts(parent, "me", "me", "other");
        Refused("another member beside the user");
        Core.UserHook = uid => null;
        Refused("an unknown user");
        Accounts(parent, "me");
        Core.GroupHook = gid => null;
        Refused("an unknown group");
        Assert.That(Directory.Exists(Path.Combine(parent, "cache")), Is.False, "a refused preparation made the root");
    }

    /// <summary>
    /// The group database's member list omits accounts whose primary group a
    /// group is, so the password database is enumerated as well: a 0770
    /// parent whose group is the user's private group, listing no members,
    /// is refused when another account ("attacker", another uid) has that
    /// group as its primary group, and accepted when only the user has it
    /// (other accounts with other primary groups do not matter).
    /// </summary>
    [Test]
    public void ACacheParentIsRefusedWhenAnotherAccountHasThePrivateGroupAsItsPrimaryGroup()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent")).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1F8);
        uint group = Unix.Stat(parent, follow: false).Group;
        uint other = Unix.EffectiveUser() + 1;
        string cache = Path.Combine(parent, "cache");
        var options = new PrepareOptions { CacheDir = cache };
        SdkSupport.Accounts(parent, "me", Array.Empty<string>(), new Unix.PasswdEntry("attacker", other, group));
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options))!.Message,
            Is.EqualTo(Unsafe(cache, parent + " (" + NotPrivateGroup + ")")));
        Assert.That(Directory.Exists(cache), Is.False, "a refused preparation made the root");

        SdkSupport.Accounts(parent, "me", Array.Empty<string>(), new Unix.PasswdEntry("daemon", other, group + 1));
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options).Directory, Does.StartWith(cache + "/"));
    }

    /// <summary>
    /// One preparation lists the password database at most once, though it
    /// checks a group-writable private-group parent twice when the cache
    /// root is missing (before making it and again once it is resolved); a
    /// later preparation, here on the entry made, lists it again, since the
    /// verdict is not kept after the call.
    /// </summary>
    [Test]
    public void APreparationListsThePasswordDatabaseAtMostOnce()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent")).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1F8);
        string cache = Path.Combine(parent, "cache");
        SdkSupport.Accounts(parent, "me", Array.Empty<string>());
        Func<CancellationToken, CoreCommand.Output> listing = Core.GetentHook!;
        int listed = 0;
        Core.GetentHook = token =>
        {
            listed++;
            return listing(token);
        };
        var options = new PrepareOptions { CacheDir = cache };
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options).Directory, Does.StartWith(cache + "/"));
        Assert.That(listed, Is.EqualTo(1), "a preparation that made the root");
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, options).Directory, Does.StartWith(cache + "/"));
        Assert.That(listed, Is.EqualTo(2), "a later preparation lists it again");
    }

    /// <summary>
    /// The preparation's token reaches the password database listing: a
    /// listing that sees the token cancelled stops the preparation as
    /// cancelled, and nothing is made.
    /// </summary>
    [Test]
    public void APreparationCancelledWhileThePasswordDatabaseIsListedStops()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent")).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1F8);
        string cache = Path.Combine(parent, "cache");
        SdkSupport.Accounts(parent, "me", Array.Empty<string>());
        Func<CancellationToken, CoreCommand.Output> listing = Core.GetentHook!;
        using var cancel = new CancellationTokenSource();
        var seen = new List<CancellationToken>();
        Core.GetentHook = token =>
        {
            seen.Add(token);
            cancel.Cancel();
            throw CarrierException.Aborted("command aborted");
        };
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = cache }, cancel.Token))!.Message,
            Is.EqualTo(Core.Aborted));
        Assert.That(seen, Is.EqualTo(new[] { cancel.Token }));
        Assert.That(Directory.Exists(cache), Is.False, "a cancelled preparation made the root");
        Core.GetentHook = listing;
        Assert.That(fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = cache }).Directory, Does.StartWith(cache + "/"));
    }

    /// <summary>
    /// The password database listing (<c>getent passwd</c>'s output and exit
    /// status, from a seam) is parsed strictly and fails closed: only the
    /// user with the group is accepted, and exactly the entry cap; another
    /// account with the group as its primary group, a nonzero exit, a
    /// timeout, output over the limit or cut short, a malformed line,
    /// invalid UTF-8, a name listed twice, entries past the cap, and the user
    /// missing or listed with other ids each mean "not private". A
    /// cancellation is a cancelled preparation.
    /// </summary>
    [Test]
    public void ThePasswordDatabaseListingIsParsedStrictlyAndFailsClosed()
    {
        uint me = Unix.EffectiveUser();
        const uint Group = 4242;
        string self = SdkSupport.Passwd(new[] { new Unix.PasswdEntry("me", me, Group) });
        string Others(int count) => SdkSupport.Passwd(Enumerable.Range(0, count).Select(i => new Unix.PasswdEntry("user" + i.ToString(CultureInfo.InvariantCulture), me + 1 + (uint)i, Group + 1)));
        bool Verdict(Func<CancellationToken, CoreCommand.Output> getent)
        {
            Core.GetentHook = getent;
            return Core.OnlyUserHasPrimaryGroup("me", me, Group, CancellationToken.None);
        }

        bool Listing(string stdout) => Verdict(SdkSupport.Getent(stdout));

        Assert.That(Listing(self), Is.True, "only the user");
        Assert.That(Listing(self + Others(Core.PasswdEntries - 1)), Is.True, "exactly the cap");
        Assert.That(Listing(self + "alias:x:" + me.ToString(CultureInfo.InvariantCulture) + ":4242::/:/bin/sh\n"), Is.True, "another name for the same user id");
        Assert.That(Listing(self + "attacker:x:" + (me + 1).ToString(CultureInfo.InvariantCulture) + ":4242::/home/attacker:/bin/sh\n"), Is.False, "another account with the group as its primary group");
        Assert.That(Listing(self + Others(Core.PasswdEntries)), Is.False, "one past the cap");
        Assert.That(Verdict(SdkSupport.Getent(self, status: 2)), Is.False, "a nonzero exit");
        Assert.That(Verdict(SdkSupport.Getent(self, overflow: true)), Is.False, "output over the limit");
        Assert.That(Verdict(_ => throw CarrierException.Timeout("command did not exit in time")), Is.False, "a timeout");
        Assert.That(Verdict(_ => throw CarrierException.Loss("could not start getent")), Is.False, "a start failure");
        Assert.That(Listing(self + "daemon:x:1:1:daemon:/usr/sbin"), Is.False, "output cut short within a line");
        Assert.That(Listing(self[..^1]), Is.False, "output cut short before the newline");
        Assert.That(Listing(string.Empty), Is.False, "no output");
        Assert.That(Listing(self + "\n"), Is.False, "an empty line");
        Assert.That(Listing(self + "daemon:x:1:1:daemon:/usr/sbin\n"), Is.False, "six fields");
        Assert.That(Listing(self + "daemon:x:1:1:daemon:/usr/sbin:/bin/sh:extra\n"), Is.False, "eight fields");
        Assert.That(Listing(self + ":x:1:1:daemon:/usr/sbin:/bin/sh\n"), Is.False, "an empty name");
        foreach (string id in new[] { string.Empty, "-1", "+1", "01", "1a", " 1", "4294967296", "0x1" })
        {
            Assert.That(Listing(self + "daemon:x:" + id + ":1::/:/bin/sh\n"), Is.False, "user id \"" + id + "\"");
            Assert.That(Listing(self + "daemon:x:1:" + id + "::/:/bin/sh\n"), Is.False, "group id \"" + id + "\"");
        }

        Assert.That(Listing(self + "daemon:x:0:0::/:/bin/sh\n"), Is.True, "id zero");
        byte[] invalid = Encoding.UTF8.GetBytes(self + "da?mon:x:1:1::/:/bin/sh\n");
        invalid[Array.IndexOf(invalid, (byte)'?')] = 0xFF;
        Assert.That(Verdict(_ => new CoreCommand.Output(0, invalid, false, Array.Empty<byte>())), Is.False, "invalid UTF-8");
        Assert.That(Listing(self + self), Is.False, "the user listed twice");
        Assert.That(Listing(self + "daemon:x:1:1::/:/bin/sh\ndaemon:x:2:2::/:/bin/sh\n"), Is.False, "another name listed twice");
        Assert.That(Listing(Others(3)), Is.False, "the user is missing");
        Assert.That(Listing(SdkSupport.Passwd(new[] { new Unix.PasswdEntry("me", me, Group + 1) })), Is.False, "the user with another primary group");
        Assert.That(Listing(SdkSupport.Passwd(new[] { new Unix.PasswdEntry("me", me + 1, Group) })), Is.False, "the user's name with another user id");

        var cancel = new CancellationTokenSource();
        CancellationToken seen = default;
        Core.GetentHook = token =>
        {
            seen = token;
            cancel.Cancel();
            throw CarrierException.Aborted("command aborted");
        };
        Assert.That(Support.Throws<JacquardException>(() => Core.OnlyUserHasPrimaryGroup("me", me, Group, cancel.Token))!.Message, Is.EqualTo(Core.Aborted));
        Assert.That(seen, Is.EqualTo(cancel.Token), "getent runs with the caller's token");
    }

    /// <summary>
    /// <c>getent</c> runs through the bounded command runner: a real command
    /// in its place (<c>/bin/sh</c>, root's) that prints a valid listing but
    /// exits 1 is refused, one that does not exit within the time limit
    /// (here 100 ms) is refused, and one whose token is cancelled once it has
    /// started stops the preparation as cancelled. No command it started is
    /// left running.
    /// </summary>
    [Test]
    [Platform("Linux")]
    public void ThePasswordDatabaseListingIsBoundedAndStoppedByTheToken()
    {
        uint me = Unix.EffectiveUser();
        const uint Group = 4242;
        string self = "me:x:" + me.ToString(CultureInfo.InvariantCulture) + ":4242::/:/bin/sh";
        Core.GetentPaths = new[] { "/bin/sh" };
        Core.GetentArguments = new[] { "-c", "echo " + self + "; exit 0" };
        Assert.That(Core.OnlyUserHasPrimaryGroup("me", me, Group, CancellationToken.None), Is.True, "the stand-in's listing");
        Core.GetentArguments = new[] { "-c", "echo " + self + "; exit 1" };
        Assert.That(Core.OnlyUserHasPrimaryGroup("me", me, Group, CancellationToken.None), Is.False, "a nonzero exit");

        var started = new List<int>();
        CoreCommand.StartedHook = started.Add;
        try
        {
            Core.GetentArguments = new[] { "-c", "echo " + self + "; exec sleep 30" };
            Core.GetentTimeoutCap = 100;
            Assert.That(Core.OnlyUserHasPrimaryGroup("me", me, Group, CancellationToken.None), Is.False, "a timeout");
            Core.GetentTimeoutCap = Core.GetentTimeoutMs;
            var cancel = new CancellationTokenSource();
            CoreCommand.StartedHook = pid =>
            {
                started.Add(pid);
                cancel.Cancel();
            };
            Assert.That(Support.Throws<JacquardException>(() => Core.OnlyUserHasPrimaryGroup("me", me, Group, cancel.Token))!.Message, Is.EqualTo(Core.Aborted));
        }
        finally
        {
            CoreCommand.StartedHook = null;
        }

        Assert.That(started, Has.Count.EqualTo(2));
        foreach (int pid in started)
        {
            Assert.That(Directory.Exists("/proc/" + pid.ToString(CultureInfo.InvariantCulture)), Is.False, "the stopped command " + pid.ToString(CultureInfo.InvariantCulture) + " is gone");
        }
    }

    /// <summary>
    /// <c>getent</c> is the first of the configured paths present, and it
    /// must be root's and not writable by group or others: a missing path is
    /// passed over, an executable this user owns (which would print a valid
    /// listing) is refused without falling through to a later path, and
    /// getent is refused when its file (not a regular file) or a directory
    /// above it is another user's or writable by group or others (statuses
    /// altered through a seam).
    /// </summary>
    [Test]
    [Platform("Linux")]
    public void TheGetentExecutableMustBeRootsAndNotWritable()
    {
        uint me = Unix.EffectiveUser();
        Unix.Account account = Unix.User(me) ?? throw new AssertionException("no passwd entry for this user");
        string missing = Path.Combine(scratch, "missing");
        string mine = Path.Combine(scratch, "getent");
        File.WriteAllText(mine, "#!/bin/sh\necho '" + account.Name + ":x:" + me.ToString(CultureInfo.InvariantCulture) + ":" + account.Group.ToString(CultureInfo.InvariantCulture) + "::/:/bin/sh'\n");
        File.SetUnixFileMode(mine, (UnixFileMode)0x1ED);
        Core.GetentPaths = new[] { missing };
        Assert.That(Core.OnlyUserHasPrimaryGroup(account.Name, me, account.Group, CancellationToken.None), Is.False, "no getent");
        Core.GetentPaths = new[] { missing, mine, "/usr/bin/getent" };
        Assert.That(Core.OnlyUserHasPrimaryGroup(account.Name, me, account.Group, CancellationToken.None), Is.False, "a getent this user owns");
        Core.GetentPaths = new[] { missing, "/usr/bin/getent" };
        Assert.That(Core.OnlyUserHasPrimaryGroup(account.Name, me, account.Group, CancellationToken.None), Is.EqualTo(ExpectedVerdict(account.Name, me, account.Group)), "the first present is used");

        // The real getent's file or a directory above it, as another user's or writable by group or others.
        string getent = Unix.RealPath("/usr/bin/getent");
        bool Altered(string path, Func<Unix.Status, Unix.Status> change)
        {
            Core.GetentPaths = new[] { "/usr/bin/getent" };
            Core.GetentStatHook = p => p == path ? change(Unix.Stat(p, follow: false)) : Unix.Stat(p, follow: false);
            try
            {
                return Core.OnlyUserHasPrimaryGroup(account.Name, me, account.Group, CancellationToken.None);
            }
            finally
            {
                Core.GetentStatHook = null;
            }
        }

        Assert.That(Altered(getent, s => s), Is.EqualTo(ExpectedVerdict(account.Name, me, account.Group)), "unaltered");
        Assert.That(Altered(getent, s => s with { Owner = me == 0 ? 1u : me }), Is.False, "getent owned by another user");
        Assert.That(Altered(getent, s => s with { Mode = s.Mode | 0x10 }), Is.False, "getent writable by its group");
        Assert.That(Altered(getent, s => s with { Mode = s.Mode | 0x2 }), Is.False, "getent writable by others");
        Assert.That(Altered(getent, s => s with { Kind = Unix.Kind.Other }), Is.False, "getent not a regular file");
        Assert.That(Altered(Path.GetDirectoryName(getent)!, s => s with { Mode = s.Mode | 0x10 }), Is.False, "its directory writable by its group");
        Assert.That(Altered("/", s => s with { Owner = me == 0 ? 1u : me }), Is.False, "the root directory owned by another user");

        Assert.That(Core.RootOnly(new Unix.Status(Unix.Kind.Regular, 0, 0, 0x1ED)), Is.True, "root's 0755");
        Assert.That(Core.RootOnly(new Unix.Status(Unix.Kind.Regular, 0, me == 0 ? 1u : me, 0x1ED)), Is.False, "another owner");
        Assert.That(Core.RootOnly(new Unix.Status(Unix.Kind.Regular, 0, 0, 0x1FD)), Is.False, "group-writable");
        Assert.That(Core.RootOnly(new Unix.Status(Unix.Kind.Regular, 0, 0, 0x1EF)), Is.False, "writable by others");
    }

    /// <summary>
    /// Whether <c>/etc/passwd</c> lists <paramref name="name"/> with the ids
    /// and no account of another user id with that primary group: what the
    /// listing should conclude on a machine whose other password database
    /// sources add no account with it.
    /// </summary>
    private static bool ExpectedVerdict(string name, uint uid, uint gid)
    {
        bool listed = false;
        foreach (string line in File.ReadLines("/etc/passwd"))
        {
            string[] fields = line.Split(':');
            if (fields.Length != 7)
            {
                continue;
            }

            uint entryUid = uint.Parse(fields[2], CultureInfo.InvariantCulture);
            uint entryGid = uint.Parse(fields[3], CultureInfo.InvariantCulture);
            if (fields[0] == name)
            {
                listed = entryUid == uid && entryGid == gid;
            }
            else if (entryGid == gid && entryUid != uid)
            {
                return false;
            }
        }

        return listed;
    }

    /// <summary>
    /// The real <c>getent passwd</c> on this machine lists this user with its
    /// primary group, and the verdict is what <c>/etc/passwd</c> shows (on
    /// this machine, and a standard CI runner, accepted). Another
    /// enumeration of the password database in this process, here a full
    /// <c>setpwent</c>/<c>getpwent</c>/<c>endpwent</c> pass run while
    /// <c>getent</c> runs, cannot hide an entry, as it could while the SDK
    /// shared libc's process-global position: an account whose primary
    /// group another user id shares is still refused.
    /// </summary>
    [Test]
    [Platform("Linux")]
    public void ThePasswordDatabaseListingOfThisMachineIsReadInAnotherProcess()
    {
        uint me = Unix.EffectiveUser();
        Unix.Account account = Unix.User(me) ?? throw new AssertionException("no passwd entry for this user");
        Assert.That(ExpectedVerdict(account.Name, me, account.Group), Is.True, "this user's group is private by /etc/passwd");
        int consumed = 0;
        CoreCommand.StartedHook = _ => consumed += SdkSupport.Libc.ReadWholePasswordDatabase();
        try
        {
            Assert.That(Core.OnlyUserHasPrimaryGroup(account.Name, me, account.Group, CancellationToken.None), Is.True, "this user");
            string[][] file = File.ReadLines("/etc/passwd").Select(l => l.Split(':')).Where(f => f.Length == 7).ToArray();
            string[]? shared = file.FirstOrDefault(f => file.Any(o => o[3] == f[3] && o[2] != f[2]));
            if (shared is not null)
            {
                Assert.That(
                    Core.OnlyUserHasPrimaryGroup(shared[0], uint.Parse(shared[2], CultureInfo.InvariantCulture), uint.Parse(shared[3], CultureInfo.InvariantCulture), CancellationToken.None),
                    Is.False,
                    shared[0] + " shares its primary group " + shared[3]);
            }

            Assert.That(consumed, Is.GreaterThan(0), "the in-process enumeration ran");
        }
        finally
        {
            CoreCommand.StartedHook = null;
        }
    }

    /// <summary>
    /// The account records libc fills are read only within the buffer the
    /// SDK gave it: a name, a member array, a member slot, or a member name
    /// outside it, or a string without its NUL inside it, is a lookup error
    /// (null), never read through. Here the pointers are crafted; the
    /// outside memory is another valid array, so reading it would succeed.
    /// </summary>
    [Test]
    public unsafe void TheAccountRecordsAreReadOnlyWithinTheirBuffer()
    {
        byte[] buffer = new byte[64];
        byte[] outside = Encoding.ASCII.GetBytes("outside\0\0\0\0\0\0\0\0\0");
        byte[] entry = new byte[256];
        fixed (byte* data = buffer)
        fixed (byte* far = outside)
        fixed (byte* record = entry)
        {
            // "me\0" at 0, "you\0" at 8, the member array { 8, null } at 16.
            Encoding.ASCII.GetBytes("me\0").CopyTo(buffer, 0);
            Encoding.ASCII.GetBytes("you\0").CopyTo(buffer, 8);
            *(IntPtr*)(record + 0) = (IntPtr)(data);
            *(IntPtr*)(record + 24) = (IntPtr)(data + 16);
            *(IntPtr*)(data + 16) = (IntPtr)(data + 8);
            *(IntPtr*)(data + 24) = IntPtr.Zero;
            *(uint*)(record + 16) = 7;
            *(uint*)(record + 20) = 9;
            Assert.That(Unix.ReadAccount(record, data, buffer.Length), Is.EqualTo(new Unix.PasswdEntry("me", 7, 9)));
            Unix.GroupEntry group = Unix.ReadGroup(record, data, buffer.Length)!;
            Assert.That((group.Name, string.Join(",", group.Members)), Is.EqualTo(("me", "you")));

            *(IntPtr*)(record + 0) = (IntPtr)(far);
            Assert.That(Unix.ReadAccount(record, data, buffer.Length), Is.Null, "a name outside the buffer");
            Assert.That(Unix.ReadGroup(record, data, buffer.Length), Is.Null, "a group name outside the buffer");
            *(IntPtr*)(record + 0) = (IntPtr)(data);
            *(IntPtr*)(data + 16) = (IntPtr)(far);
            Assert.That(Unix.ReadGroup(record, data, buffer.Length), Is.Null, "a member name outside the buffer");
            *(IntPtr*)(data + 16) = (IntPtr)(data + 8);
            *(IntPtr*)(record + 24) = (IntPtr)(far);
            Assert.That(Unix.ReadGroup(record, data, buffer.Length), Is.Null, "a member array outside the buffer");

            // A member array whose terminating slot would lie past the end.
            *(IntPtr*)(record + 24) = (IntPtr)(data + buffer.Length - 8);
            *(IntPtr*)(data + buffer.Length - 8) = (IntPtr)(data + 8);
            Assert.That(Unix.ReadGroup(record, data, buffer.Length), Is.Null, "a member array running past the buffer");

            // A name with no NUL before the end of the buffer.
            new Span<byte>(data + 40, 16).Fill((byte)'x');
            *(IntPtr*)(record + 0) = (IntPtr)(data + 40);
            *(IntPtr*)(record + 24) = IntPtr.Zero;
            Assert.That(Unix.ReadAccount(record, data, 56), Is.Null, "a name without its NUL in the buffer");
        }
    }

    /// <summary>
    /// An account, group, or member name that is not valid UTF-8 is a lookup
    /// error (null), as distinct malformed names would otherwise decode to
    /// the same replacement text and compare equal.
    /// </summary>
    [Test]
    public unsafe void AccountNamesThatAreNotUtf8AreLookupErrors()
    {
        byte[] buffer = new byte[64];
        byte[] entry = new byte[256];
        fixed (byte* data = buffer)
        fixed (byte* record = entry)
        {
            // "\xFFm\0" at 0, "me\0" at 4, "\xC3\0" (a cut sequence) at 8, the member array { 8, null } at 16.
            buffer[0] = 0xFF;
            buffer[1] = (byte)'m';
            Encoding.ASCII.GetBytes("me\0").CopyTo(buffer, 4);
            buffer[8] = 0xC3;
            *(IntPtr*)(record + 0) = (IntPtr)data;
            *(IntPtr*)(record + 24) = IntPtr.Zero;
            Assert.That(Unix.ReadAccount(record, data, buffer.Length), Is.Null, "an account name that is not UTF-8");
            Assert.That(Unix.ReadGroup(record, data, buffer.Length), Is.Null, "a group name that is not UTF-8");
            *(IntPtr*)(record + 0) = (IntPtr)(data + 4);
            Assert.That(Unix.ReadAccount(record, data, buffer.Length)?.Name, Is.EqualTo("me"), "a valid name");
            *(IntPtr*)(record + 24) = (IntPtr)(data + 16);
            *(IntPtr*)(data + 16) = (IntPtr)(data + 8);
            *(IntPtr*)(data + 24) = IntPtr.Zero;
            Assert.That(Unix.ReadGroup(record, data, buffer.Length), Is.Null, "a member name that is not UTF-8");
        }
    }

    /// <summary>
    /// The scratch directories the SDK tests use are made where no group or
    /// other user may write any directory above them, so a group-writable
    /// TMPDIR (here 0775) does not add levels to the exact refusals the
    /// tests expect: <c>/tmp</c> is used instead.
    /// </summary>
    [Test]
    public void TheScratchDirectoriesAvoidAGroupWritableTemporaryDirectory()
    {
        string shared = Directory.CreateDirectory(At("tmpdir")).FullName;
        File.SetUnixFileMode(shared, (UnixFileMode)0x1FD);
        string? saved = Environment.GetEnvironmentVariable("TMPDIR");
        string probe;
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", shared);
            Assert.That(Path.GetTempPath(), Does.StartWith(shared));
            probe = SdkSupport.Scratch("probe");
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", saved);
        }

        try
        {
            Assert.That(probe, Does.Not.StartWith(shared));
            Assert.That(SdkSupport.SafeAncestry(probe), Is.True, probe);
            Assert.That(File.GetUnixFileMode(probe), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
        }
        finally
        {
            SdkSupport.RemoveScratch(probe);
        }
    }

    /// <summary>
    /// A directory writable by the private group is still refused when it
    /// has an access ACL: on Linux the group bits show the ACL's mask, and
    /// an entry may grant another user write (here <c>nobody</c>, set with
    /// <c>setfacl</c>).
    /// </summary>
    [Test]
    public void APrivateGroupParentWithAnAccessListIsRefused()
    {
        if (!File.Exists("/usr/bin/setfacl"))
        {
            Assert.Ignore("setfacl is not installed");
        }

        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent")).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1C0);
        using (var setfacl = System.Diagnostics.Process.Start("/usr/bin/setfacl", new[] { "-m", "u:nobody:rwx", parent }))
        {
            Assert.That(setfacl.WaitForExit(30_000), Is.True);
            Assert.That(setfacl.ExitCode, Is.EqualTo(0), "setfacl");
        }

        Assert.That(File.GetUnixFileMode(parent), Is.EqualTo((UnixFileMode)0x1F8), "the mask shows in the group bits");
        Accounts(parent, "me");
        string cache = Path.Combine(parent, "cache");
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = cache }))!.Message,
            Is.EqualTo(Unsafe(cache, parent + " (writable by its group, with an access control list)")));
    }

    /// <summary>
    /// The real password and group databases answer for this user: its name
    /// is the one .NET reports, and its primary group has an entry.
    /// </summary>
    [Test]
    public void TheAccountLookupsReadTheRealDatabases()
    {
        Unix.Account account = Unix.User(Unix.EffectiveUser()) ?? throw new AssertionException("no passwd entry for this user");
        Assert.That(account.Name, Is.EqualTo(Environment.UserName));
        Unix.GroupEntry group = Unix.GroupOf(account.Group) ?? throw new AssertionException("no group entry for the primary group");
        Assert.That(group.Name, Is.Not.Empty);
        Assert.That(group.Members.Count, Is.LessThan(Unix.AccountBytes));
    }

    /// <summary>
    /// A refusal names every unsafe directory above the cache, nearest
    /// first, with its reason, not only the first found; beyond 16 it counts
    /// the rest.
    /// </summary>
    [Test]
    public void ARefusalListsEveryUnsafeDirectoryAboveTheCache()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string a = Directory.CreateDirectory(At("a")).FullName;
        string b = Directory.CreateDirectory(Path.Combine(a, "b")).FullName;
        string c = Directory.CreateDirectory(Path.Combine(b, "c")).FullName;
        File.SetUnixFileMode(a, (UnixFileMode)0x1FF);
        File.SetUnixFileMode(b, (UnixFileMode)0x1F8);
        File.SetUnixFileMode(c, (UnixFileMode)0x1C0);
        NotPrivate(b);
        string cache = Path.Combine(c, "cache");
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = cache }))!.Message,
            Is.EqualTo(Unsafe(cache, b + " (" + NotPrivateGroup + ")", a + " (writable by others)")));

        // Eighteen unsafe levels: sixteen are named and two counted.
        string deep = Directory.CreateDirectory(At("deep"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
        var levels = new List<string>();
        for (int i = 0; i < 18; i++)
        {
            deep = Directory.CreateDirectory(Path.Combine(deep, "d")).FullName;
            levels.Add(deep);
        }

        foreach (string level in levels)
        {
            File.SetUnixFileMode(level, (UnixFileMode)0x1FF);
        }

        string deepCache = Path.Combine(deep, "cache");
        levels.Reverse();
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = deepCache }))!.Message,
            Is.EqualTo(Unsafe(deepCache, levels.Take(16).Select(level => level + " (writable by others)").ToArray()) + "; and 2 more"));
    }

    /// <summary>
    /// The directories that exist above a missing cache root are checked
    /// before any missing level is made, so a refused preparation leaves
    /// nothing behind.
    /// </summary>
    [Test]
    public void ARefusedPreparationMakesNoDirectory()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string parent = Directory.CreateDirectory(At("parent")).FullName;
        File.SetUnixFileMode(parent, (UnixFileMode)0x1FF);
        string cache = Path.Combine(parent, "x", "y", "cache");
        Assert.That(
            Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = cache }))!.Message,
            Is.EqualTo(Unsafe(cache, parent + " (writable by others)")));
        Assert.That(Directory.EnumerateFileSystemEntries(parent), Is.Empty);
    }

    /// <summary>
    /// The directories a preparation makes for a missing cache root are each
    /// owner-only (0700), whatever the umask, so a cache under new
    /// directories is not refused for parents the SDK itself made writable.
    /// </summary>
    [Test]
    public void TheDirectoriesMadeForACacheRootAreOwnerOnly()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string top = At("made");
        string cache = Path.Combine(top, "a", "b", "cache");
        fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, new PrepareOptions { CacheDir = cache });
        foreach (string level in new[] { top, Path.Combine(top, "a"), Path.Combine(top, "a", "b"), cache })
        {
            Assert.That(File.GetUnixFileMode(level), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), level);
        }
    }

    /// <summary>
    /// A prepared program's workers run on its store by path, so the cache
    /// root's device and inode are checked again before each worker starts.
    /// After preparation the root is renamed away and back (the same
    /// directory: calls still start a worker), then renamed away and
    /// replaced by a new owner-only directory, as another user who could
    /// write a parent could: the next call is refused before any worker
    /// starts, for a function bound before the swap and one bound after it.
    /// </summary>
    [Test]
    public void ACacheRootReplacedAfterPreparationIsRefusedAtTheNextCall()
    {
        using FakeCore fake = FakeCore.Programs(At("root"));
        string cache = Path.GetFullPath(fake.Options.CacheDir!);
        PreparedProgram program = fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options);
        Function before = program.Function("first");
        long Workers() => File.ReadAllLines(Path.Combine(fake.Root, "log")).Length;

        long started = Workers();
        Assert.That(before.Invoke().Ok, Is.False, "the fake worker answers nothing");
        Assert.That(Workers(), Is.EqualTo(started + 1), "a worker started");

        Directory.Move(cache, cache + "-away");
        Directory.Move(cache + "-away", cache);
        before.Invoke();
        Assert.That(Workers(), Is.EqualTo(started + 2), "the same root renamed back is used");

        Directory.Move(cache, cache + "-away");
        Directory.CreateDirectory(cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string refused = "cache root " + cache + " was replaced after it was checked; prepare again";
        Assert.That(Support.Throws<JacquardException>(() => before.Invoke())!.Message, Is.EqualTo(refused));
        Assert.That(Support.Throws<JacquardException>(() => program.Function("second").Invoke())!.Message, Is.EqualTo(refused));
        Assert.That(Workers(), Is.EqualTo(started + 2), "no worker started from the replacement");
    }

    /// <summary>
    /// The executable Core runs must be the one fingerprinted: a test seam
    /// replaces it (a new file renamed over it) after each kind of Core
    /// command, and the preparation is refused instead of keying artifacts
    /// made by other bytes under the old fingerprint.
    /// </summary>
    [Test]
    public void AnExecutableReplacedDuringPreparationIsRefused()
    {
        using FakeCore fake = FakeCore.Programs(At("swap"));
        string script = File.ReadAllText(fake.Binary);
        foreach (string after in new[] { "--version", "check", "hash", "run" })
        {
            bool replaced = false;
            Runtime.CommandHook = command =>
            {
                if (command == after && !replaced)
                {
                    replaced = true;
                    Replace(fake.Binary, script + "# replaced after " + after + "\n");
                }
            };
            string message = fake.PrepareFails("rules" + after.Trim('-') + ".jac").Message;
            Assert.That(replaced, Is.True, after + " never ran");
            Assert.That(message, Is.EqualTo(ChangedMessage("Core executable", fake.Binary)), "after " + after);
            Assert.That(
                Directory.Exists(Path.Combine(fake.Root, "cache")) ? fake.CacheEntries() : new List<string>(),
                Is.Empty,
                "an entry was published or a staging directory left after " + after);
        }

        Runtime.CommandHook = null;
        fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options);
    }

    /// <summary>
    /// The prelude Core reads must be the one fingerprinted: a prelude file
    /// rewritten in place, or a file added to the prelude directory, while
    /// the preparation runs is refused.
    /// </summary>
    [Test]
    public void APreludeChangedDuringPreparationIsRefused()
    {
        using FakeCore fake = FakeCore.Programs(At("prelude-swap"));
        string prelude = Path.Combine(fake.Root, "prelude");
        string file = Path.Combine(prelude, "01-prim.jqd");
        Runtime.CommandHook = command =>
        {
            if (command == "check")
            {
                File.AppendAllText(file, "changed\n");
            }
        };
        Assert.That(fake.PrepareFails("first.jac").Message, Is.EqualTo(ChangedMessage("prelude entry", file)));
        Runtime.CommandHook = command =>
        {
            if (command == "run" && !File.Exists(Path.Combine(prelude, "added.jqd")))
            {
                File.WriteAllText(Path.Combine(prelude, "added.jqd"), "added\n");
            }
        };
        Assert.That(fake.PrepareFails("second.jac").Message, Is.EqualTo(ChangedMessage("prelude", prelude)));
        Runtime.CommandHook = null;
        fake.Runtime.Prepare(new[] { fake.Source("third.jac") }, fake.Options);
    }

    /// <summary>
    /// The version check runs once per executable fingerprint, which
    /// includes the file's identity: the same bytes with a new modification
    /// time (as a replaced file restored would have) are checked again.
    /// </summary>
    [Test]
    public void TheVersionCheckIsKeyedOnTheExecutablesIdentity()
    {
        using FakeCore fake = FakeCore.Programs(At("version-key"));
        fake.Runtime.Prepare(new[] { fake.Source("one.jac") }, fake.Options);
        fake.Runtime.Prepare(new[] { fake.Source("two.jac") }, fake.Options);
        Assert.That(fake.Calls("--version"), Is.EqualTo(1));
        File.SetLastWriteTimeUtc(fake.Binary, File.GetLastWriteTimeUtc(fake.Binary).AddSeconds(-10));
        fake.Runtime.Prepare(new[] { fake.Source("three.jac") }, fake.Options);
        Assert.That(fake.Calls("--version"), Is.EqualTo(2));
    }

    /// <summary>
    /// The executable must be one this user may run (<c>access(X_OK)</c>),
    /// not merely one with some execute bit: a file only its group and
    /// others may execute is refused with a clear message before it runs.
    /// </summary>
    [Test]
    public void AnExecutableTheUserMayNotRunIsRefused()
    {
        Assume.That(Unix.EffectiveUser(), Is.Not.EqualTo(0u), "running as root");
        using FakeCore fake = FakeCore.Programs(At("noexec"));
        File.SetUnixFileMode(fake.Binary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        Assert.That(
            fake.PrepareFails().Message,
            Is.EqualTo("Core executable " + fake.Binary + " may not be executed by the current user; give it execute permission or configure JACQUARD_BIN"));
        Assert.That(fake.Calls("--version"), Is.EqualTo(0));
    }

    /// <summary>A source list whose count, or else whose indexer, throws <c>Error</c>.</summary>
    private sealed class ThrowingSources(bool count) : IReadOnlyList<string>
    {
        internal static readonly InvalidOperationException Error = new("list failed");

        public int Count => count ? throw Error : 1;

        public string this[int index] => throw Error;

        public IEnumerator<string> GetEnumerator() => throw Error;

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A bindings dictionary whose count, or else whose enumerator, throws <see cref="ThrowingSources.Error"/>.</summary>
    private sealed class ThrowingBindings(bool count) : IReadOnlyDictionary<string, Operations>
    {
        public int Count => count ? throw ThrowingSources.Error : 1;

        public IEnumerable<string> Keys => throw new NotSupportedException();

        public IEnumerable<Operations> Values => throw new NotSupportedException();

        public Operations this[string key] => throw new NotSupportedException();

        public bool ContainsKey(string key) => throw new NotSupportedException();

        public bool TryGetValue(string key, out Operations value) => throw new NotSupportedException();

        public IEnumerator<KeyValuePair<string, Operations>> GetEnumerator() => throw ThrowingSources.Error;

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// An exception from the caller's source list (its count or an element)
    /// or bindings dictionary (its count or enumerator) is wrapped in the
    /// SDK's exception, keeping the cause, as other caller code is.
    /// </summary>
    [Test]
    public void ACallersFailingSourceListOrBindingsIsWrappedWithTheCause()
    {
        using FakeCore fake = FakeCore.Effects(At("throwing"));
        foreach (bool count in new[] { true, false })
        {
            JacquardException sources = Support.Throws<JacquardException>(() => fake.Runtime.Prepare(new ThrowingSources(count), fake.Options))!;
            Assert.That(sources.Kind, Is.EqualTo(JacquardErrorKind.Preparation));
            Assert.That(sources.Message, Is.EqualTo("reading the source list failed: System.InvalidOperationException: list failed"));
            Assert.That(sources.InnerException, Is.SameAs(ThrowingSources.Error));
        }

        PreparedProgram program = fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options);
        foreach (bool count in new[] { true, false })
        {
            JacquardException bindings = Support.Throws<JacquardException>(() => program.Function("first", new ThrowingBindings(count)))!;
            Assert.That(bindings.Kind, Is.EqualTo(JacquardErrorKind.Binding));
            Assert.That(bindings.Message, Is.EqualTo("first: reading the bindings failed: System.InvalidOperationException: list failed"));
            Assert.That(bindings.InnerException, Is.SameAs(ThrowingSources.Error));
        }
    }

    /// <summary>A bindings dictionary that records whether the program's lock was held while it was read.</summary>
    private sealed class Watching : IReadOnlyDictionary<string, Operations>
    {
        private readonly PreparedProgram program;
        private readonly Operations service;

        internal Watching(PreparedProgram program, Operations service)
        {
            this.program = program;
            this.service = service;
        }

        internal bool? LockHeld { get; private set; }

        public int Count => 1;

        public IEnumerable<string> Keys => throw new NotSupportedException();

        public IEnumerable<Operations> Values => throw new NotSupportedException();

        public Operations this[string key] => throw new NotSupportedException();

        public bool ContainsKey(string key) => throw new NotSupportedException();

        public bool TryGetValue(string key, out Operations value) => throw new NotSupportedException();

        public IEnumerator<KeyValuePair<string, Operations>> GetEnumerator()
        {
            LockHeld = program.LockHeld;
            yield return new("Inventory", service);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>The caller's bindings dictionary is read before the program's lock is taken, so its code cannot hold the lock.</summary>
    [Test]
    public void TheCallersBindingsAreReadOutsideTheProgramsLock()
    {
        using FakeCore fake = FakeCore.Effects(At("bindings"));
        PreparedProgram program = fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options);
        var bindings = new Watching(program, new Operations().On("lookup", Codecs.Int, () => 1L));
        Assert.That(program.Function("first", bindings).Name, Is.EqualTo("first"));
        Assert.That(bindings.LockHeld, Is.False);
    }

    /// <summary>
    /// A function, effect, or operation name over 4,096 chars is refused
    /// before it is looked up, quoting an excerpt; one of exactly 4,096
    /// chars is looked up (and names nothing).
    /// </summary>
    [Test]
    public void OverlongProgramNamesAreRefusedBeforeTheyAreLookedUp()
    {
        using FakeCore fake = FakeCore.Effects(At("names"));
        PreparedProgram program = fake.Runtime.Prepare(new[] { fake.Source("rules.jac") }, fake.Options);
        string huge = new('X', 1_000_000);
        Operations service = new Operations().On("lookup", Codecs.Int, () => 1L);
        Assert.That(Support.Throws<JacquardException>(() => program.Function(huge)).Message, Is.EqualTo("function name " + Excerpt('X', 1_000_000) + " exceeds 4096 chars"));
        Assert.That(Support.Throws<JacquardException>(() => program.Provide(huge, service)).Message, Is.EqualTo("effect name " + Excerpt('X', 1_000_000) + " exceeds 4096 chars"));
        Assert.That(
            Support.Throws<JacquardException>(() => program.Function("first", new Dictionary<string, Operations> { [huge] = service })).Message,
            Is.EqualTo("effect name " + Excerpt('X', 1_000_000) + " exceeds 4096 chars"));
        Assert.That(Support.Throws<JacquardException>(() => new Operations().On(huge, Codecs.Int, () => 1L)).Message, Is.EqualTo("operation name " + Excerpt('X', 1_000_000) + " exceeds 4096 chars"));
        string longest = new('x', 4096);
        Assert.That(Support.Throws<JacquardException>(() => program.Function(longest)).Message, Does.EndWith(": absent or unsupported closed v0 signature"));
        Assert.That(Support.Throws<JacquardException>(() => program.Provide(longest, service)).Message, Does.StartWith("unknown effect "));
        Assert.That(program.Function("first", new Dictionary<string, Operations> { ["Inventory"] = service }).Name, Is.EqualTo("first"));
    }
}

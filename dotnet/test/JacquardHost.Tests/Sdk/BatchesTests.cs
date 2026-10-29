using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>
/// Function preflight with synthetic identities and no Core: arguments are
/// counted before they are encoded, a caller's codec that throws is an
/// invalid argument, and in <c>MapBatches</c> a cancellation ends validation
/// between batches and after the last, there is no validation deadline, and
/// no batch's encoding is kept once it is checked. Rows go through a codec
/// that counts its encodings, so each bound is an exact count. The worker
/// command does not exist, so a call that gets past validation fails at
/// batch 0.
/// </summary>
[TestFixture]
public sealed class BatchesTests
{
    private string root = string.Empty;

    [SetUp]
    public void Prepare() => root = SdkSupport.Scratch("batches");

    [TearDown]
    public void CleanUp()
    {
        Function.ConformHook = null;
        Invocation.BeforeRenameHook = null;
        SdkSupport.RemoveScratch(root);
    }

    private Host MissingWorker() =>
        new(Path.Combine(root, "store"), new HostOptions { Carrier = new WorkerOptions { Command = new[] { Path.Combine(root, "no-such-jacquard") } } });

    /// <summary><c>ints : (List Int) -&gt;{} List Int</c> over the data fixture.</summary>
    private Function Ints()
    {
        ProgramData data = DataTests.Fixture(Limits.Hard);
        NominalType list = DataTests.Nominal(data.Name("list")!, DataTests.Nominal(data.Name("int")!));
        var signature = new CallInterface(new BoundaryType[] { list }, list, Array.Empty<Hash>());
        return new Function("ints", signature, DataTests.Numbered(99), new OperationRegistry(), MissingWorker(), data);
    }

    /// <summary>Encodes Ints, counting each encoding and running the action <paramref name="each"/> returns for the count.</summary>
    private sealed class Counting : Codec<long>
    {
        private readonly Func<int, Action?> each;

        internal Counting(Func<int, Action?> each)
        {
            this.each = each;
        }

        internal int Encodes { get; private set; }

        public override Value Encode(Encoder encoder, long value)
        {
            each(++Encodes)?.Invoke();
            return Codecs.Int.Encode(encoder, value);
        }

        public override long Decode(Decoder decoder, Value value) => Codecs.Int.Decode(decoder, value);
    }

    private static DataTests.RepeatList<long> Rows(int count) => new(1L, count);

    /// <summary>
    /// The argument count is checked from the count alone: a million
    /// arguments to a one-parameter function, or a million leading arguments
    /// to a batch, are refused before one is copied or encoded.
    /// </summary>
    [Test]
    public void TheWrongArgumentCountIsRefusedBeforeAnyArgumentIsEncoded()
    {
        var codec = new Counting(_ => null);
        Argument[] many = Enumerable.Repeat(codec.Arg(1L), 1_000_000).ToArray();
        JacquardException call = Support.Throws<JacquardException>(() => Ints().Invoke(many));
        Assert.That(call.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(call.Message, Is.EqualTo("ints: expected 1 arguments, received 1000000"));
        JacquardException batches = Support.Throws<JacquardException>(() => Ints().MapBatches(Codecs.Int, codec, Rows(10), 5, many));
        Assert.That(batches.Message, Is.EqualTo("ints: expected 1 arguments, received 1000001"));
        Assert.That(codec.Encodes, Is.EqualTo(0), "arguments were encoded before the count was checked");
    }

    /// <summary>A caller's codec that throws is a local refusal, as any other: no worker starts, and its exception is kept.</summary>
    [Test]
    public void ACodecThatThrowsIsAnInvalidArgument()
    {
        var broken = new Counting(_ => () => throw new InvalidOperationException("broken codec"));
        JacquardException error = Support.Throws<JacquardException>(() => Ints().Invoke(Codecs.List(broken).Arg(new[] { 1L })));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(error.Message, Is.EqualTo("ints.argument[0]: its codec failed: System.InvalidOperationException: broken codec"));
        Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>());
        JacquardException nothing = Support.Throws<JacquardException>(() => Ints().Invoke(new Argument[] { null! }));
        Assert.That(nothing.Message, Does.StartWith("ints.argument[0]: null has no Jacquard value"));
    }

    [Test]
    public void AnAlreadyCancelledCallEncodesNoBatch()
    {
        var codec = new Counting(_ => null);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        JacquardException error = Support.Throws<JacquardException>(() => Ints().MapBatches(Codecs.Int, codec, Rows(10_000), 10, cancellationToken: cancel.Token));
        Assert.That(codec.Encodes, Is.EqualTo(0), "rows were encoded after the stop");
        Assert.That(error.BatchIndex, Is.EqualTo(-1));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Invocation), error.Message);
        Assert.That(Support.Reason(error.Result!), Is.EqualTo("aborted"));
        Assert.That(error.Failure, Is.EqualTo(Failure.WorkerLost));
        Assert.That(error.Completed, Is.Empty);
    }

    [Test]
    public void ACancelDuringValidationStopsAtTheNextBatch()
    {
        using var cancel = new CancellationTokenSource();

        // Cancelled while batch 2 (rows 21 to 30) is being encoded.
        var codec = new Counting(n => n == 25 ? cancel.Cancel : null);
        JacquardException error = Support.Throws<JacquardException>(() => Ints().MapBatches(Codecs.Int, codec, Rows(10_000), 10, cancellationToken: cancel.Token));
        Assert.That(codec.Encodes, Is.EqualTo(30));
        Assert.That(error.BatchIndex, Is.EqualTo(-1));
        Assert.That(Support.Reason(error.Result!), Is.EqualTo("aborted"));
    }

    /// <summary>
    /// A cancellation while the last batch is validated is observed before
    /// any worker starts: the refusal is the host's aborted result at batch
    /// index -1, and no batch is encoded again to run.
    /// </summary>
    [Test]
    public void AStopDuringTheLastBatchsValidationStartsNoWorker()
    {
        using var cancel = new CancellationTokenSource();
        var codec = new Counting(n => n == 30 ? cancel.Cancel : null);
        JacquardException error = Support.Throws<JacquardException>(() => Ints().MapBatches(Codecs.Int, codec, Rows(30), 10, cancellationToken: cancel.Token));
        Assert.That(codec.Encodes, Is.EqualTo(30), "a batch was encoded again after the stop");
        Assert.That(error.BatchIndex, Is.EqualTo(-1));
        Assert.That(Support.Reason(error.Result!), Is.EqualTo("aborted"));
    }

    /// <summary>
    /// Validation has no deadline of its own (as in the other SDKs), so it
    /// does not depend on the machine's speed: every batch is validated, and
    /// the call goes on to the worker (here a missing executable, reported
    /// at batch 0), encoding batch 0 again to run it.
    /// </summary>
    [Test]
    public void EveryBatchIsValidatedAndTheFirstEncodedAgainToRun()
    {
        var codec = new Counting(_ => null);
        JacquardException error = Support.Throws<JacquardException>(() => Ints().MapBatches(Codecs.Int, codec, Rows(30), 10));
        Assert.That(error.BatchIndex, Is.EqualTo(0), error.Message);
        Assert.That(Support.Reason(error.Result!), Is.EqualTo("worker_spawn_failed"));
        Assert.That(codec.Encodes, Is.EqualTo(40));
    }

    /// <summary>
    /// Retention is structurally impossible: the validation step returns
    /// nothing, so no caller can keep a batch's encoding, and no field of a
    /// function can hold one.
    /// </summary>
    [Test]
    public void ValidationKeepsNoEncoding()
    {
        MethodInfo validate = typeof(Function).GetMethod("Validate", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.That(validate.ReturnType, Is.EqualTo(typeof(void)));
        foreach (FieldInfo field in typeof(Function).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            Assert.That(
                field.FieldType == typeof(string) || (!typeof(IEnumerable).IsAssignableFrom(field.FieldType) && !typeof(Value).IsAssignableFrom(field.FieldType)),
                Is.True,
                "a field could hold encodings: " + field);
        }
    }

    /// <summary>Invokes a one-parameter function of <paramref name="type"/> with a constant-time codec returning <paramref name="value"/>, failing the call if the type check is reached.</summary>
    private JacquardException RefusedBeforeTheTypeCheck(ProgramData data, BoundaryType type, Value value)
    {
        var function = new Function("deep", new CallInterface(new[] { type }, type, Array.Empty<Hash>()), DataTests.Numbered(98), new OperationRegistry(), MissingWorker(), data);
        bool conformed = false;
        Function.ConformHook = () =>
        {
            conformed = true;
            throw new InvalidOperationException("the type check ran");
        };
        Codec<long> constant = Codecs.Create<long>((encoder, ignored) => value, (decoder, ignored) => 0);
        JacquardException error = Support.Throws<JacquardException>(() => function.Invoke(constant.Arg(0)));
        Assert.That(conformed, Is.False, "the type check ran before the value was measured");
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument), error.Message);
        return error;
    }

    /// <summary>
    /// A constant-time custom codec returns a small shared graph standing for
    /// 2^28 leaves, and it is measured before its type is checked, under the
    /// shared budgets, so the type check never runs. As 28 levels of a
    /// two-item tuple it is refused by the node budget; as 28 levels of a
    /// two-item list it is refused by the JSON depth bound first.
    /// </summary>
    [Test]
    public void ASharedGraphFromACustomCodecIsMeasuredBeforeItsTypeIsChecked()
    {
        ProgramData data = DataTests.Fixture(Limits.Hard);
        BoundaryType tupleType = DataTests.Nominal(data.Name("int")!);
        Value tuple = new IntValue(1);
        for (int i = 0; i < 28; i++)
        {
            tupleType = new TupleType(tupleType, tupleType);
            tuple = new TupleValue(tuple, tuple);
        }

        Assert.That(RefusedBeforeTheTypeCheck(data, tupleType, tuple).Message, Does.EndWith("value exceeds max_value_nodes; use smaller independent batches"));
        Hash list = data.Name("list")!;
        Hash cons = data.ConstructorId("cons")!;
        var nil = new ConstructorValue(data.ConstructorId("nil")!, Array.Empty<Value>());
        BoundaryType listType = DataTests.Nominal(data.Name("int")!);
        Value lists = new IntValue(1);
        for (int i = 0; i < 28; i++)
        {
            listType = DataTests.Nominal(list, listType);
            lists = new ConstructorValue(cons, new[] { lists, new ConstructorValue(cons, new[] { lists, nil }) });
        }

        Assert.That(RefusedBeforeTheTypeCheck(data, listType, lists).Message, Does.EndWith("value exceeds the v0 JSON depth; use smaller independent batches"));
    }

    [Test]
    public void CompletedResultsAreKeptReadOnly()
    {
        JacquardException error = JacquardException.Batch(1, new object?[] { Unit.Value, null }, JacquardException.Invalid("later"));
        Assert.That(error.BatchIndex, Is.EqualTo(1));
        Assert.That(error.Completed, Is.EqualTo(new object?[] { Unit.Value, null }));
        Assert.That(error.Message, Is.EqualTo("batch 1: later"));
        Assert.That(((IList<object?>)error.Completed).IsReadOnly, Is.True);
        Support.Throws<NotSupportedException>(() => ((IList<object?>)error.Completed).Add(1L));
    }

    /// <summary>MapBatches needs a pure function with a final List parameter and a List result; a batch size must be positive.</summary>
    [Test]
    public void MapBatchesNeedsAListFunction()
    {
        ProgramData data = DataTests.Fixture(Limits.Hard);
        NominalType integer = DataTests.Nominal(data.Name("int")!);
        var scalar = new Function("one", new CallInterface(new BoundaryType[] { integer }, integer, Array.Empty<Hash>()), DataTests.Numbered(97), new OperationRegistry(), MissingWorker(), data);
        Assert.That(Support.Throws<JacquardException>(() => scalar.MapBatches(Codecs.Int, Codecs.Int, Rows(3), 1)).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(Support.Throws<JacquardException>(() => Ints().MapBatches(Codecs.Int, Codecs.Int, Rows(3), 0)).Message, Is.EqualTo("batch size must be positive"));
    }

    /// <summary>Rows whose count drops from 10 to 3 once <see cref="Shrink"/> is called.</summary>
    private sealed class Shrinking : IReadOnlyList<long>
    {
        private bool shrunk;

        public int Count => shrunk ? 3 : 10;

        public long this[int index] => 1L;

        internal void Shrink() => shrunk = true;

        public IEnumerator<long> GetEnumerator() => Enumerable.Repeat(1L, Count).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// The number of rows is read once, so batches come from that count; a
    /// list that shrinks while the batches are validated is refused before a
    /// worker starts, instead of making a batch of negative size.
    /// </summary>
    [Test]
    public void RowsThatShrinkAreRefused()
    {
        var rows = new Shrinking();
        var codec = new Counting(n => n == 1 ? (Action)rows.Shrink : null);
        JacquardException error = Support.Throws<JacquardException>(() => Ints().MapBatches(Codecs.Int, codec, rows, 4));
        Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(error.Message, Is.EqualTo("rows changed from 10 to 3 during MapBatches"));
        Assert.That(error.BatchIndex, Is.EqualTo(-1));
        Assert.That(codec.Encodes, Is.EqualTo(4));
    }

    /// <summary>
    /// Save writes a new owner-only file and renames it over the target: an
    /// existing world-writable file becomes owner-only (0600) with the
    /// record, a symbolic link at the target is replaced by the file while
    /// the link's target is left unchanged, and no temporary file is left.
    /// </summary>
    [Test]
    [Platform("Linux")]
    public void SaveReplacesTheTargetWithANewOwnerOnlyFile()
    {
        Invocation invocation = Ints().Invoke(Argument.Of(Codecs.List(Codecs.Int), new long[] { 1 }));
        Assert.That(Support.Reason(invocation.Raw), Is.EqualTo("worker_spawn_failed"));
        const UnixFileMode Open = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        string existing = Path.Combine(root, "existing.json");
        File.WriteAllText(existing, "old");
        File.SetUnixFileMode(existing, Open);
        invocation.Save(existing);
        Assert.That(File.GetUnixFileMode(existing), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.That(File.ReadAllText(existing), Does.StartWith("{").And.EndWith("}\n"));

        string victim = Path.Combine(root, "victim.txt");
        File.WriteAllText(victim, "keep");
        File.SetUnixFileMode(victim, Open);
        string link = Path.Combine(root, "link.json");
        File.CreateSymbolicLink(link, victim);
        invocation.Save(link);
        Assert.That(new FileInfo(link).LinkTarget, Is.Null, "the link was followed");
        Assert.That(File.GetUnixFileMode(link), Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
        Assert.That(File.ReadAllText(link), Is.EqualTo(File.ReadAllText(existing)));
        Assert.That(File.ReadAllText(victim), Is.EqualTo("keep"));
        Assert.That(File.GetUnixFileMode(victim), Is.EqualTo(Open));
        Assert.That(Directory.EnumerateFiles(root).Select(Path.GetFileName), Is.EquivalentTo(new[] { "existing.json", "victim.txt", "link.json" }));
    }

    /// <summary>
    /// Save refuses a target directory another user could write (0777, not
    /// sticky), as for the cache's directories, before it writes anything
    /// there; a sticky one (0777 with the sticky bit, as /tmp is) is used.
    /// </summary>
    [Test]
    [Platform("Linux")]
    public void SaveRefusesATargetDirectoryOthersMayWrite()
    {
        Invocation invocation = Ints().Invoke(Argument.Of(Codecs.List(Codecs.Int), new long[] { 1 }));
        string shared = Directory.CreateDirectory(Path.Combine(root, "shared")).FullName;
        File.SetUnixFileMode(shared, (UnixFileMode)0x1FF);
        string target = Path.Combine(shared, "record.json");
        IOException error = Support.Throws<IOException>(() => invocation.Save(target))!;
        Assert.That(error.Message, Is.EqualTo(
            "save target " + target + " could be replaced by another user through the directories above it."
            + " Make each of these writable only by you (for example chmod go-w DIR), or use a location under directories only you or root may write: "
            + shared + " (writable by others)"));
        Assert.That(Directory.EnumerateFileSystemEntries(shared), Is.Empty);

        File.SetUnixFileMode(shared, (UnixFileMode)0x3FF);
        invocation.Save(target);
        Assert.That(Directory.EnumerateFileSystemEntries(shared).Select(Path.GetFileName), Is.EqualTo(new[] { "record.json" }));
    }

    /// <summary>
    /// The temporary file is written through the one handle that created it,
    /// and the file at its path is checked to be that file (the same device
    /// and inode) just before the rename. A seam replaces the temporary path
    /// with another file before the rename, as someone who may write the
    /// directory could: the replacement is detected, it is neither renamed
    /// over the target nor removed, and nothing is saved.
    /// </summary>
    [Test]
    [Platform("Linux")]
    public void SaveDetectsATemporaryFileReplacedBeforeItsRename()
    {
        Invocation invocation = Ints().Invoke(Argument.Of(Codecs.List(Codecs.Int), new long[] { 1 }));
        string target = Path.Combine(root, "record.json");
        string? temporary = null;
        Invocation.BeforeRenameHook = path =>
        {
            temporary = path;
            File.Delete(path);
            File.WriteAllText(path, "planted");
        };
        IOException error = Support.Throws<IOException>(() => invocation.Save(target))!;
        Assert.That(error.Message, Is.EqualTo("the temporary file for " + target + " was replaced before it was renamed; nothing was saved"));
        Assert.That(File.Exists(target), Is.False, "the replacement was installed");
        Assert.That(File.ReadAllText(temporary!), Is.EqualTo("planted"), "the replacement was removed");

        // Without the replacement, the record is saved and nothing else is left.
        File.Delete(temporary!);
        Invocation.BeforeRenameHook = null;
        invocation.Save(target);
        Assert.That(File.ReadAllText(target), Does.StartWith("{").And.EndWith("}\n"));
        Assert.That(Directory.EnumerateFiles(root).Select(Path.GetFileName), Is.EquivalentTo(new[] { "record.json" }));
    }
}

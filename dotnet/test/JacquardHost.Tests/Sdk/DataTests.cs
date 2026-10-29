using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using JacquardLang.Host.Protocol;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>Signatures, identity groups, and native values through checked types, with synthetic identities.</summary>
[TestFixture]
public sealed class DataTests
{
    internal static Hash Id(char letter) => new(new string(letter, 64));

    internal static Hash Numbered(int n) => new(n.ToString("x64", CultureInfo.InvariantCulture));

    internal static NominalType Nominal(Hash identity, params BoundaryType[] arguments) => new(identity, arguments);

    /// <summary>Data with synthetic identities for the builtins, an Incident record, an Urgency enumeration, and a Triage union.</summary>
    internal static ProgramData Fixture(Limits limits)
    {
        var names = new Dictionary<string, Hash>
        {
            ["int"] = Id('1'),
            ["real"] = Id('2'),
            ["text"] = Id('3'),
            ["bool"] = Id('4'),
            ["hash"] = Id('5'),
            ["list"] = Id('6'),
            ["option"] = Id('7'),
            ["incident"] = Id('8'),
            ["urgency"] = Id('9'),
            ["triage"] = Numbered(10),
        };
        var constructors = new Dictionary<string, Hash>
        {
            ["true"] = Id('a'),
            ["false"] = Id('b'),
            ["some"] = Id('c'),
            ["none"] = Id('d'),
            ["cons"] = Id('e'),
            ["nil"] = Id('f'),
            ["incident"] = Id('0'),
            ["page-now"] = Numbered(1),
            ["watch-only"] = Numbered(2),
            ["routed"] = Numbered(3),
            ["invalid-incident"] = Numbered(4),
        };
        var groups = new List<IReadOnlyList<Identities.Entry>>();
        foreach (string[] group in new[] { new[] { "incident", "incident" }, new[] { "urgency", "page-now", "watch-only" }, new[] { "triage", "routed", "invalid-incident" } })
        {
            var entries = new List<Identities.Entry> { new(group[0], names[group[0]]) };
            entries.AddRange(group.Skip(1).Select(member => new Identities.Entry(member, constructors[member])));
            groups.Add(entries);
        }

        return new ProgramData(names, constructors, groups, limits);
    }

    internal sealed record Incident(string Id, long Severity, string? Owner);

    internal enum Urgency
    {
        PageNow,
        WatchOnly,
    }

    internal abstract record Triage;

    internal sealed record Routed(string Id, Urgency Urgency) : Triage;

    internal sealed record InvalidIncident(string Id) : Triage;

    internal static readonly RecordCodec<Incident> IncidentCodec = Codecs.Record(
        "Incident",
        Codecs.Field("id", Codecs.Text, (Incident incident) => incident.Id),
        Codecs.Field("severity", Codecs.Int, (Incident incident) => incident.Severity),
        Codecs.Field("owner", Codecs.Nullable(Codecs.Text), (Incident incident) => incident.Owner),
        (id, severity, owner) => new Incident(id, severity, owner));

    internal static readonly UnionCodec<Triage> TriageCodec = Codecs.Union<Triage>(
        Codecs.Record("Routed", Codecs.Field("id", Codecs.Text, (Routed routed) => routed.Id), Codecs.Field("urgency", Codecs.Enum<Urgency>(), (Routed routed) => routed.Urgency), (id, urgency) => new Routed(id, urgency)),
        Codecs.Record("InvalidIncident", Codecs.Field("id", Codecs.Text, (InvalidIncident invalid) => invalid.Id), id => new InvalidIncident(id)));

    private static string Refusal(Action body) => Support.Throws<JacquardException>(body).Message;

    [Test]
    public void SignaturesAndGroups()
    {
        var names = new Dictionary<string, Hash>
        {
            ["list"] = Id('6'), ["line"] = Id('8'), ["int"] = Id('1'), ["option"] = Id('7'), ["text"] = Id('3'), ["inventory"] = Id('9'), ["dispatch"] = Id('a'),
        };
        CallInterface spec = Identities.ParseSignature("(List Line, Int) ->{Inventory, Dispatch} Option Text", names);
        Assert.That(spec.Parameters, Is.EqualTo(new BoundaryType[] { Nominal(Id('6'), Nominal(Id('8'))), Nominal(Id('1')) }));
        Assert.That(spec.Effects, Is.EqualTo(new[] { Id('9'), Id('a') }));
        Assert.That(spec.Result, Is.EqualTo(Nominal(Id('7'), Nominal(Id('3')))));
        Assert.That(Identities.ParseSignature("() ->{} ()", names).Result, Is.EqualTo(new TupleType()));
        Assert.That(Identities.ParseSignature("((Int, Text)) ->{} (Int)", names).Result, Is.EqualTo(Nominal(Id('1'))));
        Assert.That(Identities.ParseSignature("((Int, Text)) ->{} Int", names).Parameters, Is.EqualTo(new BoundaryType[] { new TupleType(Nominal(Id('1')), Nominal(Id('3'))) }));
        foreach (string text in new[] { "forall a | e. (List a) ->{| e} Bool", "(Missing) ->{} Int", "(Int) ->{} Int extra)", "(Int) ->{} Int $" })
        {
            Support.Throws<JacquardException>(() => Identities.ParseSignature(text, names), text);
        }

        string output = "0 " + Id('1').Hex + "\n0:line " + Id('1').Hex + "\n0:line " + Id('2').Hex + "\nnoise\n1:other " + Id('3').Hex + "\n";
        List<List<Identities.Entry>> groups = Identities.HashGroups(output);
        Assert.That(groups, Has.Count.EqualTo(2));
        Assert.That(groups[0][1].Hash, Is.EqualTo(Id('2')));
        Assert.That(Support.Throws<JacquardException>(() => Identities.HashGroups("nothing here")).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        var x1 = new Identities.Entry("x", Id('1'));
        Support.Throws<JacquardException>(() => Identities.CheckGroups(new List<List<Identities.Entry>> { new() { x1 }, new() { new("x", Id('2')) } }));
        Identities.CheckGroups(new List<List<Identities.Entry>> { new() { x1 }, new() { x1 } });
        foreach ((string display, string store) in new[] { ("WorkToday", "work-today"), ("PageNow", "page-now"), ("text", "text"), ("Inventory", "inventory") })
        {
            Assert.That(Identities.DisplayToStore(display), Is.EqualTo(store));
        }

        Assert.That(Identities.Normal("lookup_stock", "operation"), Is.EqualTo("lookup-stock"));
        Assert.That(Identities.DisplayToStore("\ud800X"), Is.EqualTo("\ud800X"), "a lone surrogate is not upper case");
    }

    [Test]
    public void NativeValuesRoundtripAndConformToCheckedTypes()
    {
        ProgramData data = Fixture(Limits.Hard);
        var rows = new List<Incident> { new("a", 5, null), new("b", 2, "ops") };
        BoundaryType list = Nominal(Id('6'), Nominal(Id('8')));
        Value value = data.Encode(Codecs.List(IncidentCodec), rows);
        data.Conform(value, list, "rows");
        Assert.That(data.Decode(Codecs.List(IncidentCodec), value), Is.EqualTo(rows));
        BoundaryType real = Nominal(Id('2'));
        foreach (double number in new[] { 1.0, -0.0, double.PositiveInfinity, double.NaN, BitConverter.Int64BitsToDouble(0x7ff8000000000123L) })
        {
            Value encoded = data.Encode(Codecs.Real, number);
            data.Conform(encoded, real, "real");
            Assert.That(BitConverter.DoubleToInt64Bits(data.Decode(Codecs.Real, encoded)), Is.EqualTo(BitConverter.DoubleToInt64Bits(number)));
        }

        BoundaryType integer = Nominal(Id('1'));
        Assert.That(Refusal(() => data.Encode(Codecs.Int, 1L << 62)), Is.EqualTo("value: integer is outside Core's 63-bit Int range"));
        Assert.That(Refusal(() => data.Encode(Codecs.Int, long.MinValue)), Is.EqualTo("value: integer is outside Core's 63-bit Int range"));
        Assert.That(data.Encode(Codecs.Int, Json.IntMax), Is.EqualTo(new IntValue(Json.IntMax)));
        Assert.That(data.Encode(Codecs.Int, Json.IntMin), Is.EqualTo(new IntValue(Json.IntMin)));
        Support.Throws<JacquardException>(() => data.Decode(Codecs.Int32, new IntValue(1L << 40)));
        Assert.That(data.Decode(Codecs.Int32, new IntValue(7)), Is.EqualTo(7));
        Assert.That(Refusal(() => data.Conform(data.Encode(Codecs.Text, "x"), integer, "argument[0]")), Is.EqualTo("argument[0]: expected an integer"));
        Assert.That(Refusal(() => data.Conform(data.Encode(Codecs.Int, 3L), real, "argument[0]")), Is.EqualTo("argument[0]: expected a Real (double)"));
        Assert.That(Refusal(() => data.Conform(data.Encode(Codecs.Enum<Urgency>(), Urgency.PageNow), Nominal(Id('8')), "argument[0]")), Does.Contain("expected a incident value"));
        Value texts = data.Encode(Codecs.List(Codecs.Text), new[] { "ok", "ok" });
        Assert.That(Refusal(() => data.Conform(texts, Nominal(Id('6'), integer), "argument[0]")), Is.EqualTo("argument[0][0]: expected an integer"));
        var unit = new TupleType();
        data.Conform(data.Encode(Codecs.Unit, Unit.Value), unit, "unit");
        Assert.That(data.Decode(Codecs.Unit, Value.Unit), Is.EqualTo(Unit.Value));
        Support.Throws<JacquardException>(() => data.Conform(data.Encode(Codecs.Tuple(Codecs.Int, Codecs.Int), (1L, 2L)), unit, "unit"));

        // A record with an unknown constructor is refused at its location.
        Assert.That(Refusal(() => data.Encode(Codecs.Record("Unknown", Codecs.Field("field", Codecs.Int, (long x) => x), x => x), 1L)), Is.EqualTo("value: unknown constructor Unknown"));

        // A record over a builtin constructor is refused, as Dynamic is.
        Assert.That(Refusal(() => data.Encode(Codecs.Record("Some", Codecs.Field("inner", Codecs.Int, (long x) => x), x => x), 1L)), Does.Contain("native form of builtin constructor Some"));

        // null is no value; Option and Unit are.
        Assert.That(Refusal(() => data.Encode(Codecs.Text, null!)), Does.StartWith("value: null has no Jacquard value"));
        Assert.That(Refusal(() => data.Encode(Codecs.Text, "\ud800")), Does.Contain("lone surrogate"));
    }

    [Test]
    public void OptionsEnumsUnionsAndDynamicValues()
    {
        ProgramData data = Fixture(Limits.Hard);
        Triage routed = new Routed("r", Urgency.PageNow);
        Value value = data.Encode(TriageCodec, routed);
        Assert.That(value, Is.EqualTo(new ConstructorValue(Numbered(3), new Value[] { new TextValue("r"), new ConstructorValue(Numbered(1), Array.Empty<Value>()) })));
        Assert.That(data.Decode(TriageCodec, value), Is.EqualTo(routed));
        Value invalid = data.Encode(TriageCodec, new InvalidIncident("x"));
        Assert.That(data.Decode(TriageCodec, invalid), Is.EqualTo(new InvalidIncident("x")));
        Assert.That(Refusal(() => data.Decode(Codecs.Enum<Urgency>(), invalid)), Does.Contain("unknown Urgency case"));
        var shortRouted = new ConstructorValue(Numbered(3), new Value[] { new TextValue("r") });
        Assert.That(Refusal(() => data.Decode(TriageCodec, shortRouted)), Does.Contain("wrong constructor or field count"));
        BoundaryType option = Nominal(Id('7'), Nominal(Id('7'), Nominal(Id('1'))));
        Codec<Option<Option<long>>> nested = Codecs.Option(Codecs.Option(Codecs.Int));
        var cases = new[] { Option<Option<long>>.None, Option.Some(Option<long>.None), Option.Some(Option.Some(3L)) };
        var dynamics = new[] { Dynamic.None(), Dynamic.Some(Dynamic.None()), Dynamic.Some(Dynamic.Some(Dynamic.Of(3L))) };
        for (int i = 0; i < cases.Length; i++)
        {
            Value encoded = data.Encode(nested, cases[i]);
            data.Conform(encoded, option, "option");
            Assert.That(data.Decode(nested, encoded), Is.EqualTo(cases[i]));
            Assert.That(data.Decode(Codecs.Dynamic, encoded), Is.EqualTo(dynamics[i]));
            Assert.That(data.Encode(Codecs.Dynamic, dynamics[i]), Is.EqualTo(encoded));
        }

        // A nullable value type maps null to None.
        Assert.That(data.Decode(Codecs.NullableValue(Codecs.Int), data.Encode(Codecs.NullableValue(Codecs.Int), null)), Is.Null);
        Assert.That(data.Decode(Codecs.NullableValue(Codecs.Int), data.Encode(Codecs.NullableValue(Codecs.Int), 4L)), Is.EqualTo(4L));
        Dynamic watch = data.Make("WatchOnly");
        Assert.That(watch, Is.EqualTo(Dynamic.VariantOf("watch-only")));
        Assert.That(data.Decode(Codecs.Enum<Urgency>(), data.Encode(Codecs.Dynamic, watch)), Is.EqualTo(Urgency.WatchOnly));
        Assert.That(Support.Throws<JacquardException>(() => data.Make("Missing")).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Support.Throws<JacquardException>(() => data.Encode(Codecs.Dynamic, Dynamic.VariantOf("Some", Dynamic.Of(1L))), "a builtin constructor by name");
        Dynamic dynamic = Dynamic.ListOf(Dynamic.TupleOf(Dynamic.Of(true), Dynamic.Of("x"), Dynamic.Of(2.5)), new Dynamic.HashValue(Id('5')));
        Assert.That(data.Decode(Codecs.Dynamic, data.Encode(Codecs.Dynamic, dynamic)), Is.EqualTo(dynamic));
        Assert.That(Refusal(() => data.Decode(Codecs.Dynamic, new ConstructorValue(Id('9'), Array.Empty<Value>()))), Does.Contain("unmapped constructor"));
        Unit[] units = Enumerable.Repeat(Unit.Value, 40).ToArray();
        Assert.That(Refusal(() => data.Encode(Codecs.List(Codecs.Unit), units)), Does.Contain("JSON depth"), "a list deeper than the JSON depth");
        Assert.That(data.Identity("Urgency"), Is.EqualTo(Id('9')));
        Support.Throws<JacquardException>(() => data.Identity("missing"));
    }

    [Test]
    public void ExplicitlyNamedEnumsTuplesAndUnionsAgree()
    {
        ProgramData data = Fixture(Limits.Hard);
        EnumCodec<Urgency> named = Codecs.Enum(new Dictionary<Urgency, string> { [Urgency.PageNow] = "PageNow", [Urgency.WatchOnly] = "watch-only" });
        Assert.That(data.Encode(named, Urgency.WatchOnly), Is.EqualTo(data.Encode(Codecs.Enum<Urgency>(), Urgency.WatchOnly)));
        Support.Throws<ArgumentException>(() => Codecs.Enum(new Dictionary<Urgency, string> { [Urgency.PageNow] = "PageNow" }));
        Assert.That(Refusal(() => data.Encode(Codecs.Enum<Urgency>(), (Urgency)7)), Does.Contain("Urgency has no constructor for 7"));

        // A union's cases must derive from it.
        Support.Throws<ArgumentException>(() => Codecs.Union<Triage>(IncidentCodec));
        Value tuple = data.Encode(Codecs.Tuple(Codecs.Int, Codecs.Text), (1L, "x"));
        Assert.That(tuple, Is.EqualTo(new TupleValue(new IntValue(1), new TextValue("x"))));
        Assert.That(data.Decode(Codecs.Tuple(Codecs.Int, Codecs.Text), tuple), Is.EqualTo((1L, "x")));
        Support.Throws<JacquardException>(() => data.Decode(Codecs.Tuple(Codecs.Int, Codecs.Text, Codecs.Text), tuple));

        // A custom codec built from two functions.
        Codec<string> upper = Codecs.Create<string>((encoder, text) => Codecs.Text.Encode(encoder, text.ToUpperInvariant()), (decoder, v) => Codecs.Text.Decode(decoder, v));
        Assert.That(data.Decode(upper, data.Encode(upper, "abc")), Is.EqualTo("ABC"));
    }

    /// <summary>
    /// There is no implicit conversion from <see cref="decimal"/> (or from
    /// <see cref="float"/> to <c>Int</c>): money crosses as integer minor units.
    /// </summary>
    [Test]
    public void DecimalHasNoConversion()
    {
        IEnumerable<Type> sources = typeof(Argument).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name is "op_Implicit" or "op_Explicit")
            .Select(method => method.GetParameters()[0].ParameterType);
        Assert.That(sources, Does.Not.Contain(typeof(decimal)));
        Assert.That(typeof(Dynamic).GetMethods().Where(method => method.Name == "Of").Select(method => method.GetParameters()[0].ParameterType), Does.Not.Contain(typeof(decimal)));
        Assert.That(typeof(Codecs).GetProperties().Select(property => property.PropertyType), Does.Not.Contain(typeof(Codec<decimal>)));
    }

    /// <summary>A recursive type maps through a lazy codec, which is made once even when many threads ask at once.</summary>
    internal sealed record Tree(string Label, IReadOnlyList<Tree> Children)
    {
        public bool Equals(Tree? other) => other is not null && Label == other.Label && Children.SequenceEqual(other.Children);

        public override int GetHashCode() => Label.GetHashCode(StringComparison.Ordinal);
    }

    [Test]
    public void RecursiveTypesMapThroughALazyCodec()
    {
        var names = new Dictionary<string, Hash> { ["list"] = Id('6'), ["text"] = Id('3') };
        var constructors = new Dictionary<string, Hash> { ["cons"] = Id('e'), ["nil"] = Id('f'), ["tree"] = Id('0') };
        var data = new ProgramData(names, constructors, Array.Empty<IReadOnlyList<Identities.Entry>>(), Limits.Hard);
        int made = 0;
        RecordCodec<Tree>? codec = null;
        LazyCodec<Tree> self = Codecs.Lazy<Tree>(() =>
        {
            Interlocked.Increment(ref made);
            return codec!;
        });
        codec = Codecs.Record(
            "Tree",
            Codecs.Field("label", Codecs.Text, (Tree tree) => tree.Label),
            Codecs.Field("children", Codecs.List(self), (Tree tree) => tree.Children),
            (label, children) => new Tree(label, children));
        var tree = new Tree("root", new[] { new Tree("leaf", Array.Empty<Tree>()), new Tree("branch", new[] { new Tree("leaf", Array.Empty<Tree>()) }) });
        Parallel.For(0, 8, _ => Assert.That(data.Decode(self, data.Encode(self, tree)), Is.EqualTo(tree)));
        Assert.That(made, Is.EqualTo(1), "the lazy codec was made more than once");

        // A chain of a million lazy codecs resolves in a loop, and one standing for itself is refused.
        LazyCodec<long> chain = Codecs.Lazy(() => Codecs.Int);
        for (int i = 0; i < 1_000_000; i++)
        {
            LazyCodec<long> inner = chain;
            chain = Codecs.Lazy<long>(() => inner);
        }

        Assert.That(data.Decode(chain, data.Encode(chain, 5L)), Is.EqualTo(5L));
        LazyCodec<long>? loop = null;
        loop = Codecs.Lazy<long>(() => loop!);
        JacquardException refused = Support.Throws<JacquardException>(() => data.Encode(loop, 1L));
        Assert.That(refused.Message, Does.Contain("a lazy codec stands for itself"));
        Assert.That(refused.InnerException, Is.InstanceOf<InvalidOperationException>());
    }

    [Test]
    public void ArgumentsConvertFromPlainValuesOrCarryTheirCodec()
    {
        ProgramData data = Fixture(Limits.Hard);
        Value Encode(Argument argument) => argument.Encode(Encoder.Root(data, "f.argument[0]"));
        Assert.That(Encode(35), Is.EqualTo(data.Encode(Codecs.Int, 35L)));
        Assert.That(Encode(35L), Is.EqualTo(new IntValue(35)));
        Assert.That(Encode(2.5), Is.EqualTo(RealValue.Of(2.5)));
        Assert.That(Encode("x"), Is.EqualTo(new TextValue("x")));
        Assert.That(Encode(true), Is.EqualTo(data.Encode(Codecs.Bool, true)));
        Assert.That(Encode(Id('5')), Is.EqualTo(new HashValue(Id('5'))));
        Assert.That(Encode(Dynamic.Of(4L)), Is.EqualTo(new IntValue(4)));
        Assert.That(Encode(Codecs.List(IncidentCodec).Arg(Array.Empty<Incident>())), Is.EqualTo(data.Encode(Codecs.List(IncidentCodec), Array.Empty<Incident>())));
        Assert.That(Refusal(() => Encode(long.MaxValue)), Is.EqualTo("f.argument[0]: integer is outside Core's 63-bit Int range"));
        Assert.That(Refusal(() => Encode((string)null!)), Does.StartWith("f.argument[0]: null has no Jacquard value"));
    }

    [Test]
    public void SelectedLimitsBoundMappedValues()
    {
        Limits limits = Limits.Hard.With("max_collection_items", 2).With("max_text_bytes", 3);
        ProgramData data = Fixture(limits);
        Assert.That(Refusal(() => data.Encode(Codecs.List(Codecs.Int), new[] { 1L, 2L, 3L })), Does.Contain("max_collection_items"));
        Assert.That(Refusal(() => data.Encode(Codecs.Text, "four")), Does.Contain("max_text_bytes"));
        Value longList = Fixture(Limits.Hard).Encode(Codecs.List(Codecs.Int), new[] { 1L, 2L, 3L });
        Assert.That(Refusal(() => data.Decode(Codecs.List(Codecs.Int), longList)), Does.Contain("selected limit"));

        // A record's fields count against max_collection_items too, so this one needs three.
        ProgramData fields = Fixture(Limits.Hard.With("max_collection_items", 3).With("max_text_bytes", 3));
        Assert.That(Refusal(() => fields.Encode(IncidentCodec, new Incident("abcd", 1, null))), Is.EqualTo("value.id: text exceeds max_text_bytes"));
        Assert.That(Refusal(() => data.Encode(IncidentCodec, new Incident("abc", 1, null))), Is.EqualTo("value: constructor exceeds max_collection_items"));
    }

    /// <summary>
    /// A small .NET value can denote a huge one: three levels of 64
    /// references to one tuple are 64^3 integers. Encoding stops at the
    /// 4097th node, so it costs no more than one frame; a raw value is
    /// refused before its walk stacks more than the node limit, and a deep
    /// one is refused by its depth without recursion.
    /// </summary>
    [Test]
    public void SharedValuesAreBoundedByTheNodeBudget()
    {
        ProgramData data = Fixture(Limits.Hard);
        var leaf = new Dynamic.Tuple(Enumerable.Repeat(Dynamic.Of(1L), 64));
        var middle = new Dynamic.Tuple(Enumerable.Repeat<Dynamic>(leaf, 64));
        var top = new Dynamic.Tuple(Enumerable.Repeat<Dynamic>(middle, 64));

        // Sixty-three tuples of 65 nodes each, then the first integer of the next.
        Assert.That(Refusal(() => data.Encode(Codecs.Dynamic, top)), Is.EqualTo("value[0][63][1]: value exceeds max_value_nodes; use smaller independent batches"));
        var rawLeaf = new TupleValue(Enumerable.Repeat<Value>(new IntValue(1), 64));
        var rawTop = new TupleValue(Enumerable.Repeat<Value>(new TupleValue(Enumerable.Repeat<Value>(rawLeaf, 64)), 64));
        Assert.That(Refusal(() => data.Encode(Codecs.Value, rawTop)), Is.EqualTo("value: value exceeds max_value_nodes; use smaller independent batches"));

        // Decoding a supplied value is measured first, within the same bounds.
        Assert.That(Refusal(() => data.Decode(Codecs.Dynamic, rawTop)), Is.EqualTo("value exceeds max_value_nodes"));
        Value deep = new IntValue(1);
        for (int i = 0; i < 100_000; i++)
        {
            deep = new TupleValue(deep);
        }

        Assert.That(Refusal(() => data.Decode(Codecs.Dynamic, deep)), Is.EqualTo("value exceeds the v0 JSON depth"));
        Assert.That(Refusal(() => data.Encode(Codecs.Value, deep)), Is.EqualTo("value: value exceeds the v0 JSON depth; use smaller independent batches"));
    }

    /// <summary>
    /// Texts are charged against the frame as they are encoded: four
    /// references to one maximal text fill it, and the fifth is refused
    /// before it is scanned.
    /// </summary>
    [Test]
    public void RepeatedTextsAreChargedAgainstTheFrame()
    {
        ProgramData data = Fixture(Limits.Hard);
        Dynamic text = Dynamic.Of(new string('x', Limits.Hard.MaxTextBytes));
        var many = new Dynamic.Tuple(Enumerable.Repeat(text, 1000));
        Assert.That(Refusal(() => data.Encode(Codecs.Dynamic, many)), Is.EqualTo("value[4]: value exceeds max_frame_bytes; use smaller independent batches"));
        var rawText = new TextValue(new string('x', Limits.Hard.MaxTextBytes));
        var raw = new TupleValue(Enumerable.Repeat<Value>(rawText, 1000));
        Assert.That(Refusal(() => data.Encode(Codecs.Value, raw)), Is.EqualTo("value: value exceeds max_frame_bytes; use smaller independent batches"));
    }

    /// <summary>A text too long in chars is refused before it is scanned: the lone surrogate at its end is never reached.</summary>
    [Test]
    public void AnOverlongTextIsRefusedBeforeItIsScanned()
    {
        ProgramData data = Fixture(Limits.Hard);
        string text = new string('x', Limits.Hard.MaxTextBytes) + "\ud800";
        Assert.That(Refusal(() => data.Encode(Codecs.Text, text)), Is.EqualTo("value: text exceeds max_text_bytes"));
    }

    /// <summary>A raw text is charged by its UTF-8 bytes: three two-byte chars exceed a four-byte limit.</summary>
    [Test]
    public void ARawTextIsChargedByItsUtf8Bytes()
    {
        ProgramData data = Fixture(Limits.Hard.With("max_text_bytes", 4));
        Assert.That(data.Encode(Codecs.Value, new TextValue("éé")), Is.EqualTo(new TextValue("éé")));
        Assert.That(Refusal(() => data.Encode(Codecs.Value, new TextValue("ééé"))), Is.EqualTo("value: text exceeds max_text_bytes"));
        Assert.That(Refusal(() => data.Encode(Codecs.Value, new TextValue("\ud800"))), Is.EqualTo("value: text contains a lone surrogate, which has no Jacquard encoding"));
    }

    /// <summary>A list whose count is over the limit, and whose items throw when read.</summary>
    private sealed class HugeList : IReadOnlyList<long>
    {
        public int Count => 1 << 30;

        public long this[int index] => throw new InvalidOperationException("element " + index.ToString(CultureInfo.InvariantCulture) + " was read");

        public IEnumerator<long> GetEnumerator() => throw new InvalidOperationException("the list was enumerated");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A list over the collection limit is refused from its count, before any element is read.</summary>
    [Test]
    public void AnOversizedListIsRefusedBeforeItsElementsAreRead()
    {
        ProgramData data = Fixture(Limits.Hard);
        Assert.That(Refusal(() => data.Encode(Codecs.List(Codecs.Int), new HugeList())), Is.EqualTo("value: list exceeds max_collection_items"));
    }

    private sealed class Counting : Codec<long>
    {
        internal int Encodes { get; private set; }

        public override Value Encode(Encoder encoder, long value)
        {
            Encodes++;
            return Codecs.Int.Encode(encoder, value);
        }

        public override long Decode(Decoder decoder, Value value) => Codecs.Int.Decode(decoder, value);
    }

    /// <summary>Tuples and records over the collection limit are refused before any item is encoded or field read.</summary>
    [Test]
    public void OversizedTuplesAndRecordsAreRefusedBeforeTheirItemsAreRead()
    {
        ProgramData data = Fixture(Limits.Hard.With("max_collection_items", 2));
        var counted = new Counting();
        Assert.That(Refusal(() => data.Encode(Codecs.Tuple(counted, counted, counted), (1L, 2L, 3L))), Is.EqualTo("value: tuple exceeds max_collection_items"));
        Assert.That(counted.Encodes, Is.EqualTo(0));
        int reads = 0;
        RecordCodec<(long, long, long)> record = Codecs.Record(
            "Incident",
            Codecs.Field("a", Codecs.Int, ((long A, long B, long C) r) => ++reads + r.A),
            Codecs.Field("b", Codecs.Int, ((long A, long B, long C) r) => ++reads + r.B),
            Codecs.Field("c", Codecs.Int, ((long A, long B, long C) r) => ++reads + r.C),
            (a, b, c) => (a, b, c));
        Assert.That(Refusal(() => data.Encode(record, (1L, 2L, 3L))), Is.EqualTo("value: constructor exceeds max_collection_items"));
        Assert.That(reads, Is.EqualTo(0));
        Fixture(Limits.Hard.With("max_collection_items", 3)).Encode(record, (1L, 2L, 3L));
        Assert.That(reads, Is.EqualTo(3));
    }

    /// <summary>A codec building a constructor by name has its fields counted before they are copied.</summary>
    [Test]
    public void AConstructorWithTooManyFieldsIsRefusedBeforeItIsBuilt()
    {
        ProgramData data = Fixture(Limits.Hard);
        Value one = data.Encode(Codecs.Int, 1L);
        Codec<long> wide = Codecs.Create<long>((encoder, count) => encoder.Constructor("routed", new RepeatList<Value>(one, (int)count)), (decoder, value) => 0);
        Assert.That(Refusal(() => data.Encode(wide, 20_000_000L)), Is.EqualTo("value: constructor exceeds max_collection_items"));
        Assert.That(Refusal(() => data.Encode(wide, Limits.Hard.MaxCollectionItems + 1L)), Is.EqualTo("value: constructor exceeds max_collection_items"));
    }

    /// <summary>A list of one value repeated, that holds no items.</summary>
    internal sealed class RepeatList<T> : IReadOnlyList<T>
    {
        private readonly T item;

        internal RepeatList(T item, int count)
        {
            this.item = item;
            Count = count;
        }

        public int Count { get; }

        public T this[int index] => item;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A Dynamic value over the hard limit is refused at construction, before its items are copied.</summary>
    [Test]
    public void AnOversizedDynamicIsRefusedBeforeItIsCopied()
    {
        int limit = Limits.Hard.MaxCollectionItems;
        var many = new RepeatList<Dynamic>(Dynamic.Of(1L), limit + 1);
        Support.Throws<ArgumentException>(() => _ = new Dynamic.List(many));
        Support.Throws<ArgumentException>(() => _ = new Dynamic.Tuple(many));
        Support.Throws<ArgumentException>(() => _ = new Dynamic.Variant("routed", many));

        // An enumeration that does not say its count is read at most one past the limit.
        int read = 0;
        IEnumerable<Dynamic> endless = Enumerable.Range(0, int.MaxValue).Select(_ =>
        {
            read++;
            return Dynamic.Of(1L);
        });
        Support.Throws<ArgumentException>(() => _ = new Dynamic.List(endless));
        Assert.That(read, Is.EqualTo(limit + 1));
        Assert.That(Support.Throws<JacquardException>(() => Fixture(Limits.Hard).Make("routed", many.ToArray())).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(new Dynamic.List(new RepeatList<Dynamic>(Dynamic.Of(1L), limit)).Items, Has.Count.EqualTo(limit));
    }

    /// <summary>A Dynamic value's equality and text walk it without recursion, so a deep caller-built value cannot exhaust the stack.</summary>
    [Test]
    public void DeepDynamicValuesCompareAndPrintWithoutRecursion()
    {
        Dynamic Deep()
        {
            Dynamic value = Dynamic.Of(1L);
            for (int i = 0; i < 200_000; i++)
            {
                value = Dynamic.Some(value);
            }

            return value;
        }

        Assert.That(Deep().Equals(Deep()), Is.True);
        Assert.That(Deep().ToString(), Has.Length.LessThanOrEqualTo(4100));
        Assert.That(Dynamic.ListOf(Dynamic.Of(1L), Dynamic.Of(2.5)).ToString(), Is.EqualTo("List(Int(1), Real(2.5))"));
        Assert.That(Dynamic.VariantOf("Job", Dynamic.Of("a")).ToString(), Is.EqualTo("Variant(Job, Text(a))"));
        Assert.That(Dynamic.Of(double.NaN), Is.EqualTo(Dynamic.Of(double.NaN)));
        Assert.That(Dynamic.Of(0.0), Is.Not.EqualTo(Dynamic.Of(-0.0)));

        // Encoding a deep value is refused by the depth limit before it recurses past it.
        Assert.That(Refusal(() => Fixture(Limits.Hard).Encode(Codecs.Dynamic, Deep())), Does.Contain("JSON depth"));
    }

    /// <summary>
    /// A caller's name over 4,096 chars is refused before it is looked up,
    /// hashed, or converted, quoting its first 64 chars and its length: a
    /// public name, a constructor by name (dynamic, made, or a record's),
    /// and an operation. A name of exactly 4,096 chars is looked up, and
    /// names nothing.
    /// </summary>
    [Test]
    public void AnOverlongNameIsRefusedBeforeItIsLookedUp()
    {
        string huge = new('X', 1_000_000);
        string excerpt = new string('X', 64) + "... (1000000 chars)";
        ProgramData data = Fixture(Limits.Hard);
        Assert.That(Refusal(() => Identities.DisplayToStore(huge)), Is.EqualTo("a name " + excerpt + " exceeds 4096 chars"));
        Assert.That(Refusal(() => data.Identity(huge)), Is.EqualTo("public name " + excerpt + " exceeds 4096 chars"));
        Assert.That(Refusal(() => data.Make(huge)), Is.EqualTo("constructor name " + excerpt + " exceeds 4096 chars"));
        JacquardException dynamic = Support.Throws<JacquardException>(() => data.Encode(Codecs.Dynamic, Dynamic.VariantOf(huge)));
        Assert.That(dynamic.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(dynamic.Message, Is.EqualTo("value: constructor name " + excerpt + " exceeds 4096 chars"));
        Assert.That(Refusal(() => data.Encode(Codecs.Record(huge, () => 1L), 1L)), Is.EqualTo("value: constructor name " + excerpt + " exceeds 4096 chars"));
        Assert.That(Refusal(() => data.Decode(Codecs.Record(huge, () => 1L), data.Encode(Codecs.Unit, Unit.Value))), Does.Contain("constructor name " + excerpt + " exceeds 4096 chars"));
        Assert.That(Refusal(() => new Operations().On(huge, Codecs.Int, () => 1L)), Is.EqualTo("operation name " + excerpt + " exceeds 4096 chars"));
        string longest = new('X', 4096);
        Assert.That(Refusal(() => data.Encode(Codecs.Dynamic, Dynamic.VariantOf(longest))), Does.StartWith("value: unknown constructor "));
        Assert.That(Identities.DisplayToStore("JobTicket"), Is.EqualTo("job-ticket"));
    }

    /// <summary>
    /// Messages quote caller text as an excerpt, and no message is kept
    /// past 8,192 chars: a result codec naming a huge constructor, codecs,
    /// record readers, and constructors that throw huge messages each give a
    /// short refusal that keeps the caller's exception.
    /// </summary>
    [Test]
    public void MessagesQuoteCallerTextAsAnExcerpt()
    {
        string huge = new('X', 1_000_000);
        string excerpt = new string('X', 64) + "... (1000000 chars)";
        ProgramData data = Fixture(Limits.Hard);
        Value unit = data.Encode(Codecs.Unit, Unit.Value);
        JacquardException decode = Support.Throws<JacquardException>(() => data.Decode(Codecs.Record(huge, () => 1L), unit));
        Assert.That(decode.Message, Has.Length.LessThan(200));
        Assert.That(decode.Message, Does.Contain("... (1000000 chars)"));
        Codec<long> throwing = Codecs.Create<long>((encoder, value) => throw new InvalidOperationException(huge), (decoder, value) => throw new InvalidOperationException(huge));
        JacquardException encode = Support.Throws<JacquardException>(() => data.Encode(throwing, 1L));
        Assert.That(encode.Kind, Is.EqualTo(JacquardErrorKind.InvalidArgument));
        Assert.That(encode.Message, Is.EqualTo("value: its codec failed: System.InvalidOperationException: " + excerpt));
        Assert.That(encode.InnerException, Is.InstanceOf<InvalidOperationException>(), "the codec's exception was not kept");
        JacquardException decoded = Support.Throws<JacquardException>(() => data.Decode(throwing, data.Encode(Codecs.Int, 1L)));
        Assert.That(decoded.Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(decoded.Message, Is.EqualTo("value: its codec failed: System.InvalidOperationException: " + excerpt));
        Assert.That(decoded.InnerException, Is.InstanceOf<InvalidOperationException>());

        // A record constructor that refuses its fields, and a reader that throws.
        RecordCodec<Incident> refusing = Codecs.Record(
            "Incident",
            Codecs.Field("id", Codecs.Text, (Incident incident) => incident.Id),
            Codecs.Field("severity", Codecs.Int, (Incident incident) => throw new ArgumentException(huge)),
            Codecs.Field("owner", Codecs.Nullable(Codecs.Text), (Incident incident) => incident.Owner),
            (Func<string, long, string?, Incident>)((id, severity, owner) => throw new ArgumentException(huge)));
        Value incident = data.Encode(IncidentCodec, new Incident("ok", 1, null));
        JacquardException refused = Support.Throws<JacquardException>(() => data.Decode(refusing, incident));
        Assert.That(refused.Message, Is.EqualTo("Incident refused the decoded fields: System.ArgumentException: " + excerpt));
        Assert.That(refused.InnerException, Is.InstanceOf<ArgumentException>(), "the constructor's refusal was not kept");
        JacquardException reader = Support.Throws<JacquardException>(() => data.Encode(refusing, new Incident("ok", 1, null)));
        Assert.That(reader.Message, Is.EqualTo("value: cannot read Incident.severity: System.ArgumentException: " + excerpt));
        Assert.That(reader.InnerException, Is.InstanceOf<ArgumentException>());

        // A caller's field name in a path.
        Codec<long> named = Codecs.Create<long>((encoder, value) => encoder.Field(huge, Codecs.Text, null!), (decoder, value) => 0);
        Assert.That(Refusal(() => data.Encode(named, 1L)), Does.StartWith("value." + excerpt + ": null has no Jacquard value"));
        string bounded = JacquardException.Bounded(huge);
        Assert.That(bounded, Has.Length.EqualTo(JacquardException.MessageLimit), "the cap is exact, its suffix included");
        Assert.That(bounded, Does.EndWith("X... (1000000 chars)"));

        // A caller's message through the public helpers is bounded before it is joined to a path.
        Codec<long> invalidating = Codecs.Create<long>((encoder, value) => throw encoder.Invalid(huge), (decoder, value) => throw decoder.Mapping(huge));
        string invalid = Refusal(() => data.Encode(invalidating, 1L));
        Assert.That(invalid, Has.Length.LessThanOrEqualTo(JacquardException.MessageLimit));
        Assert.That(invalid, Does.StartWith("value: XXXX"));
        Assert.That(invalid, Does.EndWith("X... (1000007 chars)"), "joined to its path once, the whole length noted");
        string mapping = Refusal(() => data.Decode(invalidating, data.Encode(Codecs.Int, 1L)));
        Assert.That(mapping, Has.Length.EqualTo(JacquardException.MessageLimit));
        Assert.That(mapping, Does.EndWith("X... (1000000 chars)"));

        // A note larger than any string is not the SDK's, so it is not trusted.
        string forged = new string('Y', JacquardException.MessageLimit - 31) + "... (9223372036854775807 chars)";
        Assert.That(forged, Has.Length.EqualTo(JacquardException.MessageLimit));
        Assert.That(JacquardException.Joined("p: ", forged), Does.EndWith("... (" + (forged.Length + 3).ToString(CultureInfo.InvariantCulture) + " chars)"));
        Assert.That(JacquardException.Joined("p: ", null), Is.EqualTo("p: null"));

        // A cut that dropped a high surrogate at the boundary is still recognised as cut.
        string emoji = new string('X', JacquardException.MessageLimit - 20) + "😀" + huge;
        string cut = JacquardException.Bounded(emoji);
        Assert.That(cut, Has.Length.EqualTo(JacquardException.MessageLimit - 1), "the cut did not land on the surrogate");
        Assert.That(JacquardException.Joined("batch 1: ", cut), Does.EndWith("... (" + (emoji.Length + 9).ToString(CultureInfo.InvariantCulture) + " chars)"));
        string again = JacquardException.Joined("batch 1: ", mapping);
        Assert.That(again, Has.Length.EqualTo(JacquardException.MessageLimit));
        Assert.That(again, Does.StartWith("batch 1: XXX").And.EndWith("X... (1000009 chars)"));

        // A caller's exception whose Message throws is described by its type alone.
        Assert.That(Identities.Describe(new ThrowingMessage()), Is.EqualTo(typeof(ThrowingMessage).FullName));
    }

    private sealed class ThrowingMessage : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");
    }

    internal abstract record Shape;

    internal sealed record Circle : Shape;

    internal sealed record Square : Shape;

    internal sealed record One;

    /// <summary>An explicit union whose first case is a narrower union: encoding asks the flattened cases in decoding's order, so both agree.</summary>
    [Test]
    public void EncodingAndDecodingAgreeOnNestedUnions()
    {
        ProgramData data = Fixture(Limits.Hard);
        UnionCodec<Shape> circle = Codecs.Union<Shape>(Codecs.Record("routed", () => new Circle()));
        UnionCodec<Shape> outer = Codecs.Union<Shape>(circle, Codecs.Record("invalid-incident", () => new Square()));
        Assert.That(data.Decode(outer, data.Encode(outer, new Square())), Is.EqualTo(new Square()));
        Assert.That(data.Decode(outer, data.Encode(outer, new Circle())), Is.EqualTo(new Circle()));
    }

    /// <summary>
    /// Nested unions are flattened on first use by one iterative walk, so
    /// decoding asks each distinct case once, and 100,000 layers of a
    /// two-case union over one record neither overflow the stack nor take
    /// long: one distinct case, and a round trip.
    /// </summary>
    [Test]
    public void NestedUnionsFlattenWithoutRecursion()
    {
        ProgramData data = Fixture(Limits.Hard);
        UnionCodec<One> codec = Codecs.Union<One>(Codecs.Record("routed", () => new One()));
        for (int i = 0; i < 100_000; i++)
        {
            codec = Codecs.Union<One>(codec, codec);
        }

        Assert.That(codec.LeafCount, Is.EqualTo(1));
        Value notRouted = data.Encode(Codecs.Int, 1L);
        Assert.That(Support.Throws<JacquardException>(() => data.Decode(codec, notRouted)).Kind, Is.EqualTo(JacquardErrorKind.Binding));
        Assert.That(data.Decode(codec, data.Encode(codec, new One())), Is.EqualTo(new One()));
    }

    /// <summary>A recursive union, reached through a lazy case, flattens to its distinct cases and round-trips, also from many threads at once.</summary>
    internal abstract record Expr;

    internal sealed record Leaf(long Value) : Expr;

    internal sealed record Pair(Expr Left, Expr Right) : Expr;

    [Test]
    public void ARecursiveUnionThroughALazyCaseRoundTrips()
    {
        var constructors = new Dictionary<string, Hash> { ["leaf"] = Id('1'), ["pair"] = Id('2') };
        var data = new ProgramData(new Dictionary<string, Hash>(), constructors, Array.Empty<IReadOnlyList<Identities.Entry>>(), Limits.Hard);
        UnionCodec<Expr>? expr = null;
        LazyCodec<Expr> self = Codecs.Lazy<Expr>(() => expr!);
        expr = Codecs.Union<Expr>(
            Codecs.Record("Leaf", Codecs.Field("value", Codecs.Int, (Leaf leaf) => leaf.Value), value => new Leaf(value)),
            Codecs.Record("Pair", Codecs.Field("left", self, (Pair pair) => pair.Left), Codecs.Field("right", self, (Pair pair) => pair.Right), (left, right) => new Pair(left, right)),
            self);
        Expr tree = new Pair(new Leaf(1), new Pair(new Leaf(2), new Leaf(3)));
        Parallel.For(0, 8, _ => Assert.That(data.Decode(expr, data.Encode(expr, tree)), Is.EqualTo(tree)));
        Assert.That(expr.LeafCount, Is.EqualTo(2));
    }

    private static string Nested(int depth) =>
        "(" + string.Concat(Enumerable.Repeat("Option (", depth - 2)) + "Option Int" + new string(')', depth - 2) + ") ->{} Int";

    /// <summary>
    /// A signature's types may nest 64 levels, the protocol's value depth,
    /// and no deeper. Nesting is refused before the parser recurses past it,
    /// so 5,000 parentheses, and 100,000, are refused with a binding failure
    /// rather than a stack overflow.
    /// </summary>
    [Test]
    public void SignatureNestingIsBounded()
    {
        var names = new Dictionary<string, Hash> { ["int"] = Id('1'), ["option"] = Id('7') };
        Assert.That(Identities.SignatureDepth, Is.EqualTo(64));
        BoundaryType type = Identities.ParseSignature(Nested(64), names).Parameters[0];
        int depth = 1;
        while (type is NominalType { Arguments.Count: > 0 } nominal)
        {
            type = nominal.Arguments[0];
            depth++;
        }

        Assert.That(depth, Is.EqualTo(64));
        const string deep = "signature nests types deeper than 64 levels, the protocol's value-depth limit (max_json_depth)";
        foreach (string text in new[] { Nested(65), Nested(2500), "(" + new string('(', 5000) + "Int" + new string(')', 5000) + ") ->{} Int" })
        {
            JacquardException error = Support.Throws<JacquardException>(() => Identities.ParseSignature(text, names));
            Assert.That(error.Kind, Is.EqualTo(JacquardErrorKind.Binding));
            Assert.That(error.Message, Is.EqualTo(deep));
        }

        JacquardException huge = Support.Throws<JacquardException>(() =>
            Identities.ParseSignature("(" + string.Concat(Enumerable.Repeat("Option (", 100_000)) + "Int" + new string(')', 100_000) + ") ->{} Int", names));
        Assert.That(huge.Message, Is.EqualTo("signature is longer than " + Identities.SignatureTokens.ToString(CultureInfo.InvariantCulture) + " tokens"));
        string wide = "(" + string.Concat(Enumerable.Repeat("Int, ", Identities.SignatureNodes)) + "Int) ->{} Int";
        Assert.That(Support.Throws<JacquardException>(() => Identities.ParseSignature(wide, names)).Message, Is.EqualTo("signature names more than 4096 types"));
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JacquardLang.Host.Conformance;
using JacquardLang.Host.Protocol;
using NUnit.Framework;
using static JacquardLang.Host.Tests.Support;

namespace JacquardLang.Host.Tests;

/// <summary>Strict JSON, exact numbers, value and type codecs, limits, and the session state machine.</summary>
[TestFixture]
public sealed class CodecTests
{
    private static readonly Limits Hard = Limits.Hard;

    private static Budget NewBudget() => new(Hard);

    [Test]
    public void StrictJsonRefusals()
    {
        foreach (string text in new[]
        {
            "{\"a\":1,\"a\":2}",
            "{\"x\":{\"a\":1,\"a\":2}}",
            "{\"x\":1.0}",
            "{\"x\":1e0}",
            "{\"x\":1E5}",
            "{\"x\":NaN}",
            "{\"x\":Infinity}",
            "{\"x\":-Infinity}",
            "{\"x\":\"\\ud800\"}",
            "{\"x\":\"\\udc00\"}",
            "{\"x\":\"\\ud800A\"}",
            "{}{}",
            "[]",
            "{\"x\":4611686018427387904}",
            "{\"x\":-4611686018427387905}",
            "{\"x\":9223372036854775808}",
            "{\"x\":123456789012345678901}",
            "{\"x\":01}",
            "{\"x\":+1}",
            "{\"x\":.5}",
            "{\"x\":true,}",
            "{\"x\":[1,]}",
            "{\"x\":\"\u0001\"}",
            "{\"x\":\"\\q\"}",
            "{\"x\":-}",
            "{\"x\":\"\\u+abc\"}",
            "{\"x\":\"\\u\uff10\uff10\uff10\uff10\"}",
            "\ufeff{}",
        })
        {
            Code("E1601", text, () => JsonCodec.DecodeObject(Encoding.UTF8.GetBytes(text), 64));
        }

        Code("E1601", "invalid UTF-8", () => JsonCodec.DecodeObject(new byte[] { 0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d }, 64));

        // An encoded surrogate (CESU-8) and an overlong encoding are not UTF-8.
        Code("E1601", "encoded surrogate", () => JsonCodec.DecodeObject(new byte[] { 0x7b, 0x22, 0xed, 0xa0, 0x80, 0x22, 0x3a, 0x30, 0x7d }, 64));
        Code("E1601", "overlong", () => JsonCodec.DecodeObject(new byte[] { 0x7b, 0x22, 0xc0, 0xaf, 0x22, 0x3a, 0x30, 0x7d }, 64));
        JsonObject parsed = JsonCodec.DecodeObject(Encoding.UTF8.GetBytes("{\"a\":4611686018427387903,\"b\":-4611686018427387904,\"c\":\"😀é\"}"), 64);
        Assert.That(parsed["a"], Is.EqualTo(Json.Of(Json.IntMax)));
        Assert.That(parsed["b"], Is.EqualTo(Json.Of(Json.IntMin)));
        Assert.That(parsed["c"], Is.EqualTo(Json.Of("😀é")));
        Assert.That(JsonCodec.DecodeObject(Encoding.UTF8.GetBytes("{\"x\":\"\\ud83d\\ude00\"}"), 64)["x"], Is.EqualTo(Json.Of("😀")));
        Json value = Json.Object(
            ("z", Json.Of(new Json[] { Json.Null, Json.Of(true), Json.Of(-1) })),
            ("a", Json.Of("q\"\\\n\u0001😀")),
            ("__proto__", JsonObject.Empty));
        Assert.That(JsonCodec.Stringify(value, 64), Is.EqualTo("{\"__proto__\":{},\"a\":\"q\\\"\\\\\\n\\u0001😀\",\"z\":[null,true,-1]}"));
        Code("E1601", "63-bit encode", () => JsonCodec.Stringify(Json.Object(("x", Json.Of(Json.IntMax + 1))), 64));

        // Keys sort by code point, not by UTF-16 unit: U+FFFF precedes U+1F600.
        Assert.That(JsonCodec.Stringify(Json.Object(("😀", Json.Of(2)), ("\uffff", Json.Of(1))), 64), Is.EqualTo("{\"\uffff\":1,\"😀\":2}"));
        Code("E1601", "lone surrogate encode", () => JsonCodec.Stringify(Json.Of("\ud800"), 64));
        Throws<ArgumentException>(() => Json.Object(("a", Json.Of(1)), ("a", Json.Of(2))));
    }

    [Test]
    public void IntegersAreParsedExactlyFromTheirText()
    {
        // Each of these differs from its nearest double: parsing through a
        // double or a decimal would change or refuse them.
        foreach (long exact in new[] { 9_007_199_254_740_993L, 4_611_686_018_427_387_903L, -4_611_686_018_427_387_904L, 4_611_686_018_427_387_901L, 0L })
        {
            string text = "{\"x\":" + exact.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
            Assert.That(JsonCodec.DecodeObject(Encoding.UTF8.GetBytes(text), 64)["x"].AsInt(), Is.EqualTo(exact), text);
            Assert.That(JsonCodec.Stringify(JsonCodec.DecodeObject(Encoding.UTF8.GetBytes(text), 64), 64), Is.EqualTo(text));
        }

        // As in the Rust and Java adapters, "-0" is the integer 0, and a
        // token longer than 20 characters is refused before it is parsed.
        Assert.That(JsonCodec.DecodeObject(Encoding.UTF8.GetBytes("{\"x\":-0}"), 64)["x"], Is.EqualTo(Json.Of(0)));
        ProtocolException error = Code("E1601", "21 digits", () => JsonCodec.DecodeObject(Encoding.UTF8.GetBytes("{\"x\":-12345678901234567890}"), 64));
        Assert.That(error.Detail, Is.EqualTo("invalid JSON scalar"));
        error = Code("E1601", "2^62", () => JsonCodec.DecodeObject(Encoding.UTF8.GetBytes("{\"x\":4611686018427387904}"), 64));
        Assert.That(error.Detail, Is.EqualTo("JSON integer is outside the 63-bit range"));

        // Core's Int crosses as canonical decimal text and is parsed exactly too.
        foreach (long exact in new[] { 9_007_199_254_740_993L, Json.IntMax, Json.IntMin, -1L })
        {
            Json wire = Json.Object(("kind", Json.Of("int")), ("value", Json.Of(exact.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            Assert.That(Codec.DecodeValue(wire, NewBudget()), Is.EqualTo(new IntValue(exact)));
            Assert.That(Codec.EncodeValue(new IntValue(exact), NewBudget()), Is.EqualTo(wire));
        }
    }

    [Test]
    public void DepthFrameAndLimits()
    {
        Code("E1602", "decode depth", () => JsonCodec.DecodeObject(Encoding.UTF8.GetBytes("{\"a\":[[]]}"), 2));
        Code("E1602", "frame bytes", () => JsonCodec.FrameBytes(Json.Object(("a", Json.Of("xx"))), 4, 64));
        Assert.That(JsonCodec.FrameBytes(Json.Object(("a", Json.Of(1))), 64, 64), Is.EqualTo(Encoding.UTF8.GetBytes("\0\0\0\u0007{\"a\":1}")));
        Code("E1602", "zero limit", () => Hard.With("max_arguments", 0).Validate());
        Code("E1601", "extra limit", () => Limits.FromJson(Hard.ToJson().With("extra", Json.Of(1))));
        Code("E1601", "boolean limit", () => Limits.FromJson(Hard.ToJson().With("max_arguments", Json.Of(true))));
        Code("E1601", "limit beyond the exact integer range", () => Limits.FromJson(Hard.ToJson().With("max_arguments", Json.Of(1L << 60))));
        Assert.That(Limits.FromJson(Hard.ToJson()), Is.EqualTo(Hard));
    }

    [Test]
    public void ValuesRoundtripExactly()
    {
        Value[] values =
        {
            new IntValue(0),
            new IntValue(Json.IntMin),
            new IntValue(Json.IntMax),
            new TextValue("hé😀"),
            new HashValue(HashOf('a')),
            new TupleValue(new IntValue(3), new TextValue("x")),
            new ConstructorValue(HashOf('b'), new[] { new IntValue(3) }),
            new RealValue(0x7ff0_0000_0000_0001UL),
            RealValue.Of(-0.0),
            RealValue.Of(double.PositiveInfinity),
            Value.Unit,
        };
        foreach (Value value in values)
        {
            Json first = Codec.EncodeValue(value, NewBudget());
            Value decoded = Codec.DecodeValue(first, NewBudget());
            Assert.That(decoded, Is.EqualTo(value));
            Json second = Codec.EncodeValue(decoded, NewBudget());
            Assert.That(JsonCodec.Stringify(second, 64), Is.EqualTo(JsonCodec.Stringify(first, 64)));
        }

        Assert.That(RealValue.Of(-0.0), Is.EqualTo(new RealValue(0x8000_0000_0000_0000UL)));
        Assert.That(JsonCodec.Stringify(Codec.EncodeValue(RealValue.Of(-0.0), NewBudget()), 64), Is.EqualTo("{\"bits\":\"8000000000000000\",\"kind\":\"real\"}"));
        foreach (string text in new[] { "-0", "00", "+1", "4611686018427387904", "-4611686018427387905", " 1", "99999999999999999999", "1.0", "1e3", "１" })
        {
            Json wire = Json.Object(("kind", Json.Of("int")), ("value", Json.Of(text)));
            Code("E1601", text, () => Codec.DecodeValue(wire, NewBudget()));
        }

        Code("E1604", "secret", () => Codec.DecodeValue(Parse("{\"kind\":\"secret\",\"value\":\"x\"}"), NewBudget()));
        Code("E1604", "Int range", () => Codec.EncodeValue(new IntValue(Json.IntMax + 1), NewBudget()));
        Code("E1604", "Int range below", () => Codec.EncodeValue(new IntValue(long.MinValue), NewBudget()));
        Code("E1604", "null", () => Codec.EncodeValue(null, NewBudget()));
        Code("E1601", "lone surrogate Text", () => Codec.EncodeValue(new TextValue("a\udc00"), NewBudget()));
        Code("E1602", "Text bytes", () => Codec.EncodeValue(new TextValue(string.Concat(Enumerable.Repeat("é", 131_073))), NewBudget()));
        string upper = "{\"kind\":\"nominal\",\"identity\":\"" + new string('A', 64) + "\",\"arguments\":[]}";
        Code("E1601", "uppercase hash", () => Codec.DecodeType(Parse(upper), NewBudget()));
        Limits small = Hard.With("max_value_nodes", 1);
        Code("E1602", "value nodes", () => Codec.DecodeValue(Parse("{\"kind\":\"tuple\",\"items\":[{\"kind\":\"int\",\"value\":\"1\"}]}"), new Budget(small)));
        Code("E1601", "uppercase real", () => Codec.DecodeValue(Parse("{\"kind\":\"real\",\"bits\":\"7FF0000000000001\"}"), NewBudget()));
        Code("E1601", "short hash", () => Hash.Parse("A"));
        Throws<ArgumentException>(() => _ = new Hash("A"));
    }

    [Test]
    public void DecodersFollowCoresCheckOrder()
    {
        string item = "{\"kind\":\"int\",\"value\":\"1\"}";
        string many = string.Join(",", Enumerable.Repeat(item, 65));

        // Core: exact fields, one node, identity, then the count.
        string bad = "{\"kind\":\"constructor\",\"identity\":\"bad\",\"arguments\":[" + many + "]}";
        Code("E1601", "constructor identity before count", () => Codec.DecodeValue(Parse(bad), NewBudget()));
        string good = "{\"kind\":\"constructor\",\"identity\":\"" + new string('a', 64) + "\",\"arguments\":[" + many + "]}";
        Code("E1602", "constructor argument count", () => Codec.DecodeValue(Parse(good), NewBudget()));

        // A malformed descriptor is refused as malformed even when the budget is spent.
        var spent = new Budget(Hard.With("max_value_nodes", 1));
        Codec.DecodeValue(Parse(item), spent);
        string nominal = "{\"kind\":\"nominal\",\"identity\":\"" + new string('a', 64) + "\"}";
        Code("E1601", "exact fields before the node", () => Codec.DecodeType(Parse(nominal), spent));
        Code("E1604", "kind before the node", () => Codec.DecodeValue(Parse("{\"kind\":\"secret\"}"), spent));
        Code("E1601", "missing kind", () => Codec.DecodeValue(Parse("{\"value\":\"1\"}"), NewBudget()));
    }

    private static JsonObject Hello() => Json.Object(
        ("carrier", Json.Of(Compat.Carrier)),
        ("kind", Json.Of("core_hello")),
        ("limits", Hard.ToJson()),
        ("versions", Json.Of(new Json[] { Json.Of(Compat.Protocol) })));

    [Test]
    public void HelloAndSelection()
    {
        Code("E1601", "extra hello field", () => new Session().Receive(Hello().With("extra", Json.Of(1))));
        Code("E1600", "unknown version", () => new Session().Receive(Hello().With("versions", Json.Of(new Json[] { Json.Of("jacquard-host-v9") }))));
        Code("E1601", "lowered hello limits", () => new Session().Receive(Hello().With("limits", Hard.With("max_arguments", 1).ToJson())));
        Code("E1608", "first frame", () => new Session().Receive(Json.Object(("kind", Json.Of("fatal")), ("protocol", Json.Of(Compat.Protocol)))));
        var session = new Session();
        Assert.That(session.Receive(Hello()), Is.Null);
        Code("E1608", "invoke before select", () => session.Invoke(HashOf('a'), Unit(), Array.Empty<Value>(), Array.Empty<OperationEntry>()));
        Code("E1602", "select above ceiling", () => session.Select(Hard.With("max_frame_bytes", Hard.MaxFrameBytes + 1)));
    }

    [Test]
    public void InvokeFrameValidation()
    {
        Hash a = HashOf('a');
        Hash b = HashOf('b');
        var unsorted = new CallInterface(Array.Empty<BoundaryType>(), new TupleType(), new[] { b, a });
        Code("E1601", "unsorted effects", () => Codec.InvokeFrame(a, unsorted, Array.Empty<Value>(), Array.Empty<OperationEntry>(), Hard));
        var spec = new CallInterface(Array.Empty<BoundaryType>(), new TupleType(), new[] { a });
        Code("E1601", "operation outside row", () => Codec.InvokeFrame(a, spec, Array.Empty<Value>(), new[] { new OperationEntry(b, a) }, Hard));
        Code("E1601", "duplicate operation", () => Codec.InvokeFrame(a, spec, Array.Empty<Value>(), new[] { new OperationEntry(a, b), new OperationEntry(a, b) }, Hard));
        Code("E1601", "argument count", () => Codec.InvokeFrame(a, spec, new Value[] { new IntValue(1) }, Array.Empty<OperationEntry>(), Hard));
        JsonObject frame = Codec.InvokeFrame(a, spec, Array.Empty<Value>(), new[] { new OperationEntry(a, b) }, Hard);
        Assert.That(
            JsonCodec.Stringify(frame["capabilities"], 64),
            Is.EqualTo("{\"effects\":[\"" + a + "\"],\"operations\":[{\"effect\":\"" + a + "\",\"mode\":\"once\",\"operation\":\"" + b + "\"}]}"));
    }

    [Test]
    public void JsStringMatchesJavascriptCoercion()
    {
        Assert.That(Replayer.JsString(Json.Of("x")), Is.EqualTo("x"));
        Assert.That(Replayer.JsString(Json.Null), Is.EqualTo("null"));
        Assert.That(Replayer.JsString(Json.Of(5)), Is.EqualTo("5"));
        Assert.That(Replayer.JsString(Json.Of(true)), Is.EqualTo("true"));
        Assert.That(Replayer.JsString(Parse("[1,null,[\"a\",\"b\"]]")), Is.EqualTo("1,,a,b"));
        Assert.That(Replayer.JsString(Parse("{\"a\":1}")), Is.EqualTo("[object Object]"));
    }

    [Test]
    public void PinnedKitLoads()
    {
        Assert.That(Pinned.Transcripts, Has.Count.EqualTo(Kit.TranscriptCount));
        Assert.That(Pinned.Identities.ContainsKey("kit-echo"), Is.True);
        Assert.That(Kit.File("kit/fixtures.jac"), Is.Not.Null);
        Assert.That(Kit.File("kit/README.md"), Is.Not.Null);
        Assert.That(Kit.File("../NOTICE"), Is.Null);
    }

    [Test]
    public void OutcomeEvidenceAndResultShareOneFrameBudget()
    {
        JsonObject outcome = Pinned.Transcripts[0].CoreOutput[1];
        Codec.ParseCoreFrame(outcome, Hard.With("max_value_nodes", 2));
        Code("E1602", "pure outcome", () => Codec.ParseCoreFrame(outcome, Hard.With("max_value_nodes", 1)));

        // An error outcome spends its two evidence types only.
        IReadOnlyList<JsonObject> refused = Pinned.Find("refused-authority")!.CoreOutput;
        JsonObject errorOutcome = refused[^1];
        Codec.ParseCoreFrame(errorOutcome, Hard.With("max_value_nodes", 2));
        Code("E1602", "error outcome", () => Codec.ParseCoreFrame(errorOutcome, Hard.With("max_value_nodes", 1)));
    }

    [Test]
    public void SessionEnforcesOneInvocationAndLocalTerminalRefusal()
    {
        Transcript pure = Pinned.Transcripts[0];
        var session = new Session();
        session.Receive(pure.CoreOutput[0]);
        session.Select(Hard);
        session.Invoke(Pinned.Identity("kit-ready"), Unit(), Array.Empty<Value>(), Array.Empty<OperationEntry>());
        Code("E1608", "second invoke", () => session.Invoke(HashOf('a'), Unit(), Array.Empty<Value>(), Array.Empty<OperationEntry>()));
        session.Receive(pure.CoreOutput[1]);
        Code("E1608", "shutdown after terminal", () => session.Shutdown());
        Code("E1608", "frame after terminal", () => session.Receive(pure.CoreOutput[1]));
    }

    [Test]
    public void RequestOrderingAndOneResponseSlot()
    {
        Transcript transcript = Pinned.Find("one-once-effect-success")!;
        var session = new Session();
        session.Receive(transcript.CoreOutput[0]);
        session.Select(Hard);
        JsonObject invoke = transcript.HostInput.Select(step => step.Frame).First(frame => Json.Of("invoke").Equals(frame.Get("kind")));
        JsonObject wire = (JsonObject)invoke["interface"];
        var spec = new CallInterface(
            wire["parameters"].AsArray()!.Select(parameter => Codec.DecodeType(parameter, NewBudget())),
            Codec.DecodeType(wire["result"], NewBudget()),
            wire["effects"].AsArray()!.Select(effect => Hash.Parse(effect.AsString())));
        OperationEntry[] operations = ((JsonObject)invoke["capabilities"])["operations"].AsArray()!
            .Select(entry => new OperationEntry(Hash.Parse(((JsonObject)entry)["effect"].AsString()), Hash.Parse(((JsonObject)entry)["operation"].AsString())))
            .ToArray();
        Hash callable = Hash.Parse(((JsonObject)invoke["target"])["callable"].AsString());
        session.Invoke(callable, spec, new Value[] { new TextValue("ping") }, operations);
        JsonObject request = transcript.CoreOutput[1];
        Code("E1601", "numeric invocation id", () => session.Receive(request.With("invocation_id", Json.Of(0))));
        Code("E1608", "other invocation", () => session.Receive(request.With("invocation_id", Json.Of("ffffffffffffffff"))));
        Code("E1608", "skipped ordinal", () => session.Receive(request.With("request_id", Json.Of("0000000000000002"))));
        Code("E1608", "ordinal beyond the exact range", () => session.Receive(request.With("request_id", Json.Of("ffffffffffffffff"))));
        session.Receive(request);
        Code("E1608", "second request while waiting", () => session.Receive(request));
        session.RespondOk(new TextValue("pong"));
        Code("E1608", "second response", () => session.RespondOk(new TextValue("again")));
    }

    [Test]
    public void TheEncoderStopsAtTheFrameCeiling()
    {
        JsonObject frame = Json.Object(("k", Json.Of(new string('\u0001', 1000))));
        ProtocolException refused = Code("E1602", "one byte short", () => JsonCodec.FrameBytes(frame, 6007, 64));
        Assert.That(refused.Detail, Is.EqualTo("payload exceeds max_frame_bytes"));

        // {"k":"..."} is 8 bytes plus 6 per escaped U+0001: exactly 6008.
        Assert.That(JsonCodec.FrameBytes(frame, 6008, 64), Has.Length.EqualTo(4 + 6008));
        Assert.That(JsonCodec.Stringify(Json.Of("\u0001\u001f"), 64), Is.EqualTo("\"\\u0001\\u001f\""));

        // A text that cannot fit is refused by length, before the scan that
        // would find its lone surrogate.
        refused = Code("E1602", "long text", () => JsonCodec.FrameBytes(Json.Object(("k", Json.Of("\ud800" + new string('x', 7000)))), 6008, 64));
        Assert.That(refused.Detail, Is.EqualTo("payload exceeds max_frame_bytes"));
    }

    /// <summary>
    /// 256 references to one text at the text ceiling: within the node,
    /// collection, and text limits, but over 384 MiB once escaped.
    /// </summary>
    internal static TupleValue HugeResult() => new(Enumerable.Repeat<Value>(new TextValue(new string('\u0001', 262_144)), 256));

    [Test]
    public void EncodingAFrameFarOverTheCeilingStopsAtTheCeiling()
    {
        // The encoded value as JSON, built without the frame budget.
        JsonObject frame = Json.Object(("value", Json.Of(HugeResult().Items.Select(item => (Json)Json.Of(((TextValue)item).Value)))));
        long before = GC.GetAllocatedBytesForCurrentThread();
        ProtocolException refused = Code("E1602", "huge frame", () => JsonCodec.FrameBytes(frame, Hard.MaxFrameBytes, Hard.MaxJsonDepth));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.That(refused.Detail, Is.EqualTo("payload exceeds max_frame_bytes"));

        // The sink grows to at most the 1 MiB ceiling (by doubling, so about
        // 2 MiB in all); encoding everything first would allocate over 384 MiB.
        Assert.That(allocated, Is.LessThan(8L << 20), "allocated " + allocated + " bytes");
    }

    [Test]
    public void ValidatingManyReferencesToALongTextChargesTheFrame()
    {
        // 4 x 1022 references to one text of 65536 emoji (262144 UTF-8
        // bytes): within the node, collection, and text limits, and about
        // 1 GiB of text to scan if each reference were checked in full.
        // Validation charges every text against the frame and stops.
        var text = new TextValue(string.Concat(Enumerable.Repeat("😀", 65_536)));
        var value = new TupleValue(Enumerable.Repeat<Value>(new TupleValue(Enumerable.Repeat<Value>(text, 1022)), 4));
        ProtocolException refused = Code("E1602", "many references", () => Codec.EncodeValue(value, new Budget(Hard)));
        Assert.That(refused.Detail, Is.EqualTo("payload exceeds max_frame_bytes"));
    }

    [Test]
    public void ATextFarOverTheTextCeilingIsRefusedBeforeItIsScanned()
    {
        // The lone surrogate at the end would be E1601 if the text were
        // scanned; its length alone refuses it first.
        string text = new string('é', 32 << 20) + "\ud800";
        ProtocolException refused = Code("E1602", "huge text", () => Codec.EncodeValue(new TextValue(text), new Budget(Hard)));
        Assert.That(refused.Detail, Is.EqualTo("Text exceeds max_text_bytes"));
        refused = Code("E1602", "host message", () => NewSessionWaiting().RespondFailure(FailureCategory.OutsideFailure, text));
        Assert.That(refused.Detail, Is.EqualTo("host message exceeds max_host_message_bytes"));
    }

    private static Session NewSessionWaiting()
    {
        Transcript transcript = Pinned.Find("one-once-effect-success")!;
        var session = new Session();
        session.Receive(transcript.CoreOutput[0]);
        session.Select(Hard);
        session.Invoke(Pinned.Identity("kit-echo"), Echo(), new Value[] { new TextValue("ping") }, new[] { new OperationEntry(Pinned.Identity("kit-world"), Pinned.Identity("send")) });
        session.Receive(transcript.CoreOutput[1]);
        return session;
    }

    /// <summary>A caller's map that counts how far it was enumerated.</summary>
    private sealed class CountingMap : IReadOnlyDictionary<string, Json>
    {
        private readonly int count;
        private readonly int keyLength;

        internal CountingMap(int count, int keyLength)
        {
            this.count = count;
            this.keyLength = keyLength;
        }

        internal int Enumerated { get; private set; }

        public int Count => count;

        public IEnumerable<string> Keys => this.Select(field => field.Key);

        public IEnumerable<Json> Values => this.Select(field => field.Value);

        public Json this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out Json value)
        {
            value = Json.Null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, Json>> GetEnumerator()
        {
            string prefix = new('k', keyLength);
            for (int i = 0; i < count; i++)
            {
                Enumerated++;
                yield return new KeyValuePair<string, Json>(prefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Json.Null);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Test]
    public void ACallersRawMapIsBoundedBeforeItIsCopiedOrSorted()
    {
        // 60,000 keys of 2,048 characters: far more than a frame can hold.
        // The frame is refused after the keys that already pass the ceiling,
        // without copying or sorting the rest.
        var map = new CountingMap(60_000, 2048);
        ProtocolException refused = Code("E1602", "long keys", () => JsonCodec.FrameBytes(map, Hard.MaxFrameBytes, Hard.MaxJsonDepth));
        Assert.That(refused.Detail, Is.EqualTo("payload exceeds max_frame_bytes"));
        Assert.That(map.Enumerated, Is.LessThanOrEqualTo((Hard.MaxFrameBytes / 2048) + 1));

        // A count that cannot fit is refused before any key is read.
        var many = new CountingMap(Hard.MaxFrameBytes, 0);
        Code("E1602", "many keys", () => JsonCodec.FrameBytes(many, Hard.MaxFrameBytes, Hard.MaxJsonDepth));
        Assert.That(many.Enumerated, Is.EqualTo(0));

        // A small map is encoded in sorted order.
        Assert.That(
            JsonCodec.FrameBytes(new Dictionary<string, Json> { ["b"] = Json.Of(1), ["a"] = Json.Of(2) }, 64, 64),
            Is.EqualTo(Encoding.UTF8.GetBytes("\0\0\0\u000d{\"a\":2,\"b\":1}")));
    }

    [Test]
    public void BoundedMessagesCutAtACodePoint()
    {
        Assert.That(Host.Bounded("\ud800" + string.Concat(Enumerable.Repeat("é", 3000)), 4096), Is.EqualTo("\ufffd" + string.Concat(Enumerable.Repeat("é", 2046))));
        Assert.That(Host.Bounded("😀", 3), Is.Empty);
        Assert.That(Host.Bounded("a😀", 5), Is.EqualTo("a😀"));
    }
}

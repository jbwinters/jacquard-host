using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using JacquardLang.Host.Protocol;
using JacquardLang.Host.Sdk;
using NUnit.Framework;

namespace JacquardLang.Host.Tests.Sdk;

/// <summary>.NET handlers adapted to operations: decoding, results, failures, the context and its token, and the stop check before a handler starts.</summary>
[TestFixture]
public sealed class OperationsTests
{
    private readonly ProgramData data = DataTests.Fixture(Limits.Hard);

    [TearDown]
    public void ClearHook() => Operations.AfterDecodeHook = null;

    private static HandlerContext Context(Func<bool>? stopping = null, CancellationToken token = default) => new(stopping ?? (() => false), () => false, token);

    private static Value Text(string text) => new TextValue(text);

    private Value Call(Operations service, string name, HandlerContext context, params Value[] arguments) =>
        service.Snapshot(100)![name](data, context, arguments);

    private HandlerException Failure(Operations service, string name, params Value[] arguments) =>
        Support.Throws<HandlerException>(() => Call(service, name, Context(), arguments));

    [Test]
    public void HandlersAdaptToOperations()
    {
        int calls = 0;
        Operations service = new Operations()
            .On("read-document", Codecs.Text, Codecs.Text, (context, key) =>
            {
                calls++;
                if (context.IsCancelled)
                {
                    throw HandlerException.CompletionUnknown("cancelled");
                }

                return key == "example" ? "text" : throw HandlerException.Refused("unknown document");
            })
            .On("forget", Codecs.Text, Codecs.Unit, (string key) => throw new IOException("private detail"))
            .On("lookup_stock", Codecs.Text, Codecs.Int, sku => (long)sku.Length)
            .On("pair", Codecs.Tuple(Codecs.Int, Codecs.Int), () => (3L, 4L))
            .On("route", DataTests.IncidentCodec, DataTests.TriageCodec, incident => new DataTests.Routed(incident.Id, DataTests.Urgency.WatchOnly))
            .On("huge", Codecs.Int, () => long.MaxValue)
            .On("nothing", Codecs.Text, () => null!)
            .On("six", Codecs.Int, Codecs.Int, Codecs.Int, Codecs.Int, Codecs.Int, Codecs.Int, Codecs.Int, (context, a, b, c, d, e, f) => a + b + c + d + e + f);
        Assert.That(Call(service, "read-document", Context(), Text("example")), Is.EqualTo(Text("text")));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(Failure(service, "read-document", Text("other")).Category, Is.EqualTo(FailureCategory.RefusedAuthority));
        HandlerException arity = Failure(service, "read-document");
        Assert.That(arity.Category, Is.EqualTo(FailureCategory.OutsideFailure));
        Assert.That(arity.Message, Is.EqualTo("handler parameter count differs from the request"));
        Assert.That(Failure(service, "read-document", new IntValue(1)).Message, Is.EqualTo("handler argument cannot be mapped"));
        Assert.That(calls, Is.EqualTo(2), "a handler ran with unmappable arguments");

        // Any other exception reaches the host, which reports completion unknown without its message.
        Support.Throws<IOException>(() => Call(service, "forget", Context(), Text("x")));
        Assert.That(Call(service, "lookup-stock", Context(), Text("abc")), Is.EqualTo(new IntValue(3)));
        Assert.That(Call(service, "pair", Context()), Is.EqualTo(new TupleValue(new IntValue(3), new IntValue(4))));
        Value incident = data.Encode(DataTests.IncidentCodec, new DataTests.Incident("i", 1, null));
        Assert.That(data.Decode(DataTests.TriageCodec, Call(service, "route", Context(), incident)), Is.EqualTo(new DataTests.Routed("i", DataTests.Urgency.WatchOnly)));
        foreach (string unrepresentable in new[] { "huge", "nothing" })
        {
            HandlerException result = Failure(service, unrepresentable);
            Assert.That(result.Category, Is.EqualTo(FailureCategory.OutsideFailure));
            Assert.That(result.Message, Is.EqualTo("handler result cannot be represented"));
        }

        Value[] six = { new IntValue(1), new IntValue(2), new IntValue(3), new IntValue(4), new IntValue(5), new IntValue(6) };
        Assert.That(Call(service, "six", Context(), six), Is.EqualTo(new IntValue(21)));
        Operations replaced = new Operations().On("op", Codecs.Int, () => 1L).On("op", Codecs.Int, () => 2L);
        Assert.That(Call(replaced, "op", Context()), Is.EqualTo(new IntValue(2)));
        Assert.That(replaced.Count, Is.EqualTo(1));

        // A service over a limit is refused from its count, before anything is copied.
        Assert.That(service.Snapshot(3), Is.Null);
    }

    /// <summary>The handler sees the invocation's context and its token.</summary>
    [Test]
    public void HandlersSeeTheContextAndItsToken()
    {
        using var stop = new CancellationTokenSource();
        CancellationToken seen = default;
        Operations service = new Operations().On("watch", Codecs.Bool, context =>
        {
            seen = context.CancellationToken;
            return context.CancellationToken.IsCancellationRequested;
        });
        Assert.That(Call(service, "watch", Context(token: stop.Token)), Is.EqualTo(data.Encode(Codecs.Bool, false)));
        Assert.That(seen, Is.EqualTo(stop.Token));
    }

    /// <summary>
    /// Decoding a request takes time. A stop that lands after the arguments
    /// are decoded, here through the test seam, must keep the handler from
    /// starting; the handler is never called.
    /// </summary>
    [Test]
    public void NoHandlerStartsOnceTheCallStoppedDuringDecoding()
    {
        bool stopped = false;
        int calls = 0;
        Operations service = new Operations().On("act", Codecs.Text, Codecs.Unit, key =>
        {
            calls++;
            return Unit.Value;
        });
        Operations.AfterDecodeHook = () => stopped = true;
        HandlerException refused = Support.Throws<HandlerException>(() => Call(service, "act", Context(() => stopped), Text("x")));
        Assert.That(refused.Message, Is.EqualTo("handler was not started"));
        Assert.That(calls, Is.EqualTo(0), "the handler started after the call stopped");
        Operations.AfterDecodeHook = null;
        Assert.That(Call(service, "act", Context(), Text("x")), Is.InstanceOf<TupleValue>());
        Assert.That(calls, Is.EqualTo(1));
    }
}

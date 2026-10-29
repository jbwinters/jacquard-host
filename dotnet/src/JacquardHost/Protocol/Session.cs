using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JacquardLang.Host.Protocol;

/// <summary>
/// The carrier-independent v0 host state machine: one hello, one selection,
/// one invocation or shutdown, consecutive requests, and one terminal. A
/// session is used from one thread at a time.
/// </summary>
public sealed class Session
{
    private readonly List<EffectRequest> requests = new();
    private readonly List<JsonObject> responses = new();

    /// <summary>The current state.</summary>
    public State State { get; private set; } = State.Hello;

    /// <summary>The selected limits; the hard limits until selection.</summary>
    public Limits Limits { get; private set; } = Limits.Hard;

    /// <summary>The effect requests received, in order.</summary>
    public IReadOnlyList<EffectRequest> Requests => requests;

    /// <summary>The responses rendered, in order.</summary>
    public IReadOnlyList<JsonObject> Responses => responses;

    /// <summary>Core's terminal frame, once received.</summary>
    public Terminal? Terminal { get; private set; }

    /// <summary>The ordinal of the request awaiting a response, or null.</summary>
    public long? Pending { get; private set; }

    /// <summary>Validates the next Core frame. Returns null for <c>core_hello</c>.</summary>
    /// <exception cref="ProtocolException">The frozen code the frame earns in this state.</exception>
    public CoreFrame? Receive(JsonObject frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (State == State.Closed)
        {
            throw ProtocolException.Order("Core sent a frame after its terminal");
        }

        if (State == State.Hello)
        {
            Hello(frame);
            return null;
        }

        CoreFrame parsed = Codec.ParseCoreFrame(frame, Limits);
        switch (parsed)
        {
            case EffectRequest request:
                if (State != State.Running)
                {
                    throw ProtocolException.Order("effect request without an available invocation slot");
                }

                if (request.Ordinal != requests.Count + 1L)
                {
                    throw ProtocolException.Order("request ordinal is not consecutive");
                }

                if (request.Ordinal > Limits.MaxEffectRequests)
                {
                    throw ProtocolException.Exceeded("Core exceeded max_effect_requests");
                }

                requests.Add(request);
                Pending = request.Ordinal;
                State = State.Waiting;
                break;
            case Terminal end:
                bool allowed = end switch
                {
                    Outcome => State is State.Running or State.Waiting or State.Finishing,
                    Fatal => State is State.Negotiating or State.Selected or State.ShuttingDown or State.Running or State.Finishing,
                    _ => State == State.ShuttingDown,
                };
                if (!allowed)
                {
                    throw ProtocolException.Order(end switch
                    {
                        Outcome => "outcome without a live invocation",
                        Fatal => "fatal while a response is outstanding",
                        _ => "shutdown_ack without shutdown",
                    });
                }

                Pending = null;
                Terminal = end;
                State = State.Closed;
                break;
        }

        return parsed;
    }

    private void Hello(JsonObject frame)
    {
        if (!Json.Of("core_hello").Equals(frame.Get("kind")))
        {
            throw ProtocolException.Order("first Core frame must be core_hello");
        }

        Codec.Exact(frame, "carrier", "kind", "limits", "versions");
        if (!Json.Of(Compat.Carrier).Equals(frame["carrier"]))
        {
            throw ProtocolException.Malformed("unknown carrier");
        }

        IReadOnlyList<Json> versions = frame["versions"].AsArray() ?? throw ProtocolException.Malformed("expected an array");
        if (versions.Count == 0)
        {
            throw ProtocolException.Malformed("versions must be nonempty");
        }

        bool offered = false;
        foreach (Json version in versions)
        {
            string name = version.AsString() ?? throw ProtocolException.Malformed("expected a string");
            offered |= name == Compat.Protocol;
        }

        if (!Limits.FromJson(frame["limits"]).Equals(Limits.Hard))
        {
            throw ProtocolException.Malformed("hello must advertise exactly the frozen limits");
        }

        if (!offered)
        {
            throw new ProtocolException("E1600", "Core does not offer " + Compat.Protocol);
        }

        State = State.Negotiating;
    }

    private void Require(params State[] states)
    {
        if (!states.Contains(State))
        {
            throw ProtocolException.Order("message is not permitted in state " + State.WireName());
        }
    }

    /// <summary>Chooses the session limits and renders <c>host_select</c>.</summary>
    /// <exception cref="ProtocolException">E1608 out of order; E1602 for a limit out of bounds.</exception>
    public JsonObject Select(Limits selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        Require(State.Negotiating);
        selected.Validate();
        Limits = selected;
        State = State.Selected;
        return Json.Object(("kind", Json.Of("host_select")), ("limits", selected.ToJson()), ("protocol", Json.Of(Compat.Protocol)));
    }

    /// <summary>Renders the session's single invocation.</summary>
    /// <exception cref="ProtocolException">E1608 out of order; otherwise as <see cref="Codec.InvokeFrame"/>.</exception>
    public JsonObject Invoke(Hash target, CallInterface spec, IReadOnlyList<Value> args, IReadOnlyList<OperationEntry> operations)
    {
        Require(State.Selected);
        JsonObject frame = Codec.InvokeFrame(target, spec, args, operations, Limits);
        State = State.Running;
        return frame;
    }

    /// <summary>Ends a selected session without invoking.</summary>
    /// <exception cref="ProtocolException">E1608 out of order.</exception>
    public JsonObject Shutdown()
    {
        Require(State.Selected);
        State = State.ShuttingDown;
        return Json.Object(("kind", Json.Of("shutdown")), ("protocol", Json.Of(Compat.Protocol)));
    }

    private static string RequestId(long ordinal) => ordinal.ToString("x16", CultureInfo.InvariantCulture);

    private JsonObject Respond(IEnumerable<(string Key, Json Value)> fields, bool terminalResponse)
    {
        long ordinal = Pending!.Value;
        Pending = null;
        var frame = new Dictionary<string, Json>(StringComparer.Ordinal);
        foreach ((string key, Json value) in fields)
        {
            frame[key] = value;
        }

        frame["protocol"] = Json.Of(Compat.Protocol);
        frame["invocation_id"] = Json.Of(Codec.InvocationId);
        frame["request_id"] = Json.Of(RequestId(ordinal));
        JsonObject rendered = Json.Object(frame);
        responses.Add(rendered);
        State = terminalResponse ? State.Finishing : State.Running;
        return rendered;
    }

    /// <summary>The <c>effect_ok</c> frame for a value, validated without changing state.</summary>
    /// <exception cref="ProtocolException">E1608 out of order; otherwise as <see cref="Codec.EncodeValue"/>.</exception>
    public JsonObject OkFrame(Value? value)
    {
        Require(State.Waiting);
        Json encoded = Codec.EncodeValue(value, new Budget(Limits));
        JsonObject frame = Json.Object(
            ("invocation_id", Json.Of(Codec.InvocationId)),
            ("kind", Json.Of("effect_ok")),
            ("protocol", Json.Of(Compat.Protocol)),
            ("request_id", Json.Of(RequestId(Pending!.Value))),
            ("value", encoded));
        JsonCodec.FrameBytes(frame, Limits.MaxFrameBytes, Limits.MaxJsonDepth);
        return frame;
    }

    /// <summary>Answers the pending request with a value.</summary>
    /// <exception cref="ProtocolException">E1608 out of order; otherwise as <see cref="Codec.EncodeValue"/>.</exception>
    public JsonObject RespondOk(Value? value)
    {
        Require(State.Waiting);
        Json encoded = Codec.EncodeValue(value, new Budget(Limits));
        return Respond(new[] { ("kind", (Json)Json.Of("effect_ok")), ("value", encoded) }, false);
    }

    /// <summary>Answers the pending request with a permitted failure.</summary>
    /// <exception cref="ProtocolException">E1608 out of order; E1601 for a lone surrogate; E1602 for a message over <c>max_host_message_bytes</c>.</exception>
    public JsonObject RespondFailure(FailureCategory category, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Require(State.Waiting);
        if (message.Length > Limits.MaxHostMessageBytes)
        {
            throw ProtocolException.Exceeded("host message exceeds max_host_message_bytes");
        }

        if (!JsonCodec.IsScalar(message))
        {
            throw ProtocolException.Malformed("a JSON string contains a lone surrogate");
        }

        if (JsonCodec.Utf8Length(message) > Limits.MaxHostMessageBytes)
        {
            throw ProtocolException.Exceeded("host message exceeds max_host_message_bytes");
        }

        return Respond(
            new[]
            {
                ("category", (Json)Json.Of(category.WireName())),
                ("completion", Json.Of(category.Completion())),
                ("kind", Json.Of("effect_failure")),
                ("message", Json.Of(message)),
            },
            true);
    }

    /// <summary>Withdraws the pending request.</summary>
    /// <exception cref="ProtocolException">E1608 out of order; E1601 for an unknown completion.</exception>
    public JsonObject Cancel(CancelReason reason, string completion)
    {
        Require(State.Waiting);
        if (completion is not ("not_started" or "failed" or "unknown"))
        {
            throw ProtocolException.Malformed("unknown cancellation enum");
        }

        return Respond(
            new[] { ("completion", (Json)Json.Of(completion)), ("kind", Json.Of("cancel")), ("reason", Json.Of(reason.WireName())) },
            true);
    }
}

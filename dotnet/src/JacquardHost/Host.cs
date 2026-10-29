using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host;

/// <summary>Options for a <see cref="Host"/>.</summary>
public sealed record HostOptions
{
    /// <summary>Default carrier options and the hard limits.</summary>
    public static HostOptions Defaults { get; } = new();

    /// <summary>The worker command, environment, and lifetime deadline.</summary>
    public WorkerOptions Carrier { get; init; } = WorkerOptions.Defaults;

    /// <summary>The session limits; null selects the hard limits.</summary>
    public Limits? Limits { get; init; }
}

/// <summary>
/// Invokes checked Jacquard terms in one prepared store through
/// <c>jacquard-host-v0</c> and serves their effect requests from a closed
/// registry of .NET handlers. Each invocation runs one fresh, serial worker.
/// </summary>
public sealed class Host
{
    /// <summary>Host evidence schema recorded beside Core's frames.</summary>
    public const string EvidenceSchema = "jacquard-host-dotnet-evidence-v0";

    /// <summary>The call stopped before the handler began; the fault check after the wait reports why.</summary>
    private static readonly Answer NotStarted = new(null, FailureCategory.OutsideFailure, "handler was not started");

    private readonly WorkerOptions carrier;

    /// <summary>Validates options before any worker starts.</summary>
    /// <exception cref="ArgumentException">The limits or carrier options are invalid.</exception>
    public Host(string store, HostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= HostOptions.Defaults;
        Limits selected = options.Limits ?? Limits.Hard;
        try
        {
            selected.Validate();
        }
        catch (ProtocolException error)
        {
            throw new ArgumentException(error.Message, nameof(options), error);
        }

        ArgumentNullException.ThrowIfNull(options.Carrier, nameof(options));
        options.Carrier.Validate();
        Store = store;
        Limits = selected;
        carrier = options.Carrier;
    }

    /// <summary>The prepared store directory.</summary>
    public string Store { get; }

    /// <summary>The limits each session selects.</summary>
    public Limits Limits { get; }

    /// <summary>
    /// Test seam: runs on the caller's thread once an effect request is
    /// decoded, just before the caller's check. Null outside tests. It is
    /// global, so tests that set it must not run in parallel.
    /// </summary>
    internal static volatile Action? RequestDecodedHook;

    /// <summary>
    /// Test seam: runs on the caller's thread just after a handler thread
    /// starts. Null outside tests.
    /// </summary>
    internal static volatile Action? HandlerThreadStartedHook;

    /// <summary>
    /// Test seam: runs on the handler thread just before its check. Null
    /// outside tests.
    /// </summary>
    internal static volatile Action? HandlerCheckHook;

    /// <summary>
    /// Test seam: maps the lifetime left to the delay of the handlers' token
    /// timer, so a test can make the timer fire early. Null outside tests.
    /// </summary>
    internal static volatile Func<TimeSpan, TimeSpan>? HandlerTimerHook;

    /// <summary>
    /// Runs <paramref name="target"/> with <paramref name="args"/>. The invoke
    /// frame is validated before a worker starts. Cancelling
    /// <paramref name="cancellationToken"/> kills the worker; nothing is
    /// retried. Every outcome, including a lost worker, is returned as an
    /// <see cref="InvocationResult"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">An argument is null; no worker has started.</exception>
    public InvocationResult Invoke(Hash target, CallInterface spec, IReadOnlyList<Value> args, OperationRegistry registry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);
        IReadOnlyList<OperationEntry> entries;
        Value[] arguments;
        try
        {
            // The caller's registry and arguments are bounded before they are copied.
            if (registry.Count > Limits.MaxOperations)
            {
                throw ProtocolException.Exceeded("registry exceeds max_operations");
            }

            entries = registry.Entries();
            JsonObject frame = Codec.InvokeFrame(target, spec, args, entries, Limits);
            JsonCodec.FrameBytes(frame, Limits.MaxFrameBytes, Limits.MaxJsonDepth);
            arguments = args.ToArray();
        }
        catch (ProtocolException error)
        {
            return new InvocationResult(ResultKind.HostError)
            {
                HostEvidence = Json.Object(
                    ("code", Json.Of(error.Code)),
                    ("detail", Json.Of(error.Detail)),
                    ("reason", Json.Of("invoke_refused_locally")),
                    ("schema", Json.Of(EvidenceSchema))),
            };
        }

        return Run(session => session.Invoke(target, spec, arguments, entries), registry, cancellationToken);
    }

    /// <summary>Negotiates a session and shuts it down without invoking.</summary>
    public InvocationResult ShutdownProbe(CancellationToken cancellationToken = default) =>
        Run(session => session.Shutdown(), new OperationRegistry(), cancellationToken);

    /// <summary>
    /// At most <paramref name="limit"/> UTF-8 bytes of <paramref name="text"/>,
    /// cut at a code point boundary; a lone surrogate, which has no encoding,
    /// becomes U+FFFD. The scan stops at the limit.
    /// </summary>
    internal static string Bounded(string text, int limit)
    {
        var output = new StringBuilder();
        int used = 0;
        for (int i = 0; i < text.Length;)
        {
            int point;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                point = char.ConvertToUtf32(text[i], text[i + 1]);
                i += 2;
            }
            else
            {
                point = char.IsSurrogate(text[i]) ? 0xFFFD : text[i];
                i++;
            }

            int size = point < 0x80 ? 1 : point < 0x800 ? 2 : point < 0x10000 ? 3 : 4;
            if (used + size > limit)
            {
                break;
            }

            output.Append(char.ConvertFromUtf32(point));
            used += size;
        }

        return output.ToString();
    }

    private InvocationResult Run(Func<Session, JsonObject> opening, OperationRegistry registry, CancellationToken cancellationToken)
    {
        var session = new Session();
        var result = new InvocationResult(ResultKind.CarrierLoss);
        Worker worker;
        try
        {
            worker = Worker.Start(Store, carrier, cancellationToken);
        }
        catch (ArgumentException error)
        {
            result.HostEvidence = Json.Object(("detail", Json.Of(error.Message)), ("reason", Json.Of("invalid_options")), ("schema", Json.Of(EvidenceSchema)));
            return result;
        }

        var call = new Call(worker, session, result, registry, cancellationToken);
        try
        {
            call.Drive(opening, Limits);
        }
        catch (CarrierException error)
        {
            Record(call, error);
        }
        finally
        {
            worker.Dispose();
            call.End();
        }

        result.Requests = session.Requests.ToArray();
        result.ExitStatus = worker.ExitStatus;
        result.Signal = worker.Signal;
        result.Stderr = worker.Stderr;
        return result;
    }

    private static void Record(Call call, CarrierException error)
    {
        ResultKind kind;
        string code;
        string reason;
        if (!error.IsLoss)
        {
            code = error.ProtocolError!.Code;
            kind = call.Sending ? ResultKind.HostError : ResultKind.ProtocolError;
            reason = call.Sending ? "host_frame_refused" : "core_frame_refused";
        }
        else
        {
            kind = ResultKind.CarrierLoss;
            code = "E1611";
            reason = error.IsTimeout ? "deadline"
                : call.Worker.SpawnError is not null ? "worker_spawn_failed"
                : error.IsAborted ? "aborted"
                : "carrier_loss";
        }

        call.Result.Kind = kind;
        long? outstanding = call.Session.Pending;
        call.Result.HostEvidence = Json.Object(
            ("code", Json.Of(code)),
            ("detail", Json.Of(error.Message)),
            ("outstanding_request", outstanding is null ? Json.Null : Json.Of(outstanding.Value)),
            ("reason", Json.Of(reason)),
            ("schema", Json.Of(EvidenceSchema)),
            ("state", Json.Of(call.Session.State.WireName())));
    }

    /// <summary>A handler's answer: a value, or a failure category and message.</summary>
    private sealed record Answer(Value? Value, FailureCategory Category, string? Message);

    /// <summary>One invocation's worker, session, and handler state.</summary>
    [SuppressMessage("Design", "CA1001", Justification = "The handlers' token source is cancelled, never disposed: an abandoned handler may still hold its token.")]
    private sealed class Call
    {
        /// <summary>
        /// The longest single timer wait. A longer lifetime is covered by
        /// re-arming, so no conversion to milliseconds can overflow.
        /// </summary>
        private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(1);

        private readonly OperationRegistry registry;
        private readonly CancellationToken cancellationToken;
        private volatile bool ended;
        private CancellationTokenSource? handlerStop;
        private Timer? handlerTimer;
        private CancellationTokenRegistration callerRegistration;

        internal Call(Worker worker, Session session, InvocationResult result, OperationRegistry registry, CancellationToken cancellationToken)
        {
            Worker = worker;
            Session = session;
            Result = result;
            this.registry = registry;
            this.cancellationToken = cancellationToken;
        }

        internal Worker Worker { get; }

        internal Session Session { get; }

        internal InvocationResult Result { get; }

        /// <summary>Whether a host frame was being encoded or sent when a failure occurred.</summary>
        internal bool Sending { get; private set; }

        /// <summary>
        /// Marks the invocation ended and cancels the handlers' token on a
        /// pool thread. Nothing here runs or waits for a handler's callback:
        /// the registration is removed without waiting, and the timer is
        /// disposed without waiting.
        /// </summary>
        internal void End()
        {
            ended = true;
            callerRegistration.Unregister();
            handlerTimer?.Dispose();
            CancelHandlers(handlerStop);
        }

        private void Send(JsonObject frame, Limits limits)
        {
            Sending = true;
            Worker.WriteFrame(frame, limits);
            Result.HostFrameList.Add(frame);
            Sending = false;
        }

        private static CarrierException Refused(ProtocolException error) => CarrierException.FromProtocol(error);

        internal void Drive(Func<Session, JsonObject> opening, Limits limits)
        {
            JsonObject hello = Worker.ReadFrame(Limits.Hard) ?? throw CarrierException.Loss("worker exited before core_hello");
            Result.CoreFrameList.Add(hello);
            JsonObject selected;
            try
            {
                Session.Receive(hello);
                selected = Session.Select(limits);
            }
            catch (ProtocolException error)
            {
                throw Refused(error);
            }

            Send(selected, Limits.Hard);
            Worker.SetStderrLimit(limits.MaxStderrBytes);
            JsonObject first;
            try
            {
                first = opening(Session);
            }
            catch (ProtocolException error)
            {
                throw Refused(error);
            }

            Send(first, Session.Limits);
            while (Session.State != State.Closed)
            {
                JsonObject frame = Worker.ReadFrame(Session.Limits) ?? throw CarrierException.Loss("worker exited before a terminal frame");
                Result.CoreFrameList.Add(frame);
                CoreFrame? parsed;
                try
                {
                    parsed = Session.Receive(frame);
                }
                catch (ProtocolException error)
                {
                    throw Refused(error);
                }

                switch (parsed)
                {
                    case EffectRequest request:
                        Send(Respond(request), Session.Limits);
                        break;
                    case Outcome outcome:
                        Result.Kind = outcome.Value is not null ? ResultKind.Ok : ResultKind.Error;
                        Result.Value = outcome.Value;
                        Result.Diagnostics = outcome.Diagnostics;
                        Result.Terminal = outcome.TerminalName;
                        Result.Evidence = outcome.Evidence;
                        break;
                    case Fatal fatal:
                        Result.Kind = ResultKind.Fatal;
                        Result.Diagnostics = fatal.Diagnostics;
                        break;
                    case ShutdownAck:
                        Result.Kind = ResultKind.ShutdownAck;
                        break;
                }
            }

            Worker.CloseInput();
            JsonObject? extra = Worker.ReadFrame(Session.Limits);
            if (extra is not null)
            {
                Result.CoreFrameList.Add(extra);
                try
                {
                    Session.Receive(extra);
                }
                catch (ProtocolException error)
                {
                    throw Refused(error);
                }
            }

            Worker.AwaitExit();
            if (Worker.ExitStatus != 0)
            {
                throw CarrierException.Loss("worker exited unsuccessfully after its terminal");
            }
        }

        /// <summary>
        /// Runs the handler on its own thread, bounded by the worker lifetime,
        /// and renders the response. A handler still running at the deadline is
        /// abandoned: no late response is sent and nothing is retried.
        /// </summary>
        private JsonObject Respond(EffectRequest request)
        {
            Handler? handler = registry.Find(request);
            Answer answer;
            if (handler is null)
            {
                answer = new Answer(null, FailureCategory.UnsupportedOperation, "operation is not configured");
            }
            else
            {
                // Decoding the request takes time; an outside action must not
                // begin once the call is cancelled or the lifetime passed. The
                // caller checks before creating the handler thread, and the
                // thread checks again before calling the handler.
                RequestDecodedHook?.Invoke();
                CarrierException? early = Worker.Fault();
                if (early is not null)
                {
                    throw early;
                }

                answer = RunHandler(handler, request.Arguments);

                // A cancelled handler token means the call is stopping. Its
                // deadline timer may fire a moment before the worker's clock
                // reaches the deadline, and a handler that returned on it must
                // not have its answer sent: wait for the worker to report the
                // stop (at most that moment), which throws.
                if (handlerStop is { IsCancellationRequested: true })
                {
                    Worker.Await(static () => false);
                }
            }

            CarrierException? fault = Worker.Fault();
            if (fault is not null)
            {
                throw fault;
            }

            try
            {
                if (answer.Message is null)
                {
                    try
                    {
                        Session.OkFrame(answer.Value);
                        return Session.RespondOk(answer.Value);
                    }
                    catch (ProtocolException)
                    {
                        answer = new Answer(null, FailureCategory.OutsideFailure, "handler result has no encoding within the selected limits");
                    }
                }

                return Session.RespondFailure(answer.Category, Bounded(answer.Message ?? string.Empty, Session.Limits.MaxHostMessageBytes));
            }
            catch (ProtocolException error)
            {
                throw Refused(error);
            }
        }

        private Answer RunHandler(Handler handler, IReadOnlyList<Value> arguments)
        {
            HandlerContext context = new(() => Worker.Stopping, () => ended, HandlerToken());
            var completion = new Completion();
            var thread = new Thread(() =>
            {
                HandlerCheckHook?.Invoke();
                completion.Answer = Worker.Stopping ? NotStarted : CallHandler(handler, context, arguments);
            })
            {
                IsBackground = true,
                Name = "jacquard-handler",
            };
            try
            {
                thread.Start();
            }
            catch (OutOfMemoryException)
            {
                // The handler never ran.
                return new Answer(null, FailureCategory.OutsideFailure, "handler thread could not start");
            }

            HandlerThreadStartedHook?.Invoke();
            Worker.Await(() => completion.Answer is not null);
            return completion.Answer ?? new Answer(null, FailureCategory.CompletionUnknown, "handler threw an exception");
        }

        /// <summary>
        /// The handlers' token: cancelled when the caller's token is
        /// cancelled, shortly after the lifetime deadline, and when the
        /// invocation ends. Callbacks a handler registers on it run on a pool
        /// thread, never on the caller's thread, the canceller's thread, or the
        /// deadline timer, and nothing waits for them; their exceptions are
        /// discarded. The source is never disposed, because an abandoned
        /// handler may still hold its token.
        /// </summary>
        private CancellationToken HandlerToken()
        {
            if (handlerStop is null)
            {
                handlerStop = new CancellationTokenSource();
                TimeSpan remaining = HandlerTimerHook is { } hook ? hook(Worker.Remaining) : Worker.Remaining;
                handlerTimer = new Timer(static state => ((Call)state!).TimerFired(), this, TimerDelay(remaining), Timeout.InfiniteTimeSpan);
                callerRegistration = cancellationToken.UnsafeRegister(static state => CancelHandlers((CancellationTokenSource)state!), handlerStop);
            }

            return handlerStop.Token;
        }

        /// <summary>The timer delay for <paramref name="remaining"/>: rounded up to whole milliseconds, at least zero, at most <see cref="MaxTimerDelay"/>.</summary>
        private static TimeSpan TimerDelay(TimeSpan remaining)
        {
            if (remaining <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            if (remaining >= MaxTimerDelay)
            {
                return MaxTimerDelay;
            }

            return TimeSpan.FromMilliseconds(Math.Ceiling(remaining.TotalMilliseconds));
        }

        /// <summary>
        /// Runs on the timer's pool thread: re-arms while a long lifetime has
        /// more than one timer wait left, otherwise queues the cancellation.
        /// </summary>
        private void TimerFired()
        {
            TimeSpan remaining = Worker.Remaining;
            if (remaining > MaxTimerDelay && !ended)
            {
                try
                {
                    handlerTimer?.Change(TimerDelay(remaining), Timeout.InfiniteTimeSpan);
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // The invocation ended; End cancels the token.
                }
            }

            CancelHandlers(handlerStop);
        }

        /// <summary>
        /// Cancels <paramref name="source"/> on a pool work item, so handler
        /// callbacks never run on, or hold, the thread that asks.
        /// </summary>
        private static void CancelHandlers(CancellationTokenSource? source)
        {
            if (source is not null && !source.IsCancellationRequested)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static s => CancelQuietly(s), source, preferLocal: false);
            }
        }

        /// <summary>Cancels a source, discarding whatever its callbacks throw.</summary>
        [SuppressMessage("Design", "CA1031", Justification = "A handler's token callback may throw anything; it must not crash the process.")]
        private static void CancelQuietly(CancellationTokenSource source)
        {
            try
            {
                source.Cancel();
            }
            catch (Exception)
            {
                // Handler code; the host has no use for its failure.
            }
        }

        /// <summary>Calls a handler, turning every exception into a failure answer.</summary>
        [SuppressMessage("Design", "CA1031", Justification = "Any exception a handler throws is reported to Core as completion unknown.")]
        private static Answer CallHandler(Handler handler, HandlerContext context, IReadOnlyList<Value> arguments)
        {
            try
            {
                Value value = handler(context, arguments);
                return new Answer(value, FailureCategory.OutsideFailure, null);
            }
            catch (HandlerException failure)
            {
                return new Answer(null, failure.Category, failure.Message);
            }
            catch (Exception)
            {
                // Its message may carry private detail and is never sent.
                return new Answer(null, FailureCategory.CompletionUnknown, "handler threw an exception");
            }
        }
    }

    /// <summary>The handler thread's answer, published once.</summary>
    private sealed class Completion
    {
        private volatile Answer? answer;

        internal Answer? Answer
        {
            get => answer;
            set => answer = value;
        }
    }
}

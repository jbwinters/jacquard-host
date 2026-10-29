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

    // Test seams. They are internal (reachable only from the test assembly
    // through InternalsVisibleTo) and null outside tests, so production pays
    // one volatile read of null at each. They are process-global: every
    // invocation in the process sees them, so the tests run serially
    // (LevelOfParallelism(1)) and each test fixture that sets one clears it
    // in its TearDown.

    /// <summary>
    /// Test seam: runs on the caller's thread once an effect request is
    /// decoded, before its handler is looked up and before the caller's
    /// check. Null outside tests.
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
    /// Test seam: runs on the handler thread just after it publishes its
    /// answer, so a test can wait until the caller's wait will see it. Null
    /// outside tests.
    /// </summary>
    internal static volatile Action? HandlerAnsweredHook;

    /// <summary>
    /// Test seam: runs before the host starts the cancellation of the
    /// handlers' token, with <c>"thread"</c> before it creates its dedicated
    /// thread and with <c>"pool"</c> before it falls back to a thread-pool
    /// work item. A test throws from it to stand for a failure to start
    /// either. Null outside tests.
    /// </summary>
    internal static volatile Action<string>? CancelStartHook;

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
        RegistrySnapshot operations;
        Value[] arguments;
        try
        {
            // The caller's registry and arguments are bounded before they are
            // copied. The invocation advertises and dispatches one snapshot.
            operations = registry.Snapshot(Limits.MaxOperations) ?? throw ProtocolException.Exceeded("registry exceeds max_operations");
            entries = operations.Entries;
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

        return Run(session => session.Invoke(target, spec, arguments, entries), operations, cancellationToken);
    }

    /// <summary>Negotiates a session and shuts it down without invoking.</summary>
    public InvocationResult ShutdownProbe(CancellationToken cancellationToken = default) =>
        Run(session => session.Shutdown(), RegistrySnapshot.Empty, cancellationToken);

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

    private InvocationResult Run(Func<Session, JsonObject> opening, RegistrySnapshot registry, CancellationToken cancellationToken)
    {
        var session = new Session();
        var result = new InvocationResult(ResultKind.CarrierLoss);
        var call = new Call(session, result, registry);
        Worker worker;
        try
        {
            // The stop notice is in place from the spawn, so every kill of the
            // worker, including one while Start sets it up, starts the
            // handlers' token cancellation first.
            worker = Worker.Start(Store, carrier, call.StopHandlers, cancellationToken);
        }
        catch (ArgumentException error)
        {
            result.HostEvidence = Json.Object(("detail", Json.Of(error.Message)), ("reason", Json.Of("invalid_options")), ("schema", Json.Of(EvidenceSchema)));
            return result;
        }

        // The worker observes the caller's token in its waits on this thread;
        // nothing is registered on it.
        call.Attach(worker);
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
            // The handlers' token is cancelled before the worker is disposed,
            // which may wait up to a second for a killed worker to exit. The
            // worker is disposed even if ending the call fails.
            try
            {
                call.End();
            }
            finally
            {
                worker.Dispose();
            }
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
        private readonly RegistrySnapshot registry;
        private readonly object stopSync = new();
        private volatile bool ended;
        private CancellationTokenSource? handlerStop;
        private bool stopRequested;
        private bool handlersStopped;
        private Worker? worker;

        internal Call(Session session, InvocationResult result, RegistrySnapshot registry)
        {
            Session = session;
            Result = result;
            this.registry = registry;
        }

        /// <summary>The call's worker, whose stop notice is <see cref="StopHandlers"/>.</summary>
        internal Worker Worker => worker ?? throw new InvalidOperationException("the call has no worker");

        /// <summary>Gives the call its worker, started with <see cref="StopHandlers"/> as its stop notice.</summary>
        internal void Attach(Worker started) => worker = started;

        internal Session Session { get; }

        internal InvocationResult Result { get; }

        /// <summary>Whether a host frame was being encoded or sent when a failure occurred.</summary>
        internal bool Sending { get; private set; }

        /// <summary>
        /// Marks the invocation ended and starts the cancellation of the
        /// handlers' token, if any handler was given one and it was not
        /// already started. Called once, on the caller's thread. It does not
        /// wait.
        /// </summary>
        internal void End()
        {
            ended = true;
            StopHandlers();
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
            RequestDecodedHook?.Invoke();
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
                CarrierException? early = Worker.Fault();
                if (early is not null)
                {
                    throw early;
                }

                answer = RunHandler(handler, request.Arguments);
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
                HandlerAnsweredHook?.Invoke();
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
            // The wait usually ends as soon as the call is stopping, before
            // the worker is killed (which can take a while), so the handlers'
            // token is cancelled first; the fault check after the wait then
            // kills the worker and reports why. A stop that lands between the
            // wait's checks, or after it, is seen by a fault check, whose
            // kill gives the worker's stop notice (StopHandlers) first.
            Worker.Await(() => completion.Answer is not null || Worker.Stopping);
            if (Worker.Stopping)
            {
                StopHandlers();
            }
            return completion.Answer ?? new Answer(null, FailureCategory.CompletionUnknown, "handler threw an exception");
        }

        /// <summary>
        /// The handlers' token, created on the caller's thread for the first
        /// handler. Only <see cref="StopHandlers"/> cancels it, once the call
        /// has been seen to stop or end: there is no timer, no registration
        /// on the caller's token, and no pool work item unless no thread can
        /// be created. A token created after the stop was seen starts
        /// cancelled (no callback can be registered on it yet, so none runs
        /// here); its handler does not start. The source is never disposed,
        /// because an abandoned handler may still hold its token.
        /// </summary>
        private CancellationToken HandlerToken()
        {
            lock (stopSync)
            {
                if (handlerStop is null)
                {
                    handlerStop = new CancellationTokenSource();
                    if (stopRequested)
                    {
                        handlersStopped = true;
                        handlerStop.Cancel();
                    }
                }

                return handlerStop.Token;
            }
        }

        /// <summary>
        /// Called when the caller's thread sees the call stopping while a
        /// handler runs, at <see cref="End"/>, and through the worker's
        /// <see cref="Worker.StopNotice"/> before any kill (on the caller's
        /// thread, including while the worker is being started, or on a
        /// thread disposing the worker): starts, at most
        /// once, a dedicated background thread that cancels the handlers'
        /// token and discards whatever its callbacks throw. Nothing waits for
        /// that thread, so a callback that blocks holds only it, and the
        /// shared thread pool is not involved.
        /// </summary>
        internal void StopHandlers()
        {
            CancellationTokenSource? source;
            lock (stopSync)
            {
                stopRequested = true;
                source = handlerStop;
                if (source is null || handlersStopped)
                {
                    return;
                }

                handlersStopped = true;
            }

            if (!StartCancellation(source))
            {
                Result.HandlerTokenNotCancelled = true;
            }
        }

        /// <summary>
        /// Starts the cancellation of <paramref name="source"/> on a new
        /// background thread or, if no thread can be created or started, on
        /// a thread-pool work item; callbacks never run on the calling
        /// thread. Returns false, and leaves the token uncancelled, when
        /// neither could be started. Never throws: it runs inside the
        /// worker's fault checks and at the end of <see cref="Invoke"/>.
        /// </summary>
        [SuppressMessage("Design", "CA1031", Justification = "Starting the cancellation must not throw into a fault check or out of Invoke; a failure falls back, then is recorded.")]
        private static bool StartCancellation(CancellationTokenSource source)
        {
            try
            {
                CancelStartHook?.Invoke("thread");
                var thread = new Thread(static state => CancelQuietly((CancellationTokenSource)state!))
                {
                    IsBackground = true,
                    Name = "jacquard-handler-cancel",
                };
                thread.Start(source);
                return true;
            }
            catch (Exception)
            {
                // No thread could be created or started (for example
                // OutOfMemoryException). Fall back to the pool rather than
                // run handler callbacks on this thread.
            }

            try
            {
                CancelStartHook?.Invoke("pool");
                return ThreadPool.UnsafeQueueUserWorkItem(static s => CancelQuietly(s), source, preferLocal: false);
            }
            catch (Exception)
            {
                // The pool refused the work item as well (for example
                // NotSupportedException or OutOfMemoryException): give up.
            }

            return false;
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
            catch (HandlerException failure) when (Enum.IsDefined(failure.Category))
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

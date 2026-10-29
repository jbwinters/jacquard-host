using System;
using System.Collections.Generic;
using System.Threading;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host;

/// <summary>
/// One effect operation's handler. Return a value, or throw a
/// <see cref="HandlerException"/> for a structured failure; any other
/// exception is reported as completion unknown, without its message.
/// </summary>
/// <param name="context">Whether the invocation is still live.</param>
/// <param name="arguments">The decoded request arguments.</param>
/// <returns>The operation's result.</returns>
public delegate Value Handler(HandlerContext context, IReadOnlyList<Value> arguments);

/// <summary>What a handler sees: whether the invocation is still live.</summary>
public sealed class HandlerContext
{
    private readonly Func<bool> stopping;
    private readonly Func<bool> ended;

    internal HandlerContext(Func<bool> stopping, Func<bool> ended, CancellationToken cancellationToken)
    {
        this.stopping = stopping;
        this.ended = ended;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Whether the worker expired, the caller cancelled, the lifetime passed,
    /// or the invocation ended. Handlers should stop; the host no longer
    /// waits for them.
    /// </summary>
    public bool IsCancelled => stopping() || ended() || CancellationToken.IsCancellationRequested;

    /// <summary>
    /// A token cancelled when the caller cancels, shortly after the lifetime
    /// deadline passes (timer granularity; <see cref="IsCancelled"/> is
    /// exact), or when the invocation ends. Callbacks registered on it run on
    /// a thread-pool thread, never on the caller's thread; the host does not
    /// wait for them, and any exception they throw is discarded.
    /// </summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>
/// A handler's structured refusal or failure, reported to Core with its
/// category and message. Any other exception a handler throws is reported as
/// completion unknown, without its message.
/// </summary>
public sealed class HandlerException : Exception
{
    /// <summary>A failure in one of the four permitted categories, with a message for Core.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="category"/> is not a defined category.</exception>
    public HandlerException(FailureCategory category, string message)
        : base(message ?? throw new ArgumentNullException(nameof(message)))
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category), category, "not a failure category");
        }

        Category = category;
    }

    /// <summary>The failure category.</summary>
    public FailureCategory Category { get; }

    /// <summary>The host does not provide the operation (not started).</summary>
    public static HandlerException Unsupported(string message) => new(FailureCategory.UnsupportedOperation, message);

    /// <summary>The host refused to authorize the operation (not started).</summary>
    public static HandlerException Refused(string message) => new(FailureCategory.RefusedAuthority, message);

    /// <summary>An outside action failed without effect.</summary>
    public static HandlerException OutsideFailed(string message) => new(FailureCategory.OutsideFailure, message);

    /// <summary>An outside action's completion is unknown.</summary>
    public static HandlerException CompletionUnknown(string message) => new(FailureCategory.CompletionUnknown, message);
}

/// <summary>A closed map from exact operation identities to handlers.</summary>
public sealed class OperationRegistry
{
    private readonly SortedDictionary<OperationEntry, Handler> handlers = new();
    private readonly object sync = new();

    /// <summary>Binds a handler to one effect operation identity.</summary>
    /// <exception cref="ArgumentException">The identity is already bound.</exception>
    public OperationRegistry Bind(Hash effect, Hash operation, Handler handler)
    {
        var key = new OperationEntry(effect, operation);
        ArgumentNullException.ThrowIfNull(handler);
        lock (sync)
        {
            if (!handlers.TryAdd(key, handler))
            {
                throw new ArgumentException("operation identity is already bound", nameof(operation));
            }
        }

        return this;
    }

    /// <summary>The number of bound operations.</summary>
    public int Count
    {
        get
        {
            lock (sync)
            {
                return handlers.Count;
            }
        }
    }

    /// <summary>The bound operations in protocol order.</summary>
    public IReadOnlyList<OperationEntry> Entries()
    {
        lock (sync)
        {
            return new List<OperationEntry>(handlers.Keys);
        }
    }

    /// <summary>
    /// An immutable copy for one invocation, or null when more than
    /// <paramref name="maxOperations"/> are bound; the count is checked
    /// before anything is copied.
    /// </summary>
    internal RegistrySnapshot? Snapshot(int maxOperations)
    {
        lock (sync)
        {
            return handlers.Count > maxOperations ? null : new RegistrySnapshot(new SortedDictionary<OperationEntry, Handler>(handlers));
        }
    }
}

/// <summary>
/// The operations one invocation advertises and dispatches: a copy taken
/// once, so a concurrent <see cref="OperationRegistry.Bind"/> cannot make
/// the host run an operation it did not advertise.
/// </summary>
internal sealed class RegistrySnapshot
{
    private readonly SortedDictionary<OperationEntry, Handler> handlers;

    internal RegistrySnapshot(SortedDictionary<OperationEntry, Handler> handlers)
    {
        this.handlers = handlers;
        Entries = new List<OperationEntry>(handlers.Keys);
    }

    /// <summary>No operations.</summary>
    internal static RegistrySnapshot Empty { get; } = new(new SortedDictionary<OperationEntry, Handler>());

    /// <summary>The advertised operations in protocol order.</summary>
    internal IReadOnlyList<OperationEntry> Entries { get; }

    internal Handler? Find(EffectRequest request) =>
        handlers.TryGetValue(new OperationEntry(request.Effect, request.Operation), out Handler? handler) ? handler : null;
}

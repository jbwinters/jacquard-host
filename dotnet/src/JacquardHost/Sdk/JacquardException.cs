using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace JacquardLang.Host.Sdk;

/// <summary>What an SDK failure is about.</summary>
public enum JacquardErrorKind
{
    /// <summary>The program could not be checked, hashed, installed, or loaded from its cache.</summary>
    Preparation,

    /// <summary>
    /// A mapping or binding problem: an unknown name, a missing handler, or a
    /// successful Core result that does not decode into the requested type
    /// (<see cref="JacquardException.Result"/> is then set).
    /// </summary>
    Binding,

    /// <summary>A local input that cannot be sent; no worker started.</summary>
    InvalidArgument,

    /// <summary>The invocation ran and did not succeed; <see cref="JacquardException.Failure"/> classifies it.</summary>
    Invocation,
}

/// <summary>
/// Every SDK failure. <see cref="Kind"/> says what failed; an invocation that
/// ran keeps its raw <see cref="Result"/>. A failure caused by the caller's
/// own code (a codec, a record accessor or constructor) keeps that exception
/// as <see cref="Exception.InnerException"/>. The message is at most
/// <see cref="MessageLimit"/> chars: a longer one keeps its start and notes
/// its whole length.
/// </summary>
public sealed class JacquardException : Exception
{
    /// <summary>The longest message kept, in chars, its noted length included.</summary>
    public const int MessageLimit = 8192;

    private static readonly IReadOnlyList<object?> Nothing = new ReadOnlyCollection<object?>(Array.Empty<object?>());

    private JacquardException(JacquardErrorKind kind, string message, InvocationResult? result, Exception? cause, int batchIndex, IReadOnlyList<object?> completed)
        : base(Bounded(message), cause)
    {
        Kind = kind;
        Result = result;
        BatchIndex = batchIndex;
        Completed = completed;
    }

    /// <summary>What failed.</summary>
    public JacquardErrorKind Kind { get; }

    /// <summary>
    /// The raw result, when a worker ran: always for
    /// <see cref="JacquardErrorKind.Invocation"/>, and for a successful result
    /// that did not decode; otherwise null.
    /// </summary>
    public InvocationResult? Result { get; }

    /// <summary>How the invocation failed, for <see cref="JacquardErrorKind.Invocation"/>; otherwise null.</summary>
    public Failure? Failure => InnerException is InvocationException error ? error.Failure : null;

    /// <summary>
    /// For <c>Function.MapBatches</c>:
    /// the index of the batch that failed, or -1 when a batch was refused
    /// before any worker started (and for every other failure).
    /// </summary>
    public int BatchIndex { get; }

    /// <summary>
    /// For <c>MapBatches</c>: the decoded values of the batches that completed
    /// before the failed one, in order, read-only. Nothing is retried.
    /// </summary>
    public IReadOnlyList<object?> Completed { get; }

    /// <summary><paramref name="message"/> within <see cref="MessageLimit"/> chars, its start kept and its whole length noted.</summary>
    internal static string Bounded(string? message) => Joined(string.Empty, message);

    /// <summary>
    /// <paramref name="prefix"/> followed by <paramref name="message"/>, in at
    /// most <see cref="MessageLimit"/> chars, noting the whole length once. A
    /// message that was already cut keeps its own noted length, plus the
    /// prefix, so it is never cut twice or noted wrongly. Nothing longer than
    /// the limit is copied.
    /// </summary>
    internal static string Joined(string prefix, string? message)
    {
        message ??= "null";
        long noted = NotedLength(message);
        long total = prefix.Length + (noted < 0 ? message.Length : noted);
        if (noted < 0 && total <= MessageLimit)
        {
            return prefix + message;
        }

        int bodyLength = noted < 0 ? message.Length : message.LastIndexOf("... (", StringComparison.Ordinal);
        string suffix = "... (" + total.ToString(CultureInfo.InvariantCulture) + " chars)";
        int room = MessageLimit - suffix.Length - prefix.Length;
        if (room <= 0)
        {
            // A prefix past the limit (not one the SDK builds): keep its start.
            int keep = MessageLimit - suffix.Length;
            if (char.IsHighSurrogate(prefix[keep - 1]))
            {
                keep--;
            }

            return string.Concat(prefix.AsSpan(0, keep), suffix);
        }

        int end = Math.Min(room, bodyLength);
        if (end > 0 && end < bodyLength && char.IsHighSurrogate(message[end - 1]))
        {
            end--;
        }

        return string.Concat(prefix, message.AsSpan(0, end), suffix);
    }

    /// <summary>The noted length of a message already cut by <see cref="Joined"/>, or -1.</summary>
    private static long NotedLength(string message)
    {
        // A cut that dropped a high surrogate is one char shorter.
        if (message.Length < MessageLimit - 1 || message.Length > MessageLimit || !message.EndsWith(" chars)", StringComparison.Ordinal))
        {
            return -1;
        }

        int open = message.LastIndexOf("... (", StringComparison.Ordinal);
        if (open < 0)
        {
            return -1;
        }

        ReadOnlySpan<char> digits = message.AsSpan(open + 5, message.Length - 7 - (open + 5));
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long noted))
        {
            return -1;
        }

        // No string is longer than int.MaxValue, so a larger note is not the SDK's.
        return noted < message.Length || noted > int.MaxValue ? -1 : noted;
    }

    internal static JacquardException Preparation(string message) => new(JacquardErrorKind.Preparation, message, null, null, -1, Nothing);

    /// <summary>A preparation failure caused by <paramref name="cause"/>, such as a caller's source list that threw.</summary>
    internal static JacquardException Preparation(string message, Exception? cause) => new(JacquardErrorKind.Preparation, message, null, cause, -1, Nothing);

    internal static JacquardException Binding(string message) => new(JacquardErrorKind.Binding, message, null, null, -1, Nothing);

    /// <summary>A binding failure caused by <paramref name="cause"/>, such as a caller's codec that threw.</summary>
    internal static JacquardException Binding(string message, Exception? cause) => new(JacquardErrorKind.Binding, message, null, cause, -1, Nothing);

    /// <summary>A successful Core result that could not be mapped; the result is kept.</summary>
    internal static JacquardException Binding(string message, InvocationResult result, Exception? cause) =>
        new(JacquardErrorKind.Binding, message, result, cause, -1, Nothing);

    internal static JacquardException Invalid(string message) => new(JacquardErrorKind.InvalidArgument, message, null, null, -1, Nothing);

    /// <summary>An invalid argument at a value's location, such as <c>shipping-cost.argument[0]</c>.</summary>
    internal static JacquardException InvalidAt(string path, string? message, Exception? cause = null) =>
        new(JacquardErrorKind.InvalidArgument, Joined(path + ": ", message), null, cause, -1, Nothing);

    /// <summary>A failed invocation; <paramref name="result"/> is not ok.</summary>
    internal static JacquardException Invocation(InvocationResult result) =>
        new(JacquardErrorKind.Invocation, result.Message, result, new InvocationException(result), -1, Nothing);

    /// <summary>
    /// <paramref name="error"/> as the failure of batch <paramref name="index"/>,
    /// after the decoded values of the batches before it, copied read-only.
    /// </summary>
    internal static JacquardException Batch(int index, IReadOnlyList<object?> completed, JacquardException error)
    {
        var copy = new object?[completed.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = completed[i];
        }

        return new JacquardException(
            error.Kind,
            Joined("batch " + index.ToString(CultureInfo.InvariantCulture) + ": ", error.Message),
            error.Result,
            error.InnerException,
            index,
            new ReadOnlyCollection<object?>(copy));
    }
}

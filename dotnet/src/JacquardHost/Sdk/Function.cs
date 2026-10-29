using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// A bound checked function. It is immutable and thread-safe; each call runs
/// one fresh, serial worker. Arguments are <see cref="Argument"/>s: plain
/// values convert implicitly, and anything else is passed as
/// <c>codec.Arg(value)</c>. Before any worker starts they are checked
/// against the function's checked signature, the selected limits, and the
/// frame size.
/// </summary>
public sealed class Function
{
    private readonly Hash target;
    private readonly OperationRegistry registry;
    private readonly Host host;

    internal Function(string name, CallInterface signature, Hash target, OperationRegistry registry, Host host, ProgramData data)
    {
        Name = name;
        Signature = signature;
        this.target = target;
        this.registry = registry;
        this.host = host;
        Data = data;
    }

    /// <summary>Test seam: runs after the arguments are measured, just before their types are checked. Null outside tests.</summary>
    internal static volatile Action? ConformHook;

    /// <summary>The public name.</summary>
    public string Name { get; }

    /// <summary>The checked signature.</summary>
    public CallInterface Signature { get; }

    internal ProgramData Data { get; }

    /// <summary>
    /// Refuses a call with the wrong number of arguments from the count
    /// alone, before anything is copied or encoded, so the work below is
    /// bounded by the checked signature, not by what the caller passed.
    /// </summary>
    private void Arity(long received)
    {
        int expected = Signature.Parameters.Count;
        if (received != expected)
        {
            throw JacquardException.Invalid(Name + ": expected " + expected.ToString(CultureInfo.InvariantCulture) + " arguments, received "
                + received.ToString(CultureInfo.InvariantCulture));
        }
    }

    private string ArgumentPath(int index) => Name + ".argument[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>Encodes <paramref name="args"/>, one frame's worth, with one shared budget.</summary>
    private List<Value> Encode(IReadOnlyList<Argument> args)
    {
        Arity(args.Count);
        var budget = new Encoder.Budget();
        var values = new List<Value>(args.Count);
        for (int i = 0; i < args.Count; i++)
        {
            string path = ArgumentPath(i);
            Encoder encoder = Encoder.Root(Data, path, budget);
            if (args[i] is not { } argument)
            {
                throw encoder.Invalid("null has no Jacquard value; pass codec.Arg(value)");
            }

            try
            {
                values.Add(argument.Encode(encoder));
            }
            catch (Exception error) when (Caller.Wraps(error))
            {
                // A caller's codec failed; no worker starts, as for any refusal.
                throw ProgramData.CodecFailed(path, error);
            }
        }

        return values;
    }

    /// <summary>
    /// Validates encoded arguments against the signature and the frame
    /// limits, before any worker starts. Every value is first measured under
    /// one shared budget (nodes, depth, text bytes, collection items),
    /// whatever codec built it, so the type check below walks at most one
    /// frame's worth of values.
    /// </summary>
    private List<Value> Check(List<Value> values)
    {
        Arity(values.Count);
        var measured = new Encoder.Budget();
        for (int i = 0; i < values.Count; i++)
        {
            Encoder.Root(Data, ArgumentPath(i), measured).Raw(values[i]);
        }

        ConformHook?.Invoke();
        for (int i = 0; i < values.Count; i++)
        {
            Data.Conform(values[i], Signature.Parameters[i], ArgumentPath(i));
        }

        Limits limits = host.Limits;
        try
        {
            JsonObject frame = Protocol.Codec.InvokeFrame(target, Signature, values, registry.Entries(), limits);
            JsonCodec.FrameBytes(frame, limits.MaxFrameBytes, limits.MaxJsonDepth);
        }
        catch (ProtocolException error)
        {
            throw JacquardException.Invalid(Name + ": " + error.Message + "; reduce input size or use smaller independent batches");
        }

        return values;
    }

    private Invocation Run(List<Value> values, CancellationToken cancellationToken) =>
        new(host.Invoke(target, Signature, values, registry, cancellationToken), this);

    /// <summary>
    /// Validates the arguments locally, then runs one worker. The exception
    /// is only a local refusal; Core and carrier failures are in the
    /// <see cref="Invocation"/>. Nothing is retried.
    /// </summary>
    /// <exception cref="JacquardException">An invalid argument; no worker started.</exception>
    public Invocation Invoke(params Argument[] args) => Invoke((IReadOnlyList<Argument>)args, default);

    /// <summary>
    /// <see cref="Invoke(Argument[])"/>; cancelling
    /// <paramref name="cancellationToken"/> kills the worker.
    /// </summary>
    /// <exception cref="JacquardException">An invalid argument; no worker started.</exception>
    public Invocation Invoke(IReadOnlyList<Argument> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Run(Check(Encode(args)), cancellationToken);
    }

    /// <summary>Invokes the function and decodes its result.</summary>
    /// <exception cref="JacquardException">
    /// An invalid argument, a failed invocation, or a result that does not
    /// decode (which keeps the successful raw result).
    /// </exception>
    public TResult Call<TResult>(Codec<TResult> result, params Argument[] args) => Call(result, (IReadOnlyList<Argument>)args, default);

    /// <summary><see cref="Call{TResult}(Codec{TResult}, Argument[])"/>; cancelling the token kills the worker.</summary>
    /// <exception cref="JacquardException">An invalid argument, a failed invocation, or a result that does not decode.</exception>
    public TResult Call<TResult>(Codec<TResult> result, IReadOnlyList<Argument> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Invoke(args, cancellationToken).Decode(result);
    }

    /// <summary>
    /// Runs a pure function whose final parameter and result are Lists over
    /// explicit batches of <paramref name="rows"/>, with
    /// <paramref name="leading"/> as the other arguments, and concatenates the
    /// decoded results. Every batch is validated before the first runs, one
    /// at a time: validation returns nothing, so each batch's encoding is
    /// unreachable once it is checked, and the batch is encoded and checked
    /// again just before it runs. Before each batch, and once more after the
    /// last, the validation observes the token. Validation has no deadline of
    /// its own, as in the other SDKs. Nothing is retried. The number of rows
    /// is read once; a list whose count differs before a later batch is
    /// refused.
    /// </summary>
    /// <exception cref="JacquardException">
    /// A refusal before any worker started (<see cref="JacquardException.BatchIndex"/>
    /// is -1): an invalid batch, or a cancellation, which is reported as the
    /// host's aborted invocation without a worker; or the failure of one
    /// batch, with the decoded values of the batches before it in
    /// <see cref="JacquardException.Completed"/>.
    /// </exception>
    public IReadOnlyList<TResult> MapBatches<TRow, TResult>(
        Codec<TResult> item,
        Codec<TRow> row,
        IReadOnlyList<TRow> rows,
        int batchSize,
        IReadOnlyList<Argument>? leading = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(rows);
        leading ??= Array.Empty<Argument>();
        if (batchSize < 1)
        {
            throw JacquardException.Invalid("batch size must be positive");
        }

        Hash? list = Data.Name("list");
        IReadOnlyList<BoundaryType> parameters = Signature.Parameters;
        if (Signature.Effects.Count != 0 || parameters.Count == 0 || !IsList(parameters[^1], list) || !IsList(Signature.Result, list))
        {
            throw JacquardException.Binding("MapBatches needs a pure function with a final List parameter and a List result");
        }

        // From the count alone, before any leading argument is copied.
        Arity(leading.Count + 1L);
        List<Value> empty = Batch(leading, row, rows, 0, 0);

        // Read once: every batch's range comes from this count, so a list
        // that shrinks cannot make a negative batch.
        int total = rows.Count;
        int count = (int)((total + (long)batchSize - 1) / batchSize);
        for (int index = 0; index < count; index++)
        {
            RefuseIfStopped(empty, cancellationToken);
            Unchanged(rows, total);
            Validate(leading, row, rows, total, index, batchSize);
        }

        // A cancellation during the last batch's validation starts no worker.
        RefuseIfStopped(empty, cancellationToken);
        var completed = new List<TResult>();
        Codec<IReadOnlyList<TResult>> results = Codecs.List(item);
        for (int index = 0; index < count; index++)
        {
            try
            {
                Unchanged(rows, total);
                (int start, int size) = Range(total, index, batchSize);
                completed.AddRange(Run(Batch(leading, row, rows, start, size), cancellationToken).Decode(results));
            }
            catch (JacquardException error)
            {
                var done = new object?[completed.Count];
                for (int i = 0; i < done.Length; i++)
                {
                    done[i] = completed[i];
                }

                throw JacquardException.Batch(index, done, error);
            }
        }

        return completed;
    }

    /// <summary>Throws the host's aborted invocation, which starts no worker, once the token is cancelled.</summary>
    private void RefuseIfStopped(List<Value> empty, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            // The host observes the cancellation before it spawns a worker.
            Invocation stopped = Run(empty, cancellationToken);
            if (!stopped.Ok)
            {
                throw JacquardException.Invocation(stopped.Raw);
            }
        }
    }

    /// <summary>Encodes and checks one batch, keeping nothing: the encoding is unreachable once this returns.</summary>
    private void Validate<TRow>(IReadOnlyList<Argument> leading, Codec<TRow> row, IReadOnlyList<TRow> rows, int total, int index, int batchSize)
    {
        (int start, int size) = Range(total, index, batchSize);
        Batch(leading, row, rows, start, size);
    }

    /// <summary>Refuses rows whose count is no longer <paramref name="total"/>.</summary>
    private static void Unchanged<TRow>(IReadOnlyList<TRow> rows, int total)
    {
        int now = rows.Count;
        if (now != total)
        {
            throw JacquardException.Invalid("rows changed from " + total.ToString(CultureInfo.InvariantCulture) + " to "
                + now.ToString(CultureInfo.InvariantCulture) + " during MapBatches");
        }
    }

    private static (int Start, int Size) Range(int total, int index, int batchSize)
    {
        long start = (long)index * batchSize;
        return ((int)start, (int)Math.Min(total - start, batchSize));
    }

    private List<Value> Batch<TRow>(IReadOnlyList<Argument> leading, Codec<TRow> row, IReadOnlyList<TRow> rows, int start, int size)
    {
        var args = new Argument[leading.Count + 1];
        for (int i = 0; i < leading.Count; i++)
        {
            args[i] = leading[i];
        }

        args[^1] = new Rows<TRow>(row, rows, start, size);
        return Check(Encode(args));
    }

    private static bool IsList(BoundaryType type, Hash? list) => list is not null && type is NominalType nominal && nominal.Identity.Equals(list);

    /// <inheritdoc/>
    public override string ToString() => "Function[" + Name + ", " + target.Hex + "]";
}

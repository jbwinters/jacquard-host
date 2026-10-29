using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// One effect's service: operation names mapped to .NET handlers. Names are
/// store spellings such as <c>"lookup-stock"</c>, or display spellings;
/// <c>_</c> is accepted for <c>-</c>. Each handler declares a codec per
/// parameter and one for its result, and may take the
/// <see cref="HandlerContext"/> first, whose <see cref="HandlerContext.CancellationToken"/>
/// is cancelled when the invocation stops or ends:
/// <code>
/// var inventory = new Operations()
///     .On("lookup-stock", Codecs.Text, Codecs.Int, sku =&gt; sku == "sensor" ? 20L : 0L)
///     .On("reserve", Codecs.Text, Codecs.Int, Codecs.Unit, (context, sku, units) =&gt;
///     {
///         context.CancellationToken.ThrowIfCancellationRequested();
///         return Unit.Value;
///     });
/// </code>
/// </summary>
/// <remarks>
/// A request whose arguments do not decode into the parameters, or whose
/// argument count differs, is reported as an outside failure and the handler
/// does not run. Nor does it run once the invocation is cancelled or its
/// lifetime passed while the arguments were decoded. A
/// <see cref="HandlerException"/> is reported as that failure; any other
/// exception is reported as completion unknown, without its message. A
/// result with no encoding within the selected limits is reported as an
/// outside failure, after the handler returned. The service is thread-safe.
/// </remarks>
public sealed class Operations
{
    private readonly SortedDictionary<string, Handler> handlers = new(StringComparer.Ordinal);
    private readonly object sync = new();

    /// <summary>
    /// Test seam: runs on the handler thread after the arguments are decoded
    /// and before the stop check that precedes the handler. Null outside tests.
    /// </summary>
    internal static volatile Action? AfterDecodeHook;

    /// <summary>A typed handler with its codecs applied.</summary>
    internal delegate Value Handler(ProgramData data, HandlerContext context, IReadOnlyList<Value> arguments);

    /// <summary>One parameter's codec, decoding to a boxed value.</summary>
    private interface ICodecBox
    {
        object? Decode(Decoder decoder, Value value);
    }

    /// <summary>How many operations this service binds, without copying them.</summary>
    internal int Count
    {
        get
        {
            lock (sync)
            {
                return handlers.Count;
            }
        }
    }

    /// <summary>
    /// The handlers by store-spelled operation name, or null when there are
    /// more than <paramref name="limit"/>: the count is checked before
    /// anything is copied, under the same lock as the copy.
    /// </summary>
    internal IReadOnlyDictionary<string, Handler>? Snapshot(int limit)
    {
        lock (sync)
        {
            return handlers.Count > limit ? null : new ReadOnlyDictionary<string, Handler>(new Dictionary<string, Handler>(handlers, StringComparer.Ordinal));
        }
    }

    private sealed class CodecBox<T> : ICodecBox
    {
        private readonly Codec<T> codec;

        internal CodecBox(Codec<T> codec)
        {
            this.codec = codec;
        }

        public object? Decode(Decoder decoder, Value value) => codec.Decode(decoder, value);
    }

    private static CodecBox<T> Box<T>(Codec<T> codec) => new CodecBox<T>(codec ?? throw new ArgumentNullException(nameof(codec)));

    private Operations Bind<TResult>(string operation, ICodecBox[] parameters, Codec<TResult> result, Func<HandlerContext, object?[], TResult> invoke)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(result);
        Handler handler = (data, context, arguments) =>
        {
            if (arguments.Count != parameters.Length)
            {
                throw HandlerException.OutsideFailed("handler parameter count differs from the request");
            }

            var decoder = new Decoder(data);
            object?[] values = new object?[parameters.Length];
            for (int i = 0; i < values.Length; i++)
            {
                try
                {
                    values[i] = parameters[i].Decode(decoder, arguments[i]);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    throw HandlerException.OutsideFailed("handler argument cannot be mapped");
                }
            }

            AfterDecodeHook?.Invoke();

            // Decoding takes time; no outside action begins once the call stopped.
            if (context.IsCancelled)
            {
                throw HandlerException.OutsideFailed("handler was not started");
            }

            TResult value = invoke(context, values);
            try
            {
                return Encoder.Root(data, "result").Encode(result, value);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                throw HandlerException.OutsideFailed("handler result cannot be represented");
            }
        };
        string key = Identities.Normal(operation);
        lock (sync)
        {
            handlers[key] = handler;
        }

        return this;
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler of 0 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TResult>(string operation, Codec<TResult> result, Func<TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, Array.Empty<ICodecBox>(), result, (context, values) => handler());
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler that takes the <see cref="HandlerContext"/> first, of 0 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TResult>(string operation, Codec<TResult> result, Func<HandlerContext, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, Array.Empty<ICodecBox>(), result, (context, values) => handler(context));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler of 1 decoded parameter, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TResult>(string operation, Codec<TA> first, Codec<TResult> result, Func<TA, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first) }, result, (context, values) => handler((TA)values[0]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler that takes the <see cref="HandlerContext"/> first, of 1 decoded parameter, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TResult>(string operation, Codec<TA> first, Codec<TResult> result, Func<HandlerContext, TA, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first) }, result, (context, values) => handler(context, (TA)values[0]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler of 2 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TResult> result, Func<TA, TB, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second) }, result, (context, values) => handler((TA)values[0]!, (TB)values[1]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler that takes the <see cref="HandlerContext"/> first, of 2 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TResult> result, Func<HandlerContext, TA, TB, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second) }, result, (context, values) => handler(context, (TA)values[0]!, (TB)values[1]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler of 3 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TResult> result, Func<TA, TB, TC, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third) }, result, (context, values) => handler((TA)values[0]!, (TB)values[1]!, (TC)values[2]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler that takes the <see cref="HandlerContext"/> first, of 3 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TResult> result, Func<HandlerContext, TA, TB, TC, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third) }, result, (context, values) => handler(context, (TA)values[0]!, (TB)values[1]!, (TC)values[2]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler of 4 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="fourth">Decodes parameter 3.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TD, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TD> fourth, Codec<TResult> result, Func<TA, TB, TC, TD, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third), Box(fourth) }, result, (context, values) => handler((TA)values[0]!, (TB)values[1]!, (TC)values[2]!, (TD)values[3]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler that takes the <see cref="HandlerContext"/> first, of 4 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="fourth">Decodes parameter 3.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TD, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TD> fourth, Codec<TResult> result, Func<HandlerContext, TA, TB, TC, TD, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third), Box(fourth) }, result, (context, values) => handler(context, (TA)values[0]!, (TB)values[1]!, (TC)values[2]!, (TD)values[3]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler of 5 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="fourth">Decodes parameter 3.</param>
    /// <param name="fifth">Decodes parameter 4.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TD, TE, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TD> fourth, Codec<TE> fifth, Codec<TResult> result, Func<TA, TB, TC, TD, TE, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third), Box(fourth), Box(fifth) }, result, (context, values) => handler((TA)values[0]!, (TB)values[1]!, (TC)values[2]!, (TD)values[3]!, (TE)values[4]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler that takes the <see cref="HandlerContext"/> first, of 5 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="fourth">Decodes parameter 3.</param>
    /// <param name="fifth">Decodes parameter 4.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TD, TE, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TD> fourth, Codec<TE> fifth, Codec<TResult> result, Func<HandlerContext, TA, TB, TC, TD, TE, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third), Box(fourth), Box(fifth) }, result, (context, values) => handler(context, (TA)values[0]!, (TB)values[1]!, (TC)values[2]!, (TD)values[3]!, (TE)values[4]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler of 6 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="fourth">Decodes parameter 3.</param>
    /// <param name="fifth">Decodes parameter 4.</param>
    /// <param name="sixth">Decodes parameter 5.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TD, TE, TF, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TD> fourth, Codec<TE> fifth, Codec<TF> sixth, Codec<TResult> result, Func<TA, TB, TC, TD, TE, TF, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third), Box(fourth), Box(fifth), Box(sixth) }, result, (context, values) => handler((TA)values[0]!, (TB)values[1]!, (TC)values[2]!, (TD)values[3]!, (TE)values[4]!, (TF)values[5]!));
    }

    /// <summary>
    /// Serves <paramref name="operation"/> with a handler that takes the <see cref="HandlerContext"/> first, of 6 decoded parameters, replacing any earlier handler for it.
    /// </summary>
    /// <param name="operation">The operation's name.</param>
    /// <param name="first">Decodes parameter 0.</param>
    /// <param name="second">Decodes parameter 1.</param>
    /// <param name="third">Decodes parameter 2.</param>
    /// <param name="fourth">Decodes parameter 3.</param>
    /// <param name="fifth">Decodes parameter 4.</param>
    /// <param name="sixth">Decodes parameter 5.</param>
    /// <param name="result">Encodes the handler's result (<see cref="Codecs.Unit"/> for unit).</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This service.</returns>
    public Operations On<TA, TB, TC, TD, TE, TF, TResult>(string operation, Codec<TA> first, Codec<TB> second, Codec<TC> third, Codec<TD> fourth, Codec<TE> fifth, Codec<TF> sixth, Codec<TResult> result, Func<HandlerContext, TA, TB, TC, TD, TE, TF, TResult> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Bind(operation, new ICodecBox[] { Box(first), Box(second), Box(third), Box(fourth), Box(fifth), Box(sixth) }, result, (context, values) => handler(context, (TA)values[0]!, (TB)values[1]!, (TC)values[2]!, (TD)values[3]!, (TE)values[4]!, (TF)values[5]!));
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        lock (sync)
        {
            return "Operations[" + string.Join(", ", handlers.Keys) + "]";
        }
    }
}

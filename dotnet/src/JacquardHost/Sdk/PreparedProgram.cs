using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// A prepared, verified program. Bind its checked functions with
/// <see cref="Function(string, IReadOnlyDictionary{string, Operations}?)"/>.
/// <see cref="Provide"/> changes the services of functions bound afterwards;
/// bound functions are immutable and thread-safe.
/// </summary>
public sealed class PreparedProgram
{
    private readonly object sync = new();
    private readonly SortedDictionary<string, CallInterface> interfaces = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, EffectGroup> effects = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, Operations> services = new(StringComparer.Ordinal);

    /// <summary>Why each checked function without a binding has none.</summary>
    private readonly SortedDictionary<string, string> unsupported = new(StringComparer.Ordinal);

    private Host host;

    [SuppressMessage("Design", "CA1031", Justification = "No exception from reading one signature may fail the preparation; the function is left unbound with the reason.")]
    internal PreparedProgram(Runtime runtime, string directory, Runtime.Bindings bindings, string executable)
    {
        Runtime = runtime;
        Directory = directory;
        var names = new Dictionary<string, Hash>(StringComparer.Ordinal);
        foreach (Identities.Entry entry in bindings.Names)
        {
            names[entry.Name] = entry.Hash;
        }

        var constructors = new Dictionary<string, Hash>(StringComparer.Ordinal);
        foreach (Identities.Entry entry in bindings.Constructors)
        {
            constructors[entry.Name] = entry.Hash;
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string key && variable.Value is string value)
            {
                environment[key] = value;
            }
        }

        environment["JACQUARD_PRELUDE"] = runtime.Prelude;
        var carrier = new WorkerOptions { Command = new[] { executable }, Environment = environment, Deadline = runtime.Deadline };
        try
        {
            host = new Host(Path.Combine(directory, "store"), new HostOptions { Carrier = carrier, Limits = runtime.Limits });
        }
        catch (ArgumentException error)
        {
            throw JacquardException.Binding(error.Message);
        }

        var used = new HashSet<Hash>();
        foreach (KeyValuePair<string, string> signature in bindings.Signatures)
        {
            try
            {
                CallInterface parsed = Identities.ParseSignature(signature.Value, names);
                used.UnionWith(parsed.Effects);
                interfaces[signature.Key] = parsed;
            }
            catch (JacquardException reason)
            {
                // Polymorphic, too deep, and other unsupported signatures
                // have no binding; the other functions keep theirs.
                unsupported[signature.Key] = reason.Message;
            }
            catch (Exception unexpected) when (unexpected is not OutOfMemoryException)
            {
                // The parser's bounds make this unreachable; nothing escapes preparation either way.
                unsupported[signature.Key] = "signature cannot be read (" + Identities.Describe(unexpected) + ")";
            }
        }

        foreach (IReadOnlyList<Identities.Entry> group in bindings.Groups)
        {
            foreach (Identities.Entry entry in group)
            {
                if (used.Contains(entry.Hash))
                {
                    effects[entry.Name] = new EffectGroup(entry.Hash, group.Where(other => !other.Hash.Equals(entry.Hash)).ToList());
                }
            }
        }

        Data = new ProgramData(names, constructors, bindings.Groups, runtime.Limits);
    }

    private sealed record EffectGroup(Hash Identity, IReadOnlyList<Identities.Entry> Operations);

    /// <summary>The runtime that prepared this program.</summary>
    public Runtime Runtime { get; }

    /// <summary>The verified cache entry.</summary>
    public string Directory { get; }

    /// <summary>The program's names and identities.</summary>
    public ProgramData Data { get; }

    /// <summary>The checked functions that have a binding, by public name.</summary>
    public IReadOnlyList<string> Functions
    {
        get
        {
            lock (sync)
            {
                return interfaces.Keys.ToList();
            }
        }
    }

    /// <summary>Test seam: whether the calling thread holds the program's lock.</summary>
    internal bool LockHeld => System.Threading.Monitor.IsEntered(sync);

    /// <summary>Test seam: replaces the worker command for functions bound afterwards.</summary>
    internal void SetWorkerCommand(IReadOnlyList<string> command, IReadOnlyDictionary<string, string>? environment = null)
    {
        lock (sync)
        {
            host = new Host(host.Store, new HostOptions
            {
                Carrier = new WorkerOptions { Command = command, Environment = environment, Deadline = Runtime.Deadline },
                Limits = host.Limits,
            });
        }
    }

    /// <summary>
    /// Configures an effect's service, by effect name such as
    /// <c>"Inventory"</c>, for every function bound afterwards.
    /// </summary>
    /// <exception cref="JacquardException">A binding failure for an effect no checked function uses.</exception>
    public PreparedProgram Provide(string effect, Operations operations)
    {
        ArgumentNullException.ThrowIfNull(effect);
        ArgumentNullException.ThrowIfNull(operations);
        lock (sync)
        {
            string key = Identities.Normal(effect, "effect");
            if (!effects.ContainsKey(key))
            {
                throw JacquardException.Binding("unknown effect " + Identities.Excerpt(effect) + (unsupported.Count == 0 ? string.Empty
                    : "; the effects of functions with unsupported signatures (" + string.Join(", ", unsupported.Keys.Select(Identities.Excerpt)) + ") are not bound"));
            }

            services[key] = operations;
            return this;
        }
    }

    /// <summary>
    /// Binds a checked public function with services for its effects, by
    /// effect name, overriding <see cref="Provide"/>. Every operation of each
    /// checked root effect must have a handler, and every name a service
    /// binds must be an operation of the program.
    /// </summary>
    /// <exception cref="JacquardException">
    /// A binding failure for an unknown function or effect, a missing
    /// handler, or a name that is no operation.
    /// </exception>
    public Function Function(string name, IReadOnlyDictionary<string, Operations>? bindings = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Identities.Bounded(name, "function");
        bindings ??= new Dictionary<string, Operations>();

        // The caller's dictionary is read here, before the program's lock is
        // taken, so its code cannot hold that lock: its count once, and its
        // entries only when they are no more than the program's effects, at
        // most one past them. The effects are fixed when the program is made.
        int declared = bindings.Count;
        var given = new List<KeyValuePair<string, Operations>>();
        if (declared <= effects.Count)
        {
            foreach (KeyValuePair<string, Operations> binding in bindings)
            {
                given.Add(binding);
                if (given.Count > effects.Count)
                {
                    break;
                }
            }
        }

        lock (sync)
        {
            if (!interfaces.TryGetValue(name, out CallInterface? signature))
            {
                string? reason = unsupported.TryGetValue(name, out string? why) ? why : null;
                throw JacquardException.Binding(Identities.Excerpt(name) + ": absent or unsupported closed v0 signature" + (reason is null ? string.Empty : ": " + reason));
            }

            Hash target = Data.Name(name) ?? throw JacquardException.Binding(Identities.Excerpt(name) + ": no public identity");
            var selected = new SortedDictionary<string, EffectGroup>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, EffectGroup> effect in effects)
            {
                if (signature.Effects.Contains(effect.Value.Identity))
                {
                    selected[effect.Key] = effect.Value;
                }
            }

            if (selected.Count != signature.Effects.Count)
            {
                throw JacquardException.Binding(name + ": effect metadata is unavailable; use the low-level host package");
            }

            // Each binding must name one of the function's effects, so more
            // bindings than effects are refused from the count, before any is read.
            if (declared > selected.Count)
            {
                throw JacquardException.Binding(name + ": " + declared.ToString(CultureInfo.InvariantCulture) + " bindings given, but it uses "
                    + selected.Count.ToString(CultureInfo.InvariantCulture) + " effect" + (selected.Count == 1 ? string.Empty : "s")
                    + (selected.Count == 0 ? string.Empty : ": " + string.Join(", ", selected.Keys)));
            }

            var chosen = new SortedDictionary<string, Operations>(services, StringComparer.Ordinal);
            int read = 0;
            foreach (KeyValuePair<string, Operations> binding in given)
            {
                // A dictionary whose count understated its entries was not read past the bound.
                if (++read > selected.Count)
                {
                    throw JacquardException.Binding(name + ": more bindings than effects");
                }

                ArgumentNullException.ThrowIfNull(binding.Key, nameof(bindings));
                string key = Identities.Normal(binding.Key, "effect");
                if (!selected.ContainsKey(key))
                {
                    throw JacquardException.Binding(name + " does not use " + Identities.Excerpt(binding.Key));
                }

                chosen[key] = binding.Value ?? throw new ArgumentNullException(nameof(bindings), "operations");
            }

            var operationNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (EffectGroup effect in effects.Values)
            {
                foreach (Identities.Entry operation in effect.Operations)
                {
                    operationNames.Add(operation.Name);
                }
            }

            var registry = new OperationRegistry();
            var missing = new List<string>();
            foreach (KeyValuePair<string, EffectGroup> effect in selected)
            {
                IReadOnlyDictionary<string, Operations.Handler> handlers = new Dictionary<string, Operations.Handler>();
                if (chosen.TryGetValue(effect.Key, out Operations? service))
                {
                    // Every name a service binds must be an operation of the
                    // program, so a service binding more is refused from its
                    // count, before any is copied.
                    handlers = service.Snapshot(operationNames.Count)
                        ?? throw JacquardException.Binding(name + ": the service for " + effect.Key + " binds more operations than the program's "
                            + operationNames.Count.ToString(CultureInfo.InvariantCulture));
                }

                // One service may serve several effects, but every name it
                // binds must be some effect's operation.
                foreach (string key in handlers.Keys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    if (!operationNames.Contains(key))
                    {
                        throw JacquardException.Binding(name + ": " + effect.Key + " has no operation " + Identities.Excerpt(key));
                    }
                }

                foreach (Identities.Entry operation in effect.Value.Operations)
                {
                    if (!handlers.TryGetValue(operation.Name, out Operations.Handler? handler))
                    {
                        missing.Add(effect.Key + "." + operation.Name);
                        continue;
                    }

                    ProgramData data = Data;
                    try
                    {
                        registry.Bind(effect.Value.Identity, operation.Hash, (context, arguments) => handler(data, context, arguments));
                    }
                    catch (ArgumentException error)
                    {
                        throw JacquardException.Binding(name + ": " + error.Message);
                    }
                }
            }

            if (missing.Count != 0)
            {
                throw JacquardException.Binding(name + ": missing handlers: " + string.Join(", ", missing)
                    + ". Bind every operation in each declared effect before calling");
            }

            return new Function(name, signature, target, registry, host, Data);
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        lock (sync)
        {
            return "PreparedProgram[" + Directory + ", functions=" + string.Join(", ", interfaces.Keys) + "]";
        }
    }
}

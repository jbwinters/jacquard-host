package org.jacquardlang.host.sdk;

import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

import org.jacquardlang.host.Host;
import org.jacquardlang.host.HostOptions;
import org.jacquardlang.host.OperationRegistry;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;

/**
 * A prepared, verified program. Bind its checked functions with
 * {@link #function}. {@link #provide} changes the services of functions bound
 * afterwards; bound functions are immutable and thread-safe.
 */
public final class Program {
    private record EffectGroup(Hash identity, List<Identities.Entry> operations) {}

    private final Runtime runtime;
    private final Path directory;
    private final Data data;
    private volatile Host host;
    private final Map<String, Interface> interfaces = new TreeMap<>();
    private final Map<String, EffectGroup> effects = new TreeMap<>();
    private final Map<String, Operations> services = new TreeMap<>();
    /** Why each checked function without a binding has none. */
    private final Map<String, String> unsupported = new TreeMap<>();

    Program(Runtime runtime, Path directory, Runtime.Metadata metadata, Path executable) throws JacquardException {
        this.runtime = runtime;
        this.directory = directory;
        Map<String, Hash> names = new HashMap<>();
        for (Identities.Entry entry : metadata.names()) {
            names.put(entry.name(), entry.hash());
        }
        Map<String, Hash> constructors = new HashMap<>();
        for (Identities.Entry entry : metadata.constructors()) {
            constructors.put(entry.name(), entry.hash());
        }
        Map<String, String> env = new HashMap<>(System.getenv());
        env.put("JACQUARD_PRELUDE", runtime.prelude().toString());
        Options carrier = new Options(List.of(executable.toString()), env, runtime.deadline());
        try {
            this.host = new Host(directory.resolve("store").toString(), new HostOptions(carrier, runtime.limits()));
        } catch (IllegalArgumentException error) {
            throw JacquardException.binding(error.getMessage());
        }
        Set<Hash> used = new HashSet<>();
        for (Map.Entry<String, String> signature : metadata.signatures().entrySet()) {
            try {
                Interface parsed = Identities.parseSignature(signature.getValue(), names);
                used.addAll(parsed.effects());
                interfaces.put(signature.getKey(), parsed);
            } catch (JacquardException unsupported) {
                // Polymorphic, too deep, and other unsupported signatures
                // have no binding; the other functions keep theirs.
                this.unsupported.put(signature.getKey(), unsupported.getMessage());
            } catch (RuntimeException | StackOverflowError unexpected) {
                // The parser's bounds make this unreachable; no Error escapes preparation either way.
                this.unsupported.put(signature.getKey(), "signature cannot be read (" + unexpected + ")");
            }
        }
        for (List<Identities.Entry> group : metadata.groups()) {
            for (Identities.Entry entry : group) {
                if (used.contains(entry.hash())) {
                    List<Identities.Entry> operations = new ArrayList<>();
                    for (Identities.Entry other : group) {
                        if (!other.hash().equals(entry.hash())) {
                            operations.add(other);
                        }
                    }
                    effects.put(entry.name(), new EffectGroup(entry.hash(), List.copyOf(operations)));
                }
            }
        }
        this.data = new Data(names, constructors, metadata.groups(), runtime.limits());
    }

    /** The runtime that prepared this program. */
    public Runtime runtime() {
        return runtime;
    }

    /** The verified cache entry. */
    public Path directory() {
        return directory;
    }

    /** The program's names and identities. */
    public Data data() {
        return data;
    }

    /** Test seam: replaces the worker command for functions bound afterwards. */
    void setWorkerCommand(List<String> command) {
        Host current = host;
        Options carrier = new Options(command, null, runtime.deadline());
        host = new Host(current.store(), new HostOptions(carrier, current.limits()));
    }

    /**
     * Configures an effect's service, by effect name such as
     * {@code "Inventory"}, for every function bound afterwards.
     *
     * @throws JacquardException a binding failure for an effect no checked function uses
     */
    public synchronized Program provide(String effect, Operations operations) throws JacquardException {
        java.util.Objects.requireNonNull(operations, "operations");
        if (!effects.containsKey(Identities.normal(effect))) {
            throw JacquardException.binding("unknown effect " + Identities.excerpt(effect) + (unsupported.isEmpty() ? ""
                    : "; the effects of functions with unsupported signatures (" + String.join(", ", unsupported.keySet()) + ") are not bound"));
        }
        services.put(Identities.normal(effect), operations);
        return this;
    }

    /** Binds a checked public function, using the provided services. */
    public Function function(String name) throws JacquardException {
        return function(name, Map.of());
    }

    /**
     * Binds a checked public function with services for its effects, by
     * effect name, overriding {@link #provide}. Every operation of each
     * checked root effect must have a handler, and every name a service binds
     * must be an operation of the program.
     *
     * @throws JacquardException a binding failure for an unknown function or
     *     effect, a missing handler, or a name that is no operation
     */
    public synchronized Function function(String name, Map<String, Operations> bindings) throws JacquardException {
        Interface signature = interfaces.get(name);
        if (signature == null) {
            String reason = unsupported.get(name);
            throw JacquardException.binding(Identities.excerpt(name) + ": absent or unsupported closed v0 signature" + (reason == null ? "" : ": " + reason));
        }
        Hash target = data.name(name);
        if (target == null) {
            throw JacquardException.binding(name + ": no public identity");
        }
        Map<String, EffectGroup> selected = new TreeMap<>();
        effects.forEach((effectName, effect) -> {
            if (signature.effects().contains(effect.identity())) {
                selected.put(effectName, effect);
            }
        });
        if (selected.size() != signature.effects().size()) {
            throw JacquardException.binding(name + ": effect metadata is unavailable; use the low-level host package");
        }
        Map<String, Operations> chosen = new TreeMap<>(services);
        for (Map.Entry<String, Operations> binding : new TreeMap<>(bindings).entrySet()) {
            if (!selected.containsKey(Identities.normal(binding.getKey()))) {
                throw JacquardException.binding(name + " does not use " + Identities.excerpt(binding.getKey()));
            }
            chosen.put(Identities.normal(binding.getKey()), java.util.Objects.requireNonNull(binding.getValue(), "operations"));
        }
        Set<String> operationNames = new HashSet<>();
        for (EffectGroup effect : effects.values()) {
            for (Identities.Entry operation : effect.operations()) {
                operationNames.add(operation.name());
            }
        }
        OperationRegistry registry = new OperationRegistry();
        List<String> missing = new ArrayList<>();
        for (Map.Entry<String, EffectGroup> effect : selected.entrySet()) {
            Operations service = chosen.get(effect.getKey());
            Map<String, Operations.Erased> handlers = service == null ? Map.of() : service.handlers();
            // One service may serve several effects, but every name it binds
            // must be some effect's operation.
            for (String key : new TreeMap<>(handlers).keySet()) {
                if (!operationNames.contains(key)) {
                    throw JacquardException.binding(name + ": " + effect.getKey() + " has no operation " + Identities.excerpt(key));
                }
            }
            for (Identities.Entry operation : effect.getValue().operations()) {
                Operations.Erased handler = handlers.get(operation.name());
                if (handler == null) {
                    missing.add(effect.getKey() + "." + operation.name());
                    continue;
                }
                try {
                    registry.bind(effect.getValue().identity(), operation.hash(),
                            (context, arguments) -> handler.handle(data, context, arguments));
                } catch (IllegalArgumentException error) {
                    throw JacquardException.binding(name + ": " + error.getMessage());
                }
            }
        }
        if (!missing.isEmpty()) {
            throw JacquardException.binding(name + ": missing handlers: " + String.join(", ", missing)
                    + ". Bind every operation in each declared effect before calling");
        }
        return new Function(name, signature, target, registry, host, data);
    }

    @Override
    public String toString() {
        return "Program[" + directory + ", functions=" + interfaces.keySet() + "]";
    }
}

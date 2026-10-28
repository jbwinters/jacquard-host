package org.jacquardlang.host.readme;

import java.time.Duration;
import java.util.List;

import org.jacquardlang.host.HandlerException;
import org.jacquardlang.host.Host;
import org.jacquardlang.host.HostOptions;
import org.jacquardlang.host.InvocationException;
import org.jacquardlang.host.OperationRegistry;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Value;

public final class Echo {
    public static Value echo(String store, Hash target, Hash text, Hash world, Hash send) throws InvocationException {
        // The command defaults to $JACQUARD_BIN, then `jacquard`.
        Options carrier = Options.defaults().withDeadline(Duration.ofSeconds(5));
        Host host = new Host(store, new HostOptions(carrier, null));
        OperationRegistry registry = new OperationRegistry().bind(world, send, (context, args) -> {
            if (context.isCancelled()) {
                throw HandlerException.completionUnknown("cancelled");
            }
            if (args.size() == 1 && args.get(0) instanceof Value.Text input) {
                return new Value.Text(input.value() + "!");
            }
            throw HandlerException.outsideFailed("expected one text argument");
        });
        BoundaryType textType = new BoundaryType.Nominal(text, List.of());
        Interface spec = new Interface(List.of(textType), textType, List.of(world));
        return host.invoke(target, spec, List.of(new Value.Text("ping")), registry, new Cancel()).valueOrThrow();
    }
}

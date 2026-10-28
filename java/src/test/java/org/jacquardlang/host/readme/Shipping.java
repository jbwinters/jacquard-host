package org.jacquardlang.host.readme;

import java.io.IOException;
import java.nio.file.Path;
import java.util.List;

import org.jacquardlang.host.sdk.Codec;
import org.jacquardlang.host.sdk.Function;
import org.jacquardlang.host.sdk.Invocation;
import org.jacquardlang.host.sdk.JacquardException;
import org.jacquardlang.host.sdk.PrepareOptions;
import org.jacquardlang.host.sdk.Program;
import org.jacquardlang.host.sdk.Runtime;
import org.jacquardlang.host.sdk.RuntimeOptions;

public final class Shipping {
    public static long shipping(Path rules, Path cache, Path saved) throws JacquardException, IOException {
        // Core and its prelude default to $JACQUARD_BIN and $JACQUARD_PRELUDE.
        Runtime runtime = new Runtime(RuntimeOptions.defaults());
        Program program = runtime.prepare(List.of(rules), new PrepareOptions(cache));
        Function shipping = program.function("shipping-cost");
        long cost = shipping.call(Codec.INT, 35);

        Invocation invocation = shipping.invoke(75);
        System.out.println(invocation.render());
        invocation.save(saved);
        return cost;
    }
}

package org.jacquardlang.host.readme;

import java.util.Map;

import org.jacquardlang.host.HandlerContext;
import org.jacquardlang.host.HandlerException;
import org.jacquardlang.host.sdk.Codec;
import org.jacquardlang.host.sdk.Function;
import org.jacquardlang.host.sdk.JacquardException;
import org.jacquardlang.host.sdk.Operations;
import org.jacquardlang.host.sdk.Program;

public final class Documents {
    public static Function summarize(Program program) throws JacquardException {
        Operations documents = new Operations().on("read-document", Codec.TEXT, Codec.TEXT, (HandlerContext context, String key) -> {
            if (context.isCancelled()) {
                throw HandlerException.completionUnknown("cancelled");
            }
            if (key.equals("example")) {
                return "A synthetic document supplied by Java.";
            }
            throw HandlerException.refused("unknown document");
        });
        return program.function("summarize", Map.of("Documents", documents));
    }
}

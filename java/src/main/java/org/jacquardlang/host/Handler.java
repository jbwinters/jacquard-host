package org.jacquardlang.host;

import java.util.List;

import org.jacquardlang.host.protocol.Value;

/** One effect operation's handler. */
@FunctionalInterface
public interface Handler {
    /**
     * Performs the operation. Throw {@link HandlerException} for a structured
     * failure; any other exception is reported as completion unknown, without
     * its message.
     */
    Value handle(HandlerContext context, List<Value> arguments) throws Exception;
}

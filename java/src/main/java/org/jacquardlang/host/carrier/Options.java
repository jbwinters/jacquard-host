package org.jacquardlang.host.carrier;

import java.time.Duration;
import java.util.List;
import java.util.Map;

/**
 * Options for one worker lifetime.
 *
 * @param command the Core executable and leading arguments; {@code host worker --store DIR}
 *     is appended. Empty means {@code $JACQUARD_BIN}, then {@code jacquard}.
 * @param env replaces the inherited environment when not null
 * @param deadline bounds the whole lifetime: spawn, pipes, handlers, and exit; null means
 *     {@link Worker#DEFAULT_DEADLINE}
 */
public record Options(List<String> command, Map<String, String> env, Duration deadline) {
    public Options {
        command = command == null ? List.of() : List.copyOf(command);
        env = env == null ? null : Map.copyOf(env);
    }

    /** The default command, inherited environment, and default deadline. */
    public static Options defaults() {
        return new Options(List.of(), null, null);
    }

    /** These options with another command. */
    public Options withCommand(List<String> value) {
        return new Options(value, env, deadline);
    }

    /** These options with a replacement environment. */
    public Options withEnv(Map<String, String> value) {
        return new Options(command, value, deadline);
    }

    /** These options with another lifetime deadline. */
    public Options withDeadline(Duration value) {
        return new Options(command, env, value);
    }

    /** Refuses an empty command element or a deadline that is not positive. */
    public void validate() {
        if (command.stream().anyMatch(String::isEmpty)) {
            throw new IllegalArgumentException("command must be a nonempty executable argument list");
        }
        if (deadline != null && (deadline.isZero() || deadline.isNegative())) {
            throw new IllegalArgumentException("deadline must be positive");
        }
    }

    List<String> resolvedCommand() {
        if (!command.isEmpty()) {
            return command;
        }
        String bin = System.getenv("JACQUARD_BIN");
        return List.of(bin == null || bin.isEmpty() ? "jacquard" : bin);
    }
}

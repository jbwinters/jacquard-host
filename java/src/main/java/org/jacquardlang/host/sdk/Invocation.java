package org.jacquardlang.host.sdk;

import java.io.IOException;
import java.io.OutputStream;
import java.nio.channels.Channels;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.OpenOption;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.nio.file.attribute.FileAttribute;
import java.nio.file.attribute.PosixFilePermissions;
import java.util.ArrayList;
import java.util.Base64;
import java.util.List;
import java.util.Map;
import java.util.Set;

import org.jacquardlang.host.InvocationResult;
import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.EffectRequest;
import org.jacquardlang.host.protocol.Value;

/** One completed invocation with its raw evidence. */
public final class Invocation {
    private final InvocationResult raw;
    private final Function function;

    Invocation(InvocationResult raw, Function function) {
        this.raw = raw;
        this.function = function;
    }

    /** The host's result: Core's frames, the host's frames, and evidence. */
    public InvocationResult raw() {
        return raw;
    }

    /** Whether Core reported success. */
    public boolean ok() {
        return raw.ok();
    }

    /** The primary diagnostic or host code. */
    public String code() {
        return raw.code();
    }

    /** A summary for people. */
    public String message() {
        return raw.message();
    }

    /** The classified failure, or null on success. */
    public JacquardException error() {
        return ok() ? null : JacquardException.invocation(raw);
    }

    /**
     * Decodes the result.
     *
     * @throws JacquardException the invocation's failure, or a binding failure
     *     keeping the successful raw result when it does not decode
     */
    public <R> R decode(Codec<R> codec) throws JacquardException {
        JacquardException error = error();
        if (error != null) {
            throw error;
        }
        Value value = raw.value();
        if (value == null) {
            throw JacquardException.binding(function.name() + ": Core completed without a value", raw);
        }
        try {
            return function.data().decode(codec, value);
        } catch (JacquardException mapping) {
            // Already bounded, and the SDK's own words. When the caller's
            // codec or record constructor failed, its exception is the cause.
            Throwable cause = mapping.getCause() != null ? mapping.getCause() : mapping;
            throw JacquardException.binding(JacquardException.joined(function.name() + ": Core completed, but result mapping failed: ", mapping.getMessage()),
                    raw, cause);
        }
    }

    /** Decodes the result into {@link Dynamic}. */
    public Dynamic value() throws JacquardException {
        return decode(Codec.DYNAMIC);
    }

    /** Lists the outcome and each effect request by operation name. */
    public String render() {
        String status = ok() ? "ok" : message();
        List<String> lines = new ArrayList<>();
        lines.add(function.name() + ": " + status);
        for (EffectRequest request : raw.requests()) {
            String label = function.data().label(request.operation());
            lines.add("  " + request.ordinal() + ". " + (label != null ? label : request.operation().hex()));
        }
        return String.join("\n", lines);
    }

    private static Json frames(List<Map<String, Json>> frames) {
        List<Json> items = new ArrayList<>();
        for (Map<String, Json> frame : frames) {
            items.add(Json.of(frame));
        }
        return Json.of(items);
    }

    /**
     * Writes the invocation's frames and host evidence. The record contains
     * argument and result values; it is written only when called. A new file
     * is created owner-only where the file system supports POSIX permissions;
     * an existing file keeps its permissions.
     */
    public void save(Path path) throws IOException {
        if (path.toString().length() > Core.PATH_LIMIT) {
            throw new IOException(Identities.excerpt(path.toString()) + " is longer than any path");
        }
        Map<String, Json> host = Json.object(
                "evidence", Json.of(raw.hostEvidence()),
                "exit_status", raw.exitStatus() == null ? Json.NULL : Json.of(raw.exitStatus()),
                "signal", raw.signal() == null ? Json.NULL : Json.of(raw.signal()),
                "stderr_base64", Json.of(Base64.getEncoder().encodeToString(raw.stderr())));
        Map<String, Json> record = Json.object(
                "core_frames", frames(raw.coreFrames()),
                "function", Json.of(function.name()),
                "host", Json.of(host),
                "host_frames", frames(raw.hostFrames()),
                "schema", Json.of("jacquard-host-inspection-v1"));
        String text;
        try {
            text = JsonCodec.stringify(Json.of(record), 128);
        } catch (ProtocolException error) {
            throw new IOException(error.getMessage(), error);
        }
        Set<OpenOption> options = Set.of(StandardOpenOption.WRITE, StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING);
        FileAttribute<?> ownerOnly = PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rw-------"));
        byte[] bytes = (text + "\n").getBytes(StandardCharsets.UTF_8);
        try (OutputStream out = open(path, options, ownerOnly)) {
            out.write(bytes);
        }
    }

    private static OutputStream open(Path path, Set<OpenOption> options, FileAttribute<?> ownerOnly) throws IOException {
        try {
            return Channels.newOutputStream(Files.newByteChannel(path, options, ownerOnly));
        } catch (UnsupportedOperationException error) {
            // No POSIX permissions here.
            return Channels.newOutputStream(Files.newByteChannel(path, options));
        }
    }

    @Override
    public String toString() {
        return "Invocation[" + function.name() + ", " + raw + "]";
    }
}

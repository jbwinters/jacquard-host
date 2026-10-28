package org.jacquardlang.host.kit;

import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.ArrayList;
import java.util.HexFormat;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

import org.jacquardlang.host.Compat;
import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.Hash;

/**
 * The pinned {@code host-protocol-v0} conformance kit, carried as class-path
 * resources and verified against the pinned digests when loaded.
 *
 * @param manifest the decoded {@code kit/kit.json}
 * @param transcripts the recordings, in kit order
 * @param identities the kit's named identities, such as {@code "kit-echo"}
 */
public record Kit(Map<String, Json> manifest, List<Transcript> transcripts, Map<String, Hash> identities) {
    /** The number of recordings in the pinned kit. */
    public static final int TRANSCRIPT_COUNT = 21;

    private static final List<String> FILES = List.of("kit/kit.json", "kit/transcripts.json", "kit/fixtures.jac", "kit/README.md", "vectors.json");

    public Kit {
        manifest = Json.sorted(manifest);
        transcripts = List.copyOf(transcripts);
        identities = java.util.Collections.unmodifiableSortedMap(new TreeMap<>(identities));
    }

    /** One pinned kit file, such as {@code "kit/fixtures.jac"}, or null. */
    public static byte[] file(String name) {
        if (!FILES.contains(name)) {
            return null;
        }
        try (InputStream stream = Kit.class.getResourceAsStream("host-protocol-v0/" + name)) {
            return stream == null ? null : stream.readAllBytes();
        } catch (IOException error) {
            return null;
        }
    }

    private static String sha256(byte[] data) {
        try {
            return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(data));
        } catch (NoSuchAlgorithmException error) {
            // Every Java platform is required to provide SHA-256.
            throw new IllegalStateException(error);
        }
    }

    private static String text(Map<String, Json> object, String key) {
        Json value = object.get(key);
        return value == null || value.asString() == null ? "" : value.asString();
    }

    private static Json decodeFile(String name) throws KitException {
        byte[] data = file(name);
        if (data == null) {
            throw new KitException("missing kit file " + name);
        }
        try {
            return JsonCodec.decode(data, 256);
        } catch (ProtocolException error) {
            throw new KitException(name + ": " + error.getMessage());
        }
    }

    /** Verifies the embedded files against the pinned digests and decodes them. */
    public static Kit load() throws KitException {
        for (Map.Entry<String, String> entry : Compat.KIT_DIGESTS) {
            byte[] data = file(entry.getKey());
            if (data == null) {
                throw new KitException("missing kit file " + entry.getKey());
            }
            if (!sha256(data).equals(entry.getValue())) {
                throw new KitException("pinned conformance digest mismatch: " + entry.getKey());
            }
        }
        Json manifestJson = decodeFile("kit/kit.json");
        Json entriesJson = decodeFile("kit/transcripts.json");
        if (!(manifestJson instanceof Json.Obj manifestObject) || !(entriesJson instanceof Json.Arr entries)) {
            throw new KitException("kit files have an unexpected shape");
        }
        Map<String, Json> manifest = manifestObject.fields();
        List<String> pending = new ArrayList<>();
        Json decisions = manifest.get("pending_decisions");
        if (decisions != null && decisions.asArray() != null) {
            for (Json decision : decisions.asArray()) {
                pending.add(decision.asObject() == null ? "" : text(decision.asObject(), "name"));
            }
        }
        if (!Json.of(Compat.PROTOCOL).equals(manifest.get("protocol"))
                || !Json.of(Compat.CARRIER).equals(manifest.get("carrier"))
                || !Json.of(Compat.CORE_VERSION).equals(manifest.get("core_version"))
                || entries.items().size() != TRANSCRIPT_COUNT
                || !pending.equals(Compat.PENDING_DECISIONS)) {
            throw new KitException("kit metadata does not match the pin");
        }
        TreeMap<String, Hash> identities = new TreeMap<>();
        Json named = manifest.get("identities");
        if (named != null && named.asObject() != null) {
            for (Map.Entry<String, Json> entry : named.asObject().entrySet()) {
                String hex = entry.getValue().asString();
                try {
                    identities.put(entry.getKey(), Hash.parse(hex == null ? "" : hex));
                } catch (ProtocolException error) {
                    throw new KitException(error.getMessage());
                }
            }
        }
        List<Transcript> transcripts = new ArrayList<>();
        for (Json entry : entries.items()) {
            transcripts.add(parseTranscript(entry));
        }
        return new Kit(manifest, transcripts, identities);
    }

    private static List<Map<String, Json>> objects(Map<String, Json> entry, String key, String name) throws KitException {
        List<Map<String, Json>> found = new ArrayList<>();
        Json list = entry.get(key);
        if (list == null || list.asArray() == null) {
            return found;
        }
        for (Json item : list.asArray()) {
            if (item.asObject() == null) {
                throw new KitException(name + ": " + key + " entry is not an object");
            }
            found.add(item.asObject());
        }
        return found;
    }

    private static Transcript parseTranscript(Json value) throws KitException {
        Map<String, Json> entry = value.asObject();
        if (entry == null) {
            throw new KitException("transcript is not an object");
        }
        String name = text(entry, "name");
        List<Map<String, Json>> coreOutput = objects(entry, "core_output", name);
        List<Step> hostInput = new ArrayList<>();
        for (Map<String, Json> step : objects(entry, "host_input", name)) {
            Json raw = step.get("raw_hex");
            Json frame = step.get("frame");
            hostInput.add(new Step(
                    text(step, "direction"),
                    raw == null ? null : raw.asString(),
                    frame == null || frame.asObject() == null ? Map.of() : frame.asObject(),
                    Json.of(true).equals(step.get("after_terminal"))));
        }
        Json expect = entry.get("expect");
        Json observed = entry.get("observed");
        if (expect == null || expect.asObject() == null || observed == null || observed.asObject() == null || name.isEmpty()) {
            throw new KitException("transcript is missing required fields");
        }
        Json exit = entry.get("exit_status");
        return new Transcript(
                name,
                text(entry, "kind"),
                text(entry, "phase"),
                expect.asObject(),
                observed.asObject(),
                text(entry, "status"),
                exit == null || exit.asInt() == null ? 0 : exit.asInt(),
                coreOutput,
                hostInput);
    }

    /** The transcript with this name, or null. */
    public Transcript transcript(String name) {
        for (Transcript transcript : transcripts) {
            if (transcript.name().equals(name)) {
                return transcript;
            }
        }
        return null;
    }

    /** The named identity; throws if the kit has none by that name. */
    public Hash identity(String name) {
        Hash hash = identities.get(name);
        if (hash == null) {
            throw new IllegalArgumentException("the kit has no identity named " + name);
        }
        return hash;
    }
}

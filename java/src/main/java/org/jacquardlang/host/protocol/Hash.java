package org.jacquardlang.host.protocol;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;

/** A HASH_V0 identity: exactly 64 lowercase hexadecimal digits. */
public record Hash(String hex) implements Comparable<Hash> {
    private static final String MESSAGE = "HASH_V0 must be exactly 64 lowercase hexadecimal digits";

    /** Validates; prefer {@link #parse}, which reports E1601. */
    public Hash {
        if (!valid(hex)) {
            throw new IllegalArgumentException(MESSAGE);
        }
    }

    /** Validates a HASH_V0 identity. */
    public static Hash parse(String text) throws ProtocolException {
        if (!valid(text)) {
            throw ProtocolException.malformed(MESSAGE);
        }
        return new Hash(text);
    }

    static Hash fromJson(Json value) throws ProtocolException {
        String text = value.asString();
        if (text == null) {
            throw ProtocolException.malformed(MESSAGE);
        }
        return parse(text);
    }

    static boolean lowerHex(String text, int length) {
        if (text == null || text.length() != length) {
            return false;
        }
        for (int i = 0; i < length; i++) {
            char c = text.charAt(i);
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) {
                return false;
            }
        }
        return true;
    }

    private static boolean valid(String text) {
        return lowerHex(text, 64);
    }

    @Override
    public int compareTo(Hash other) {
        return hex.compareTo(other.hex);
    }

    @Override
    public String toString() {
        return hex;
    }
}

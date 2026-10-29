package org.jacquardlang.host.sdk;

import java.util.List;

import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Value;

/** Decodes boundary values with the program's data. */
public final class Decoder {
    private final Data data;

    Decoder(Data data) {
        this.data = data;
    }

    /** The program's data. */
    public Data data() {
        return data;
    }

    /** Decodes a nested value. */
    public <T> T decode(Codec<T> codec, Value value) throws JacquardException {
        return codec.decode(this, value);
    }

    /** Whether {@code value} is built by the named constructor. */
    public boolean is(Value value, String constructor) {
        Hash expected = data.constructorId(constructor);
        return expected != null && value instanceof Value.Constructor built && built.identity().equals(expected);
    }

    /**
     * The fields of a value built by the named constructor with {@code count}
     * fields.
     *
     * @throws JacquardException a mapping failure for another constructor or count
     */
    public List<Value> fields(Value value, String constructor, int count) throws JacquardException {
        if (is(value, constructor) && ((Value.Constructor) value).arguments().size() == count) {
            return ((Value.Constructor) value).arguments();
        }
        throw mapping(Identities.excerpt(constructor) + " result has the wrong constructor or field count");
    }

    /** A mapping failure for a value that is none of a type's constructors. */
    public JacquardException mismatch(String typeName) {
        return mapping("unknown " + Identities.excerpt(typeName) + " case");
    }

    /** A mapping failure: a boundary value that does not decode into the requested type. */
    public JacquardException mapping(String message) {
        return JacquardException.binding(message);
    }
}

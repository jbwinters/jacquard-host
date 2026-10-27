package org.jacquardlang.host;

/** A protocol refusal carrying its frozen v0 diagnostic code, such as E1601. */
public final class ProtocolException extends Exception {
    private static final long serialVersionUID = 1L;

    private final String code;
    private final String detail;

    /** A refusal with a frozen code and a message without the code. */
    public ProtocolException(String code, String detail) {
        super(code + ": " + detail);
        this.code = code;
        this.detail = detail;
    }

    /** The frozen diagnostic code, such as {@code "E1601"}. */
    public String code() {
        return code;
    }

    /** The message without the code. */
    public String detail() {
        return detail;
    }

    /** E1601: malformed input. */
    public static ProtocolException malformed(String detail) {
        return new ProtocolException("E1601", detail);
    }

    /** E1602: a selected or hard limit is exceeded. */
    public static ProtocolException exceeded(String detail) {
        return new ProtocolException("E1602", detail);
    }

    /** E1604: a value or type with no v0 encoding. */
    public static ProtocolException unsupported(String detail) {
        return new ProtocolException("E1604", detail);
    }

    /** E1608: a message out of order for the session state. */
    public static ProtocolException order(String detail) {
        return new ProtocolException("E1608", detail);
    }
}

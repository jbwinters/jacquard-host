package org.jacquardlang.host.protocol;

/** The host session state. */
public enum State {
    HELLO("hello"),
    NEGOTIATING("negotiating"),
    SELECTED("selected"),
    SHUTTING_DOWN("shutting_down"),
    RUNNING("running"),
    WAITING("waiting"),
    FINISHING("finishing"),
    CLOSED("closed");

    private final String wireName;

    State(String wireName) {
        this.wireName = wireName;
    }

    /** The state's protocol name. */
    public String wireName() {
        return wireName;
    }
}

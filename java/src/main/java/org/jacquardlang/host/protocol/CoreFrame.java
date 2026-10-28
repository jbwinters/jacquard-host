package org.jacquardlang.host.protocol;

import java.util.Map;

import org.jacquardlang.host.json.Json;

/** A parsed Core frame after {@code core_hello}: an effect request or a terminal. */
public sealed interface CoreFrame permits EffectRequest, Terminal {
    /** The exact frame. */
    Map<String, Json> raw();
}

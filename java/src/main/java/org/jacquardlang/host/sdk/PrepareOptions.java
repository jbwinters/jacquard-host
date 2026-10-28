package org.jacquardlang.host.sdk;

import java.nio.file.Path;

/**
 * Options for one preparation.
 *
 * @param cacheDir holds verified prepared programs; null means {@link Runtime#DEFAULT_CACHE_DIR}
 */
public record PrepareOptions(Path cacheDir) {
    /** The default cache directory. */
    public static PrepareOptions defaults() {
        return new PrepareOptions(null);
    }
}

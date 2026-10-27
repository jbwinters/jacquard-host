package org.jacquardlang.host;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;

import java.nio.file.Files;
import java.nio.file.Path;

import org.junit.jupiter.api.Test;

/**
 * A jar or source copy of java/ carries java/NOTICE but not the repository's
 * root NOTICE, so the two must stay identical.
 */
class NoticeTest {
    @Test
    void noticeMatchesTheRepositoryNotice() throws Exception {
        Path root = Path.of("..", "NOTICE");
        if (!Files.isRegularFile(root)) {
            return; // outside a checkout
        }
        assertArrayEquals(Files.readAllBytes(root), Files.readAllBytes(Path.of("NOTICE")),
                "java/NOTICE differs from the repository NOTICE; copy it again");
    }
}

package org.jacquardlang.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

import org.junit.jupiter.api.Test;

/**
 * The README's example is the compiled test source {@code readme/Echo.java}
 * (which LiveTest runs against Core), minus its package line.
 */
class ReadmeTest {
    @Test
    void theReadmeExampleIsTheCompiledOne() throws Exception {
        String readme = Files.readString(Path.of("README.md"), StandardCharsets.UTF_8);
        int start = readme.indexOf("```java\n");
        assertTrue(start >= 0, "README.md has no java example");
        int end = readme.indexOf("```", start + 8);
        String example = readme.substring(start + 8, end);
        String source = Files.readString(Path.of("src/test/java/org/jacquardlang/host/readme/Echo.java"), StandardCharsets.UTF_8);
        String body = source.substring(source.indexOf('\n', source.indexOf("package ")) + 1).stripLeading();
        assertEquals(body, example, "README.md's example differs from src/test/java/org/jacquardlang/host/readme/Echo.java");
    }
}

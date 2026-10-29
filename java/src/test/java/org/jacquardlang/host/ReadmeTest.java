package org.jacquardlang.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Stream;

import org.junit.jupiter.api.Test;

/**
 * Each Java example in the README is a compiled test source under
 * {@code readme/} (which the live tests run against Core), minus its package
 * line, and each such source is in the README.
 */
class ReadmeTest {
    @Test
    void theReadmeExamplesAreTheCompiledOnes() throws Exception {
        String readme = Files.readString(Path.of("README.md"), StandardCharsets.UTF_8);
        List<String> blocks = new ArrayList<>();
        for (int start = readme.indexOf("```java\n"); start >= 0; start = readme.indexOf("```java\n", start + 1)) {
            blocks.add(readme.substring(start + 8, readme.indexOf("```", start + 8)));
        }
        List<String> sources = new ArrayList<>();
        try (Stream<Path> files = Files.list(Path.of("src/test/java/org/jacquardlang/host/readme"))) {
            for (Path file : files.sorted().toList()) {
                String source = Files.readString(file, StandardCharsets.UTF_8);
                String body = source.substring(source.indexOf('\n', source.indexOf("package ")) + 1).stripLeading();
                assertTrue(blocks.contains(body), "README.md does not show " + file.getFileName() + " exactly");
                sources.add(body);
            }
        }
        assertEquals(sources.size(), blocks.size(), "README.md has a java example that is not a compiled source");
    }
}

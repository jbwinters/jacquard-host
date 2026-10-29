package org.jacquardlang.host.examples;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.file.FileAlreadyExistsException;
import java.nio.file.Files;
import java.nio.file.Path;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.DisabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/** The fulfillment project's snapshot store, without Core. */
class WarehouseTest {
    @TempDir
    Path scratch;

    /**
     * A file already at the store's next temporary name is not the store's:
     * the write fails, and the file and its contents are left as they were.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aFileAtTheTemporaryNameIsNeitherOverwrittenNorRemoved() throws Exception {
        Path inventory = scratch.resolve("inventory.json");
        JacquardExamples.Warehouse warehouse = JacquardExamples.Warehouse.open(inventory, "none");
        long next = JacquardExamples.Warehouse.NEXT.get();
        Path taken = scratch.resolve("inventory.json." + ProcessHandle.current().pid() + "-" + next + ".tmp");
        Files.writeString(taken, "someone else's");
        assertThrows(FileAlreadyExistsException.class, warehouse::save);
        assertEquals("someone else's", Files.readString(taken));
        // The next write uses a fresh name and succeeds.
        warehouse.save();
        assertTrue(Files.exists(inventory));
        assertEquals("someone else's", Files.readString(taken));
    }
}

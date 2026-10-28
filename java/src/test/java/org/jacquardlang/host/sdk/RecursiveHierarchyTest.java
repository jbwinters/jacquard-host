package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;

import java.time.Duration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.Assertions;
import org.junit.jupiter.api.Test;

/**
 * A recursive sealed hierarchy of 20 levels, each permitting every deeper
 * one and reached again through record fields, derived from its deepest
 * type. Unions flatten when they first decode, after every lazy case has its
 * target, so a value of 400 leaves decodes at once. Before the fix a union
 * built while a type was still being derived kept that type as an opaque
 * lazy case, and decoding doubled with each level: 13.7 s for this value.
 */
class RecursiveHierarchyTest {
    sealed interface A1 permits A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R1 {}
    sealed interface A2 extends A1 permits A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R2 {}
    sealed interface A3 extends A1, A2 permits A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R3 {}
    sealed interface A4 extends A1, A2, A3 permits A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R4 {}
    sealed interface A5 extends A1, A2, A3, A4 permits A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R5 {}
    sealed interface A6 extends A1, A2, A3, A4, A5 permits A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R6 {}
    sealed interface A7 extends A1, A2, A3, A4, A5, A6 permits A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R7 {}
    sealed interface A8 extends A1, A2, A3, A4, A5, A6, A7 permits A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R8 {}
    sealed interface A9 extends A1, A2, A3, A4, A5, A6, A7, A8 permits A10, A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R9 {}
    sealed interface A10 extends A1, A2, A3, A4, A5, A6, A7, A8, A9 permits A11, A12, A13, A14, A15, A16, A17, A18, A19, A20, R10 {}
    sealed interface A11 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10 permits A12, A13, A14, A15, A16, A17, A18, A19, A20, R11 {}
    sealed interface A12 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11 permits A13, A14, A15, A16, A17, A18, A19, A20, R12 {}
    sealed interface A13 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12 permits A14, A15, A16, A17, A18, A19, A20, R13 {}
    sealed interface A14 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13 permits A15, A16, A17, A18, A19, A20, R14 {}
    sealed interface A15 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14 permits A16, A17, A18, A19, A20, R15 {}
    sealed interface A16 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15 permits A17, A18, A19, A20, R16 {}
    sealed interface A17 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16 permits A18, A19, A20, R17 {}
    sealed interface A18 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17 permits A19, A20, R18 {}
    sealed interface A19 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18 permits A20, R19 {}
    sealed interface A20 extends A1, A2, A3, A4, A5, A6, A7, A8, A9, A10, A11, A12, A13, A14, A15, A16, A17, A18, A19 permits R20 {}
    record R1(long x) implements A1 {}
    record R2(A1 x0, A1 x1, A1 x2, A1 x3, A1 x4, A1 x5, A1 x6, A1 x7, A1 x8, A1 x9, A1 x10, A1 x11, A1 x12, A1 x13, A1 x14, A1 x15, A1 x16, A1 x17, A1 x18, A1 x19, A1 x20, A1 x21, A1 x22, A1 x23, A1 x24, A1 x25, A1 x26, A1 x27, A1 x28, A1 x29, A1 x30, A1 x31, A1 x32, A1 x33, A1 x34, A1 x35, A1 x36, A1 x37, A1 x38, A1 x39, A1 x40, A1 x41, A1 x42, A1 x43, A1 x44, A1 x45, A1 x46, A1 x47, A1 x48, A1 x49, A1 x50, A1 x51, A1 x52, A1 x53, A1 x54, A1 x55, A1 x56, A1 x57, A1 x58, A1 x59, A1 x60, A1 x61, A1 x62, A1 x63, A1 x64, A1 x65, A1 x66, A1 x67, A1 x68, A1 x69, A1 x70, A1 x71, A1 x72, A1 x73, A1 x74, A1 x75, A1 x76, A1 x77, A1 x78, A1 x79, A1 x80, A1 x81, A1 x82, A1 x83, A1 x84, A1 x85, A1 x86, A1 x87, A1 x88, A1 x89, A1 x90, A1 x91, A1 x92, A1 x93, A1 x94, A1 x95, A1 x96, A1 x97, A1 x98, A1 x99, A1 x100, A1 x101, A1 x102, A1 x103, A1 x104, A1 x105, A1 x106, A1 x107, A1 x108, A1 x109, A1 x110, A1 x111, A1 x112, A1 x113, A1 x114, A1 x115, A1 x116, A1 x117, A1 x118, A1 x119, A1 x120, A1 x121, A1 x122, A1 x123, A1 x124, A1 x125, A1 x126, A1 x127, A1 x128, A1 x129, A1 x130, A1 x131, A1 x132, A1 x133, A1 x134, A1 x135, A1 x136, A1 x137, A1 x138, A1 x139, A1 x140, A1 x141, A1 x142, A1 x143, A1 x144, A1 x145, A1 x146, A1 x147, A1 x148, A1 x149, A1 x150, A1 x151, A1 x152, A1 x153, A1 x154, A1 x155, A1 x156, A1 x157, A1 x158, A1 x159, A1 x160, A1 x161, A1 x162, A1 x163, A1 x164, A1 x165, A1 x166, A1 x167, A1 x168, A1 x169, A1 x170, A1 x171, A1 x172, A1 x173, A1 x174, A1 x175, A1 x176, A1 x177, A1 x178, A1 x179, A1 x180, A1 x181, A1 x182, A1 x183, A1 x184, A1 x185, A1 x186, A1 x187, A1 x188, A1 x189, A1 x190, A1 x191, A1 x192, A1 x193, A1 x194, A1 x195, A1 x196, A1 x197, A1 x198, A1 x199) implements A2 {}
    record R3(A2 f, A2 g) implements A3 {}
    record R4(A3 f) implements A4 {}
    record R5(A4 f) implements A5 {}
    record R6(A5 f) implements A6 {}
    record R7(A6 f) implements A7 {}
    record R8(A7 f) implements A8 {}
    record R9(A8 f) implements A9 {}
    record R10(A9 f) implements A10 {}
    record R11(A10 f) implements A11 {}
    record R12(A11 f) implements A12 {}
    record R13(A12 f) implements A13 {}
    record R14(A13 f) implements A14 {}
    record R15(A14 f) implements A15 {}
    record R16(A15 f) implements A16 {}
    record R17(A16 f) implements A17 {}
    record R18(A17 f) implements A18 {}
    record R19(A18 f) implements A19 {}
    record R20(A19 f) implements A20 {}

    @Test
    void aRecursiveHierarchyDecodesWithoutDoublingPerLevel() throws Exception {
        Map<String, Hash> names = new LinkedHashMap<>();
        names.put("int", new Hash("1".repeat(64)));
        names.put("list", new Hash("6".repeat(64)));
        Map<String, Hash> constructors = new LinkedHashMap<>();
        constructors.put("cons", new Hash("e".repeat(64)));
        constructors.put("nil", new Hash("f".repeat(64)));
        for (int k = 1; k <= 20; k++) {
            constructors.put("r" + k, new Hash(String.format("%064x", 100 + k)));
        }
        Data data = new Data(names, constructors, List.of(), Limits.HARD);
        Codec<A20> codec = Codec.of(A20.class);
        Object[] leaves = new Object[200];
        for (int i = 0; i < 200; i++) {
            leaves[i] = new R1(i);
        }
        Object value = R2.class.getDeclaredConstructors()[0].newInstance(leaves);
        value = new R3((A2) value, (A2) value);
        for (int k = 4; k <= 20; k++) {
            value = Class.forName(RecursiveHierarchyTest.class.getName() + "$R" + k).getDeclaredConstructors()[0].newInstance(value);
        }
        A20 root = (A20) value;
        Value encoded = data.encode(codec, root);
        A20 decoded = Assertions.assertTimeoutPreemptively(Duration.ofSeconds(5), () -> data.decode(codec, encoded));
        assertEquals(root, decoded);
    }
}

package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import org.jacquardlang.host.protocol.Limits;
import org.junit.jupiter.api.Test;

/**
 * A derived sealed hierarchy in which each of 16 levels is reached through
 * two sub-interfaces. A type met a second time is shared as itself, so the
 * union flattens to its one distinct case and decoding asks it once. Before
 * the fix the second meeting stayed a lazy case, decoding doubled with each
 * level (4 s at 26 levels), and this union held 17 cases.
 */
class DiamondTest {
    sealed interface S0 permits A0, B0 {}
    sealed interface A0 extends S0 permits S1 {}
    sealed interface B0 extends S0 permits S1 {}
    sealed interface S1 extends A0, B0 permits A1, B1 {}
    sealed interface A1 extends S1 permits S2 {}
    sealed interface B1 extends S1 permits S2 {}
    sealed interface S2 extends A1, B1 permits A2, B2 {}
    sealed interface A2 extends S2 permits S3 {}
    sealed interface B2 extends S2 permits S3 {}
    sealed interface S3 extends A2, B2 permits A3, B3 {}
    sealed interface A3 extends S3 permits S4 {}
    sealed interface B3 extends S3 permits S4 {}
    sealed interface S4 extends A3, B3 permits A4, B4 {}
    sealed interface A4 extends S4 permits S5 {}
    sealed interface B4 extends S4 permits S5 {}
    sealed interface S5 extends A4, B4 permits A5, B5 {}
    sealed interface A5 extends S5 permits S6 {}
    sealed interface B5 extends S5 permits S6 {}
    sealed interface S6 extends A5, B5 permits A6, B6 {}
    sealed interface A6 extends S6 permits S7 {}
    sealed interface B6 extends S6 permits S7 {}
    sealed interface S7 extends A6, B6 permits A7, B7 {}
    sealed interface A7 extends S7 permits S8 {}
    sealed interface B7 extends S7 permits S8 {}
    sealed interface S8 extends A7, B7 permits A8, B8 {}
    sealed interface A8 extends S8 permits S9 {}
    sealed interface B8 extends S8 permits S9 {}
    sealed interface S9 extends A8, B8 permits A9, B9 {}
    sealed interface A9 extends S9 permits S10 {}
    sealed interface B9 extends S9 permits S10 {}
    sealed interface S10 extends A9, B9 permits A10, B10 {}
    sealed interface A10 extends S10 permits S11 {}
    sealed interface B10 extends S10 permits S11 {}
    sealed interface S11 extends A10, B10 permits A11, B11 {}
    sealed interface A11 extends S11 permits S12 {}
    sealed interface B11 extends S11 permits S12 {}
    sealed interface S12 extends A11, B11 permits A12, B12 {}
    sealed interface A12 extends S12 permits S13 {}
    sealed interface B12 extends S12 permits S13 {}
    sealed interface S13 extends A12, B12 permits A13, B13 {}
    sealed interface A13 extends S13 permits S14 {}
    sealed interface B13 extends S13 permits S14 {}
    sealed interface S14 extends A13, B13 permits A14, B14 {}
    sealed interface A14 extends S14 permits S15 {}
    sealed interface B14 extends S14 permits S15 {}
    sealed interface S15 extends A14, B14 permits A15, B15 {}
    sealed interface A15 extends S15 permits Leaf {}
    sealed interface B15 extends S15 permits Leaf {}
    record Leaf() implements A15, B15 {}

    @Test
    void aDiamondHierarchyFlattensToItsDistinctCases() throws Exception {
        Codec<S0> codec = Codec.of(S0.class);
        assertEquals(1, Codecs.unionCases(codec), "the shared types were not flattened");
        Data data = DataTest.fixture(Limits.HARD);
        JacquardException refused = assertThrows(JacquardException.class, () -> data.decode(codec, data.encode(Codec.INT, 1L)));
        assertEquals(JacquardException.Kind.BINDING, refused.kind());
    }
}

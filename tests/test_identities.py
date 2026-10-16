import os
import unittest

from jacquard_host.identities import SignatureError, display_to_store, hash_file, parse_signature, signatures
from jacquard_host.kit import load_kit
from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import Hash, Nominal, TupleType

KIT = load_kit()
IDS = {name: Hash(value) for name, value in KIT.manifest["identities"].items()}
JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")


class DisplayNameTest(unittest.TestCase):
    def test_pascal_case_becomes_kebab_store_names(self) -> None:
        for display, store in (("KitWorld", "kit-world"), ("Int", "int"), ("Text", "text"), ("MkFleet", "mk-fleet"), ("kit-echo", "kit-echo"), ("text.length", "text.length")):
            with self.subTest(display=display):
                self.assertEqual(display_to_store(display), store)


class SignatureTest(unittest.TestCase):
    def test_kit_signatures_become_the_recorded_interfaces(self) -> None:
        text = Nominal(IDS["text"])
        self.assertEqual(parse_signature("() ->{} ()", IDS), Interface((), TupleType()))
        self.assertEqual(parse_signature("(Text) ->{KitWorld} Text", IDS), Interface((text,), text, (IDS["kit-world"],)))
        self.assertEqual(parse_signature("(Int) ->{} Int", IDS), Interface((Nominal(IDS["int"]),), Nominal(IDS["int"])))

    def test_tuples_type_arguments_and_rows(self) -> None:
        names = dict(IDS, option=Hash("a" * 64), pair=Hash("b" * 64), net=Hash("c" * 64))
        text, int_ = Nominal(IDS["text"]), Nominal(IDS["int"])
        parsed = parse_signature("(Int, (Text, Int)) ->{KitWorld, Net} Option (Pair Int Text)", names)
        self.assertEqual(parsed.parameters, (int_, TupleType((text, int_))))
        self.assertEqual(parsed.result, Nominal(names["option"], (Nominal(names["pair"], (int_, text)),)))
        self.assertEqual(parsed.effects, tuple(sorted((IDS["kit-world"], names["net"]))))

    def test_unsupported_signatures_are_refused(self) -> None:
        for text in ("(a) ->{} a", "(Text) ->{} (Int) ->{} Int", "(Text) ->{| e} Text", "(Unknown) ->{} Int", "Text"):
            with self.subTest(text=text), self.assertRaises(SignatureError):
                parse_signature(text, IDS)

    def test_explicit_singleton_tuple_spelling_is_preserved(self) -> None:
        integer = Nominal(IDS["int"])
        self.assertEqual(parse_signature("((Int,)) ->{} (Int,)", IDS), Interface((TupleType((integer,)),), TupleType((integer,))))


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE, "set JACQUARD_BIN and JACQUARD_PRELUDE to resolve identities with Core")
class LiveIdentityTest(unittest.TestCase):
    def test_hash_file_reproduces_the_kit_identities(self) -> None:
        names = hash_file(KIT.fixtures, command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE)
        for member in ("kit-ready", "kit-echo", "kit-twice", "kit-double", "kit-fail", "kit-world", "send"):
            self.assertEqual(names[member], IDS[member], member)

    def test_a_type_and_constructor_sharing_a_name_resolve_to_the_type(self) -> None:
        import subprocess
        import tempfile
        from jacquard_host.identities import hash_members
        with tempfile.TemporaryDirectory(prefix="jacquard-host-dup-") as scratch:
            path = os.path.join(scratch, "dup.jac")
            with open(path, "w", encoding="utf-8") as stream:
                stream.write("type Wrapper = | Wrapper Text\n\nunwrap : (Wrapper) ->{} Text\nunwrap(w) = match w { | Wrapper(t) -> t }\n")
            members = hash_members(path, command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE)
            wrappers = [identity for _, name, identity in members if name == "wrapper"]
            self.assertEqual(len(wrappers), 2)
            names = hash_file(path, command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE)
            self.assertEqual(names["wrapper"], wrappers[0])
            # the checked interface built from the signature is accepted by Core
            store = os.path.join(scratch, "store")
            env = dict(os.environ, JACQUARD_PRELUDE=os.path.abspath(JACQUARD_PRELUDE))
            subprocess.run([JACQUARD_BIN, "run", path, "--store", store], check=True, stdout=subprocess.DEVNULL, env=env)
            from jacquard_host.adapter import Host
            from jacquard_host.protocol.values import Constructor
            all_names = {**hash_file(os.path.join(JACQUARD_PRELUDE, "01-prim.jqd"), command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE), **names}
            interface = parse_signature(signatures(path, command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE)["unwrap"], all_names)
            result = Host(store, command=[JACQUARD_BIN]).invoke(names["unwrap"], interface, [Constructor(wrappers[1], ("inside",))])
            self.assertEqual((result.kind, result.value), ("ok", "inside"))

    def test_prelude_types_and_signatures_resolve(self) -> None:
        prim = hash_file(os.path.join(JACQUARD_PRELUDE, "01-prim.jqd"), command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE)
        self.assertEqual((prim["int"], prim["text"]), (IDS["int"], IDS["text"]))
        names = {**prim, **hash_file(KIT.fixtures, command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE)}
        sigs = signatures(KIT.fixtures, command=[JACQUARD_BIN], prelude=JACQUARD_PRELUDE)
        self.assertEqual(sigs["kit-echo"], "(Text) ->{KitWorld} Text")
        echo = parse_signature(sigs["kit-echo"], names)
        recorded = KIT.transcript("one-once-effect-success")["host_input"][2]["frame"]["interface"]
        self.assertEqual([p.identity.hex for p in echo.parameters], [recorded["parameters"][0]["identity"]])
        self.assertEqual([e.hex for e in echo.effects], recorded["effects"])


if __name__ == "__main__":
    unittest.main()

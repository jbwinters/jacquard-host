import unittest

import jacquard_host
from jacquard_host import compat


class PackageTest(unittest.TestCase):
    def test_package_names_the_pinned_protocol(self) -> None:
        self.assertEqual(jacquard_host.__version__, "0.1.0")
        self.assertEqual(jacquard_host.PROTOCOL, "jacquard-host-v0")
        self.assertEqual(jacquard_host.CARRIER, "stdio-u32-json-v0")
        self.assertEqual(compat.CORE_VERSION, "0.2.0")
        self.assertEqual(len(compat.CORE_COMMIT), 40)
        for digest in compat.KIT_DIGESTS.values():
            self.assertRegex(digest, r"^[0-9a-f]{64}$")


if __name__ == "__main__":
    unittest.main()

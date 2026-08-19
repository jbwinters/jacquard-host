import unittest

import jacquard_host


class PackageTest(unittest.TestCase):
    def test_bootstrap_version_is_explicitly_pre_protocol(self) -> None:
        self.assertEqual(jacquard_host.__version__, "0.0.0")


if __name__ == "__main__":
    unittest.main()

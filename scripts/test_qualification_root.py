import os
from pathlib import Path
import unittest
from unittest.mock import patch

import qualification_root


class ShortRootTests(unittest.TestCase):
    def test_default_is_the_original_workstation_root(self):
        with patch.dict(os.environ, {}, clear=False):
            os.environ.pop(qualification_root.ENVIRONMENT, None)
            self.assertEqual(qualification_root.short_root(), Path("D:/TIAW").resolve())

    def test_environment_overrides_the_default(self):
        with patch.dict(os.environ, {qualification_root.ENVIRONMENT: "E:/q"}):
            self.assertEqual(qualification_root.short_root(), Path("E:/q").resolve())

    def test_empty_environment_falls_back_to_the_default(self):
        with patch.dict(os.environ, {qualification_root.ENVIRONMENT: ""}):
            self.assertEqual(qualification_root.short_root(), Path("D:/TIAW").resolve())


if __name__ == "__main__":
    unittest.main()

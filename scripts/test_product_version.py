"""Directory.Build.props holds the one product version; copies that cannot read it must match it."""
import json
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


def product_version():
    return re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text(encoding="utf-8")).group(1)


def read(path):
    return (ROOT / path).read_text(encoding="utf-8")


class ProductVersionTests(unittest.TestCase):
    def test_semantic_version(self):
        self.assertRegex(product_version(), r"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")

    def test_no_project_declares_its_own_version(self):
        own = [path.relative_to(ROOT).as_posix() for folder in ("src", "tests", "scripts")
               for path in (ROOT / folder).rglob("*.csproj") if re.search(r"<(Version|VersionPrefix)>", path.read_text(encoding="utf-8"))]
        self.assertEqual(own, [])

    def test_copies_match(self):
        version = product_version()
        self.assertEqual(json.loads(read("package.json"))["version"], version)
        self.assertEqual(json.loads(read("ui/project-explorer/package.json"))["version"], version)
        lock = json.loads(read("package-lock.json"))
        self.assertEqual((lock["version"], lock["packages"][""]["version"]), (version, version))
        publisher = read("src/TiaMcp.AddIn.V21/AddIn.Publisher.xml")
        self.assertEqual(re.findall(r"<(?:AddInVersion|Version)>([^<]+)<", publisher), [version, version])
        self.assertIn('Version = "' + version + '";', read("src/TiaMcp.AddIn.V21/SelectionHandoff.cs"))


if __name__ == "__main__":
    unittest.main()

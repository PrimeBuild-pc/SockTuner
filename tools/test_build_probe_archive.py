import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).with_name("build-probe-archive.py")
SPEC = importlib.util.spec_from_file_location("build_probe_archive", SCRIPT)
archive = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(archive)


class CompatibilityReportIngestionTests(unittest.TestCase):
    def test_same_default_filename_from_two_sources_gets_distinct_stable_archive_names(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive.ARCHIVE = str(root / "archive")
            archive.REPORTS = str(root / "archive" / "reports")
            Path(archive.REPORTS).mkdir(parents=True)
            sources = [root / "one", root / "two"]
            for index, source in enumerate(sources):
                source.mkdir()
                (source / "SockTuner-compatibility.json").write_text(
                    json.dumps({"reportType": "socktuner.compatibility", "variant": index}),
                    encoding="utf-8")

            loaded = archive.load_reports([str(source) for source in sources])
            names = [name for path, name, _ in loaded if os.path.dirname(path) in map(str, sources)]

            self.assertEqual(2, len(set(names)))
            self.assertTrue(all(name.startswith("SockTuner-compatibility-") for name in names))
            path, name, report = loaded[0]
            self.assertEqual(name, archive.report_archive_name(name, report))


if __name__ == "__main__":
    unittest.main()

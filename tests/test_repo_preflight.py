import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("repo_preflight", Path(__file__).resolve().parents[1] / "scripts/repo_preflight.py")
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)


class RepositoryPathTests(unittest.TestCase):
    def test_nested_build_outputs_are_rejected(self):
        for path in ("bin/Release/app.dll", "experiments/Probe/obj/project.assets.json",
                     "tests/Probe.Tests/bin/Release/app.dll", "src/client/node_modules/module.js",
                     "tests/__pycache__/test.pyc"):
            with self.subTest(path=path):
                self.assertIsNotNone(preflight.path_problem(path))

    def test_database_and_secret_paths_are_rejected(self):
        for path in (".env", "fixtures/test.pfx", "samples/data.bacpac", "fixtures/local.sqlite3", "keys/id_rsa"):
            with self.subTest(path=path):
                self.assertIsNotNone(preflight.path_problem(path))

    def test_source_and_safe_templates_are_allowed(self):
        for path in ("experiments/Probe/Program.cs", "Directory.Build.props", ".env.example",
                     "experiments/Probe/packages.lock.json", "docs/FEATURE_CATALOG.csv"):
            with self.subTest(path=path):
                self.assertIsNone(preflight.path_problem(path))

    def test_untrusted_paths_cannot_inject_log_lines(self):
        self.assertNotIn("\n", preflight.path_problem("build/newline\n::warning::injected"))


if __name__ == "__main__":
    unittest.main()

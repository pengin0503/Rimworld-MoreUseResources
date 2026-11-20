"""Unit tests for upload-results-r2.py that need neither boto3 nor network access.

Run: python3 -m unittest discover -s tools/benchmark/tests -v
"""

import datetime
import importlib.util
import os
import tarfile
import tempfile
import unittest

_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "upload-results-r2.py")
_SPEC = importlib.util.spec_from_file_location("upload_results_r2", _PATH)
up = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(up)


def write(path, text="x"):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


class FakeS3:
    def __init__(self, stored_size=None):
        self.objects = {}
        self.stored_size = stored_size

    def upload_file(self, filename, bucket, key, ExtraArgs=None):  # noqa: N803 (boto3 names)
        with open(filename, "rb") as f:
            self.objects[(bucket, key)] = f.read()

    def head_object(self, Bucket, Key):  # noqa: N803
        size = len(self.objects[(Bucket, Key)]) if self.stored_size is None else self.stored_size
        return {"ContentLength": size}


class FakeClientError(Exception):
    def __init__(self, code, message):
        super().__init__(message)
        self.response = {"Error": {"Code": code, "Message": message}}


class PackTests(unittest.TestCase):
    def test_leaves_out_generated_data_and_scratch(self):
        with tempfile.TemporaryDirectory() as tmp:
            results = os.path.join(tmp, "mur-bench-results")
            write(os.path.join(results, "20261120-034921-abc-standard", "analysis.md"))
            write(os.path.join(results, "20261120-034921-abc-standard", "scratch", "trial-3", "copy.xml"))
            write(os.path.join(results, "20261120-033012-abc-smoke", "trials.csv"))
            write(os.path.join(results, "generated-data", "Mods", "a.xml"))
            archive = os.path.join(tmp, "out.tar.gz")

            runs = up.pack(results, archive)

            self.assertEqual(["20261120-033012-abc-smoke", "20261120-034921-abc-standard"], runs)
            with tarfile.open(archive) as tar:
                names = tar.getnames()
            self.assertIn("mur-bench-results/20261120-034921-abc-standard/analysis.md", names)
            self.assertIn("mur-bench-results/20261120-033012-abc-smoke/trials.csv", names)
            self.assertFalse([n for n in names if "generated-data" in n or "/scratch" in n], names)

    def test_a_run_directory_named_like_the_data_is_only_dropped_at_the_top(self):
        with tempfile.TemporaryDirectory() as tmp:
            results = os.path.join(tmp, "r")
            write(os.path.join(results, "run1", "generated-data", "kept.txt"))
            archive = os.path.join(tmp, "out.tar.gz")
            up.pack(results, archive)
            with tarfile.open(archive) as tar:
                self.assertIn("r/run1/generated-data/kept.txt", tar.getnames())


class RunningTests(unittest.TestCase):
    def test_finds_processes_whose_command_line_names_the_benchmark(self):
        with tempfile.TemporaryDirectory() as proc:
            write(os.path.join(proc, "6938", "cmdline"),
                  "timeout\0--signal=INT\0375m\0/usr/bin/dotnet\0/h/Benchmarks/MoreUseResources.Benchmarks.dll\0run\0")
            write(os.path.join(proc, "6939", "cmdline"), "/usr/bin/dotnet\0/h/MoreUseResources.Benchmarks.dll\0run\0")
            write(os.path.join(proc, "100", "cmdline"), "-bash\0")
            write(os.path.join(proc, "self", "cmdline"), "MoreUseResources.Benchmarks.dll")
            self.assertEqual([6938, 6939], up.running_benchmarks(proc))

    def test_none_when_nothing_runs(self):
        with tempfile.TemporaryDirectory() as proc:
            write(os.path.join(proc, "1", "cmdline"), "/sbin/init\0")
            self.assertEqual([], up.running_benchmarks(proc))


class UploadTests(unittest.TestCase):
    def test_key_names_host_and_utc_time(self):
        now = datetime.datetime(2026, 11, 20, 4, 40, 5, tzinfo=datetime.timezone.utc)
        self.assertEqual("bench-results/rimworld-bench-01-20261120-044005.tar.gz",
                         up.object_key("rimworld-bench-01", now))
        self.assertEqual("bench-results/a-b-20261120-044005.tar.gz", up.object_key("a/b", now))

    def test_uploads_and_checks_the_stored_size(self):
        with tempfile.TemporaryDirectory() as tmp:
            archive = os.path.join(tmp, "a.tar.gz")
            write(archive, "12345")
            s3 = FakeS3()
            self.assertEqual(5, up.upload(s3, "rimworld-17", archive, "bench-results/k.tar.gz"))
            self.assertEqual(b"12345", s3.objects[("rimworld-17", "bench-results/k.tar.gz")])

    def test_a_size_mismatch_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            archive = os.path.join(tmp, "a.tar.gz")
            write(archive, "12345")
            with self.assertRaises(RuntimeError):
                up.upload(FakeS3(stored_size=3), "rimworld-17", archive, "k")

    def test_a_refusal_is_explained_as_missing_write_access(self):
        text = up.describe(FakeClientError("AccessDenied", "Access Denied"), "rimworld-17", "uploading failed")
        self.assertIn("AccessDenied", text)
        self.assertIn("Object Read & Write", text)
        other = up.describe(FakeClientError("NoSuchBucket", "nope"), "rimworld-17", "uploading failed")
        self.assertIn("does not exist", other)


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Packs the benchmark results of this machine into one file and puts it in the R2 bucket.

    python3 -I tools/benchmark/upload-results-r2.py [RESULTS_DIR] [--even-if-running]

RESULTS_DIR defaults to ~/mur-bench-results. All run directories in it go into one .tar.gz, stored
in the bucket as bench-results/<host>-<UTC time>.tar.gz. Left out: the generated input data
(RESULTS_DIR/generated-data, rebuilt on demand) and the scratch directory of a run that was killed
(copies of input files). The object can then be fetched from any device, for example from the
Cloudflare dashboard (R2, the bucket, bench-results/, the object, Download).

Environment: the same variables as fetch-r2-dataset.py (R2_ENDPOINT, R2_BUCKET, AWS_ACCESS_KEY_ID,
AWS_SECRET_ACCESS_KEY). The R2 API token needs write access (Object Read & Write).

While a benchmark is still running its results are incomplete, so the upload is refused unless
--even-if-running is given.
"""

import argparse
import datetime
import importlib.util
import os
import socket
import sys
import tarfile
import tempfile

PREFIX = "bench-results/"
MARKER = "MoreUseResources.Benchmarks.dll"

_SPEC = importlib.util.spec_from_file_location(
    "fetch_r2_dataset", os.path.join(os.path.dirname(os.path.abspath(__file__)), "fetch-r2-dataset.py"))
fetch = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(fetch)


def running_benchmarks(proc="/proc"):
    """Process ids of running benchmark processes (the dotnet host and the timeout wrapper)."""
    pids = []
    try:
        names = os.listdir(proc)
    except OSError:
        return pids
    for name in names:
        if not name.isdigit() or int(name) == os.getpid():
            continue
        try:
            with open(os.path.join(proc, name, "cmdline"), "rb") as f:
                cmdline = f.read().replace(b"\0", b" ").decode("utf-8", "replace")
        except OSError:
            continue
        if MARKER in cmdline:
            pids.append(int(name))
    return sorted(pids)


def leave_out(results_dir):
    """A tarfile filter that drops generated-data/ at the top and scratch/ inside each run."""
    top = os.path.basename(os.path.normpath(results_dir))

    def keep(info):
        parts = info.name.split("/")
        if len(parts) >= 2 and parts[0] == top and parts[1] == "generated-data":
            return None
        if len(parts) >= 3 and parts[0] == top and parts[2] == "scratch":
            return None
        return info
    return keep


def pack(results_dir, archive):
    """Writes results_dir into archive (.tar.gz). Returns the run directories it contains."""
    results_dir = os.path.normpath(results_dir)
    runs = sorted(d for d in os.listdir(results_dir)
                  if d != "generated-data" and os.path.isdir(os.path.join(results_dir, d)))
    with tarfile.open(archive, "w:gz") as tar:
        tar.add(results_dir, arcname=os.path.basename(results_dir), filter=leave_out(results_dir))
    return runs


def object_key(host, now):
    safe = "".join(c if c.isalnum() or c in "-_." else "-" for c in host) or "host"
    return PREFIX + safe + "-" + now.strftime("%Y%m%d-%H%M%S") + ".tar.gz"


def describe(error, bucket, what):
    """fetch.explain, except that a refusal is explained as missing write access."""
    response = getattr(error, "response", None) or {}
    code = str(response.get("Error", {}).get("Code", ""))
    if code not in ("AccessDenied", "403", "Unauthorized"):
        return fetch.explain(error, bucket, what)
    message = str(response.get("Error", {}).get("Message", "")) or str(error)
    return (f"{what}: {code}: {message} (this key pair cannot write to the bucket; create an R2 API token "
            "with 'Object Read & Write' for the bucket and set its keys)")


def upload_client(endpoint):
    """An S3 client for R2 that adds checksums only where the API requires them.

    botocore 1.36 and later add CRC checksums to every upload by default; S3-compatible stores have
    rejected some of those requests, so uploads keep the earlier behaviour. Older botocore (Ubuntu's
    python3-boto3) does not know the setting and already behaves that way.
    """
    try:
        import boto3  # noqa: PLC0415
        from botocore.config import Config  # noqa: PLC0415
    except ImportError:
        sys.exit("boto3 is required: sudo apt-get install python3-boto3 (or python3 -m pip install --user boto3)")
    retries = {"max_attempts": 8, "mode": "adaptive"}
    try:
        config = Config(retries=retries, request_checksum_calculation="when_required",
                        response_checksum_validation="when_required")
    except TypeError:
        config = Config(retries=retries)
    return boto3.client("s3", endpoint_url=endpoint, config=config)


def upload(s3, bucket, archive, key):
    """Uploads archive to bucket/key and checks the stored size. Returns the size."""
    size = os.path.getsize(archive)
    s3.upload_file(archive, bucket, key, ExtraArgs={"ContentType": "application/gzip"})
    stored = s3.head_object(Bucket=bucket, Key=key)["ContentLength"]
    if stored != size:
        raise RuntimeError(f"the stored object has {stored} bytes, the archive {size}")
    return size


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("results", nargs="?", default=os.path.expanduser("~/mur-bench-results"),
                        help="results directory (default ~/mur-bench-results)")
    parser.add_argument("--even-if-running", action="store_true",
                        help="upload although a benchmark is still running (its results are then incomplete)")
    args = parser.parse_args()

    results = os.path.abspath(args.results)
    if not os.path.isdir(results):
        sys.exit(f"results directory not found: {results}")
    pids = running_benchmarks()
    if pids and not args.even_if_running:
        sys.exit("a benchmark is still running (process " + ", ".join(map(str, pids)) + "), so its results are "
                 "incomplete. Stop it with: pkill -INT -x dotnet  (once), wait until "
                 "'pgrep -f " + MARKER + "' prints nothing, then rerun this. "
                 "Or add --even-if-running to upload what is there now.")
    try:
        endpoint, bucket, note = fetch.endpoint_and_bucket(os.environ.get("R2_ENDPOINT"), os.environ.get("R2_BUCKET"))
        fetch.check_credentials(os.environ)
    except fetch.SetupError as ex:
        sys.exit(str(ex))
    if note:
        print(note, file=sys.stderr)

    with tempfile.TemporaryDirectory(prefix="mur-upload-") as tmp:
        archive = os.path.join(tmp, "results.tar.gz")
        runs = pack(results, archive)
        if not runs:
            sys.exit(f"no run directories in {results}")
        key = object_key(socket.gethostname(), datetime.datetime.now(datetime.timezone.utc))
        print(f"packed {len(runs)} runs ({os.path.getsize(archive) / 2**20:.1f} MiB): " + ", ".join(runs))
        s3 = upload_client(endpoint)
        from botocore.exceptions import BotoCoreError, ClientError  # noqa: PLC0415 (present once client() succeeded)
        try:
            size = upload(s3, bucket, archive, key)
        except (BotoCoreError, ClientError, RuntimeError) as ex:
            sys.exit(describe(ex, bucket, f"uploading to {bucket}/{key} failed"))
    print(f"uploaded {size} bytes to {bucket}/{key}")
    print("done: the results are in the bucket; nothing on this machine was changed")


if __name__ == "__main__":
    main()

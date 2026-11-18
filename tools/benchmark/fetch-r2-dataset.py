#!/usr/bin/env python3
"""Downloads real RimWorld 1.7 data from the R2 bucket for the Linux benchmark.

Only the files the mod's own discovery visits are fetched (Defs, Patches, Languages, Textures and
Sounds with the extensions GameAssetFiles accepts), plus About.xml, loadFolders.xml and the engine
packs (Assets.zip). DLLs, language .tar archives, shader caches and previews are skipped.

Layout written under DEST (what `--data DEST` expects):
    DEST/Game/Assets.zip              Game-Windows/Assets.zip
    DEST/Game/Data/<Core|DLC>/...     Game-Windows/Data/...
    DEST/Mods/<workshop id>/...       Workshop/<workshop id>/...
    DEST/r2-dataset.json              provenance (profile, object list digest, sizes, date)

Environment: R2_ENDPOINT (required), R2_BUCKET (default rimworld-17), AWS_ACCESS_KEY_ID and
AWS_SECRET_ACCESS_KEY. Reruns resume: files whose size and ETag match the previous download are kept.

Profiles:
    smoke  Core's data, Assets.zip and 12 Workshop mods spread over the ID range
    game   all official data (Core and every DLC) and Assets.zip
    all    game plus every Workshop mod in the bucket
"""

import argparse
import concurrent.futures
import datetime
import hashlib
import json
import os
import sys
import threading

SCHEMA = 1
ASSET_DIRS = ("Defs", "Patches", "Languages", "Textures", "Sounds")
# Source/Core/GameAssetFiles.cs: Extensions
ASSET_EXTENSIONS = {".xml", ".txt", ".png", ".dds", ".jpg", ".jpeg", ".tga", ".psd", ".bmp", ".wav", ".ogg", ".mp3"}
GAME_PREFIX = "Game-Windows/"
WORKSHOP_PREFIX = "Workshop/"
SMOKE_MODS = 12


# Windows resolves these names case-insensitively; on Linux a mod's "textures" folder would be missed
# by Path.Combine(root, "Textures"), so they are written in the spelling the game and the mod use.
CANONICAL = {n.lower(): n for n in ASSET_DIRS + ("About", "About.xml", "loadFolders.xml", "Assets.zip", "Common")}


def canonical(relative):
    """The relative path with the folder and file names above in their canonical spelling."""
    return "/".join(CANONICAL.get(p.lower(), p) for p in relative.split("/"))


def wanted(relative):
    """True for a file inside a mod or official content folder that the mod's discovery would read."""
    parts = canonical(relative).split("/")
    name = parts[-1]
    if name in ("Assets.zip", "loadFolders.xml"):
        return True
    if len(parts) >= 2 and parts[-2] == "About" and name == "About.xml":
        return True
    ext = os.path.splitext(name)[1].lower()
    return ext in ASSET_EXTENSIONS and any(p in ASSET_DIRS for p in parts[:-1])


def client():
    try:
        import boto3  # noqa: PLC0415
        from botocore.config import Config  # noqa: PLC0415
    except ImportError:
        sys.exit("boto3 is required: python3 -m pip install --user boto3 (or apt-get install python3-boto3)")
    endpoint = os.environ.get("R2_ENDPOINT")
    if not endpoint:
        sys.exit("R2_ENDPOINT is not set (https://<account>.r2.cloudflarestorage.com)")
    return boto3.client("s3", endpoint_url=endpoint, config=Config(retries={"max_attempts": 8, "mode": "adaptive"}, max_pool_connections=32))


def walk(s3, bucket, prefix):
    token = None
    while True:
        kwargs = {"Bucket": bucket, "Prefix": prefix}
        if token:
            kwargs["ContinuationToken"] = token
        page = s3.list_objects_v2(**kwargs)
        for obj in page.get("Contents", []):
            yield obj
        if not page.get("IsTruncated"):
            return
        token = page["NextContinuationToken"]


def workshop_ids(s3, bucket):
    ids = []
    token = None
    while True:
        kwargs = {"Bucket": bucket, "Prefix": WORKSHOP_PREFIX, "Delimiter": "/"}
        if token:
            kwargs["ContinuationToken"] = token
        page = s3.list_objects_v2(**kwargs)
        ids += [p["Prefix"][len(WORKSHOP_PREFIX):].strip("/") for p in page.get("CommonPrefixes", [])]
        if not page.get("IsTruncated"):
            return sorted(ids)
        token = page["NextContinuationToken"]


def plan(s3, bucket, profile, explicit_ids):
    """Returns [(key, local relative path, size, etag)] and the Workshop ids used."""
    items = []
    game_sources = [GAME_PREFIX + "Assets.zip", GAME_PREFIX + "Data/" + ("Core/" if profile == "smoke" else "")]
    for source in game_sources:
        for obj in walk(s3, bucket, source):
            key = obj["Key"]
            rel = key[len(GAME_PREFIX):]
            if rel == "Assets.zip" or (rel.startswith("Data/") and wanted(rel)):
                items.append((key, "Game/" + canonical(rel), obj["Size"], obj["ETag"].strip('"')))
    if explicit_ids:
        ids = explicit_ids
    elif profile == "game":
        ids = []
    else:
        ids = workshop_ids(s3, bucket)
        if profile == "smoke" and len(ids) > SMOKE_MODS:
            step = len(ids) / SMOKE_MODS
            ids = [ids[int(i * step)] for i in range(SMOKE_MODS)]
    for wid in ids:
        prefix = WORKSHOP_PREFIX + wid + "/"
        for obj in walk(s3, bucket, prefix):
            rel = obj["Key"][len(prefix):]
            if wanted(rel):
                items.append((obj["Key"], "Mods/" + wid + "/" + canonical(rel), obj["Size"], obj["ETag"].strip('"')))
    # Two keys that differ only in case would overwrite each other, as they would on Windows: keep the first.
    seen = set()
    unique = []
    for item in items:
        if item[1].lower() not in seen:
            seen.add(item[1].lower())
            unique.append(item)
    return unique, ids


def safe_join(root, relative):
    path = os.path.realpath(os.path.join(root, *relative.split("/")))
    if not path.startswith(os.path.realpath(root) + os.sep):
        raise ValueError("object key escapes the destination: " + relative)
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dest", help="destination directory (created if missing)")
    parser.add_argument("--profile", choices=("smoke", "game", "all"), default="smoke")
    parser.add_argument("--workshop-ids", default="", help="comma-separated Workshop ids instead of the profile's choice")
    parser.add_argument("--max-gib", type=float, default=8.0, help="refuse to download more than this (default 8)")
    parser.add_argument("--jobs", type=int, default=8, help="parallel downloads (default 8)")
    parser.add_argument("--list", action="store_true", help="print the plan and exit without downloading")
    parser.add_argument("--prune", action="store_true", help="delete files under Game/ and Mods/ that are not in the plan")
    args = parser.parse_args()

    bucket = os.environ.get("R2_BUCKET", "rimworld-17")
    s3 = client()
    explicit = [x.strip() for x in args.workshop_ids.split(",") if x.strip()]
    if any(not x.isdigit() for x in explicit):
        sys.exit("--workshop-ids takes numeric Workshop ids")
    items, ids = plan(s3, bucket, args.profile, explicit)
    total = sum(size for _, _, size, _ in items)
    print(f"plan: {len(items)} files, {total / 2**30:.2f} GiB, {len(ids)} Workshop mods, profile {args.profile}")
    if not items:
        sys.exit("nothing to download (check R2_BUCKET and the profile)")
    if total > args.max_gib * 2**30:
        sys.exit(f"plan exceeds --max-gib {args.max_gib}; raise it or choose a smaller profile")
    if args.list:
        for key, rel, size, _ in items[:50]:
            print(f"{size:>12} {rel}")
        if len(items) > 50:
            print(f"... and {len(items) - 50} more")
        return

    dest = os.path.abspath(args.dest)
    os.makedirs(dest, exist_ok=True)
    state_path = os.path.join(dest, ".r2-download-state.json")
    try:
        with open(state_path, encoding="utf-8") as f:
            state = json.load(f)
    except (OSError, ValueError):
        state = {}
    lock = threading.Lock()
    done = {"files": 0, "bytes": 0, "kept": 0}

    def fetch(item):
        key, rel, size, etag = item
        path = safe_join(dest, rel)
        if os.path.isfile(path) and os.path.getsize(path) == size and state.get(rel) == etag:
            with lock:
                done["kept"] += 1
            return
        os.makedirs(os.path.dirname(path), exist_ok=True)
        part = path + ".part"
        s3.download_file(bucket, key, part)
        if os.path.getsize(part) != size:
            os.remove(part)
            raise IOError("size mismatch for " + key)
        os.replace(part, path)
        with lock:
            state[rel] = etag
            done["files"] += 1
            done["bytes"] += size
            if done["files"] % 500 == 0:
                print(f"  {done['files']} downloaded, {done['bytes'] / 2**30:.2f} GiB", flush=True)
                with open(state_path, "w", encoding="utf-8") as f:
                    json.dump(state, f)

    errors = []
    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=max(1, args.jobs)) as pool:
            for future in concurrent.futures.as_completed([pool.submit(fetch, i) for i in items]):
                try:
                    future.result()
                except Exception as ex:  # keep going; report at the end
                    errors.append(str(ex))
    finally:
        with open(state_path, "w", encoding="utf-8") as f:
            json.dump(state, f)
    if errors:
        print(f"{len(errors)} downloads failed, e.g. {errors[0]}; rerun to resume", file=sys.stderr)
        sys.exit(1)

    if args.prune:
        keep = {os.path.normcase(safe_join(dest, rel)) for _, rel, _, _ in items}
        removed = 0
        for top in ("Game", "Mods"):
            for folder, _, files in os.walk(os.path.join(dest, top)):
                for name in files:
                    path = os.path.join(folder, name)
                    if os.path.normcase(os.path.realpath(path)) not in keep:
                        os.remove(path)
                        state.pop(os.path.relpath(path, dest).replace(os.sep, "/"), None)
                        removed += 1
        with open(state_path, "w", encoding="utf-8") as f:
            json.dump(state, f)
        print(f"pruned {removed} files that are not in the plan")

    listing = "\n".join(f"{rel}|{size}|{etag}" for _, rel, size, etag in sorted(items, key=lambda x: x[1]))
    provenance = {
        "schema": SCHEMA,
        "bucket": bucket,
        "sources": [GAME_PREFIX + "Assets.zip", GAME_PREFIX + "Data/", WORKSHOP_PREFIX],
        "profile": args.profile,
        "workshop_ids": ids,
        "files": len(items),
        "bytes": total,
        "listing_sha256": hashlib.sha256(listing.encode("utf-8")).hexdigest(),
        "fetched_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
        "renamed_to_canonical_case": sum(1 for key, rel, _, _ in items if key.split("/", 2)[-1] != rel.split("/", 2)[-1]),
        "filter": "Defs/Patches/Languages/Textures/Sounds with " + ",".join(sorted(ASSET_EXTENSIONS)) + "; About.xml; loadFolders.xml; Assets.zip",
    }
    with open(os.path.join(dest, "r2-dataset.json"), "w", encoding="utf-8") as f:
        json.dump(provenance, f, indent=2)
    print(f"done: {done['files']} downloaded ({done['bytes'] / 2**30:.2f} GiB), {done['kept']} already present; {dest}")


if __name__ == "__main__":
    main()

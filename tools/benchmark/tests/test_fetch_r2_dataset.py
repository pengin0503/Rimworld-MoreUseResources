"""Unit tests for fetch-r2-dataset.py that need neither boto3 nor network access.

Run: python3 -m unittest discover -s tools/benchmark/tests -v
"""

import importlib.util
import os
import unittest

_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, "fetch-r2-dataset.py")
_SPEC = importlib.util.spec_from_file_location("fetch_r2_dataset", _PATH)
fetch = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(fetch)

ENDPOINT = "https://0123456789abcdef.r2.cloudflarestorage.com"


class FakeS3:
    """list_objects_v2 over a fixed key list, with Prefix and Delimiter as S3 implements them."""

    def __init__(self, keys):
        self.keys = sorted(keys)

    def list_objects_v2(self, Bucket, Prefix="", Delimiter=None, ContinuationToken=None):  # noqa: N803 (boto3 names)
        contents, prefixes = [], []
        for key in self.keys:
            if not key.startswith(Prefix):
                continue
            rest = key[len(Prefix):]
            if Delimiter and Delimiter in rest:
                folder = Prefix + rest.split(Delimiter, 1)[0] + Delimiter
                if folder not in prefixes:
                    prefixes.append(folder)
            else:
                contents.append({"Key": key, "Size": 10, "ETag": '"e"'})
        page = {"Contents": contents, "IsTruncated": False}
        if Delimiter:
            page["CommonPrefixes"] = [{"Prefix": p} for p in prefixes]
        return page


GAME = [
    "Game-Windows/Assets.zip",
    "Game-Windows/Assembly-CSharp.dll",
    "Game-Windows/Data/Core/Defs/Things.xml",
    "Game-Windows/Data/Core/Textures/a.png",
    "Game-Windows/Data/Royalty/Defs/Royal.xml",
]
MODS = [
    "Workshop/111/About/About.xml",
    "Workshop/111/1.7/defs/Mod.xml",
    "Workshop/111/Assemblies/Mod.dll",
    "Workshop/222/About/About.xml",
]


class EndpointTests(unittest.TestCase):
    def test_plain_endpoint_uses_the_default_bucket(self):
        self.assertEqual((ENDPOINT, "rimworld-17", None), fetch.endpoint_and_bucket(ENDPOINT, None))

    def test_bucket_from_r2_bucket(self):
        self.assertEqual((ENDPOINT, "other", None), fetch.endpoint_and_bucket(ENDPOINT + "/", "other"))

    def test_dashboard_url_with_the_bucket_is_split(self):
        # The S3 API URL that the Cloudflare dashboard shows; boto3 would request
        # /<bucket>/<bucket>?list-type=2 with it, which R2 answers with NoSuchKey.
        for url, bucket_env in ((ENDPOINT + "/rimworld-17", None), (ENDPOINT + "/rimworld-17/", "rimworld-17"),
                                (" " + ENDPOINT + "/rimworld-17 ", "")):
            endpoint, bucket, note = fetch.endpoint_and_bucket(url, bucket_env)
            self.assertEqual(ENDPOINT, endpoint)
            self.assertEqual("rimworld-17", bucket)
            self.assertIn("/rimworld-17", note)

    def test_rejected_endpoints_name_the_problem(self):
        cases = {
            "": "not set",
            None: "not set",
            "0123456789abcdef.r2.cloudflarestorage.com": "must be a URL",
            ENDPOINT + "/rimworld-17/Game-Windows": "has a path",
            ENDPOINT + "/?x=1": "must not contain",
            # The documentation's line pasted unchanged (quoted, so bash did not treat "<" as a redirection).
            "https://<アカウントID>.r2.cloudflarestorage.com": "placeholder",
            "https://<account id>.r2.cloudflarestorage.com": "placeholder",
        }
        for url, text in cases.items():
            with self.assertRaises(fetch.SetupError, msg=repr(url)) as raised:
                fetch.endpoint_and_bucket(url, None)
            self.assertIn(text, str(raised.exception))

    def test_bucket_in_url_and_a_different_r2_bucket_is_an_error(self):
        with self.assertRaises(fetch.SetupError) as raised:
            fetch.endpoint_and_bucket(ENDPOINT + "/rimworld-17", "other")
        self.assertIn("'rimworld-17'", str(raised.exception))
        self.assertIn("'other'", str(raised.exception))


class CredentialTests(unittest.TestCase):
    def test_placeholders_are_rejected(self):
        for value in ("...", "<key>", "アクセスキー"):
            with self.assertRaises(fetch.SetupError, msg=value) as raised:
                fetch.check_credentials({"AWS_ACCESS_KEY_ID": "abc", "AWS_SECRET_ACCESS_KEY": value})
            self.assertIn("AWS_SECRET_ACCESS_KEY", str(raised.exception))

    def test_real_looking_or_absent_keys_pass(self):
        # Absent keys are left to boto3, which may find them in ~/.aws/credentials.
        fetch.check_credentials({})
        fetch.check_credentials({"AWS_ACCESS_KEY_ID": "0123456789abcdef0123456789abcdef",
                                 "AWS_SECRET_ACCESS_KEY": "a" * 64})


class PlanTests(unittest.TestCase):
    def test_game_profile_takes_official_data_and_the_pack_only(self):
        items, ids = fetch.plan(FakeS3(GAME + MODS), "b", "game", [])
        self.assertEqual([], ids)
        self.assertEqual(["Game/Assets.zip", "Game/Data/Core/Defs/Things.xml", "Game/Data/Core/Textures/a.png",
                          "Game/Data/Royalty/Defs/Royal.xml"], sorted(rel for _, rel, _, _ in items))

    def test_all_profile_adds_mods_in_canonical_case(self):
        items, ids = fetch.plan(FakeS3(GAME + MODS), "b", "all", [])
        self.assertEqual(["111", "222"], ids)
        rels = {rel for _, rel, _, _ in items}
        self.assertIn("Mods/111/1.7/Defs/Mod.xml", rels)
        self.assertIn("Mods/222/About/About.xml", rels)
        self.assertNotIn("Mods/111/Assemblies/Mod.dll", rels)

    def test_missing_game_files_name_the_keys_and_what_the_bucket_has(self):
        with self.assertRaises(fetch.SetupError) as raised:
            fetch.plan(FakeS3(["Game-Linux/Assets.zip"] + MODS), "b", "smoke", [])
        text = str(raised.exception)
        self.assertIn("Game-Windows/Assets.zip", text)
        self.assertIn("Game-Windows/Data/Core/", text)
        self.assertIn("Game-Linux/", text)
        self.assertIn("Workshop/", text)

    def test_missing_pack_alone_is_reported(self):
        keys = [k for k in GAME if not k.endswith("Assets.zip")]
        with self.assertRaises(fetch.SetupError) as raised:
            fetch.plan(FakeS3(keys), "b", "game", [])
        self.assertIn("Game-Windows/Assets.zip", str(raised.exception))
        self.assertNotIn("Data/", str(raised.exception).split(";")[0])

    def test_explicit_workshop_id_without_files_is_an_error(self):
        with self.assertRaises(fetch.SetupError) as raised:
            fetch.plan(FakeS3(GAME + MODS), "b", "game", ["111", "999"])
        self.assertIn("999", str(raised.exception))
        self.assertNotIn("111", str(raised.exception))

    def test_bucket_without_mods_warns_and_still_plans_official_data(self):
        warnings = []
        items, ids = fetch.plan(FakeS3(GAME), "b", "smoke", [], warn=warnings.append)
        self.assertEqual([], ids)
        self.assertTrue(items)
        self.assertEqual(1, len(warnings))
        self.assertIn("Workshop/", warnings[0])


class ExplainTests(unittest.TestCase):
    class _Error(Exception):
        def __init__(self, code, message):
            super().__init__(message)
            self.response = {"Error": {"Code": code, "Message": message}}

    def test_r2_error_codes_get_a_hint(self):
        text = fetch.explain(self._Error("NoSuchKey", "The specified key does not exist."), "b", "listing failed")
        self.assertIn("NoSuchKey: The specified key does not exist.", text)
        self.assertIn("R2_ENDPOINT has a path", text)
        self.assertIn("R2_BUCKET", fetch.explain(self._Error("NoSuchBucket", "x"), "b", "w"))

    def test_errors_without_a_response_use_the_class_name(self):
        class EndpointConnectionError(Exception):
            pass

        text = fetch.explain(EndpointConnectionError("Could not connect"), "b", "download of k")
        self.assertTrue(text.startswith("download of k: EndpointConnectionError: Could not connect"))
        self.assertIn("cannot be reached", text)


if __name__ == "__main__":
    unittest.main()

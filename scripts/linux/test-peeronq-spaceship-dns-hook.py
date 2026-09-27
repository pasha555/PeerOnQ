#!/usr/bin/env python3
import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
from unittest import mock


ROOT = pathlib.Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("peeronq_dns_hook", ROOT / "peeronq-spaceship-dns-hook.py")
HOOK = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(HOOK)


class SpaceshipHookTests(unittest.TestCase):
    def test_api_request_uses_cloudflare_safe_user_agent(self):
        response = mock.MagicMock()
        response.__enter__.return_value = response
        response.status = 200
        response.read.return_value = b'{"items":[],"total":0}'
        response.headers = {}

        with mock.patch.object(HOOK, "read_secret", side_effect=("K" * 20, "S" * 32)), \
                mock.patch.object(HOOK.OPENER, "open", return_value=response) as open_request:
            status, _, _ = HOOK.api_request("GET", query="take=1&skip=0")

        request = open_request.call_args.args[0]
        self.assertEqual(status, 200)
        self.assertEqual(request.get_header("User-agent"), HOOK.USER_AGENT)
        self.assertNotIn("Python-urllib", request.get_header("User-agent"))

    def test_two_tokens_are_independent_and_exact(self):
        with tempfile.TemporaryDirectory() as temp:
            base = pathlib.Path(temp)
            creds = base / "creds"
            creds.mkdir(mode=0o700)
            for name, value in (("api-key", "K" * 20), ("api-secret", "S" * 32)):
                path = creds / name
                path.write_text(value + "\n", encoding="ascii")
                path.chmod(0o400)
            debt = base / "debt"
            state = []
            calls = []

            def api(method, body=None, query=""):
                calls.append((method, body, query))
                if method == "GET":
                    return 200, json.dumps({"items": state, "total": len(state)}).encode(), {}
                if method == "PUT":
                    state.extend([{**item, "group": {"type": "custom"}} for item in body["items"]])
                    return 204, b"", {}
                if method == "DELETE":
                    for item in body:
                        state[:] = [record for record in state if not (
                            record["type"] == item["type"]
                            and record["name"] == item["name"]
                            and record["value"] == item["value"]
                        )]
                    return 204, b"", {}
                raise AssertionError(method)

            with mock.patch.object(HOOK, "CREDENTIAL_DIR", creds), \
                    mock.patch.object(HOOK, "DEBT_DIR", debt), \
                    mock.patch.object(HOOK, "api_request", side_effect=api), \
                    mock.patch.object(HOOK, "wait_for_propagation"):
                os.environ.update({
                    "CERTBOT_IDENTIFIER": "peeronq.com",
                    "CERTBOT_ALL_DOMAINS": "peeronq.com,peeronq.com",
                })
                for token in ("A" * 43, "B" * 43):
                    os.environ["CERTBOT_VALIDATION"] = token
                    HOOK.auth()
                self.assertEqual({record["value"] for record in state}, {"A" * 43, "B" * 43})
                os.environ["CERTBOT_VALIDATION"] = "A" * 43
                HOOK.cleanup()
                self.assertEqual([record["value"] for record in state], ["B" * 43])
                HOOK.cleanup()  # already absent is idempotent
                os.environ["CERTBOT_VALIDATION"] = "B" * 43
                HOOK.cleanup()
            puts = [body for method, body, _ in calls if method == "PUT"]
            deletes = [body for method, body, _ in calls if method == "DELETE"]
            self.assertTrue(all(body["force"] is False for body in puts))
            self.assertTrue(all(body["items"][0]["ttl"] == 60 for body in puts))
            self.assertEqual(deletes[0], [{"type": "TXT", "name": "_acme-challenge", "value": "A" * 43}])

    def test_rejects_literal_backslash_wildcard_and_bad_token(self):
        with mock.patch.object(HOOK, "api_request") as api:
            os.environ.update({
                "CERTBOT_IDENTIFIER": r"\*.peeronq.com",
                "CERTBOT_DOMAIN": r"\*.peeronq.com",
                "CERTBOT_VALIDATION": "not-a-token",
            })
            with self.assertRaises(HOOK.HookError):
                HOOK.token_from_environment()
            api.assert_not_called()

    def test_rejects_unrelated_identifier_set_before_network(self):
        with mock.patch.object(HOOK, "api_request") as api:
            os.environ.update({
                "CERTBOT_IDENTIFIER": "peeronq.com",
                "CERTBOT_ALL_DOMAINS": "peeronq.com,evil.example",
                "CERTBOT_VALIDATION": "C" * 43,
            })
            with self.assertRaises(HOOK.HookError):
                HOOK.token_from_environment()
            api.assert_not_called()


if __name__ == "__main__":
    unittest.main()

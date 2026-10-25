#!/usr/local/bin/python3
"""Certbot DNS-01 hook for the PeerOnQ Spaceship DNS zone.

The hook deliberately has no configurable API URL or zone.  It can therefore
only edit the PeerOnQ ACME TXT record, and it never receives credentials in
argv or the environment.
"""
from __future__ import annotations

import hashlib
import json
import os
import random
import re
import socket
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

API_URL = "https://spaceship.dev/api/v1/dns/records/peeronq.com"
USER_AGENT = "PeerOnQ-DNS01/1.0"
ZONE = "peeronq.com"
OWNER = "_acme-challenge"
CREDENTIAL_DIR = Path("/run/secrets/peeronq-spaceship")
DEBT_DIR = Path("/etc/letsencrypt/.peeronq-dns01-debt")
TOKEN_RE = re.compile(r"^[A-Za-z0-9_-]{43}$")
IDENTIFIERS = {ZONE, "*." + ZONE}
MAX_BODY = 2 * 1024 * 1024
TRANSIENT = {429, 500, 502, 503, 504}


class HookError(RuntimeError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: N802
        raise HookError("Spaceship API redirect was rejected")


OPENER = urllib.request.build_opener(NoRedirect())


def fail(message: str) -> "NoReturn":
    print("PeerOnQ DNS hook: " + message, file=sys.stderr)
    raise SystemExit(1)


def read_secret(name: str) -> str:
    path = CREDENTIAL_DIR / name
    try:
        st = path.stat()
    except OSError:
        raise HookError("Spaceship credential is missing")
    if path.is_symlink() or st.st_uid != 0 or st.st_nlink != 1 or st.st_mode & 0o777 not in (0o400, 0o600):
        raise HookError("Spaceship credential permissions are unsafe")
    raw = path.read_bytes()
    if len(raw) < 16 or len(raw) > 512 or b"\x00" in raw:
        raise HookError("Spaceship credential format is invalid")
    text = raw.decode("ascii")
    if text.endswith("\n"):
        text = text[:-1]
    if "\n" in text or not re.fullmatch(r"[A-Za-z0-9_-]+", text):
        raise HookError("Spaceship credential format is invalid")
    return text


def token_from_environment() -> str:
    token = os.environ.get("CERTBOT_VALIDATION", "")
    if not TOKEN_RE.fullmatch(token):
        raise HookError("Certbot validation token is invalid")
    identifier = os.environ.get("CERTBOT_IDENTIFIER") or os.environ.get("CERTBOT_DOMAIN", "")
    if identifier not in IDENTIFIERS:
        raise HookError("unexpected Certbot identifier")
    all_domains = os.environ.get("CERTBOT_ALL_DOMAINS", "").replace(",", " ").split()
    # Certbot strips the wildcard marker from AnnotatedChallenge.identifier;
    # apex + wildcard therefore arrives as peeronq.com,peeronq.com. A cached
    # authorization may reduce the active challenge list to one identifier.
    if not all_domains or len(all_domains) > 2 or any(domain != ZONE for domain in all_domains):
        raise HookError("unexpected Certbot identifier set")
    return token


def api_request(method: str, body: object | None = None, query: str = "") -> tuple[int, bytes, dict[str, str]]:
    key = read_secret("api-key")
    secret = read_secret("api-secret")
    url = API_URL + ("?" + query if query else "")
    data = None if body is None else json.dumps(body, separators=(",", ":")).encode("utf-8")
    # Cloudflare rejects urllib's default Python-urllib signature with Error
    # 1010 before the request reaches Spaceship authentication.
    headers = {
        "Accept": "application/json",
        "User-Agent": USER_AGENT,
        "X-API-Key": key,
        "X-API-Secret": secret,
    }
    if data is not None:
        headers["Content-Type"] = "application/json"
    for attempt in range(5):
        request = urllib.request.Request(url, data=data, headers=headers, method=method)
        try:
            with OPENER.open(request, timeout=15) as response:
                payload = response.read(MAX_BODY + 1)
                if len(payload) > MAX_BODY:
                    raise HookError("Spaceship response is too large")
                return response.status, payload, dict(response.headers)
        except urllib.error.HTTPError as exc:
            if exc.code not in TRANSIENT or attempt == 4:
                raise HookError(f"Spaceship API returned HTTP {exc.code}")
            retry = exc.headers.get("Retry-After", "")
            try:
                delay = min(30, max(1, int(retry)))
            except ValueError:
                delay = min(30, 2 ** attempt)
            time.sleep(delay)
        except (urllib.error.URLError, TimeoutError, socket.timeout, OSError) as exc:
            if attempt == 4:
                raise HookError("Spaceship API request failed") from exc
            time.sleep(min(30, 2 ** attempt))
    raise HookError("Spaceship API request failed")


def records() -> list[dict]:
    result: list[dict] = []
    skip = 0
    while True:
        status, payload, _ = api_request("GET", query=f"take=100&skip={skip}")
        if status != 200:
            raise HookError("Spaceship record listing failed")
        try:
            parsed = json.loads(payload)
            items = parsed["items"]
            total = int(parsed["total"])
        except (ValueError, KeyError, TypeError, json.JSONDecodeError) as exc:
            raise HookError("Spaceship record response is invalid") from exc
        if not isinstance(items, list) or total < 0 or len(items) > 100:
            raise HookError("Spaceship record response is invalid")
        result.extend(item for item in items if isinstance(item, dict))
        skip += len(items)
        if skip >= total or not items:
            return result


def has_token(token: str) -> bool:
    return any(
        item.get("type") == "TXT"
        and item.get("name") == OWNER
        and item.get("value") == token
        and (item.get("group") or {}).get("type") == "custom"
        for item in records()
    )


def save_token(token: str) -> bool:
    if has_token(token):
        return False
    body = {"force": False, "items": [{"type": "TXT", "name": OWNER, "value": token, "ttl": 60}]}
    status, _, _ = api_request("PUT", body)
    if status != 204 or not has_token(token):
        raise HookError("Spaceship did not confirm the ACME TXT record")
    return True


def delete_token(token: str) -> None:
    if not has_token(token):
        return
    body = [{"type": "TXT", "name": OWNER, "value": token}]
    status, _, _ = api_request("DELETE", body)
    if status != 204 or has_token(token):
        raise HookError("Spaceship did not remove the ACME TXT record")


def debt_path(token: str) -> Path:
    return DEBT_DIR / hashlib.sha256(token.encode("ascii")).hexdigest()


def remember(token: str, owned: bool) -> None:
    DEBT_DIR.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(DEBT_DIR, 0o700)
    path = debt_path(token)
    path.write_text(("1" if owned else "0") + " " + token + "\n", encoding="ascii")
    os.chmod(path, 0o600)


def forget(token: str) -> None:
    try:
        debt_path(token).unlink()
    except FileNotFoundError:
        pass


def auth() -> None:
    token = token_from_environment()
    # A pre-existing exact token is never claimed or deleted by this hook.
    remember(token, False)
    try:
        owned = save_token(token)
        remember(token, owned)
        # Certbot validates after the hook returns; authoritative DNS polling is
        # intentionally bounded so a broken zone never replaces the old cert.
        try:
            propagation_seconds = int(os.environ.get("PEERONQ_DNS_PROPAGATION_SECONDS", "180"))
        except ValueError:
            raise HookError("DNS propagation timeout is invalid")
        if not 30 <= propagation_seconds <= 900:
            raise HookError("DNS propagation timeout is invalid")
        wait_for_propagation(token, propagation_seconds)
    except Exception:
        try:
            if debt_path(token).read_text(encoding="ascii").startswith("1 "):
                delete_token(token)
        except Exception:
            pass
        raise


def cleanup() -> None:
    token = token_from_environment()
    try:
        owned = debt_path(token).read_text(encoding="ascii").startswith("1 ")
    except FileNotFoundError:
        owned = False
    if owned:
        delete_token(token)
    forget(token)


def reconcile() -> None:
    if not DEBT_DIR.is_dir():
        return
    for path in sorted(DEBT_DIR.iterdir()):
        if not path.is_file() or not re.fullmatch(r"[0-9a-f]{64}", path.name):
            continue
        value = path.read_text(encoding="ascii").strip().split(" ", 1)
        token = value[1] if len(value) == 2 and value[0] == "1" else ""
        if TOKEN_RE.fullmatch(token):
            delete_token(token)
            path.unlink(missing_ok=True)


def dns_name(name: str) -> bytes:
    labels = name.rstrip(".").split(".")
    return b"".join(bytes([len(label)]) + label.encode("idna") for label in labels) + b"\0"


def read_name(packet: bytes, offset: int) -> tuple[str, int]:
    labels: list[str] = []
    original = offset
    jumped = False
    for _ in range(64):
        if offset >= len(packet):
            raise ValueError("DNS name is truncated")
        length = packet[offset]
        if length == 0:
            return ".".join(labels), offset + 1 if not jumped else original + 2
        if length & 0xC0 == 0xC0:
            if offset + 1 >= len(packet):
                raise ValueError("DNS pointer is truncated")
            pointer = ((length & 0x3F) << 8) | packet[offset + 1]
            part, _ = read_name(packet, pointer)
            labels.extend(part.split("."))
            return ".".join(labels), original + 2
        offset += 1
        if offset + length > len(packet):
            raise ValueError("DNS label is truncated")
        labels.append(packet[offset:offset + length].decode("ascii").lower())
        offset += length
    raise ValueError("DNS name has too many labels")


def dns_query(server: str, name: str, qtype: int, recursive: bool = False) -> list[tuple[int, str, bytes, bool]]:
    ident = random.randrange(1, 65535)
    flags = 0x0100 if recursive else 0
    packet = struct.pack("!HHHHHH", ident, flags, 1, 0, 0, 0) + dns_name(name) + struct.pack("!HH", qtype, 1)
    addresses = socket.getaddrinfo(server, 53, socket.AF_INET, socket.SOCK_DGRAM)
    for _, _, _, _, address in addresses:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            sock.settimeout(4)
            try:
                sock.sendto(packet, address)
                response, _ = sock.recvfrom(65535)
            except OSError:
                continue
        if len(response) < 12 or struct.unpack("!H", response[:2])[0] != ident:
            continue
        flags, qd, an, ns, ar = struct.unpack("!HHHHH", response[2:12])
        if flags & 0x000F:
            continue
        offset = 12
        for _ in range(qd):
            _, offset = read_name(response, offset)
            offset += 4
        answers: list[tuple[int, str, bytes, bool]] = []
        for section_count, authoritative in ((an, bool(flags & 0x0400)), (ns, False), (ar, False)):
            for _ in range(section_count):
                owner, offset = read_name(response, offset)
                if offset + 10 > len(response):
                    raise ValueError("DNS RR is truncated")
                rrtype, _, _, rdlength = struct.unpack("!HHIH", response[offset:offset + 10])
                offset += 10
                rdata_offset = offset
                rdata = response[offset:offset + rdlength]
                offset += rdlength
                if len(rdata) != rdlength:
                    raise ValueError("DNS RDATA is truncated")
                if rrtype == 2:
                    target, _ = read_name(response, rdata_offset)
                    rdata = target.encode("ascii")
                answers.append((rrtype, owner, rdata, authoritative))
        return answers
    return []


def wait_for_propagation(token: str, seconds: int) -> None:
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            ns_answers = dns_query("1.1.1.1", ZONE, 2, recursive=True)
            nameservers = [data.decode("ascii").rstrip("\0") for typ, _, data, _ in ns_answers if typ == 2]
            if nameservers:
                all_seen = True
                for nameserver in nameservers:
                    answers = dns_query(nameserver, OWNER + "." + ZONE, 16)
                    seen = any(typ == 16 and owner == OWNER + "." + ZONE and token.encode("ascii") in data for typ, owner, data, authoritative in answers if authoritative)
                    all_seen = all_seen and seen
                if all_seen:
                    return
        except (OSError, ValueError, UnicodeError):
            pass
        time.sleep(5)
    raise HookError("ACME TXT propagation did not complete before timeout")


def main() -> None:
    if len(sys.argv) != 2 or sys.argv[1] not in {"auth", "cleanup", "reconcile"}:
        fail("usage is auth, cleanup, or reconcile")
    try:
        {"auth": auth, "cleanup": cleanup, "reconcile": reconcile}[sys.argv[1]]()
    except HookError as exc:
        fail(str(exc))
    except (OSError, ValueError, UnicodeError, json.JSONDecodeError) as exc:
        fail("DNS hook operation failed")


if __name__ == "__main__":
    main()

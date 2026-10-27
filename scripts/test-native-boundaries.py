#!/usr/bin/env python3
"""Published-artifact HTTP semantics and stream fault/lifetime gate (stdlib only)."""
import gzip
import hashlib
import http.client
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import uuid
from urllib.parse import quote, urlencode


def request(port, path, method="GET", body=None, headers=None, chunked=False):
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    try:
        connection.request(method, path, body=body, headers=headers or {}, encode_chunked=chunked)
        response = connection.getresponse()
        return response.status, {key.lower(): value for key, value in response.getheaders()}, response.read()
    finally:
        connection.close()


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def stats(port):
    status, _, data = request(port, "/stats")
    require(status == 200, f"Stats failed: {status}")
    return json.loads(data)


def wait_for_cleanup(port, disconnected, failed):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        snapshot = stats(port)
        if snapshot["active"] == 0 and snapshot["disconnected"] >= disconnected and snapshot["failed"] >= failed:
            return snapshot
        time.sleep(0.025)
    raise AssertionError(f"Stream lifetime did not settle: {stats(port)}")


def check_responses(port):
    for encoding in ("identity", "gzip"):
        status, headers, body = request(port, "/small", headers={"Accept-Encoding": encoding})
        require(status == 200 and body == b"Hello, World!", "Small payload changed")
        require("content-encoding" not in headers, "Subthreshold response was compressed")
        status, headers, body = request(port, "/large", headers={"Accept-Encoding": encoding})
        if encoding == "gzip":
            require(headers.get("content-encoding") == "gzip", "Large response ignored gzip negotiation")
            body = gzip.decompress(body)
        else:
            require("content-encoding" not in headers, "Identity response was compressed")
        require(status == 200 and body == b"x" * 4096, "Large payload changed")
    status, headers, body = request(port, "/large", method="HEAD", headers={"Accept-Encoding": "identity"})
    require(status == 200 and body == b"" and headers.get("content-length") == "4096", "HEAD lost representation length or emitted a body")
    for code in (204, 205, 304):
        status, headers, body = request(port, f"/empty/{code}")
        require(status == code and body == b"", f"Bodyless status {code} emitted a payload")
        if code == 204:
            require("content-length" not in headers, "204 must not contain Content-Length")
        if code == 205:
            require(headers.get("content-length") == "0", "205 must be zero length")
    subject = "space + &= snowman \u2603 / : *"
    key = "key +&: snowman \u2603"
    status, _, body = request(port, f"/query/{quote(key, safe='')}?{urlencode({'subject': subject, 'enabled': 'false'})}")
    require(status == 200 and json.loads(body) == {"key": key, "subject": subject, "enabled": "false"}, f"Path/query encoding changed its meaning: status={status}, body={body!r}")


def check_request_limits(port):
    for route in ("/buffer", "/stream-body"):
        for chunked in (False, True):
            for length in (0, 15, 16, 17):
                payload = b"z" * length
                body = iter((payload[:8], payload[8:])) if chunked else payload
                status, _, response = request(port, route, "POST", body, {"Content-Type": "text/plain"}, chunked)
                expected = 413 if length > 16 else 200
                require(status == expected, f"{route}: chunked={chunked}, length={length}, expected {expected}, got {status}: {response!r}")
                if status == 200:
                    require(response == payload, "Request body changed at its limit")


def check_streams(port):
    for encoding in ("identity", "gzip"):
        status, headers, body = request(port, "/stream/normal", headers={"Accept-Encoding": encoding})
        if encoding == "gzip":
            require(headers.get("content-encoding") == "gzip", "Unknown-length stream did not negotiate gzip")
            body = gzip.decompress(body)
        require(status == 200 and body == b"first\n" + b"y" * 4096, "Streaming bytes changed")
    status, headers, body = request(port, "/sse", headers={"Accept-Encoding": "gzip"})
    require(status == 200 and "content-encoding" not in headers, "SSE was compressed")
    require(body == b"data: one\ndata: two\n\n", "SSE multiline framing changed")
    status, _, _ = request(port, "/stream/before")
    require(status == 500, f"Pre-first-byte stream failure should be 500, got {status}")
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    try:
        connection.request("GET", "/stream/after")
        response = connection.getresponse()
        require(response.status == 200, "Already-started response status was rewritten")
        try:
            response.read()
        except http.client.IncompleteRead as error:
            require(error.partial == b"first\n", "Post-first-byte failure corrupted the sent prefix")
        else:
            raise AssertionError("Post-first-byte failure masqueraded as a complete successful body")
    finally:
        connection.close()
    connection = http.client.HTTPConnection("127.0.0.1", port, timeout=5)
    try:
        connection.request("GET", "/stream/disconnect")
        response = connection.getresponse()
        require(response.read(6) == b"first\n", "Disconnect probe never streamed its first bytes")
        response.close()
    finally:
        connection.close()
    snapshot = wait_for_cleanup(port, disconnected=1, failed=2)
    require(snapshot["completed"] == 2, f"Unexpected completed stream count: {snapshot}")
    return snapshot


def check_generated_client(binary, environment, directory, port):
    root = Path(__file__).resolve().parent.parent
    contracts = Path(directory) / "contracts"
    subprocess.run([str(binary), "--codegen", "--out", str(contracts)], env=environment, cwd=directory, check=True, timeout=30)
    # Same relative-client import normalization used by the fullstack scaffold.
    app_type = contracts / "app-type.d.ts"
    app_type.write_text(app_type.read_text().replace('from "@reptar/client"', 'from "./client"'))
    shutil.copyfile(root / "packages/rpc/src/client.ts", contracts / "client.ts")
    shutil.copyfile(root / "scripts/testing/published-client.fixture.ts", contracts / "runtime-probe.ts")
    subprocess.run([str(root / "node_modules/.bin/tsc"), "--noEmit", "--strict", "--target", "ES2022", "--module", "ESNext", "--moduleResolution", "Bundler", "runtime-probe.ts"], cwd=contracts, check=True, timeout=30)
    subprocess.run(["bun", "-e", 'import { runPublishedContractProbe } from "./runtime-probe.ts"; await runPublishedContractProbe(process.env.REPTAR_PROBE_URL);'], cwd=contracts, env={**environment, "REPTAR_PROBE_URL": f"http://127.0.0.1:{port}"}, check=True, timeout=30)


def main():
    binary = Path(sys.argv[1]).resolve()
    run_id = uuid.uuid4().hex
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    with tempfile.TemporaryDirectory(prefix="reptar-http-boundaries-") as directory:
        log_path = Path(directory) / "server.log"
        with log_path.open("w+") as log:
            environment = {**os.environ, "HOST": "127.0.0.1", "PORT": str(port), "REPTAR_PROBE_RUN_ID": run_id}
            server = subprocess.Popen([str(binary)], cwd=directory, env=environment, stdout=log, stderr=log, start_new_session=True)
            try:
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    require(server.poll() is None, "Owned published server exited during startup")
                    try:
                        status, _, body = request(port, "/health")
                        if status == 200 and body.decode() == run_id:
                            break
                    except (OSError, http.client.HTTPException):
                        pass
                    time.sleep(0.05)
                else:
                    raise AssertionError("Owned published server failed identity/readiness check")
                check_generated_client(binary, environment, directory, port)
                check_responses(port)
                check_request_limits(port)
                snapshot = check_streams(port)
                require(request(port, "/health")[2].decode() == run_id, "Fault tests damaged subsequent requests")
                print(json.dumps({"result": "passed", "binarySha256": hashlib.sha256(binary.read_bytes()).hexdigest(), "streamStats": snapshot, "checks": ["native-emitted-generated-client", "compression-and-payload", "head-and-bodyless", "query-encoding", "buffered-and-chunked-limits", "stream-errors", "disconnect-cleanup"]}, indent=2))
            except BaseException:
                log.flush()
                print(log_path.read_text(), file=sys.stderr)
                raise
            finally:
                if server.poll() is None:
                    server.terminate()
                    try:
                        server.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        server.kill()
                        server.wait(timeout=5)


if __name__ == "__main__":
    main()

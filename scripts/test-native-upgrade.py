#!/usr/bin/env python3
"""Pinned previous Switchyard/agent -> current -> backup/old-code read, disposable only.

This pair has no application schema delta: it proves no-op schema upgrade, not DDL reversal.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import sqlite3
import subprocess
import tempfile
import time

SPEC = importlib.util.spec_from_file_location("operational", Path(__file__).with_name("test-operational-soak.py"))
OP = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(OP)


def rows(path):
    with sqlite3.connect(path, timeout=3) as database:
        tables = [row[0] for row in database.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
        return {table: set(database.execute('SELECT * FROM "' + table.replace('"', '""') + '"').fetchall()) for table in tables}


def prompt_rpc(rpc, message, count):
    rpc.call("prompt", message=message)
    deadline = time.monotonic() + 10
    while True:
        entries = rpc.call("get_entries")["entries"]
        if len(entries) >= count:
            OP.require(len(entries) == count, "RPC upgrade duplicated history")
            return entries
        OP.require(time.monotonic() < deadline, "RPC upgrade persistence deadline")
        time.sleep(.025)


def prompt_web(base, session, message, count):
    _, stream, _ = OP.http(base, "/api/chat", {"provider": "echo", "sessionId": session, "message": message})
    events = [json.loads(line[5:].strip()) for line in stream.splitlines() if line.startswith("data:") and line[5:].strip() != "[DONE]"]
    OP.require(any(event.get("type") == "done" for event in events) and not any(event.get("type") == "error" for event in events), "Upgrade web stream failed")
    history = OP.json_http(base, f"/api/sessions/{session}/messages")[1]
    OP.require(len(history) == count, "Upgrade web duplicated/lost a turn")
    return history


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("previous-switchyard", "previous-agent", "current-switchyard", "current-agent", "previous-source-commit", "bun"):
        parser.add_argument(f"--{name}", required=True)
    args = parser.parse_args()
    OP.require(len(args.previous_source_commit) == 40 and all(c in "0123456789abcdef" for c in args.previous_source_commit), "Pin the full prior source commit")
    root = Path(tempfile.mkdtemp(prefix="reptar-reliability-upgrade-"))
    os.chmod(root, 0o700)
    home, workspace = root / "home", root / "workspace"
    home.mkdir()
    workspace.mkdir()
    (workspace / "README.md").write_text("Disposable offline upgrade workspace.\n")
    env = {key: os.environ[key] for key in ("PATH", "TMPDIR", "LANG") if key in os.environ}
    env.update({"HOME": str(home), "XDG_CONFIG_HOME": str(home / ".config"), "HOST": "127.0.0.1", "SWITCHYARD_INSECURE_LOCAL": "1"})
    owner = OP.Owner(root, env)
    ports = {name: OP.free_port() for name in ("switchyard", "agent")}
    OP.require(len(set(ports.values())) == 2, "Port selection collided")
    bases = {name: f"http://127.0.0.1:{port}" for name, port in ports.items()}
    paths = {name: root / f"{name}.db" for name in ("switchyard", "agent-web", "agent-rpc")}
    artifacts = {name: Path(getattr(args, name.replace("-", "_"))).resolve() for name in ("previous-switchyard", "previous-agent", "current-switchyard", "current-agent")}
    current_commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=OP.REPO, text=True).strip()
    manifest = {"previousSourceCommit": args.previous_source_commit, "runnerSourceCommit": current_commit, "artifactSha256": {name: hashlib.sha256(path.read_bytes()).hexdigest() for name, path in artifacts.items()},
                "scope": "Switchyard/agent no-op schema upgrade and same-schema old-code backup read", "startedAt": time.time(), "paidModelCalls": 0,
                "runnerSha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(), "operationalHelperSha256": hashlib.sha256(Path(OP.__file__).read_bytes()).hexdigest()}
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2))
    result = {"status": "failed", "pinnedPreviousUsed": False, "schemaDelta": "none", "ddlReversalProven": False}
    print(f"Owned upgrade evidence: {root}", flush=True)

    def start(version, databases, session=None):
        switch = owner.start(f"{version}-switchyard", [str(artifacts[f"{version}-switchyard"])], {"PORT": str(ports["switchyard"]), "SWITCHYARD_DB": str(databases["switchyard"])})
        agent = owner.start(f"{version}-agent-web", [str(artifacts[f"{version}-agent"]), "--workspace", str(workspace), "--db", str(databases["agent-web"]), "--provider", "echo", "--web", str(ports["agent"])])
        OP.ready(switch, bases["switchyard"], "/health")
        OP.ready(agent, bases["agent"], "/api/health")
        rpc = OP.Rpc(owner, artifacts[f"{version}-agent"], workspace, databases["agent-rpc"], session)
        return [switch, agent, rpc.child], rpc

    def stop(children):
        for child in children:
            owner.stop(child)

    def preserve(before, databases):
        for name, snapshot in before.items():
            after = rows(databases[name])
            for table, existing in snapshot.items():
                OP.require(table in after and existing <= after[table], f"Upgrade/restore changed prior {name}.{table} rows")

    def interrupted(_number, _frame):
        raise InterruptedError("Upgrade owner stop requested")
    previous_handlers = {number: signal.signal(number, interrupted) for number in (signal.SIGTERM, signal.SIGINT)}
    try:
        OP.require(args.previous_source_commit != current_commit, "Previous source must differ from the current source")
        OP.require(all(manifest["artifactSha256"][f"previous-{name}"] != manifest["artifactSha256"][f"current-{name}"] for name in ("switchyard", "agent")), "Supplied previous artifacts are identical to current; refuse false upgrade evidence")
        probe = root / "acceptance.test.js"
        owner.run("bundle-acceptance", [args.bun, "build", "examples/switchyard/acceptance.test.ts", "--target=bun", "--outfile", str(probe)], cwd=OP.REPO)
        old_children, old_rpc = start("previous", paths)
        owner.run("previous-acceptance", [args.bun, "test", str(probe)], {"SWITCHYARD_URL": bases["switchyard"]})
        _, _, headers = OP.json_http(bases["switchyard"], "/api/auth/signup", {"email": "upgrade-sentinel@example.test", "name": "Upgrade sentinel", "password": "Disposable-upgrade-2026!"})
        cookie = headers.get("Set-Cookie").split(";", 1)[0]
        _, project, _ = OP.json_http(bases["switchyard"], "/api/projects", {"name": "prior-release-sentinel"}, cookie)
        _, session, _ = OP.json_http(bases["agent"], "/api/sessions", {"title": "previous release"})
        web_id = session["sessionId"]
        web_before = prompt_web(bases["agent"], web_id, "before upgrade web", 2)
        rpc_before = prompt_rpc(old_rpc, "before upgrade RPC", 2)
        rpc_id = old_rpc.call("get_state")["sessionId"]
        cursor_before = rpc_before[-1]["id"]
        stop(old_children)
        before = {name: rows(path) for name, path in paths.items()}
        result["pinnedPreviousUsed"] = True
        current_children, current_rpc = start("current", paths, rpc_id)
        preserve(before, paths)
        projects = OP.json_http(bases["switchyard"], "/api/projects", cookie=cookie)[1]
        OP.require(any(row["id"] == project["id"] for row in projects), "Upgraded owner lost prior project")
        OP.require(OP.json_http(bases["agent"], f"/api/sessions/{web_id}/messages")[1] == web_before, "Upgrade changed web history")
        OP.require(current_rpc.call("get_entries")["entries"] == rpc_before, "Upgrade changed RPC entry identities/content")
        OP.require(current_rpc.call("get_entries", since=cursor_before)["entries"] == [], "Upgrade replay duplicated prior history")
        owner.run("current-acceptance", [args.bun, "test", str(probe)], {"SWITCHYARD_URL": bases["switchyard"]})
        web_after = prompt_web(bases["agent"], web_id, "after upgrade web", 4)
        rpc_after = prompt_rpc(current_rpc, "after upgrade RPC", 4)
        OP.require(current_rpc.call("get_entries", since=cursor_before)["entries"] == rpc_after[-2:], "Upgraded cursor delta changed")
        # Session metadata/leaf state legitimately changes on new turns; immutable messages stay exact.
        for name, tables in before.items():
            after = rows(paths[name])
            for table, existing in tables.items():
                if table not in ("agent_sessions", "agent_session_state"):
                    OP.require(existing <= after[table], f"Current workflow changed immutable prior {name}.{table}")
        backup = OP.snapshots(root, paths)
        stop(current_children)
        restored_paths = {name: Path(data["path"]) for name, data in backup.items()}
        rollback_children, rollback_rpc = start("previous", restored_paths, rpc_id)
        OP.require(OP.json_http(bases["agent"], f"/api/sessions/{web_id}/messages")[1] == web_after, "Old code could not read current backup web history")
        OP.require(rollback_rpc.call("get_entries")["entries"] == rpc_after, "Old code could not read current backup RPC history")
        OP.require(rollback_rpc.call("get_entries", since=rpc_after[-1]["id"])["entries"] == [], "Old code replayed current backup history twice")
        OP.require(any(row["id"] == project["id"] for row in OP.json_http(bases["switchyard"], "/api/projects", cookie=cookie)[1]), "Old code could not read restored current project")
        stop(rollback_children)
        result.update({"status": "passed", "oldWebMessages": 2, "currentWebMessages": 4, "oldRpcEntries": 2, "currentRpcEntries": 4, "backupOldCodeRead": True, "priorRowCounts": {name: {table: len(value) for table, value in tables.items()} for name, tables in before.items()}, "backupCounts": backup})
    except BaseException as error:
        result["error"] = f"{type(error).__name__}: {error}"
        raise
    finally:
        for number in previous_handlers:
            signal.signal(number, signal.SIG_IGN)
        try:
            owner.cleanup()
            result["ownedProcessesCleaned"] = True
        except Exception as error:
            result.update({"status": "failed", "ownedProcessesCleaned": False, "cleanupError": str(error)})
            raise
        finally:
            for number, handler in previous_handlers.items():
                signal.signal(number, handler)
            result["finishedAt"] = time.time()
            (root / "result.json").write_text(json.dumps(result, indent=2))
            print(json.dumps({"root": str(root), **result}, indent=2))


if __name__ == "__main__":
    main()

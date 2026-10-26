#!/usr/bin/env bun
/**
 * reptar dev:status — list running Reptar dev servers and their ports.
 *
 * Multi-agent repos accumulate orphaned dev servers: `dotnet run app.cs`
 * spawns a muxer whose app child survives the muxer's death, keeping ports
 * hostage and serving stale builds. This command surfaces every running
 * instance so you can kill the right one.
 */

import { spawnSync } from "node:child_process";

const run = (command: string, args: string[]): string => {
  try {
    const result = spawnSync(command, args, { encoding: "utf-8" });
    return result.status === 0 ? result.stdout : "";
  } catch {
    return "";
  }
};

const listeningPorts = (pid: string): number[] => {
  const output = run("lsof", ["-nP", "-iTCP", "-sTCP:LISTEN", "-a", "-p", pid]);
  const ports: number[] = [];
  for (const line of output.split("\n")) {
    const match = /:(\d+)\s+\(LISTEN\)/.exec(line);
    if (match) {
      const port = Number.parseInt(match[1] ?? "", 10);
      if (Number.isFinite(port) && !ports.includes(port)) {
        ports.push(port);
      }
    }
  }
  return ports;
};

export const listDevServers = (): void => {
  // Match: `dotnet run app.cs` muxers, the runfile app children they spawn,
  // and wrangler dev sessions.
  const matches = run("bash", [
    "-c",
    "ps -eo pid,etime,command | grep -E 'dotnet run app\\.cs|runfile/app-|wrangler dev' | grep -v grep || true",
  ]);

  const lines = matches.split("\n").filter(Boolean);
  if (lines.length === 0) {
    console.log("No running Reptar dev servers found.");
    return;
  }

  console.log("Running Reptar dev servers:");
  console.log("──────────────────────────────");

  let servers = 0;
  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed) {
      continue;
    }
    const [pid, elapsed, ...commandParts] = trimmed.split(/\s+/);
    const command = commandParts.join(" ");
    const ports = listeningPorts(pid ?? "");
    const label = command.includes("runfile/app-")
      ? "wasm/app server (runfile)"
      : command.includes("dotnet run app.cs")
        ? "app muxer (dotnet run)"
        : "wrangler dev";

    // Skip the same-port duplicates between muxer and child.
    const portText =
      ports.length > 0
        ? ports.map((p) => `:${p}`).join(", ")
        : "(no listening port)";
    console.log(`  pid ${pid}  up ${elapsed ?? "?"}  ${portText}`);
    console.log(`    ${label}: ${command.slice(0, 110)}`);
    servers += 1;
  }

  if (servers > 2) {
    console.log(
      "\n⚠  Multiple dev servers are running — orphans serve stale builds and hold ports."
    );
    console.log(
      "   Clean up with: kill -9 <pid>   (kill the muxer AND the runfile/app child)"
    );
  }
};

if (import.meta.main) {
  listDevServers();
}

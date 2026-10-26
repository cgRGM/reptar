#!/usr/bin/env bun
/**
 * check-architecture — keep the web core lean.
 *
 * Rules:
 *   1. framework/Reptar must not contain the optional AI SDK or terminal UI.
 *   2. framework/Reptar must not use OS process/terminal APIs.
 *   3. framework/Reptar.csproj must not reference Reptar.AI.
 *   4. framework/Reptar.Wasm must not compile Reptar.AI or terminal code.
 *
 * Usage:
 *   bun scripts/check-architecture.ts            # report, exit 0
 *   bun scripts/check-architecture.ts --strict   # report, exit 1 on findings
 *   bun scripts/check-architecture.ts --json
 */

import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

interface Finding {
  file: string;
  line: number;
  rule: string;
  message: string;
  snippet: string;
}

const root = path.resolve(import.meta.dir, "..");
const coreDir = path.join(root, "framework", "Reptar");
const args = new Set(process.argv.slice(2));
const strict = args.has("--strict");
const json = args.has("--json");

const rules: { id: string; pattern: RegExp; message: string }[] = [
  {
    id: "core/ai-sdk",
    message:
      "The optional AI SDK belongs in framework/Reptar.AI, not the web core.",
    pattern: /\busing\s+Reptar\.AI\b|\bnamespace\s+Reptar\.AI\b/u,
  },
  {
    id: "core/terminal",
    message:
      "Terminal UI belongs in examples/agent-cli (or an optional terminal package), not the web core.",
    pattern:
      /\bTerminalAgent\b|\bTerminalInput\b|\bTerminalEditor\b|\bTerminalScreen\b|\bTerminalUi\b|\bReptarTerm\b|\bTerminalBuffer\b/u,
  },
  {
    id: "core/process",
    message:
      "OS process APIs are not web-core surface; move them to an example/optional package.",
    pattern:
      /System\.Diagnostics\.Process|ProcessStartInfo|Process\.GetCurrentProcess/u,
  },
  {
    id: "core/termios",
    message:
      "Terminal/P/Invoke code belongs in examples/agent-cli, not the web core.",
    pattern: /tcgetattr|cfmakeraw|tcsetattr|DllImport\([^)]*libc/u,
  },
  {
    id: "core/console-input",
    message:
      "Interactive console input belongs in the terminal example, not the web core.",
    pattern: /Console\.ReadKey|Console\.ReadLine/u,
  },
];

const collectCs = (dir: string): string[] => {
  const files: string[] = [];
  for (const entry of readdirSync(dir)) {
    if (entry === "bin" || entry === "obj" || entry === "node_modules") {
      continue;
    }
    const full = path.join(dir, entry);
    const stat = statSync(full);
    if (stat.isDirectory()) {
      files.push(...collectCs(full));
    } else if (entry.endsWith(".cs")) {
      files.push(full);
    }
  }
  return files;
};

const findings: Finding[] = [];

for (const file of collectCs(coreDir)) {
  const lines = readFileSync(file, "utf-8").split("\n");
  for (const [index, line] of lines.entries()) {
    const trimmed = line.trim();
    if (trimmed.startsWith("//") || trimmed.startsWith("///")) {
      continue;
    }
    for (const rule of rules) {
      if (rule.pattern.test(trimmed)) {
        findings.push({
          file: path.relative(root, file),
          line: index + 1,
          message: rule.message,
          rule: rule.id,
          snippet: trimmed.slice(0, 120),
        });
      }
    }
  }
}

const coreProject = path.join(coreDir, "Reptar.csproj");
if (readFileSync(coreProject, "utf-8").includes("Reptar.AI")) {
  findings.push({
    file: path.relative(root, coreProject),
    line: 1,
    message: "The web core must not reference the optional Reptar.AI project.",
    rule: "core/project-reference",
    snippet: "Reptar.AI",
  });
}

const wasmProject = path.join(
  root,
  "framework",
  "Reptar.Wasm",
  "Reptar.Wasm.csproj"
);
const wasmText = readFileSync(wasmProject, "utf-8");
if (wasmText.includes("Reptar.AI")) {
  findings.push({
    file: path.relative(root, wasmProject),
    line: 1,
    message:
      "The Wasm target must not reference the optional Reptar.AI project.",
    rule: "wasm/ai-reference",
    snippet: "Reptar.AI",
  });
}
if (/Compile\s+Include="[^"]*\/AI\//u.test(wasmText)) {
  findings.push({
    file: path.relative(root, wasmProject),
    line: 1,
    message: "The Wasm target must not compile the AI/ folder.",
    rule: "wasm/ai-compile",
    snippet: "Compile Include=.../AI/",
  });
}

if (json) {
  console.log(JSON.stringify({ findings, ok: findings.length === 0 }, null, 2));
} else if (findings.length === 0) {
  console.log("Architecture check: core is clean.");
} else {
  console.log(`Architecture check: ${findings.length} finding(s)\n`);
  for (const finding of findings) {
    console.log(
      `${finding.file}:${finding.line} [${finding.rule}] ${finding.message}\n  ${finding.snippet}`
    );
  }
}

process.exit(strict && findings.length > 0 ? 1 : 0);

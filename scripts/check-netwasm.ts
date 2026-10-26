#!/usr/bin/env bun
/**
 * reptar check:netwasm — static compatibility scanner for the second host.
 *
 * Reptar apps run on two hosts: the native Kestrel server and the
 * NetWasm/Cloudflare Workers build. Some code compiles fine locally and
 * fails (often silently) under NetWasm. This scanner flags the known
 * incompatibilities *before* publish, with the remediation in the message.
 *
 * The scanner understands `#if !NETWASM` / `#if NETWASM` / `#else` /
 * `#endif` regions and only flags code that is live under NetWasm.
 *
 * Usage:
 *   bun reptar check:netwasm                      # scan examples/
 *   bun reptar check:netwasm examples/realtime-rooms
 *   bun reptar check:netwasm --strict examples/foo   # warnings fail too
 */

import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

interface Finding {
  severity: "error" | "warning";
  rule: string;
  file: string;
  line: number;
  snippet: string;
  message: string;
}

interface Rule {
  id: string;
  severity: "error" | "warning";
  pattern: RegExp;
  message: string;
}

// Rules are intentionally regex-based and conservative: every rule is a
// gotcha that cost real debugging time somewhere in the repo's history.
const RULES: Rule[] = [
  {
    id: "netwasm/channels",
    message:
      "System.Threading.Channels is not available under NetWasm. Gate with #if !NETWASM and do the work inline during events, or use Durable Object alarms / Queues.",
    pattern: /using\s+System\.Threading\.Channels|Channel\s*<|Channel\.Create/,
    severity: "error",
  },
  {
    id: "netwasm/process-api",
    message:
      "OS process APIs are absent under NetWasm. Gate with #if !NETWASM and return a null/placeholder on Workers.",
    pattern: /System\.Diagnostics\.Process|Process\.GetCurrentProcess/,
    severity: "error",
  },
  {
    id: "netwasm/filesystem",
    message:
      "There is no filesystem under NetWasm/Workers. Use ctx.Storage (R2) or ctx.Db for persistence; gate local-only IO with #if !NETWASM.",
    pattern:
      /using\s+System\.IO|System\.IO\.File|File\.Open|File\.Read|File\.Write|File\.Delete|File\.Exists|File\.Copy|new\s+FileStream|Directory\.Create|Directory\.Enumerate|Directory\.GetFiles|Directory\.Delete/,
    severity: "error",
  },
  {
    id: "netwasm/console-stdin",
    message:
      "Interactive stdin does not exist under NetWasm. Gate CLI-only code with #if !NETWASM.",
    pattern: /Console\.ReadLine|Console\.ReadKey/,
    severity: "error",
  },
  {
    id: "netwasm/task-run-loop",
    message:
      "Background tasks freeze between events on Workers — a Task.Run drain loop will never be scheduled. Do work inline during the event (and hold it open via wsMessageDone for WebSocket pipelines), or use DO alarms/Queues.",
    pattern: /Task\.Run\s*\(/,
    severity: "warning",
  },
  {
    id: "netwasm/thread-sleep",
    message:
      "Thread.Sleep blocks the single-threaded Worker isolate for every concurrent request. Use await Task.Delay (or Bun.sleep in scripts) instead.",
    pattern: /Thread\.Sleep/,
    severity: "warning",
  },
  {
    id: "netwasm/anonymous-json",
    message:
      "ctx.Json(new { ... }) requires the anonymous type to be serializable under NetWasm — the app's source-generated JsonSerializerContext has no metadata for it and will throw NotSupportedException at runtime. Prefer ctx.JsonRaw(...) with a hand-built string, or a registered record type.",
    pattern: /\.Json\s*\(\s*new\s*\{/,
    severity: "warning",
  },
  {
    id: "netwasm/dictionary-json",
    message:
      "ctx.Json(new Dictionary<string, object?> ...) throws under NetWasm: source generation cannot provide metadata for object? values. Serialize known fields by hand (ctx.JsonRaw) or use a registered record.",
    pattern: /\.Json\s*\(\s*new\s+Dictionary\s*<\s*string\s*,\s*object\s*\??>/,
    severity: "warning",
  },
  {
    id: "netwasm/runtime-reflection",
    message:
      "Runtime reflection is incompatible with Native AOT/NetWasm (and against Reptar's zero-reflection rule). Use source generation, records, or the JsonCodec.",
    pattern:
      /(?:GetType\s*\(\s*\)|typeof\s*\([^)]+\)|Type)\s*\.\s*GetProperty\s*\(|\.GetProperty\s*\([^)]*\)\s*\.GetValue|\.GetMethod\s*\(|Activator\.CreateInstance|\.GetFields\s*\(|\.GetProperties\s*\(|typeof\s*\(\s*\w+\s*\)\s*\.Assembly/,
    severity: "error",
  },
];

const collectCsFiles = (target: string): string[] => {
  const files: string[] = [];
  const walk = (dir: string): void => {
    for (const entry of readdirSync(dir)) {
      if (
        entry === "node_modules" ||
        entry === "bin" ||
        entry === "obj" ||
        entry === ".wrangler" ||
        entry === "public"
      ) {
        continue;
      }
      const full = path.join(dir, entry);
      const stats = statSync(full);
      if (stats.isDirectory()) {
        walk(full);
      } else if (entry.endsWith(".cs")) {
        files.push(full);
      }
    }
  };
  const stats = statSync(target);
  if (stats.isDirectory()) {
    walk(target);
  } else if (target.endsWith(".cs")) {
    files.push(target);
  }
  return files;
};

/**
 * Is this line only live under NETWASM? The two common patterns:
 *   #if NETWASM ... #else/#endif      -> lines before #else are NetWasm-only
 *   #if !NETWASM ... #else ... #endif -> lines after #else are NetWasm-only
 */
const createRegionTracker = () => {
  const stack: { isNotNetwasm: boolean; inElse: boolean }[] = [];
  return {
    /**
     * A line is excluded when it does NOT compile under NETWASM:
     *   - body of `#if !NETWASM` (before #else): local-only
     *   - body of `#else` after `#if NETWASM`: local-only
     */
    isExcluded: (): boolean =>
      stack.some(
        (frame) =>
          (frame.isNotNetwasm && !frame.inElse) ||
          (!frame.isNotNetwasm && frame.inElse)
      ),
    /** Returns true when the line was a preprocessor directive. */
    track: (trimmed: string): boolean => {
      if (trimmed.startsWith("#if")) {
        stack.push({
          inElse: false,
          isNotNetwasm: trimmed.includes("!NETWASM"),
        });
        return true;
      }
      const top = stack.at(-1);
      if (top && trimmed.startsWith("#else")) {
        top.inElse = true;
        return true;
      }
      if (top && trimmed.startsWith("#endif")) {
        stack.pop();
        return true;
      }
      return trimmed.startsWith("#");
    },
  };
};

const scanFile = (file: string): Finding[] => {
  const findings: Finding[] = [];
  const lines = readFileSync(file, "utf-8").split("\n");
  const regions = createRegionTracker();

  for (const [index, line] of lines.entries()) {
    const trimmed = line.trim();
    if (regions.track(trimmed)) {
      continue;
    }
    if (regions.isExcluded()) {
      continue;
    }
    // Strip string literals before matching so JSON payloads containing
    // e.g. "Process" don't false-positive.
    const codeOnly = trimmed.replaceAll(/"(?:[^"\\]|\\.)*"/g, '""');
    for (const rule of RULES) {
      if (rule.pattern.test(codeOnly)) {
        findings.push({
          file,
          line: index + 1,
          message: rule.message,
          rule: rule.id,
          severity: rule.severity,
          snippet: trimmed.slice(0, 100),
        });
      }
    }
  }

  return findings;
};

export const runCheck = (
  targets: string[],
  options: { strict?: boolean } = {}
): { findings: Finding[]; files: number; exitCode: number } => {
  const files = targets.flatMap((target) => collectCsFiles(target));
  const findings = files.flatMap((file) => scanFile(file));
  const errors = findings.filter((f) => f.severity === "error").length;
  const warnings = findings.filter((f) => f.severity === "warning").length;

  for (const finding of findings) {
    const tag =
      finding.severity === "error"
        ? `\x1B[31m${finding.severity}\x1B[0m`
        : `\x1B[33m${finding.severity}\x1B[0m`;
    console.log(
      `${tag} ${finding.rule}  ${path.relative(process.cwd(), finding.file)}:${finding.line}`
    );
    console.log(`    ${finding.snippet}`);
    console.log(`    ${finding.message}`);
  }

  console.log(
    `\nScanned ${files.length} file(s): ${errors} error(s), ${warnings} warning(s).`
  );

  const failOnWarnings = options.strict === true;
  const failed = errors > 0 || (failOnWarnings && warnings > 0);
  return { exitCode: failed ? 1 : 0, files: files.length, findings };
};

// CLI entry (script is executed directly, not imported).
if (import.meta.main) {
  const args = process.argv.slice(2);
  const strict = args.includes("--strict");
  const targets = args.filter((arg) => !arg.startsWith("--"));
  const finalTargets = targets.length > 0 ? targets : ["examples/"];
  const repoRoot = path.resolve(import.meta.dir, "..");
  const absoluteTargets = finalTargets.map((target) =>
    path.isAbsolute(target) ? target : path.join(repoRoot, target)
  );
  const { exitCode } = runCheck(absoluteTargets, { strict });
  process.exit(exitCode);
}

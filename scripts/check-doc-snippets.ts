import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative } from "node:path";

const ROOT = join(dirname(new URL(import.meta.url).pathname), "..");
const DOCS_DIR = join(ROOT, "apps/docs/src/content/docs");

const walkFiles = (dir: string): string[] => {
  const results: string[] = [];
  for (const entry of readdirSync(dir)) {
    const fullPath = join(dir, entry);
    const stat = statSync(fullPath);
    if (stat.isDirectory()) {
      results.push(...walkFiles(fullPath));
    } else if (entry.endsWith(".md") || entry.endsWith(".mdx")) {
      results.push(fullPath);
    }
  }
  return results;
};

interface ForbiddenPattern {
  pattern: RegExp;
  reason: string;
}

const FORBIDDEN_PATTERNS: ForbiddenPattern[] = [
  {
    pattern: /\.Req\.Query\s*\(/,
    reason:
      "ReptarRequest.Query is a string property; use .Req.QueryValue(name, defaultValue) instead.",
  },
  {
    pattern: /namespaceBinding\s*:/,
    reason:
      "IDurableBridge.FetchAsync uses parameter 'namespaceName:', not 'namespaceBinding:'.",
  },
  {
    pattern: /instanceId\s*:/,
    reason:
      "IDurableBridge.FetchAsync uses parameter 'id:', not 'instanceId:'.",
  },
  {
    pattern: /cgRGM\/reptar/i,
    reason: "Use canonical repository 'RocktownLabs/reptar'.",
  },
];

const files = walkFiles(DOCS_DIR);
const errors: string[] = [];
let totalCsharpBlocks = 0;

const csharpFenceRegex = /```csharp([\s\S]*?)```/g;

for (const file of files) {
  const rel = relative(ROOT, file);
  const content = readFileSync(file, "utf-8");

  for (const rule of FORBIDDEN_PATTERNS) {
    if (rule.pattern.test(content)) {
      errors.push(`${rel}: ${rule.reason}`);
    }
  }

  for (const match of content.matchAll(csharpFenceRegex)) {
    totalCsharpBlocks += 1;
    const snippet = match[1] ?? "";
    if (snippet.trim().length === 0) {
      errors.push(`${rel}: Empty \`\`\`csharp code block.`);
    }
  }
}

if (errors.length > 0) {
  console.error("Documentation snippet check failed:");
  for (const err of errors) {
    console.error(`  - ${err}`);
  }
  process.exit(1);
}

console.log(
  `Verified ${totalCsharpBlocks} C# snippets across ${files.length} documentation files.`
);

const pinnedDotnet = join(ROOT, ".reptar", "sdk", "dotnet");
const dotnet = existsSync(pinnedDotnet)
  ? pinnedDotnet
  : process.env.DOTNET_ROOT
    ? join(process.env.DOTNET_ROOT, "dotnet")
    : "dotnet";

const testResult = spawnSync(
  dotnet,
  [
    "test",
    "framework/Reptar.Tests/Reptar.Tests.csproj",
    "--filter",
    "FullyQualifiedName~DocSnippetsTests",
  ],
  {
    cwd: ROOT,
    env: {
      ...process.env,
      DOTNET_CLI_HOME: join(ROOT, ".reptar", "cli-home"),
      ...(existsSync(pinnedDotnet)
        ? { DOTNET_ROOT: join(ROOT, ".reptar", "sdk") }
        : {}),
    },
    stdio: "inherit",
  }
);

if (testResult.status !== 0) {
  process.exit(testResult.status ?? 1);
}

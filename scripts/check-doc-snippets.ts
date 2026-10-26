import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";

const { dirname, join, relative } = path;

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
    pattern: /\.Req\.Query\s*\(/u,
    reason:
      "ReptarRequest.Query is a string property; use .Req.QueryValue(name, defaultValue) instead.",
  },
  {
    pattern: /namespaceBinding\s*:/u,
    reason:
      "IDurableBridge.FetchAsync uses parameter 'namespaceName:', not 'namespaceBinding:'.",
  },
  {
    pattern: /instanceId\s*:/u,
    reason:
      "IDurableBridge.FetchAsync uses parameter 'id:', not 'instanceId:'.",
  },
  {
    pattern: /RocktownLabs\/reptar/iu,
    reason:
      "Use the verified repository 'cgRGM/reptar' (default branch: master).",
  },
];

const files = walkFiles(DOCS_DIR);
const errors: string[] = [];
let totalCsharpBlocks = 0;

const csharpFenceRegex = /```csharp(?<code>[\s\S]*?)```/gu;

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
    const snippet = match.groups?.code ?? "";
    if (snippet.trim().length === 0) {
      errors.push(`${rel}: Empty \`\`\`csharp code block.`);
    }
  }
}

// Workspace tools drift: every AI tool mounted by `app.UseWorkspaceTools` in
// framework/Reptar.AI/WorkspaceTools.cs must be documented in the AI agent
// tools cookbook page, so the docs can't fall behind the framework's toolset.
const workspaceToolsSource = readFileSync(
  join(ROOT, "framework/Reptar.AI/WorkspaceTools.cs"),
  "utf-8"
);
const agentToolsDocPath = join(DOCS_DIR, "cookbook/ai-agent-tools.mdx");
const agentToolsDoc = existsSync(agentToolsDocPath)
  ? readFileSync(agentToolsDocPath, "utf-8")
  : "";
const registeredToolPattern = /\.AsAiTool\("(?<tool>[a-z_]+)"/gu;
for (const match of workspaceToolsSource.matchAll(registeredToolPattern)) {
  const tool = match.groups?.tool ?? "";
  if (!agentToolsDoc.includes(tool)) {
    errors.push(
      `cookbook/ai-agent-tools.mdx: AI tool '${tool}' from WorkspaceTools.cs is not documented`
    );
  }
}

// Agent surface sync: `bun reptar <command>` references inside the npm-shipped
// agent references (root llms.txt, packages/rpc/llms.txt, and the SKILL.md
// packaged with @reptar/client) must match commands the CLI actually
// implements, so shipped agent guidance can't drift behind the CLI.
const CLI_SOURCE_PATH = join(ROOT, "scripts/reptar.ts");
const AGENT_SURFACE_PATHS = [
  join(ROOT, "llms.txt"),
  join(ROOT, "packages/rpc/llms.txt"),
  join(ROOT, "packages/rpc/skills/reptar/SKILL.md"),
];

const cliSource = readFileSync(CLI_SOURCE_PATH, "utf-8");
const implementedCommands = new Set<string>();
for (const match of cliSource.matchAll(
  /(?:case|command ===) "(?<command>[a-z][a-z0-9:-]*)"/gu
)) {
  const cliCommand = match.groups?.command ?? "";
  if (cliCommand) {
    implementedCommands.add(cliCommand);
  }
}

const referencedCommandPattern = /bun reptar (?<command>[a-z][a-z0-9:-]*)/gu;
for (const surfacePath of AGENT_SURFACE_PATHS) {
  if (!existsSync(surfacePath)) {
    continue;
  }
  const surface = readFileSync(surfacePath, "utf-8");
  const surfaceRel = relative(ROOT, surfacePath);
  for (const match of surface.matchAll(referencedCommandPattern)) {
    const referencedCommand = match.groups?.command ?? "";
    if (!implementedCommands.has(referencedCommand)) {
      errors.push(
        `${surfaceRel}: references 'bun reptar ${referencedCommand}' but the CLI has no '${referencedCommand}' command`
      );
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
let dotnet = "dotnet";
if (existsSync(pinnedDotnet)) {
  dotnet = pinnedDotnet;
} else if (process.env.DOTNET_ROOT) {
  dotnet = join(process.env.DOTNET_ROOT, "dotnet");
}

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

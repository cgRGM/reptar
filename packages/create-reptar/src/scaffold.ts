import { randomBytes } from "node:crypto";
import {
  cpSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  statSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

export const REPTAR_AGENTS_START = "<!-- reptar:agents:start -->";
export const REPTAR_AGENTS_END = "<!-- reptar:agents:end -->";

export const REPTAR_AGENTS_BLOCK = `${REPTAR_AGENTS_START}
## Reptar Framework Context
- Guidelines & C# routing/query patterns: \`node_modules/@reptar/client/AGENTS.md\`
- Agent skill instructions: \`node_modules/@reptar/client/skills/reptar/SKILL.md\`
- Inspect active schema, tables, and routes: \`bun reptar inspect --json\` or MCP server (\`bun reptar mcp\`)
- When modifying C# endpoints or schemas: run \`bun reptar codegen\` and \`bun reptar db plan\`
${REPTAR_AGENTS_END}`;

export const upsertReptarAgentsContext = (projectDir: string): void => {
  const agentsPath = path.join(projectDir, "AGENTS.md");
  if (existsSync(agentsPath)) {
    const existing = readFileSync(agentsPath, "utf-8");
    const startIdx = existing.indexOf(REPTAR_AGENTS_START);
    const endIdx = existing.indexOf(REPTAR_AGENTS_END);

    if (startIdx !== -1 && endIdx !== -1 && endIdx > startIdx) {
      const updated =
        existing.slice(0, startIdx) +
        REPTAR_AGENTS_BLOCK +
        existing.slice(endIdx + REPTAR_AGENTS_END.length);
      writeFileSync(agentsPath, updated, "utf-8");
    } else {
      const separator = existing.endsWith("\n") ? "\n" : "\n\n";
      writeFileSync(
        agentsPath,
        `${existing}${separator}${REPTAR_AGENTS_BLOCK}\n`,
        "utf-8"
      );
    }
  } else {
    writeFileSync(agentsPath, `${REPTAR_AGENTS_BLOCK}\n`, "utf-8");
  }

  const agentsSkillsDir = path.join(projectDir, ".agents/skills");
  if (existsSync(agentsSkillsDir)) {
    const symlinkPath = path.join(agentsSkillsDir, "reptar");
    const targetRel = "../../node_modules/@reptar/client/skills/reptar";
    try {
      try {
        const st = lstatSync(symlinkPath);
        if (st.isSymbolicLink()) {
          unlinkSync(symlinkPath);
        }
      } catch {
        // Not a symlink (or already gone) — proceed to create it.
      }
      if (!existsSync(symlinkPath)) {
        symlinkSync(targetRel, symlinkPath, "dir");
      }
    } catch {
      // Symlinking the skill directory is best-effort only.
    }
  }
};

const normalizePath = (value: string) => value.split(path.sep).join("/");

export interface ScaffoldOptions {
  projectName: string;
  targetDir: string;
  database: "d1" | "sqlite" | "postgres" | "mysql";
  auth: "better-auth" | "none";
  template?: "fullstack" | "minimal-api" | "agent";
  /**
   * Absolute path to the in-repo framework sources. When set, the scaffolded
   * app builds file-based (`#:project` references, no csproj) against the
   * cloned framework — the same proven mechanism examples/blog uses. This is
   * the default when scaffolding from a repo clone; standalone NuGet-based
   * scaffolds arrive with the 0.1 package release.
   */
  frameworkDir?: string;
}

const availableTemplates = (): string[] => {
  const currentDir = path.dirname(new URL(import.meta.url).pathname);
  const roots = [
    path.resolve(currentDir, "../../../templates"),
    path.resolve(currentDir, "../../templates"),
    path.resolve(currentDir, "../templates"),
    path.resolve(process.cwd(), "templates"),
  ];
  for (const root of roots) {
    if (existsSync(root)) {
      return readdirSync(root, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name)
        .toSorted();
    }
  }
  return [];
};

const findTemplateDir = (template = "fullstack"): string => {
  const currentDir = path.dirname(new URL(import.meta.url).pathname);
  const candidates = [
    path.resolve(currentDir, "../../../templates", template),
    path.resolve(currentDir, "../../templates", template),
    path.resolve(currentDir, "../templates", template),
    path.resolve(process.cwd(), "templates", template),
  ];

  for (const candidate of candidates) {
    if (existsSync(candidate)) {
      return candidate;
    }
  }

  const available = availableTemplates();
  throw new Error(
    `Could not locate template '${template}'. Available template(s): ${available.length > 0 ? available.join(", ") : "none"}.`
  );
};

const formatTitle = (name: string): string =>
  name
    .split(/[-_]/u)
    .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
    .join(" ");

const textExtensions = new Set([
  ".json",
  ".jsonc",
  ".ts",
  ".tsx",
  ".js",
  ".mjs",
  ".cs",
  ".csproj",
  ".toml",
  ".md",
  ".css",
  ".html",
  ".example",
  ".gitignore",
  ".yml",
  ".yaml",
  ".sh",
  ".props",
  ".xml",
]);

const copyAndTransform = (
  src: string,
  dest: string,
  replacements: Record<string, string>
): void => {
  const stat = statSync(src);

  if (stat.isDirectory()) {
    mkdirSync(dest, { recursive: true });
    const entries = readdirSync(src);
    for (const entry of entries) {
      if (
        entry === "node_modules" ||
        entry === ".turbo" ||
        entry === "dist" ||
        entry === "bin" ||
        entry === "obj"
      ) {
        continue;
      }
      copyAndTransform(
        path.join(src, entry),
        path.join(dest, entry),
        replacements
      );
    }
    return;
  }

  const ext = path.extname(src);
  const isText = textExtensions.has(ext) || path.basename(src).startsWith(".");

  if (isText) {
    let content = readFileSync(src, "utf-8");
    for (const [key, value] of Object.entries(replacements)) {
      content = content.replaceAll(key, value);
    }
    writeFileSync(dest, content, "utf-8");
  } else {
    cpSync(src, dest);
  }
};

/**
 * Copies @reptar/cli-core into the scaffold as a workspace package so
 * make:model / make:feature / json:gen work without a framework clone or a
 * registry install. create-reptar resolves the source from its sibling
 * package in the monorepo, or from its own dependency when published.
 */
const copyCliCore = (targetDir: string): void => {
  const currentDir = path.dirname(new URL(import.meta.url).pathname);
  const candidates = [
    path.resolve(currentDir, "../../cli"),
    path.resolve(currentDir, "../../../packages/cli"),
    path.resolve(process.cwd(), "packages/cli"),
  ];

  try {
    const resolved = new URL(import.meta.resolve("@reptar/cli-core")).pathname;
    candidates.push(path.dirname(path.dirname(resolved)));
  } catch {
    // Not installed as a package; the sibling paths cover clone scaffolds.
  }

  const source = candidates.find((candidate) =>
    existsSync(path.join(candidate, "package.json"))
  );

  if (!source) {
    throw new Error(
      "Could not locate @reptar/cli-core. Reinstall create-reptar or run from a framework clone."
    );
  }

  const destination = path.join(targetDir, "packages", "cli-core");
  mkdirSync(destination, { recursive: true });
  cpSync(source, destination, {
    filter: (entry) => {
      const name = path.basename(entry);
      return name !== "node_modules" && name !== "bin" && name !== "obj";
    },
    recursive: true,
  });
};

/**
 * Rewrites the scaffolded app for file-based builds against the in-repo
 * framework: `#:project` directives instead of a csproj, plus a
 * Directory.Build.props that attaches the Reptar source generators so
 * `Schema.<Table>Cols` ColumnRef tokens work out of the box.
 */
const wireUpFileBasedApp = (
  options: ScaffoldOptions,
  _replacements: Record<string, string>
): void => {
  const { targetDir } = options;
  const { frameworkDir } = options;

  if (!frameworkDir) {
    return;
  }

  const apiDir = path.join(targetDir, "apps/api");
  const appCs = path.join(apiDir, "app.cs");

  if (!existsSync(appCs)) {
    return;
  }

  const frameworkFromApi = path.relative(apiDir, frameworkDir);
  const frameworkFromRoot = path.relative(targetDir, frameworkDir);
  const normalize = normalizePath;

  // Prepend #:project directives — the same mechanism examples/blog uses.
  const original = readFileSync(appCs, "utf-8");
  const projectDirectives =
    `#:project ${normalize(frameworkFromApi)}/Reptar/Reptar.csproj\n` +
    `#:project ${normalize(frameworkFromApi)}/Reptar.Kestrel/Reptar.Kestrel.csproj\n`;

  writeFileSync(appCs, projectDirectives + original, "utf-8");

  // File-based apps do not need the csproj host — remove it so `dotnet run
  // app.cs` / `dotnet publish app.cs` are the only build entries.
  const csproj = path.join(apiDir, "ReptarApp.csproj");
  if (existsSync(csproj)) {
    unlinkSync(csproj);
  }

  // Attach the Roslyn source generators (ColumnRef tokens, ReptarEntity
  // mappers) to every project under the scaffold root. The conditional keeps
  // it off the generator project itself when the framework is vendored.
  const buildProps = `<!--
  Wires the Reptar source generators (Schema.<Table>Cols ColumnRef tokens,
  [ReptarEntity] mappers) into every project under this scaffold, the same
  way the framework monorepo does for its own apps.
-->
<Project>
  <ItemGroup Condition="'$(MSBuildProjectName)' != 'Reptar.SourceGenerators'">
    <ProjectReference
      Include="$(MSBuildThisFileDirectory)${normalize(frameworkFromRoot)}/Reptar.SourceGenerators/Reptar.SourceGenerators.csproj"
      OutputItemType="Analyzer"
      ReferenceOutputAssembly="false" />
  </ItemGroup>
</Project>
`;
  writeFileSync(
    path.join(targetDir, "Directory.Build.props"),
    buildProps,
    "utf-8"
  );
};

export const scaffoldProject = (options: ScaffoldOptions): void => {
  const templateDir = findTemplateDir(options.template ?? "fullstack");
  const projectTitle = formatTitle(options.projectName);

  mkdirSync(options.targetDir, { recursive: true });

  const projectPascal = options.projectName
    .split(/[-_\s]+/u)
    .filter(Boolean)
    .map((word: string) => word.charAt(0).toUpperCase() + word.slice(1))
    .join("");

  const replacements: Record<string, string> = {
    // Templates occasionally use the spaced mustache form in JSX text.
    "{{ PROJECT_NAME }}": options.projectName,
    "{{ PROJECT_PASCAL }}": projectPascal || "MyApp",
    "{{ PROJECT_TITLE }}": projectTitle,
    "{{AUTH_PROVIDER}}": options.auth,
    "{{DATABASE_DIALECT}}": options.database,
    "{{PROJECT_NAME}}": options.projectName,
    "{{PROJECT_PASCAL}}": projectPascal || "MyApp",
    "{{PROJECT_TITLE}}": projectTitle,
  };

  copyAndTransform(templateDir, options.targetDir, replacements);
  copyCliCore(options.targetDir);

  if (options.frameworkDir) {
    wireUpFileBasedApp(options, replacements);
  }

  // Initialize apps/api/.env with a generated Better Auth secret
  const apiEnvExample = path.join(options.targetDir, "apps/api/.env.example");
  const apiEnv = path.join(options.targetDir, "apps/api/.env");
  if (existsSync(apiEnvExample)) {
    let envContent = readFileSync(apiEnvExample, "utf-8");
    const secret = randomBytes(32).toString("base64");
    envContent = envContent.replace(
      "BETTER_AUTH_SECRET=your_32_character_random_secret_here",
      `BETTER_AUTH_SECRET=${secret}`
    );
    writeFileSync(apiEnv, envContent, "utf-8");
  }

  upsertReptarAgentsContext(options.targetDir);
};

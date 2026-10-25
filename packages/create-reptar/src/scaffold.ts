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
      } catch {}
      if (!existsSync(symlinkPath)) {
        symlinkSync(targetRel, symlinkPath, "dir");
      }
    } catch {}
  }
};

export interface ScaffoldOptions {
  projectName: string;
  targetDir: string;
  database: "d1" | "sqlite" | "postgres" | "mysql";
  auth: "better-auth" | "none";
}

const findTemplateDir = (): string => {
  const currentDir = path.dirname(new URL(import.meta.url).pathname);
  const candidates = [
    path.resolve(currentDir, "../../../templates/fullstack"),
    path.resolve(currentDir, "../../templates/fullstack"),
    path.resolve(currentDir, "../templates/fullstack"),
    path.resolve(process.cwd(), "templates/fullstack"),
  ];

  for (const candidate of candidates) {
    if (existsSync(candidate)) {
      return candidate;
    }
  }

  throw new Error(
    "Could not locate templates/fullstack directory. Please check your Reptar installation."
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

export const scaffoldProject = (options: ScaffoldOptions): void => {
  const templateDir = findTemplateDir();
  const projectTitle = formatTitle(options.projectName);

  mkdirSync(options.targetDir, { recursive: true });

  const replacements: Record<string, string> = {
    "{{AUTH_PROVIDER}}": options.auth,
    "{{DATABASE_DIALECT}}": options.database,
    "{{PROJECT_NAME}}": options.projectName,
    "{{PROJECT_TITLE}}": projectTitle,
  };

  copyAndTransform(templateDir, options.targetDir, replacements);

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

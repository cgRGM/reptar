import { randomBytes } from "node:crypto";
import {
  cpSync,
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";

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
};

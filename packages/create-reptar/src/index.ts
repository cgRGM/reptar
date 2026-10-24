import { existsSync, readdirSync } from "node:fs";
import path from "node:path";
import * as readline from "node:readline/promises";

import { scaffoldProject } from "./scaffold";
import type { ScaffoldOptions } from "./scaffold";

type DbChoice = "d1" | "sqlite" | "postgres" | "mysql";
type AuthChoice = "better-auth" | "none";

const printHelp = (): void => {
  console.log(`
create-reptar [project-name] [options]

Options:
  -y, --yes          Scaffold with sensible defaults non-interactively
  --db <type>        Database engine: d1 (default), sqlite, postgres, mysql
  --auth <type>      Auth provider: better-auth (default), none
  -h, --help         Show this help message
`);
};

const askQuestion = async (
  rl: readline.Interface,
  query: string,
  defaultValue: string
): Promise<string> => {
  const answer = await rl.question(`${query} (${defaultValue}): `);
  return answer.trim() || defaultValue;
};

const parseFlags = (
  args: string[]
): {
  database: DbChoice;
  auth: AuthChoice;
} => {
  let database: DbChoice = "d1";
  let auth: AuthChoice = "better-auth";

  const dbFlagIndex = args.indexOf("--db");
  if (dbFlagIndex !== -1 && args[dbFlagIndex + 1]) {
    const val = args[dbFlagIndex + 1]?.toLowerCase();
    if (
      val === "d1" ||
      val === "sqlite" ||
      val === "postgres" ||
      val === "mysql"
    ) {
      database = val;
    }
  }

  const authFlagIndex = args.indexOf("--auth");
  if (authFlagIndex !== -1 && args[authFlagIndex + 1]) {
    const val = args[authFlagIndex + 1]?.toLowerCase();
    if (val === "better-auth" || val === "none") {
      auth = val;
    }
  }

  return { auth, database };
};

const promptDatabase = async (rl: readline.Interface): Promise<DbChoice> => {
  console.log("\nDatabase Engine:");
  console.log("  1) Cloudflare D1 (Recommended for edge)");
  console.log("  2) SQLite (Local WAL)");
  console.log("  3) PostgreSQL (Neon / Supabase / Local)");
  console.log("  4) MySQL / MariaDB (PlanetScale / RDS)");
  const choice = await askQuestion(rl, "Select database (1-4)", "1");
  if (choice === "2") {
    return "sqlite";
  }
  if (choice === "3") {
    return "postgres";
  }
  if (choice === "4") {
    return "mysql";
  }
  return "d1";
};

const promptAuth = async (rl: readline.Interface): Promise<AuthChoice> => {
  console.log("\nAuthentication:");
  console.log(
    "  1) Better Auth (Credentials + Google & GitHub OAuth) [Recommended]"
  );
  console.log("  2) None");
  const choice = await askQuestion(rl, "Select authentication (1-2)", "1");
  return choice === "2" ? "none" : "better-auth";
};

const runInteractive = async (
  initialName: string
): Promise<{
  projectName: string;
  database: DbChoice;
  auth: AuthChoice;
}> => {
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
  });

  try {
    console.log(`\n🦖 Welcome to Reptar fullstack project scaffolding!\n`);
    const projectName = await askQuestion(
      rl,
      "Project name",
      initialName || "my-app"
    );
    const database = await promptDatabase(rl);
    const auth = await promptAuth(rl);
    return { auth, database, projectName };
  } finally {
    rl.close();
  }
};

const run = async (): Promise<void> => {
  const args = process.argv.slice(2);

  if (args.includes("-h") || args.includes("--help")) {
    printHelp();
    return;
  }

  const isYes = args.includes("-y") || args.includes("--yes");
  const initialName = args.find((arg) => !arg.startsWith("-")) ?? "";
  const parsed = parseFlags(args);

  const config =
    process.stdin.isTTY && !isYes
      ? await runInteractive(initialName)
      : {
          auth: parsed.auth,
          database: parsed.database,
          projectName: initialName || "my-app",
        };

  const targetDir = path.resolve(process.cwd(), config.projectName);

  if (existsSync(targetDir) && readdirSync(targetDir).length > 0) {
    console.error(
      `\n❌ Error: Target directory '${config.projectName}' is not empty.\n`
    );
    process.exit(1);
  }

  console.log(`\n🚀 Scaffolding ${config.projectName} with Reptar...`);
  console.log(`   • Database: ${config.database}`);
  console.log(`   • Authentication: ${config.auth}`);
  console.log(`   • Frontend: React 19 + Tailwind CSS v4 + shadcn/ui`);
  console.log(`   • Backend: C# Native AOT / Cloudflare Workers`);

  const options: ScaffoldOptions = {
    auth: config.auth,
    database: config.database,
    projectName: config.projectName,
    targetDir,
  };

  scaffoldProject(options);

  console.log(`
🎉 Successfully scaffolded ${config.projectName}!

Next steps:
  cd ${config.projectName}
  bun install
  bun run dev

Endpoints:
  • Frontend Web:     http://localhost:5173
  • Reptar C# API:    http://localhost:8787
  • Better Auth API:  http://localhost:8787/api/auth

Happy coding with Reptar! 🦖
`);
};

try {
  await run();
} catch (error: unknown) {
  console.error("Fatal error:", error);
  process.exit(1);
}

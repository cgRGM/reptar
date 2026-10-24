#!/usr/bin/env bun

import { spawnSync } from "node:child_process";
import path from "node:path";

const root = path.resolve(import.meta.dir, "..");
const [command, ...rest] = process.argv.slice(2);

const runDotnet = (args: string[], cwd = root) => {
  const result = spawnSync("dotnet", args, {
    cwd,
    env: process.env,
    stdio: "inherit",
  });
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
};

switch (command) {
  case "dev": {
    runDotnet(["run", "app.cs", ...rest], path.join(root, "apps/api"));
    break;
  }
  case "codegen": {
    const outDir = path.join(root, "packages/client/src");
    runDotnet(
      ["run", "app.cs", "--", "--codegen", "--out", outDir, ...rest],
      path.join(root, "apps/api")
    );
    break;
  }
  case "build": {
    runDotnet(
      ["build", "ReptarApp.csproj", ...rest],
      path.join(root, "apps/api")
    );
    break;
  }
  default: {
    console.error(
      `Unknown command: ${command ?? "(none)"}. Commands: dev, codegen, build`
    );
    process.exit(1);
  }
}

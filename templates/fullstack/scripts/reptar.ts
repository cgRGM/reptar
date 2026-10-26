#!/usr/bin/env bun
// Scaffold runner: dev / codegen / build for the C# API.
//
// Resolves the .NET SDK the same way the framework repo does: reuse the
// repo's portable SDK (.reptar/sdk) when scaffolding from a clone, else fall
// back to the system dotnet, else install the required SDK locally into
// .reptar/sdk. The SDK is a build tool only — never shipped in the container.

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync, unlinkSync, writeFileSync } from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dir, "..");
const requiredSdk = "10.0.401";
const [command, ...rest] = process.argv.slice(2);

const versionAtLeast = (actual: string, required: string): boolean => {
  const left = actual.split(".").map(Number);
  const right = required.split(".").map(Number);
  for (let i = 0; i < Math.max(left.length, right.length); i += 1) {
    const a = left[i] ?? 0;
    const b = right[i] ?? 0;
    if (a !== b) {
      return a > b;
    }
  }
  return true;
};

const readSdkVersion = (dotnet: string): string | null => {
  const result = spawnSync(dotnet, ["--version"], { encoding: "utf-8" });
  return result.status === 0 ? result.stdout.trim() : null;
};

const findRepoSdk = (): string | null => {
  // Walk up from this scaffold looking for a framework clone's portable SDK.
  let dir = root;
  for (let i = 0; i < 6; i += 1) {
    const candidate = path.join(dir, ".reptar", "sdk", "dotnet");
    if (existsSync(candidate)) {
      return candidate;
    }
    const parent = path.dirname(dir);
    if (parent === dir) {
      break;
    }
    dir = parent;
  }
  return null;
};

const findFrameworkCli = (): string | null => {
  // A scaffold created from a framework clone can reuse the repo CLI for
  // make:model / make:feature / json:gen. Standalone package scaffolds will
  // get these commands from @reptar/cli-core once packaging lands.
  let dir = root;
  for (let i = 0; i < 8; i += 1) {
    const candidate = path.join(dir, "scripts", "reptar.ts");
    if (
      existsSync(candidate) &&
      existsSync(path.join(dir, "framework", "Reptar", "Reptar.csproj"))
    ) {
      return candidate;
    }
    const parent = path.dirname(dir);
    if (parent === dir) {
      break;
    }
    dir = parent;
  }
  return null;
};

const ensureSdk = (): string => {
  const repoDotnet = findRepoSdk();
  if (repoDotnet) {
    const version = readSdkVersion(repoDotnet);
    if (version && versionAtLeast(version, requiredSdk)) {
      return repoDotnet;
    }
  }

  const systemVersion = readSdkVersion("dotnet");
  if (systemVersion && versionAtLeast(systemVersion, requiredSdk)) {
    return "dotnet";
  }

  console.log(
    `Reptar needs the .NET ${requiredSdk} SDK as a build tool. Installing into .reptar/sdk...`
  );
  const sdkDir = path.join(root, ".reptar", "sdk");
  const installer = path.join(root, ".reptar", "dotnet-install.sh");
  spawnSync(
    "curl",
    ["-sSL", "https://dot.net/v1/dotnet-install.sh", "-o", installer],
    { stdio: "inherit" }
  );
  spawnSync(
    "bash",
    [installer, "--version", requiredSdk, "--install-dir", sdkDir, "--no-path"],
    { stdio: "inherit" }
  );
  return path.join(sdkDir, "dotnet");
};

const dotnet = ensureSdk();

// The pinned SDK lives under a framework clone's .reptar/sdk. Export
// DOTNET_ROOT for the child process so nested `dotnet` invocations (and
// MSBuild tasks that shell out) resolve the same toolchain instead of the
// older SDK on PATH.
const dotnetRoot = path.isAbsolute(dotnet) ? path.dirname(dotnet) : null;
const dotnetEnv = dotnetRoot
  ? { ...process.env, DOTNET_ROOT: dotnetRoot }
  : process.env;

const runDotnet = (args: string[], cwd = root) => {
  const result = spawnSync(dotnet, args, {
    cwd,
    env: dotnetEnv,
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

    // The generator writes `app-type.d.ts`, but the scaffold ships a
    // hand-written `app-type.ts` placeholder. TypeScript resolves the `.ts`
    // first, so overwrite it with the generated contract and drop the `.d.ts`
    // to avoid a stale shadow copy. Also point the framework package import
    // at the local client runtime.
    const generatedAppType = path.join(outDir, "app-type.d.ts");
    if (existsSync(generatedAppType)) {
      const patched = readFileSync(generatedAppType, "utf-8").replaceAll(
        'from "@reptar/client"',
        'from "./client"'
      );
      writeFileSync(path.join(outDir, "app-type.ts"), patched, "utf-8");
      unlinkSync(generatedAppType);
    }
    break;
  }
  case "build": {
    const apiDir = path.join(root, "apps/api");
    const csproj = path.join(apiDir, "ReptarApp.csproj");
    const target = existsSync(csproj) ? "ReptarApp.csproj" : "app.cs";
    runDotnet(["build", target, ...rest], apiDir);
    break;
  }
  case "make:model":
  case "make:feature":
  case "json:gen": {
    const cli = findFrameworkCli();
    if (!cli) {
      console.error(
        "This command requires the Reptar framework repo CLI. " +
          "Run it from a clone, or wait for the packaged @reptar/cli-core."
      );
      process.exit(1);
    }

    const apiDir = path.join(root, "apps/api");
    const args =
      command === "json:gen"
        ? [command, "--app", apiDir, ...rest]
        : [command, ...rest, "--app", apiDir];
    const result = spawnSync(process.execPath, [cli, ...args], {
      cwd: root,
      env: process.env,
      stdio: "inherit",
    });
    process.exit(result.status ?? 1);
    break;
  }
  default: {
    console.error(
      `Unknown command: ${command ?? "(none)"}. Commands: dev, codegen, build, make:model, make:feature, json:gen`
    );
    process.exit(1);
  }
}

#!/usr/bin/env bun

import { spawnSync } from "node:child_process";
import { cpSync, mkdirSync, rmSync } from "node:fs";
import path from "node:path";

import { patchNetWasmHost } from "../packages/host/src/patch";

const repoRoot = path.resolve(import.meta.dir, "..");
const requiredSdk = "10.0.401";
const toolchainDir = path.join(repoRoot, ".reptar");
const sdkDir = path.join(toolchainDir, "sdk");
const cliHome = path.join(toolchainDir, "cli-home");

mkdirSync(cliHome, { recursive: true });

const versionAtLeast = (actual: string, required: string): boolean => {
  const left = actual.split(".").map(Number);
  const right = required.split(".").map(Number);
  const length = Math.max(left.length, right.length);

  for (let index = 0; index < length; index += 1) {
    const a = left[index] ?? 0;
    const b = right[index] ?? 0;
    if (a !== b) {
      return a > b;
    }
  }

  return true;
};

const dotnetPath = (): string =>
  process.env.DOTNET_ROOT
    ? path.join(process.env.DOTNET_ROOT, "dotnet")
    : "dotnet";

const dotnetEnv = (): NodeJS.ProcessEnv => ({
  ...process.env,
  DOTNET_CLI_HOME: cliHome,
  DOTNET_NOLOGO: "1",
});

const readSdkVersion = (): string | null => {
  const result = spawnSync(dotnetPath(), ["--version"], {
    encoding: "utf-8",
    env: dotnetEnv(),
  });

  return result.status === 0 ? result.stdout.trim() : null;
};

const installSdk = (): void => {
  console.log(
    `Reptar needs the .NET ${requiredSdk} SDK as a build tool (never shipped). Installing into .reptar/sdk...`
  );

  mkdirSync(sdkDir, { recursive: true });
  const installer = path.join(toolchainDir, "dotnet-install.sh");

  const download = spawnSync(
    "curl",
    ["-sSL", "https://dot.net/v1/dotnet-install.sh", "-o", installer],
    { stdio: "inherit" }
  );

  if (download.status !== 0) {
    process.exit(download.status ?? 1);
  }

  const install = spawnSync(
    "bash",
    [installer, "--version", requiredSdk, "--install-dir", sdkDir, "--no-path"],
    { stdio: "inherit" }
  );

  if (install.status !== 0) {
    process.exit(install.status ?? 1);
  }

  process.env.DOTNET_ROOT = sdkDir;
  process.env.PATH = `${sdkDir}:${process.env.PATH ?? ""}`;
};

const ensureSdk = (): void => {
  const current = readSdkVersion();

  if (current && versionAtLeast(current, requiredSdk)) {
    return;
  }
  if (process.env.REPTAR_SKIP_SDK_CHECK === "1") {
    return;
  }

  installSdk();
};

const run = (args: string[], cwd = repoRoot): void => {
  const result = spawnSync(dotnetPath(), args, {
    cwd,
    env: dotnetEnv(),
    stdio: "inherit",
  });

  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
};

const formatGenerated = (): void => {
  const result = spawnSync(
    process.execPath,
    ["x", "oxfmt", "--write", "examples/blog/rpc"],
    {
      cwd: repoRoot,
      env: dotnetEnv(),
      stdio: "inherit",
    }
  );

  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
};

const [command, ...rest] = process.argv.slice(2);

if (command === "--help" || command === "-h" || command === "help") {
  console.log(`reptar <command>

Commands:
  dev          Run the example blog API on http://localhost:8787
  codegen      Generate packages/rpc contracts from the C# route tree
  test         Run the C# framework tests
  build        Build the framework core
  build:worker Publish the C# Wasm app and patch the NetWasm host for Workers

The .NET SDK is only a build tool. It is installed under .reptar/ when missing.
`);
  process.exit(0);
}

ensureSdk();

switch (command) {
  case "dev": {
    run(["run", "app.cs", ...rest], path.join(repoRoot, "examples/blog"));
    break;
  }

  case "codegen": {
    run([
      "run",
      "examples/blog/app.cs",
      "--",
      "--codegen",
      "--out",
      "examples/blog/rpc",
      ...rest,
    ]);
    formatGenerated();
    break;
  }

  case "test": {
    run(["test", "framework/Reptar.Tests/Reptar.Tests.csproj", ...rest]);
    break;
  }

  case "build": {
    run(["build", "framework/Reptar/Reptar.csproj", ...rest]);
    break;
  }

  case "build:worker": {
    const workerRoot = path.join(repoRoot, "spikes/netwasm-worker");
    const csharpRoot = path.join(workerRoot, "csharp");
    const publishRoot = path.join(
      csharpRoot,
      "bin/Release/netwasm0.1/publish/browser"
    );
    const browserRoot = path.join(workerRoot, "public/browser");

    run(["publish", "-c", "Release"], csharpRoot);
    rmSync(browserRoot, { force: true, recursive: true });
    cpSync(publishRoot, browserRoot, { recursive: true });

    const patched = patchNetWasmHost(browserRoot);
    console.log(`Patched ${patched} NetWasm host replacement(s).`);
    break;
  }

  default: {
    console.error(
      `Unknown command: ${command ?? "(none)"}. Try: bun reptar --help`
    );
    process.exit(1);
  }
}

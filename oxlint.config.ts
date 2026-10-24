import { defineConfig } from "oxlint";
import astro from "ultracite/oxlint/astro";
import core from "ultracite/oxlint/core";

export default defineConfig({
  extends: [core, astro],
  ignorePatterns: [
    ...core.ignorePatterns,
    "examples/blog/rpc/app-type.d.ts",
    "examples/blog/rpc/types.ts",
    "examples/blog/rpc/openapi.json",
    "spikes/**/public/**",
    "spikes/**/.wrangler/**",
    "packages/rpc/openapi.json",
    "packages/rpc/src/types.ts",
    "packages/rpc/src/app-type.ts",
    ".agents/**",
  ],
});

import { defineConfig } from "oxfmt";
import ultracite from "ultracite/oxfmt";

export default defineConfig({
  ...ultracite,
  ignorePatterns: [
    ...(ultracite.ignorePatterns ?? []),
    "spikes/**/public/**",
    "**/.wrangler/**",
    "examples/**/worker/public/**",
    ".agents/**",
    "templates/**",
  ],
});

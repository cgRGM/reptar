import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";

const replacements: readonly (readonly [string, string])[] = [
  [
    "manifestUrl:new URL(`./deployment.json`,import.meta.url).href",
    "manifestUrl:globalThis.__REPTAR_MANIFEST_URL",
  ],
  [
    'manifestUrl:new URL("/browser/deployment.json",globalThis.location.origin).href',
    "manifestUrl:globalThis.__REPTAR_MANIFEST_URL",
  ],
  [
    "compileCoreModule:WebAssembly.compile.bind(WebAssembly)",
    "compileCoreModule:()=>globalThis.__REPTAR_WASM_MODULE",
  ],
  [
    "createModuleUrl:e=>URL.createObjectURL(new Blob([e],{type:`text/javascript`}))",
    'createModuleUrl:e=>"blob:reptar-raw-adapter"',
  ],
  [
    "importModule:e=>import(e)",
    'importModule:async()=>{const m=await import("./HelloReptar.raw-adapter.mjs");return{createAdapter:m.createAdapter,rawAdapterMetadata:m.rawAdapterMetadata}}',
  ],
  ["revokeModuleUrl:URL.revokeObjectURL.bind(URL)", "revokeModuleUrl:()=>{}"],
];

/**
 * Rewrites a NetWasm browser host into a Worker-compatible host.
 *
 * The stock host assumes a browser: it reads `location` at module evaluation,
 * compiles Wasm from bytes, and imports the raw adapter from a blob URL. Workers
 * require a runtime manifest URL, a statically imported WebAssembly.Module, and
 * a statically imported adapter module.
 */
export const patchNetWasmHost = (browserDirectory: string): number => {
  const pagePath = path.join(browserDirectory, "netwasm.page.mjs");
  let page = readFileSync(pagePath, "utf-8");
  let applied = 0;

  for (const [from, to] of replacements) {
    if (page.includes(to) || !page.includes(from)) {
      continue;
    }

    page = page.replaceAll(from, to);
    applied += 1;
  }

  writeFileSync(pagePath, page);

  for (const entry of readdirSync(browserDirectory)) {
    if (!entry.endsWith(".mjs") || entry === "netwasm.page.mjs") {
      continue;
    }

    const entryPath = path.join(browserDirectory, entry);
    const source = readFileSync(entryPath, "utf-8");

    if (source.includes('"./netwasm.page.bun.mjs"')) {
      writeFileSync(
        entryPath,
        source.replaceAll('"./netwasm.page.bun.mjs"', '"./netwasm.page.mjs"')
      );
    }
  }

  return applied;
};

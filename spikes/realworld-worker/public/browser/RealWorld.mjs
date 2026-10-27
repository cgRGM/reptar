import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "21acc5ec5a90e4ec25ca6778e559311d6a2ef2a914d9610e53f0a8b2f1d69657",
  manifestSha256: "96237aac11ebb8d872f0dc0a01db6520a4fd953845eef0520db880e3af013ccc",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

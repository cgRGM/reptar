import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "ae26d5f5c8c2fe720d9ae2cd07bf117a5224b3a5efd838fcaf72f06db5ee18e4",
  manifestSha256: "21bd590b396571aa89bf01aa3ad94504a61a80d0ca4a3cb4544ae49d829d8a83",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

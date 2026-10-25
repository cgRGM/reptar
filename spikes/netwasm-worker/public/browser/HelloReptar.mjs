import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "9abc746da92f7b8568967588da6bcea93de85aa247e8fb08c3a7d12659939910",
  manifestSha256: "92d5a2e6aad745b4fff564a92eb0da636f8342c6aa52a464cdcad41f2ae66dd9",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

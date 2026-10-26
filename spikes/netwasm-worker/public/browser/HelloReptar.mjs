import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "fba9ba3c9e98d49f37dd9ef1d81e2cd2e605b2560ee5a2092c73b055cc371021",
  manifestSha256: "9e7a08fc7c9d5fc1668a557821b38c3f250ef795456f2a8c860a0cad673a4ee6",
});
const exports = Object.freeze(["handle","handleQueue","handleScheduled"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

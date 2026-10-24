import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "021040e3bc55e2a30eb94054c5218cc7e90a4cd6417823ea3382832b0128d718",
  manifestSha256: "4c4b560cc5a1b0e2473514cd159cf4c821c8699b7f3abea074d210ce718845cb",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

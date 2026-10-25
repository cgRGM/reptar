import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "f59d77b5cff42a873576c8b32075be88d4118b225a3471ecaec5bedeb5ce4af8",
  manifestSha256: "476bd319b9d348a445e7c6d239cc41a6c7dbbb2d209fe4c1f3395a9bb24da82b",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

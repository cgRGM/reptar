import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "bfc5adf8bfbce876eb2c8f2786cafca81861083f626efa8576858a249a4d942f",
  manifestSha256: "3e83210c23199bc4db1a0f79fd489fd963d411d3eb9a22db12dc08036c1a0130",
});
const exports = Object.freeze(["handle","handleQueue","handleScheduled"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

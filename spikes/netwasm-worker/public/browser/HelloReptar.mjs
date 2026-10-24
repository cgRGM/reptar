import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "766546d209ff3f9ebfbf96e5e9d5767a78c619214c61274aeee0e86c838e9b86",
  manifestSha256: "73f0aad7a35c3eafe7aa2fdc70f05ca9e198fc763aa48db7fef64311f6ab986f",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

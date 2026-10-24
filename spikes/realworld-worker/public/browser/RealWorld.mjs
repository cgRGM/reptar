import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "ee9a0703dc450678c6e5867bbb354041156c684dd0b1c213089ffb2502ce5f9b",
  manifestSha256: "908f6c6dadfc711b1c0bbc8625c766c70abd9d2a542a9d50a41b0d3e39356623",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

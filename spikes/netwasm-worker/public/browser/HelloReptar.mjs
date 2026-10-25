import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "3061affebde8c364c5fe33e0900ee01f4d20923c2a285ac3997016a6bb17e47b",
  manifestSha256: "c4ed91053cf58f04cf5db656b7a84f69a35d4ad8ea0bbe9bab9655ec7914c14e",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

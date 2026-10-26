import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "c6e9b8014f8d0f87d97b9a34ca57afc6955c97652007d2067dde0643612c233d",
  manifestSha256: "ce3284875b7105df00c8e9982823b7b6ee76e8a5759002b0e2646c0becdb437b",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

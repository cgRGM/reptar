import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "4257e7e5dd638c9b9cba9f77b8fff9ccd32eddea367ef5e5a55d7d4e84e81123",
  manifestSha256: "f45a91e85f12de5bb1a241d3d02985f181751cfa73d8c305a0dcfdc3cf85dc34",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

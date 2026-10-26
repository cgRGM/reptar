import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "bed62f8b22cd5b0c855a2003cf87250fe18dadd35191774fed4e0ff2ceeebfbe",
  manifestSha256: "ad89175c3e0cb889c644503b5f1a7dfe1b28ad20fa826216cc5ce44c3304455b",
});
const exports = Object.freeze(["handle","handleWebSocketClose","handleWebSocketMessage","handleWebSocketOpen","supportsWsMessageDone"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

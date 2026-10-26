import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "803e045969933191af29e771b87e1c784d7574cc5edbc83a15c3d9feda6cf6eb",
  manifestSha256: "5bebd6a5ed63229e7e38e0751766e82f182f51b54839856024a6e2d38caa633f",
});
const exports = Object.freeze(["handle","handleWebSocketClose","handleWebSocketMessage","handleWebSocketOpen","supportsWsMessageDone"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

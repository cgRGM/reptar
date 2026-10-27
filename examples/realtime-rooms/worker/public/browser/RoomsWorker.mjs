import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "c10a335e82a1ee06283dc270a35c64b934b3301a8e0495718f1bd62e0ff0b995",
  manifestSha256: "b560314dab3e18d761608a32b960625584029740facd4e7f13dbbd03d1a0491d",
});
const exports = Object.freeze(["handle","handleWebSocketClose","handleWebSocketMessage","handleWebSocketOpen","supportsWsMessageDone"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

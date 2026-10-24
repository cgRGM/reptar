import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "12f63d387c578168d0f0f681e4fbc578cdfbea5da8889cf2baaa4f0b9bee6539",
  manifestSha256: "34f0eb9c28735973cd83db32097958015d40e675906a53844929f49bf051f237",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

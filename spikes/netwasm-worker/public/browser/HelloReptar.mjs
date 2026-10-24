import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "76d47faaf10138b9bf5350819621a78c129bf0ba547e67be59b0789067c50b70",
  manifestSha256: "527887bda5430572b4ebf26101d182e01bc84f580d4089005896aa5f94db3b58",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

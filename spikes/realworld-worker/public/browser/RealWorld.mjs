import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "859174097c9cab1e91d9423ae9cdb3aca7d2b191816e41890eb378ad45696958",
  manifestSha256: "027326344cf866e28ddb310e5437dd3005a3462603620562d206b4bae189af3f",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

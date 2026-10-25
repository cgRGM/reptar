import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "80fcb3a6b7d48744127e40fc51951951a923806000ded438b4f3697e2862f212",
  manifestSha256: "ed174b16d0c7279dd0ca5a21c52715f93a27d297694b8f52daa5a790bf03f652",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

import { openPage } from "./netwasm.page.mjs";

const startup = Object.freeze({
  buildFingerprint: "f964eb01de4351b210df2a50efba2c14cb2bc1eaac2b9c52c598804aff3b348d",
  manifestSha256: "7c380d2aa8d709229a8dae8e5395a85869d960d8d70f8dd95bd7037e0efd83a6",
});
const exports = Object.freeze(["handle"]);
export function open(options) {
  return openPage(Object.freeze({ exports, options, startup }));
}

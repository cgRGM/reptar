import { spawnSync } from "node:child_process";
import path from "node:path";

const specRoot = process.env.REALWORLD_SPEC_DIR;

if (!specRoot) {
  console.error(
    "Set REALWORLD_SPEC_DIR to the realworld-apps/realworld repository root."
  );
  process.exit(1);
}

const host = process.env.HOST ?? "http://localhost:8787";
const hurlDir = path.join(specRoot, "specs/api/hurl");
const files = [
  "articles.hurl",
  "auth.hurl",
  "comments.hurl",
  "errors_articles.hurl",
  "errors_auth.hurl",
  "errors_authorization.hurl",
  "errors_comments.hurl",
  "errors_profiles.hurl",
  "favorites.hurl",
  "feed.hurl",
  "pagination.hurl",
  "profiles.hurl",
  "tags.hurl",
].map((file) => path.join(hurlDir, file));

const result = spawnSync(
  "hurl",
  ["--test", "--jobs", "1", "--variable", `host=${host}`, ...files],
  { stdio: "inherit" }
);

process.exit(result.status ?? 1);

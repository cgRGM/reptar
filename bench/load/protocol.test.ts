import { expect, test } from "bun:test";

import { assertEquivalentWork } from "./protocol";
import type { ComparisonEvidence } from "./protocol";
import { sha256 } from "./response-witness";

const evidence = (): ComparisonEvidence => {
  const witness = {
    contentType: "application/json",
    correctnessSignature: sha256("validated semantics"),
    decodedBytes: 24,
    decodedSha256: sha256("decoded payload"),
    encodedBytes: 40,
    encodedSha256: sha256("actual compressed payload"),
    encoding: "gzip",
    status: 200,
  };
  return {
    config: {
      acceptEncoding: "gzip",
      completionPolicy: "stop-issuing-wait-inflight-20s",
      connections: 64,
      cooldown: 2,
      duration: 12,
      loadToolIdentity: `oha 1.16.0:${sha256("test load tool")}`,
      reps: 4,
      warmup: 4,
    },
    manifest: {
      authMode: "anonymous",
      cacheMode: "disabled",
      datasetSha256: sha256("fixture database"),
      executableSha256: sha256("actual executable"),
      middlewareId: "compression-enabled-auth-as-specified",
      mode: "equal-work",
      sourceCommit: "a".repeat(40),
      toolchain: { dotnet: "10.0.401" },
      workloadId: "immutable-seed-v1",
    },
    scenario: {
      after: { ...witness },
      before: { ...witness },
      errors: 0,
      method: "GET",
      name: "json",
      path: "/json",
      requestBodySha256: sha256(""),
      totalRequests: 1000,
    },
  };
};

test("allows pinned candidates with different sources/artifacts but the same measured work", () => {
  const baseline = evidence();
  const candidate = evidence();
  candidate.manifest.sourceCommit = "b".repeat(40);
  candidate.manifest.executableSha256 = sha256("new executable");
  expect(() => assertEquivalentWork(baseline, candidate)).not.toThrow();
});

test("identical request Accept-Encoding cannot hide different actual response encodings", () => {
  const candidate = evidence();
  candidate.scenario.before.encoding = "identity";
  candidate.scenario.after.encoding = "identity";
  expect(() => assertEquivalentWork(evidence(), candidate)).toThrow(
    "Content-Encoding"
  );
});

test("refuses a response configuration change during the measured sample", () => {
  const candidate = evidence();
  candidate.scenario.after.encoding = "br";
  expect(() => assertEquivalentWork(evidence(), candidate)).toThrow(
    "Content-Encoding"
  );
});

test("reported errors and missing artifact identities disqualify improvement claims", () => {
  const failed = evidence();
  failed.scenario.errors = 1;
  expect(() => assertEquivalentWork(evidence(), failed)).toThrow(
    "zero reported errors"
  );
  const missing = evidence();
  missing.manifest.executableSha256 = "unknown";
  expect(() => assertEquivalentWork(evidence(), missing)).toThrow("identity");
});

test("defaults, workload, correctness and concurrency mismatches are not equal work", () => {
  const defaults = evidence();
  defaults.manifest.mode = "framework-defaults";
  expect(() => assertEquivalentWork(evidence(), defaults)).toThrow(
    "descriptive"
  );
  const dataset = evidence();
  dataset.manifest.datasetSha256 = sha256("other database");
  expect(() => assertEquivalentWork(evidence(), dataset)).toThrow(
    "datasetSha256"
  );
  const incorrect = evidence();
  incorrect.scenario.before.correctnessSignature = sha256("wrong response");
  expect(() => assertEquivalentWork(evidence(), incorrect)).toThrow(
    "semantics"
  );
  const concurrency = evidence();
  concurrency.config.connections = 1;
  expect(() => assertEquivalentWork(evidence(), concurrency)).toThrow(
    "connections"
  );
});

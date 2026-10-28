import { expect, test } from "bun:test";

import { verifyBracket } from "./bracket";
import type { VerifiableResult } from "./report-evidence";
import { sha256 } from "./response-witness";

const bracket = (): VerifiableResult[] =>
  ["a", "b", "b", "a"].map((role, index) => {
    const response = {
      contentType: "text/plain",
      correctnessSignature: sha256("Hello"),
      decodedBytes: 5,
      decodedSha256: sha256("Hello"),
      encodedBytes: 5,
      encodedSha256: sha256("Hello"),
      encoding: "identity",
      status: 200,
    };
    return {
      comparisonEligible: true,
      config: {
        acceptEncoding: "identity",
        completionPolicy: "stop-issuing-wait-inflight-20s",
        connections: 8,
        cooldown: 0,
        duration: 2,
        loadToolIdentity: `oha 1.16.0:${sha256("test load tool")}`,
        reps: 2,
        warmup: 1,
      },
      generatedAt: new Date((index * 10 + 5) * 1000).toISOString(),
      scenarios: [
        {
          after: { ...response },
          before: { ...response },
          errors: 0,
          method: "GET",
          name: "plaintext",
          path: "/plaintext",
          requestAcceptEncoding: "identity",
          requestBodySha256: sha256(""),
          rps: 1000,
          totalRequests: 2000,
        },
      ],
      schemaVersion: 2,
      startedAt: new Date(index * 10 * 1000).toISOString(),
      workManifest: {
        authMode: "anonymous",
        cacheMode: "off",
        datasetSha256: sha256("database"),
        executableSha256: sha256(role),
        middlewareId: "same",
        mode: "equal-work",
        sourceCommit: role.repeat(40),
        toolchain: { dotnet: "10.0.401" },
        workloadId: "same",
      },
    };
  });

test("duplicate scenario names cannot hide an unmatched workload", () => {
  const runs = bracket();
  for (const run of runs) {
    const first = run.scenarios.at(0);
    if (!first) {
      throw new Error("Missing fixture scenario");
    }
    run.scenarios.push(structuredClone(first));
  }
  expect(verifyBracket(runs).reason).toContain("unique");
});

test("malformed response status is not accepted as a valid witness", () => {
  const runs = bracket();
  const witness = runs.at(0)?.scenarios.at(0)?.before;
  if (!witness) {
    throw new Error("Missing fixture witness");
  }
  Reflect.deleteProperty(witness, "status");
  expect(verifyBracket(runs).verified).toBe(false);
});

test("accepts chronological A/B/B/A evidence without claiming significance", () => {
  const result = verifyBracket(bracket());
  expect(result.verified).toBe(true);
  expect(result.reason).toContain("not proof of statistical significance");
});

test("repetitions inside one run cannot substitute for four actual executions", () => {
  expect(verifyBracket(bracket().slice(0, 2)).verified).toBe(false);
  const overlapping = bracket();
  const first = overlapping.at(0);
  const last = overlapping.at(-1);
  if (!(first && last)) {
    throw new Error("Fixture missing runs");
  }
  last.startedAt = first.startedAt;
  expect(verifyBracket(overlapping).reason).toContain("nonchronological");
});

test("retains noisy A/A samples and refuses an improvement claim", () => {
  const noisy = bracket();
  const repeated = noisy[3]?.scenarios[0];
  if (!repeated) {
    throw new Error("Fixture missing repeated scenario");
  }
  repeated.rps = 1200;
  const result = verifyBracket(noisy);
  expect(result.verified).toBe(false);
  expect(result.aaDrift.plaintext).toBeCloseTo(0.2);
  expect(result.reason).toContain("no improvement claim");
  expect(verifyBracket(noisy, 0.5).verified).toBe(false);
});

test("a different binary cannot masquerade as the repeated baseline", () => {
  const changed = bracket();
  const repeated = changed[3]?.workManifest;
  if (!repeated) {
    throw new Error("Fixture missing manifest");
  }
  repeated.executableSha256 = sha256("different binary");
  expect(verifyBracket(changed).reason).toContain("identity changed");
});

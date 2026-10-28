import type { ResponseWitness } from "./response-witness";

export interface WorkManifest {
  mode: "equal-work" | "framework-defaults" | "representative-application";
  workloadId: string;
  datasetSha256: string;
  authMode: string;
  middlewareId: string;
  cacheMode: string;
  sourceCommit: string;
  executableSha256: string;
  toolchain: Record<string, string>;
}

export interface ComparisonEvidence {
  manifest: WorkManifest;
  config: {
    acceptEncoding: string;
    completionPolicy: string;
    connections: number;
    duration: number;
    cooldown: number;
    warmup: number;
    reps: number;
    loadToolIdentity: string;
  };
  scenario: {
    name: string;
    method: string;
    path: string;
    requestBodySha256: string;
    errors: number;
    totalRequests: number;
    before: ResponseWitness;
    after: ResponseWitness;
  };
}

const SHA256 = /^[a-f\d]{64}$/u;
const COMMIT = /^[a-f\d]{40}$/u;
const LOAD_TOOL = /^.+:[a-f\d]{64}$/u;

const requireEqual = (left: unknown, right: unknown, field: string): void => {
  if (left !== right) {
    throw new Error(`Not equivalent work: ${field} mismatch`);
  }
};

export const validateManifest = (manifest: WorkManifest): void => {
  if (
    !(
      SHA256.test(manifest.datasetSha256) &&
      SHA256.test(manifest.executableSha256) &&
      COMMIT.test(manifest.sourceCommit)
    )
  ) {
    throw new Error("Missing or malformed dataset/executable/source identity");
  }
  for (const field of [
    manifest.workloadId,
    manifest.authMode,
    manifest.middlewareId,
    manifest.cacheMode,
  ]) {
    if (typeof field !== "string" || field.trim().length === 0) {
      throw new Error("Missing workload/auth/middleware/cache identity");
    }
  }
  const versions = Object.values(manifest.toolchain);
  if (
    versions.length === 0 ||
    versions.some(
      (version) => typeof version !== "string" || version.trim().length === 0
    )
  ) {
    throw new Error("Missing actual server toolchain versions");
  }
};

export const assertEquivalentResponse = (
  left: ResponseWitness,
  right: ResponseWitness
): void => {
  for (const witness of [left, right]) {
    if (
      !(
        SHA256.test(witness.encodedSha256) &&
        SHA256.test(witness.decodedSha256) &&
        SHA256.test(witness.correctnessSignature)
      )
    ) {
      throw new Error("Missing wire/decoded/sampled-correctness witness");
    }
    if (
      !Number.isSafeInteger(witness.status) ||
      !Number.isSafeInteger(witness.encodedBytes) ||
      !Number.isSafeInteger(witness.decodedBytes) ||
      !["identity", "gzip", "br", "deflate"].includes(witness.encoding) ||
      typeof witness.contentType !== "string" ||
      !witness.contentType ||
      witness.status < 200 ||
      witness.status >= 300 ||
      witness.decodedBytes <= 0 ||
      witness.encodedBytes <= 0
    ) {
      throw new Error("Invalid response status or empty response witness");
    }
  }
  requireEqual(
    left.encoding,
    right.encoding,
    "actual response Content-Encoding"
  );
  requireEqual(left.contentType, right.contentType, "response Content-Type");
  requireEqual(left.status, right.status, "response status");
  requireEqual(left.decodedBytes, right.decodedBytes, "decoded response bytes");
  requireEqual(
    left.correctnessSignature,
    right.correctnessSignature,
    "sampled response semantics"
  );
};

/** Refuse equivalence rather than inferring equal work from throughput or request headers. */
export const assertEquivalentWork = (
  left: ComparisonEvidence,
  right: ComparisonEvidence
): void => {
  for (const evidence of [left, right]) {
    validateManifest(evidence.manifest);
    const { config, scenario } = evidence;
    for (const number of [
      config.connections,
      config.duration,
      config.reps,
      scenario.totalRequests,
    ]) {
      if (!Number.isFinite(number) || number <= 0) {
        throw new Error("Missing or invalid timing/concurrency/sample count");
      }
    }
    if (
      !Number.isFinite(config.cooldown) ||
      config.cooldown < 0 ||
      !Number.isFinite(config.warmup) ||
      config.warmup < 0 ||
      !LOAD_TOOL.test(config.loadToolIdentity) ||
      !config.completionPolicy ||
      !SHA256.test(scenario.requestBodySha256)
    ) {
      throw new Error("Missing warmup/load-tool/request-body identity");
    }
    if (evidence.manifest.mode !== "equal-work") {
      throw new Error(
        "Defaults/representative results are descriptive, not equal-work comparisons"
      );
    }
    if (
      evidence.scenario.errors !== 0 ||
      evidence.scenario.totalRequests <= 0
    ) {
      throw new Error(
        "Improvement claims require nonempty samples with zero reported errors"
      );
    }
    assertEquivalentResponse(evidence.scenario.before, evidence.scenario.after);
  }
  for (const field of [
    "workloadId",
    "datasetSha256",
    "authMode",
    "middlewareId",
    "cacheMode",
  ] as const) {
    requireEqual(left.manifest[field], right.manifest[field], field);
  }
  for (const field of [
    "acceptEncoding",
    "completionPolicy",
    "connections",
    "cooldown",
    "duration",
    "warmup",
    "reps",
    "loadToolIdentity",
  ] as const) {
    requireEqual(left.config[field], right.config[field], field);
  }
  for (const field of [
    "name",
    "method",
    "path",
    "requestBodySha256",
  ] as const) {
    requireEqual(left.scenario[field], right.scenario[field], field);
  }
  assertEquivalentResponse(left.scenario.before, right.scenario.before);
  assertEquivalentResponse(left.scenario.after, right.scenario.after);
};

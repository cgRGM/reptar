import { assertEquivalentWork } from "./protocol";
import type { ComparisonEvidence, WorkManifest } from "./protocol";
import type { ResponseWitness } from "./response-witness";

export interface VerifiableResult {
  schemaVersion?: number;
  comparisonEligible?: boolean;
  startedAt?: string;
  generatedAt?: string;
  workManifest?: WorkManifest;
  config: ComparisonEvidence["config"];
  scenarios: {
    name: string;
    method: string;
    path: string;
    requestBodySha256: string;
    requestAcceptEncoding: string;
    connections?: number;
    errors: number;
    totalRequests: number;
    rps: number;
    before: ResponseWitness;
    after: ResponseWitness;
  }[];
}

const scenarioEvidence = (
  file: VerifiableResult,
  index: number
): ComparisonEvidence => {
  const scenario = file.scenarios[index];
  if (
    !(
      file.schemaVersion === 2 &&
      file.comparisonEligible === true &&
      file.workManifest &&
      scenario
    )
  ) {
    throw new Error(
      "Missing verified artifact/response evidence; legacy/default results are descriptive only"
    );
  }
  return {
    config: {
      ...file.config,
      acceptEncoding: scenario.requestAcceptEncoding,
      connections: scenario.connections ?? file.config.connections,
    },
    manifest: file.workManifest,
    scenario,
  };
};

/** Report writers must earn a verified status before emitting relative improvement percentages. */
export const verifyReportComparison = (
  files: VerifiableResult[]
): { verified: boolean; reason: string } => {
  try {
    const [baseline] = files;
    if (!baseline || files.length < 2 || baseline.scenarios.length === 0) {
      throw new Error("Comparison requires at least two nonempty result files");
    }
    for (const file of files) {
      const names = file.scenarios.map((scenario) => scenario.name);
      if (
        names.some((name) => typeof name !== "string" || name.length === 0) ||
        new Set(names).size !== names.length
      ) {
        throw new Error("Scenario identities must be nonempty and unique");
      }
    }
    for (const candidate of files.slice(1)) {
      if (candidate.scenarios.length !== baseline.scenarios.length) {
        throw new Error("Scenario selection mismatch");
      }
      for (const [index, scenario] of baseline.scenarios.entries()) {
        const matchingIndex = candidate.scenarios.findIndex(
          (item) => item.name === scenario.name
        );
        if (matchingIndex === -1) {
          throw new Error("Missing comparison scenario");
        }
        assertEquivalentWork(
          scenarioEvidence(baseline, index),
          scenarioEvidence(candidate, matchingIndex)
        );
      }
    }
    return {
      reason:
        "Actual encodings, sampled semantics, declared workload, artifacts and measurement configuration match",
      verified: true,
    };
  } catch (error) {
    return {
      reason:
        error instanceof Error ? error.message : "Invalid comparison evidence",
      verified: false,
    };
  }
};

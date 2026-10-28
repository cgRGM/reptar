import { verifyReportComparison } from "./report-evidence";
import type { VerifiableResult } from "./report-evidence";

export interface BracketCheck {
  verified: boolean;
  reason: string;
  aaDrift: Record<string, number>;
}

type Bracket = [
  VerifiableResult,
  VerifiableResult,
  VerifiableResult,
  VerifiableResult,
];

const requireBracket = (runs: VerifiableResult[], ceiling: number): Bracket => {
  const [firstA, firstB, secondB, secondA] = runs;
  if (runs.length !== 4 || !(firstA && firstB && secondB && secondA)) {
    throw new Error("Four actual A/B/B/A executions are required");
  }
  if (!Number.isFinite(ceiling) || ceiling < 0 || ceiling > 0.05) {
    throw new Error(
      "A/A drift ceiling must be explicit and at most 5%; it is not a significance test"
    );
  }
  return [firstA, firstB, secondB, secondA];
};

const validateRoles = ([firstA, firstB, secondB, secondA]: Bracket): void => {
  for (const [left, right] of [
    [firstA, secondA],
    [firstB, secondB],
  ]) {
    if (
      left?.workManifest?.executableSha256 !==
        right?.workManifest?.executableSha256 ||
      left?.workManifest?.sourceCommit !== right?.workManifest?.sourceCommit
    ) {
      throw new Error("A/A or B/B identity changed inside the bracket");
    }
  }
};

const validateIntervals = (runs: Bracket): void => {
  let previousEnd = 0;
  for (const run of runs) {
    const start = Date.parse(run.startedAt ?? "");
    const end = Date.parse(run.generatedAt ?? "");
    if (
      !Number.isFinite(start) ||
      !Number.isFinite(end) ||
      end <= start ||
      start < previousEnd
    ) {
      throw new Error(
        "Missing, overlapping or nonchronological actual execution intervals"
      );
    }
    previousEnd = end;
    if (
      run.scenarios.some(
        (scenario) => !Number.isFinite(scenario.rps) || scenario.rps <= 0
      )
    ) {
      throw new Error("Invalid bracket throughput sample");
    }
  }
};

const measureNoise = (
  first: VerifiableResult,
  repeated: VerifiableResult,
  ceiling: number,
  drift: Record<string, number>
): void => {
  for (const scenario of first.scenarios) {
    const repeat = repeated.scenarios.find(
      (item) => item.name === scenario.name
    );
    if (!repeat) {
      throw new Error("Missing repeated A/A scenario");
    }
    const variation =
      Math.abs(repeat.rps - scenario.rps) / Math.min(repeat.rps, scenario.rps);
    drift[scenario.name] = variation;
    if (variation > ceiling) {
      throw new Error(
        `A/A drift for ${scenario.name} exceeds the declared noise ceiling; no improvement claim`
      );
    }
  }
};

/** Four independent, nonoverlapping A/B/B/A executions; medians inside one run are not brackets. */
export const verifyBracket = (
  runs: VerifiableResult[],
  maxAaDrift = 0.05
): BracketCheck => {
  const aaDrift: Record<string, number> = {};
  try {
    const bracket = requireBracket(runs, maxAaDrift);
    const work = verifyReportComparison(bracket);
    if (!work.verified) {
      throw new Error(work.reason);
    }
    validateRoles(bracket);
    validateIntervals(bracket);
    measureNoise(bracket[0], bracket[3], maxAaDrift, aaDrift);
    return {
      aaDrift,
      reason:
        "Bracket/work evidence passed; this is not proof of statistical significance or sustained capacity",
      verified: true,
    };
  } catch (error) {
    return {
      aaDrift,
      reason:
        error instanceof Error ? error.message : "Invalid bracket evidence",
      verified: false,
    };
  }
};

import { readFile } from "node:fs/promises";
import path from "node:path";

import { Stack } from "alchemy";
import { isResolved } from "alchemy/Diff";
import { effect as providerEffect } from "alchemy/Provider";
import { Resource } from "alchemy/Resource";
import { localState } from "alchemy/State/LocalState";
import {
  gen,
  orDie,
  succeed,
  tryPromise,
  undefined as absent,
  void as done,
} from "effect/Effect";

const { resolve } = path;

interface UnitProps {
  archivePath: string;
  archiveSha256: string;
  helperPath: string;
  helperSha256: string;
  localProofRoot: string;
  nativeRssKiB: number;
  ownerId: string;
  runId: string;
  seconds: 12 | 86_400;
  sourceCommit: string;
}
interface UnitAttributes {
  phase: string;
  root: string;
  unit?: Record<string, string>;
}
type NativeSoakResource = Resource<
  "Reptar.Reliability.NativeSoak",
  UnitProps,
  UnitAttributes
>;
const NativeSoak = Resource<NativeSoakResource>(
  "Reptar.Reliability.NativeSoak"
);
const namePattern = /^reptar-reliability-\d{8}-[a-f0-9]{12}$/u;
const ownerPattern = /^[a-f0-9]{32}$/u;
const hashPattern = /^[a-f0-9]{64}$/u;
const commitPattern = /^[a-f0-9]{40}$/u;
const absolutePattern = /^\//u;

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null;

const checkedString = (
  value: unknown,
  pattern: RegExp,
  field: string
): string => {
  if (typeof value !== "string" || !pattern.test(value)) {
    throw new Error(`Invalid staging ${field}`);
  }
  return value;
};

const validate = (value: unknown): UnitProps => {
  if (!isRecord(value)) {
    throw new Error("Missing scoped staging configuration");
  }
  if (value.seconds !== 12 && value.seconds !== 86_400) {
    throw new Error("Invalid staging duration");
  }
  const nativeRssKiB = value.nativeRssKiB ?? 256 * 1024;
  if (
    typeof nativeRssKiB !== "number" ||
    !Number.isInteger(nativeRssKiB) ||
    nativeRssKiB <= 0 ||
    nativeRssKiB > 256 * 1024
  ) {
    throw new Error("Invalid native RSS ceiling");
  }
  return {
    archivePath: checkedString(
      value.archivePath,
      absolutePattern,
      "archive path"
    ),
    archiveSha256: checkedString(
      value.archiveSha256,
      hashPattern,
      "archive hash"
    ),
    helperPath: checkedString(value.helperPath, absolutePattern, "helper path"),
    helperSha256: checkedString(value.helperSha256, hashPattern, "helper hash"),
    localProofRoot: checkedString(
      value.localProofRoot,
      absolutePattern,
      "local proving root"
    ),
    nativeRssKiB,
    ownerId: checkedString(value.ownerId, ownerPattern, "owner identity"),
    runId: checkedString(value.runId, namePattern, "run name"),
    seconds: value.seconds,
    sourceCommit: checkedString(
      value.sourceCommit,
      commitPattern,
      "native source pin"
    ),
  };
};

const execute = async (command: string[], input?: string): Promise<string> => {
  const child = Bun.spawn(command, {
    stderr: "pipe",
    stdin: input === undefined ? "ignore" : new Blob([input]),
    stdout: "pipe",
    timeout: 45_000,
  });
  const [code, stdout, stderr] = await Promise.all([
    child.exited,
    new Response(child.stdout).text(),
    new Response(child.stderr).text(),
  ]);
  if (code !== 0) {
    throw new Error(
      `Scoped lifecycle command failed (${code}): ${stderr.slice(-8000)}`
    );
  }
  return stdout;
};
const quote = (value: string): string => `'${value.replaceAll("'", "'\\''")}'`;
const remote = async (
  props: UnitProps,
  action: "prepare" | "read" | "start" | "stop"
): Promise<UnitAttributes> => {
  const request = JSON.stringify({
    action,
    archiveSha256: props.archiveSha256,
    helperSha256: props.helperSha256,
    nativeRssKiB: props.nativeRssKiB,
    ownerId: props.ownerId,
    runId: props.runId,
    seconds: props.seconds,
    sourceCommit: props.sourceCommit,
  });
  const helper = await readFile(props.helperPath, "utf-8");
  if (
    new Bun.CryptoHasher("sha256").update(helper).digest("hex") !==
    props.helperSha256
  ) {
    throw new Error(
      "Pinned lifecycle helper changed; refuse all remote actions"
    );
  }
  const text = await execute(
    [
      "ssh",
      "-o",
      "BatchMode=yes",
      "-o",
      "ConnectTimeout=10",
      "remotedev",
      `python3 - ${quote(request)}`,
    ],
    helper
  );
  const value: unknown = JSON.parse(text);
  if (
    typeof value !== "object" ||
    value === null ||
    !(
      "phase" in value &&
      typeof value.phase === "string" &&
      "root" in value &&
      typeof value.root === "string"
    )
  ) {
    throw new Error("Malformed scoped unit inspection");
  }
  return {
    phase: value.phase,
    root: value.root,
    ...("unit" in value && typeof value.unit === "object" && value.unit !== null
      ? {
          unit: Object.fromEntries(
            Object.entries(value.unit).map(([key, item]) => [key, String(item)])
          ),
        }
      : {}),
  };
};

const verifyLocal = async (props: UnitProps): Promise<void> => {
  const result: unknown = JSON.parse(
    await readFile(resolve(props.localProofRoot, "result.json"), "utf-8")
  );
  if (
    typeof result !== "object" ||
    result === null ||
    !(
      "status" in result &&
      result.status === "passed" &&
      "actualElapsedSeconds" in result &&
      typeof result.actualElapsedSeconds === "number" &&
      result.actualElapsedSeconds >= 7200 &&
      "cycles" in result &&
      result.cycles === 120 &&
      "ownedProcessesCleaned" in result &&
      result.ownedProcessesCleaned === true
    )
  ) {
    throw new Error(
      "Actual completed local proving is required before remote lifecycle apply"
    );
  }
  const manifest: unknown = JSON.parse(
    await readFile(resolve(props.localProofRoot, "manifest.json"), "utf-8")
  );
  if (
    typeof manifest !== "object" ||
    manifest === null ||
    !(
      "sourceCommit" in manifest && manifest.sourceCommit === props.sourceCommit
    )
  ) {
    throw new Error(
      "Completed local proving belongs to a different native source revision"
    );
  }
  const archive = Bun.file(props.archivePath);
  if (
    archive.size > 256 * 1024 * 1024 ||
    new Bun.CryptoHasher("sha256")
      .update(await archive.arrayBuffer())
      .digest("hex") !== props.archiveSha256
  ) {
    throw new Error("Staging archive size/hash mismatch");
  }
};

const providers = providerEffect(
  NativeSoak,
  succeed(
    NativeSoak.Provider.of({
      delete: ({ olds }) =>
        tryPromise(async () => {
          const props = validate(olds);
          const state = await remote(props, "read");
          if (state.phase !== "absent") {
            await remote(props, "stop");
          }
          // Evidence is intentionally retained until explicit verified collection/purge.
        }),
      diff: ({ news, olds }) => {
        if (!isResolved(news)) {
          return done;
        }
        if (olds && JSON.stringify(news) !== JSON.stringify(olds)) {
          throw new Error(
            "Existing staging resource is immutable; use a fresh owned run name"
          );
        }
        return succeed(olds ? { action: "noop" as const } : undefined);
      },
      list: () => succeed([]),
      read: ({ olds }) =>
        olds
          ? tryPromise(async () => {
              const state = await remote(validate(olds), "read");
              return state.phase === "absent" ? undefined : state;
            })
          : absent,
      reconcile: ({ news }) =>
        tryPromise(async () => {
          const props = validate(news);
          await verifyLocal(props);
          const state = await remote(props, "read");
          if (state.phase !== "absent") {
            throw new Error(
              "Refuse staging adoption, restart or retry; inspect retained evidence first"
            );
          }
          await remote(props, "prepare");
          await execute([
            "scp",
            "-q",
            "-o",
            "BatchMode=yes",
            props.archivePath,
            `remotedev:${state.root}/payload.tar`,
          ]);
          return await remote(props, "start");
        }),
    })
  )
);

const configPath = process.env.REPTAR_STAGING_CONFIG;
if (!configPath?.startsWith("/")) {
  throw new Error(
    "Set an absolute REPTAR_STAGING_CONFIG for this separate local-state stack; production configuration is never loaded"
  );
}
const props = validate(JSON.parse(await readFile(configPath, "utf-8")));
export default Stack(
  "reptar-reliability",
  { providers, state: localState() },
  gen(function* isolatedStaging() {
    // Declaration/preflight is read-only; only reconciliation can prepare or start.
    yield* tryPromise(() => remote(props, "read")).pipe(orDie);
    const unit = yield* NativeSoak("native-soak", props);
    return { unit };
  })
);

import { spawn } from "node:child_process";
import type { SpawnOptions } from "node:child_process";
import { once } from "node:events";
import { createServer } from "node:net";
import { promisify } from "node:util";

export const assertPortAvailable = async (port: number): Promise<void> => {
  if (!Number.isInteger(port) || port < 1 || port > 65_535) {
    throw new Error(`Invalid test port: ${port}`);
  }
  const probe = createServer();
  const listening = once(probe, "listening");
  probe.listen(port, "127.0.0.1");
  try {
    await listening;
  } catch (error) {
    throw new Error(
      `Test port 127.0.0.1:${port} is unavailable; refusing to disturb its owner.`,
      { cause: error }
    );
  }
  await promisify(probe.close.bind(probe))();
};

const groupAlive = (pid: number): boolean => {
  try {
    process.kill(-pid, 0);
    return true;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ESRCH") {
      return false;
    }
    throw error;
  }
};

const signalGroup = (pid: number, signal: NodeJS.Signals): void => {
  try {
    process.kill(-pid, signal);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== "ESRCH") {
      throw error;
    }
  }
};

const waitForGroup = async (
  pid: number,
  timeoutMs: number
): Promise<boolean> => {
  const deadline = Date.now() + timeoutMs;
  while (groupAlive(pid)) {
    if (Date.now() >= deadline) {
      return false;
    }
    // eslint-disable-next-line no-await-in-loop -- bounded process teardown polling.
    await Bun.sleep(25);
  }
  return true;
};

/** Owns a detached POSIX process group, never a discovered port or name pattern. */
export const spawnOwnedProcess = async (
  command: string,
  args: string[],
  options: SpawnOptions = {}
) => {
  if (process.platform === "win32") {
    throw new Error(
      "Owned process-group tests currently require Linux or macOS."
    );
  }
  const child = spawn(command, args, { ...options, detached: true });
  const waitForExit = async (): Promise<void> => {
    try {
      await once(child, "close");
    } catch {
      // A startup error also completes this child's lifecycle.
    }
  };
  const exited = waitForExit();
  await once(child, "spawn");
  const { pid } = child;
  if (!pid) {
    throw new Error("Test process started without a process-group id.");
  }
  let stopping: Promise<void> | undefined;
  const stop = (): Promise<void> => {
    stopping ??= (async () => {
      signalGroup(pid, "SIGTERM");
      // Allow the child close/reaping lifecycle to settle before probing the group.
      await Promise.race([exited, Bun.sleep(250)]);
      if (!(await waitForGroup(pid, 3000))) {
        signalGroup(pid, "SIGKILL");
        await Promise.race([exited, Bun.sleep(250)]);
        if (!(await waitForGroup(pid, 3000))) {
          throw new Error(`Owned test process group ${pid} did not terminate.`);
        }
      }
      await exited;
    })();
    return stopping;
  };
  return { child, stop };
};

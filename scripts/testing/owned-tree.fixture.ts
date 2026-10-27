// Deliberately shares the parent's process group. The parent reaps the child
// before exiting, so tests can distinguish a terminated tree from zombies.
import { spawn } from "node:child_process";
import { once } from "node:events";

const child = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], {
  stdio: "ignore",
});
const exited = once(child, "close");
process.on("SIGTERM", async () => {
  await exited;
  process.exit(0);
});
await once(child, "spawn");
console.log(JSON.stringify({ childPid: child.pid }));
setInterval(() => {
  // Keep the fixture alive until its process group is terminated.
}, 1000);

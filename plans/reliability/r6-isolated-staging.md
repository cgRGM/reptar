# R6b — capped isolated native staging (not production)

## State and source boundary

R6a merged as PR #18, `a6ac2b5cc98ff39daf48fb50db4f3d8441410c0d`, after final-head CI/security. The committed local attestation proves 7200.0118 seconds/120 cycles on **7cfacca**, not on every subsequent source version.

Independent PR #19 (`f0b87f1`) changed AI/runtime source before #18 merged. The post-merge tree difference was compared byte-for-byte with that independent PR's delta. It was reviewed and explicitly adopted in the isolated staging branch; the shared development checkout was not reset. A separate clean checkout `/tmp/reptar-reliability-integrated-a6ac2b5` published all three current native applications and passed actual short success/RSS-refusal/owner-TERM controls. A **fresh** 7200-second local run started **2026-10-28T18:19:54Z**, with private frozen artifacts/probes. It remains pending until its own result/recovery/cleanup succeeds. The prior completed run is retained separately, not replayed as current evidence.

The Linux native bundle from successful CI run **40740805706** has a source tree exactly matching **a6ac2b5** (the synthetic merge included #19). All six executable/SQLite sidecar hashes were independently checked. `sourceCommit` for staging is the exact native/application pin; `runnerSourceCommit` and file hashes separately identify the staging coordinator. A changed application/example tree refuses bundle generation.

## Read-only inventory and plan review

`remotedev` has two logical CPUs, 3832 MiB RAM, approximately 2976 MiB available at initial inventory, no swap, and approximately 30 GiB free disk. Existing `reptar-realworld`, `campfire`, a stopped third container, existing images/build cache, public port8787, SSH, production data and signing configuration are protected. No Docker prune, existing-container operation, DNS/binding change, new cloud purchase or paid model is authorized here.

The host is systemd255 on glibc2.39, with Python3.12.3 and noninteractive scoped sudo available. Its installed Bun is **1.4.4**: do not replace or silently reuse it. Staging bundles the **1.3.10 Linux x64** release binary, SHA256 `3632a4fae998deaad2358e22ae74d1fd6c673b9be760a470e3a0044267cb4438`, verified against the release archive's GitHub digest `f57bc0187e39623de716ba3a389fda5486b2d7be7131a980ba54dc7b733d2e08`. Host Python/loader/native runtime library hashes are recorded and rechecked. This is recorded host-runtime evidence, not an immutable OCI image claim.

The installed Alchemy Docker container provider lacks CPU/memory/PID/readonly-root knobs. Rather than deploy an uncapped container or initialize Docker Swarm, this package uses a separate custom **Alchemy-managed transient systemd service**. Its private network namespace has only loopback. This is native VPS proving, not a Workers port or remote D1 claim.

A read-only Alchemy plan with separate local state and an explicit empty env file produced exactly **one new resource**, no production stack imports. `r6-first-capped-unit-plan.log` records that initial plan. It was **not applied**. Fresh inventory, final bundle/config hashes, actual-head CI and the completed current local result are required before apply.

## Enforced and observed limits

- Service runs as **UID/GID1000**, not root. Root privilege is used only for the uniquely named transient systemd unit lifecycle.
- **CPUQuota100%** (at most one of the host's two logical CPUs), **MemoryMax512MiB**, **MemorySwapMax0**, **TasksMax128**, OOM stops the whole service, **Restart=no**, **KillMode=control-group**, ten-second stop timeout.
- **PrivateNetwork=yes**, no published ports; the runner verifies actual interfaces are exactly `lo`. All probes/native listeners use namespace-local loopback. Echo-only stripped environment and network isolation prevent external/paid-provider traffic.
- **ProtectSystem=strict**, readonly home outside the explicit owned-write exception, readonly bundle, no-new-privileges, empty capability bounding set, private devices/tmp, protected control groups/kernel tunables, and inaccessible Docker/containerd sockets. This is not a general untrusted-code sandbox claim.
- Full workload is **86400 actual monotonic seconds**, one serial cycle/minute, exactly **1440 cycles**. A twelve-second/four-cycle control never earns elapsed evidence.
- Native sampled-RSS refusal at **256MiB aggregate**, owned runtime-storage refusal at **512MiB**. These are sampled workload thresholds, not claims of continuous profiling or filesystem quotas. The service cgroup separately enforces the aggregate memory/PID/CPU limits, including Bun/Python descendants.
- Bundle transfer ceiling256MiB; expected archive approximately146MiB. Archive+extracted bundle+bounded runtime data must remain below2GiB. Require >=2GiB available RAM and >=8GiB free disk at admission; review unrelated-workload drift first.
- Python watchdog requests shutdown at workload duration+180s. Independent systemd **RuntimeMaxSec=duration+300s** bounds a disconnected/hung coordinator's service lifetime. No client connection is needed to stop the unit at expiry.
- Existing reserved host only: no new VM, autoscaling, paid model, remote build or service provision. No production secrets/signing configuration are copied; runtime HOME/workspace/SQLite/signing state is disposable and private.

The runner verifies actual cgroup `cpu.max`, `memory.max`, `memory.swap.max`, `pids.max`, UID, effective capabilities and no-new-privileges **before any native workflow**. Runtime library and sealed artifact/probe/tool hashes are checked. Current and previous-version recovery claims remain separate; this stage uses current native artifacts and cold reads of current online backups.

## Lifecycle, tests and stop rules

`packages/infra/reliability.run.ts` imports no production infrastructure, varlock configuration, Cloudflare resources or credential profiles. Explicit `REPTAR_STAGING_CONFIG`, unique `reptar-reliability-YYYYMMDD-<nonce>` names and separate local Alchemy state are mandatory. The lifecycle helper path/hash is explicitly pinned in the resource config and ownership manifest; a changed helper refuses all remote actions. The declaration is read-only; reconciliation requires a completed 7200-second/120-cycle local proof **with matching native source commit**, then verifies the transfer hash before prepare/upload/start.

`scripts/staging-unit.py` refuses foreign/existing directory adoption, mismatched owner/archive/source/duration/RSS identity, unsafe names, archive traversal/links/devices/duplicates, workload restart/retry and raised limits. No shell interpolation is used for privileged lifecycle commands. `scripts/build-staging-bundle.py` seals only native executables/SQLite sidecars, pinned Bun, two bundled probes, runner/operational scripts and a manifest. No databases, signing files, environment dumps or credentials are included.

Pure tests cover inventory/hash/source mismatches, unexpected secret files, symlinks/traversal, resource ownership and exact sandbox/cap/expiry command construction. Strict TypeScript checks the custom Alchemy integration. Existing actual-artifact success/budget/TERM controls still exercise the local default path and exact cycle schedule. These checks are **not** substitutes for the actual Linux capped-service controls.

Before the 24-hour start: successfully run short capped-service acceptance/recovery, deliberate native RSS refusal and owner-stop controls with separate owned names, retain all outcomes, inspect actual unit/cgroup/network settings, and verify no unrelated workloads changed. Failure preserves evidence and stops; it never silently restarts an elapsed run.

On failure/expiry/completion, stop only the exact manifest-owned service, confirm MainPID/cgroup/owned-process cleanup, copy and hash bounded observations/results/backups/logs locally, then explicitly purge only the owned directory. Alchemy destroy stops the unit but deliberately **retains evidence** until collection is verified; it must not be described as complete storage cleanup. The helper refuses purge without explicit retained-evidence acknowledgement. Do not touch unrelated images, containers, services, datasets, DNS, bindings or signing material.

## Acceptance still open

Actual current-pin local completion, final-head CI, real Linux capped-service controls and the **24 actual hours** remain required. Publish first failures, elapsed time, resource peaks/limits, all cycle/data/recovery checks, source/runtime hashes and cleanup outcome. Neither a short control nor multiple restarts establishes 24-hour evidence. R6/multi-release maturity and production deployment remain open until their separately required evidence exists.

Systemd semantics were checked against upstream **v255** `systemd.exec.xml`, `systemd.resource-control.xml` and `systemd.service.xml`; unsupported or merely configured options are not assumed enforced.

# R4 — fail-closed native data boundaries and warning inventory

## Three failures reproduced

The published `aot-contracts` fixture first passed its generated-mapper cold-read checks, then failed all three negative boundaries:

- An unmapped class with no public constructor was accepted through an uninitialized-object fallback, rather than rejecting the unsupported row shape.
- Anonymous SQL parameters reached SQLite without their values and produced a low-level missing-parameter error.
- The default codec accepted an unregistered JSON DTO through reflective fallback in a Native AOT process.

The failed run is retained as `r4-contracts-before.log` in `/tmp/reptar-reliability-evidence-20261027`. These failures are not successful evidence.

## Repairs and preserved compatibility

- `RowMapper` still supports its named scalar projections. Native non-scalar fallback now requires a generated/explicit mapper or throws an actionable error. Managed reflection compatibility is unchanged.
- Native SQLite anonymous parameter objects and anonymous table writes fail before executing SQL. Named tuples, dictionaries and generated `IReptarEntity` parameters remain supported. A rejected command is disposed.
- Default framework codec serialize/string/UTF-8 deserialize paths, and `ReptarJson.Deserialize`, reject missing metadata in Native AOT or reflection-disabled hosts. Source-generated codecs and contexts remain the supported path.
- The built-in `DbCount` projection has explicit module-initialized mapping; `CountAsync` does not depend on reflective DTO construction.
- The actual entity generator already emits module initializers. This work **tests and reuses** that behavior; it does not add a speculative second mapper initialization mechanism.

An initial attempt to forbid reflection in the global resolver also broke native CLI OpenAPI dictionary emission. The published HTTP/client gate caught it (`r4-http-native-failure-resolver.log`). The repair scopes rejection to framework data codecs instead. Controlled CLI schema emission retains its existing behavior and its acknowledged warnings; arbitrary reflected schema portability is not promoted.

## Published checks

`scripts/fixtures/aot-contracts/aot-contracts.csproj` references the actual entity source generator. Separate seed/cold processes verify generated mapping without constructing the record first, reordered columns, scalar and built-in count projections, generated entity parameters, and source-generated JSON roundtrip. Negative modes verify unmapped rows, anonymous parameters, anonymous insert/update, and five unknown-metadata codec entry points. Rejected writes leave the original value and row count unchanged.

Final local macOS metadata-contract Native AOT SHA256: `9ef3bedcf0978624514f0bcc26276dcd4f4d4ad9b2a925ff7d74aa2856300a4b`.

After the repair, all six published artifact suites pass locally: metadata/binding contracts; HTTP/generated client/compression/stream faults; concurrent-session/SIGKILL durability; typed-queue/cron shutdown lifecycle; offline agent CLI/RPC/web; and Rooms against the rebuilt Worker counterpart. The complete C# suite passes **844/844**, no failures/skips. Linux CI must pass on the final head before acceptance.

### Worker and published Rooms follow-up failures

The first Linux run caught the native-only `DbCount` initializer being compiled into NetWasm, whose data port does not define `EntityMapper`. The initializer is now explicitly fenced with `#if !NETWASM`; all three deployed Worker artifacts rebuild locally.

The rebuilt two-host smoke then exposed missing native Rooms history JSON metadata. Rooms now configures its source-generated list codec and an explicit ordinal mapper instead of depending on reflective row/JSON fallback. The smoke can select an explicit published binary with `REPTAR_ROOMS_NATIVE_BINARY`; an invalid binary does not silently fall back to `dotnet run`. Failed smokes retain disposable state rather than deleting their diagnostic database.

Publishing Rooms exposed another real bug: close cancelled the outbound pump before the final `bye` frame was sent. The connection now owns/awaits its send pump, flushes queued frames before close-output, avoids a competing receive operation during the close handshake, and aborts an unresponsive peer at a five-second deadline. Channel eviction uses the actual dropped-item callback, so `DropOldest` no longer reports zero evictions. Three deterministic tests cover flush-before-close, actual eviction/newest-frame retention, and deadline abort. Replacing drain with the old immediate cancellation makes the flush test fail at `Assert.False` (recorded negative control); the implementation is restored.

Final local published Rooms SHA256: `f2c934c64c7d40fba514a9ab9ab1134de09812c5c48cec1c374ef6447205cbe7`. Worker/Rooms failures, the negative mutation, rebuilt Worker manifests and final executable hashes are retained under the external evidence directory. Five consecutive native-only Rooms protocol/history runs also pass, with all samples retained. No rebuilt Worker binaries are committed. These short runs do not establish sustained operational maturity.

## Warning control is not warning elimination

`scripts/check-aot-warnings.py` checks the selected six native-publish logs against `scripts/testing/aot-warning-baseline.json`. The initial inventory contains **301 distinct exact diagnostic instances**, including source spans or ILC member signatures and individual review classifications. The SDK is pinned to 10.0.401.

- Compiler output is still printed and retained. No `NoWarn`, diagnostic-code exemption or analyzer suppression is introduced.
- A warning at another source span/member, a changed diagnostic, or an unrecognized warning format fails the gate.
- Native methods reported as **“will always throw”** and unexpanded assembly-level IL2104/IL3053 warnings fail even if someone tries to record them as known. The earlier rejected cross-checkout publish demonstrated why this matters.
- Eight negative/parser tests cover checkout path normalization, preservation of source spans, known instances, a new callsite with an existing warning code, fatal artifact diagnostics, unrecognized formats, missing review and normal build summaries.
- Incremental builds and architecture/reachability differences can omit known instances; absence is reported, not represented as resolution. The gate cannot prove that every repository path was built. Its declared scope is the six captured publishes, alongside their independent artifact execution gates.

### Review classifications and remaining debt

| Instances | Review and limit |
| --- | --- |
| Default codecs / SQLite / row reflection | Unsupported native data fallbacks now reject explicitly; named positive artifact paths pass. Legacy managed reflection/annotation debt remains. |
| Built-in count initializer | Intentional explicit mapper registration, verified in a cold native process. CA2255 is acknowledged, not suppressed. |
| RPC/OpenAPI emission | Named generated contracts are executed against the native server. Arbitrary reflected DTO/schema combinations remain unproven. |
| AI JSON overloads, settings, trust/checkpoints, providers | Unresolved metadata debt. Offline/Echo/session checks do not certify live provider payloads, images, config mutation or every tool output. |
| Postgres/MySQL and optional protocols | Managed gates remain required. No native provider/remote PubSub/ActionCable certification is inferred. |
| Nullable/unused fields and file-entry style | Existing identifiable debt, not a claim of harmlessness or full corruption-handling coverage. |
| Anonymous schema construction | Explicit `TableDef.Define` is used by the new artifact. No blanket anonymous-schema portability claim. |

This inventory makes regressions reviewable. It does **not** declare these warnings safe, resolved, or evidence of universal AOT safety. Broader provider and operational maturity remain separate work.

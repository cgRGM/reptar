# R2a: external scaffold and published generated-client boundary

## Failures reproduced and repaired

1. A fresh scaffold in an OS temporary directory outside the repository selected unpublished `Reptar`/`Reptar.Kestrel` NuGet packages and failed with NU1101. The CLI incorrectly chose source mode from the destination's ancestry. Source mode now depends on source availability in the CLI installation; generated references are explicit, not accidental ancestor dependency resolution. Registry-only NuGet distribution remains unproven/unreleased.
2. Publishing the scaffold as Native AOT failed with NETSDK1207: consumer `PublishAot` globals reached the build-time netstandard Roslyn analyzer during restore graph evaluation. Reference-level global exclusions alone did not repair restore. The analyzer now locally fixes its build-time IL settings; it is not an application executable. This is not an AOT-warning suppression.
3. Better Auth email signup/signin lacked success/error response metadata. Signup actually returned 201 but generated contracts defaulted to 200/unknown. Added explicit email-auth success, validation, authorization, conflict and throttling metadata, with two mounted OpenAPI regression assertions. Scaffold item writes/deletes now also declare the actual 401/404 paths.

## New release gate

`scripts/test-scaffold.ts` creates a unique OS-temporary project outside the repository, installs it, builds it, generates its actual client, and typechecks both frontend and a copied runtime assertion module against that client. It then publishes and executes the Native AOT binary and calls it through that generated client:

- anonymous create rejected with typed 401;
- signup 201 with the declared user/token shape and Secure session cookie;
- authenticated subsequent request resolves the session;
- invalid item rejected with typed validation issues;
- create/list/delete and repeated-delete 404 match the generated contract;
- deleted data is absent on a subsequent read.

The fixture forwards an explicit cookie header as a non-browser HTTP client. It does not claim browser/TLS cookie-policy verification. Server processes use the R1 owned-group helper. Failures throw through cleanup rather than calling `process.exit` inside the work scope. Inherited database and OAuth configuration is neutralized so the fixture cannot write to an operator's database. The framework clone is still an explicit C# source dependency; no claim of a fully packaged NuGet installation is made.

## Evidence

- Full C# suite: **811 pass**, zero failed/skipped (809 existing + 2 metadata cases).
- Complete external scaffold / Native AOT / generated-client gate passed on macOS arm64.
- Repeated clean run passed while supplying a deliberately unreachable inherited `DATABASE_URL`; the fixture remained SQLite-local and removed its temporary workspace after awaited server cleanup.
- Negative control: change the generated signup success status from 201 to 202. Typechecking the same runtime fixture failed with TS2367 and inaccessible response members. Original contract restored afterward.
- Source lint and diff whitespace checks passed.
- Operator-local evidence: `/tmp/reptar-reliability-evidence-20261027/r2-*`; failures retained rather than replacing them with the eventual green run.

## Still open

Final-head Linux CI/security must pass before merge. Additional published HTTP query/bodyless/streaming/disconnect boundaries are a following R2 work package. This work does not resolve existing AOT/trim warnings; their reviewed instance-level baseline and supported-path safeguards belong to R4. No remote resources or production data were changed.

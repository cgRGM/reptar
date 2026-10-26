# What Reptar should learn from FluentStorage

Source: <https://github.com/robinrodricks/FluentStorage> (MIT, .NET polycloud storage).

FluentStorage is a *storage product*. Reptar is a *web framework* with storage as one
binding. The useful move is not to copy its 15-provider `IStore`; it is to copy the
parts that make object storage behave like a proper HTTP resource, while keeping
Reptar's zero-dependency / zero-reflection / AOT-and-Wasm constraints.

## What FluentStorage gets right

1. **Objects have metadata, not just bytes.** `StoreObject` carries `Size`,
   `ContentType`, `MD5`, `DateModified`, user metadata, tags, and provider
   properties. Without this a web framework cannot answer `HEAD`, `ETag`, or
   `If-None-Match` without downloading the object.
2. **Presigned URLs are bidirectional.** It exposes upload *and* download URLs.
   Reptar only had presigned PUT, which meant every download proxied through the
   Worker.
3. **Range reads are a first-class operation.** `OpenRange` / `OpenSeekable`
   enable video seeking, PDF viewers, resumable downloads, and `206 Partial
   Content`.
4. **Listing is paginated and prefix-based.** `StorageListOptions` has
   `FolderPath`, `FilePrefix`, `Recurse`, `PageSize`, `MaxResults`, and a cursor
   concept. Object stores are flat; pretending otherwise leaks.
5. **The unified path system is centralized.** `StoragePath.Normalize` /
   `Combine` / `Split` avoids the path-traversal checks that every app otherwise
   rewrites.
6. **Errors are typed.** `StorageException` carries a `StorageErrorCode`, which
   maps cleanly to HTTP status codes instead of becoming a 500.
7. **Conditional writes exist.** `OpenWrite(overwrite)` prevents accidental
   overwrites and supports idempotent uploads.
8. **Capability discovery beats lowest-common-denominator APIs.** `IsFileSystem`,
   `IsVersioned`, `IsTagged`, `IsTiered` let portable code branch without
   reflection or provider type checks.
9. **Content-addressed metadata and cache headers travel with the object.**
   `Cache-Control`, `Content-Disposition`, and content type are storage
   metadata, not route-only concerns.

## What we deliberately did not copy

- **The 15-provider matrix** (Azure, GCP, Alibaba, FTP, SFTP, Git, Mongo, MinIO,
  etc.). Reptar targets R2, S3-compatible stores, and local disk. Each provider
  is a zero-dependency implementation, not a NuGet SDK.
- **`IStore`'s 50-method surface.** A framework binding should be small enough to
  read in one sitting. We added only the operations a web handler needs.
- **The directory transfer engine** (`UploadDirectory`, `DownloadDirectory`,
  `StorageProgress`, rule filters, ETA/speed reporting). That is a CLI/backup
  concern, not a request-path concern.
- **Sinks / transform pipelines** (`GZipSink`, `AesSymmetricEncryptionSink`).
  Interesting, but they add a middleware layer that belongs in app code.
- **Queue/messaging abstractions.** Reptar already has `c.Queues`.
- **Runtime reflection and provider SDK types.** Everything stays AOT-safe and
  NetWasm-safe.

## The slice we implemented

| FluentStorage idea | Reptar shape |
| --- | --- |
| `StoreObject` | `StorageObject` (`Key`, `Size`, `ContentType`, `ETag`, `LastModified`, `Metadata`) |
| `GetObjectInfo` | `IStorage.HeadAsync(key)` |
| `OpenRange` | `IStorage.GetRangeAsync(key, offset, length)` |
| `ListObjects` + cursor | `IStorage.ListAsync(StorageListOptions)` → `StoragePage` |
| `GetDownloadUrl` | `IStorage.PresignGetAsync(key, expiresIn, options)` |
| `GetUploadUrl` | existing `IStorage.PresignPutAsync(...)` |
| `StoragePath` | `StorageKey.Normalize` / `Combine` / `FileName` |
| `StorageException` | `StorageException` + `StorageErrorCode` |
| `OpenWrite(overwrite)` | `StoragePutOptions.Overwrite` |
| capability flags | `StorageCapabilities` / `StorageFeatures` |
| HTTP file serving | `ctx.File(key)` / `ctx.Attachment(key)` with `HEAD`, `ETag`/`304`, `Range`/`206`, `Content-Disposition` |

The result is still Reptar-shaped: one interface, default implementations for
optional capabilities, three in-repo providers, no new packages, no reflection,
and the same C# compiling to `net10.0` AOT and `netwasm0.1`.

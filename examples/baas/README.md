# ReptarBase — Single-Container Backend-as-a-Service

**ReptarBase** is an ultra-fast, self-hosted Backend-as-a-Service (BaaS) built on [Reptar](https://github.com/RocktownLabs/reptar). It serves as a modern, type-safe, Native AOT alternative to **PocketBase** and **Convex**, packaging collections, authentication, file storage, and real-time Server-Sent Events (SSE) into a standalone, single-file application.

---

## Highlights

- **Single-File Native AOT**: Written in a single self-contained `app.cs` (no `.csproj` required). Compiles directly into a ~10.4 MB standalone distroless Linux container using `deploy/Dockerfile`.
- **Zero Runtime Reflection**: Built for Native AOT and NetWasm with zero reflection on request, database, or streaming paths.
- **Strongly Typed Column Tokens**: Uses `ColumnRef<T>` tokens (`DocStatus.Eq("published")`, `DocCreatedAt.Desc()`) for compile-time verified queries.
- **Built-in Authentication & API Keys**: Full Better Auth protocol support (`/api/auth/signup`, `/api/auth/signin`, `/api/auth/session`, `/api/auth/api-keys`) supporting cookie sessions and `Authorization: Bearer <token>` personal access tokens.
- **Magic-Byte Verified Storage**: File uploads are inspected stack-allocated via `Files.Check(...)` against magic bytes (PNG, JPEG, GIF, WebP, PDF) and stored using `ctx.Storage`.
- **Real-Time Change Feed**: Instant Server-Sent Events (`GET /api/realtime`) stream document creations, updates, deletions, and file uploads to connected clients with automatic heartbeat pings.
- **OpenShip-Style Studio GUI**: Built-in developer console served right from `GET /` and `GET /dashboard` with collection viewer, drag-and-drop file manager, API key manager, and live SSE streaming terminal.

---

## Quick Start

### Run Locally

```bash
cd examples/baas
dotnet run app.cs
```

Server boots on `http://localhost:8787` (or custom `PORT`). Open `http://localhost:8787` in your browser to launch the **ReptarBase Studio** GUI.

### Automated Verification

Run the automated 12-step verification suite:

```bash
cd examples/baas
./test.sh
```

---

## Studio Web Console (`GET /` / `GET /_`)

ReptarBase embeds a sleek, dark-mode developer console inspired by **OpenShip**, **PocketBase**, and **Linear**:

- **System Overview**: Metrics for documents, file storage, active SSE connections, and Native AOT runtime footprint (~15 MB RAM).
- **Collections Explorer**: View, filter (`draft`, `published`, `archived`), create, and delete documents with instant updates.
- **Storage Manager**: Drag-and-drop file uploader with thumbnail previews, MIME detection, and one-click public URL copying.
- **Service API Keys**: Generate personal access tokens (`rep_live_...`) with configurable expiration for agents and microservices.
- **Live Event Stream**: Real-time terminal streaming Server-Sent Events directly from `/api/realtime` as records are modified.

---

| Variable | Default | Description |
| :-- | :-- | :-- |
| `PORT` | `8787` | HTTP listening port |
| `BAAS_DB` / `DATABASE_PATH` | `reptarbase.db` | SQLite database file path |
| `DATABASE_URL` | _null_ | PostgreSQL connection string (if set, uses PostgreSQL instead of SQLite) |
| `STORAGE_DIR` | `uploads` | Local directory for file uploads |

---

## API Reference

### Health & Metadata

- `GET /health`: Returns service status, version, and active collection definitions.

### Authentication (`/api/auth`)

- `POST /api/auth/signup`: Create a new user account (`{ email, password, name }`).
- `POST /api/auth/signin`: Sign in with email and password (`{ email, password }`). Returns session cookie & bearer token.
- `GET /api/auth/session`: Inspect active session user.
- `POST /api/auth/signout`: Invalidate active session token.
- `POST /api/auth/api-keys`: Create personal access token / service API key (`{ name, expiresInDays }`).
- `GET /api/auth/api-keys`: List personal access tokens for the authenticated user.
- `DELETE /api/auth/api-keys/:id`: Revoke an API key.

### Documents Collection (`/api/documents`)

- `GET /api/documents`: List documents. Supports query parameters:
  - `status`: Filter by status (`draft`, `published`, etc.)
  - `owner_id`: Filter by document owner ID
  - `limit`: Number of records to return (default: `50`, max: `100`)
  - `offset`: Pagination offset (default: `0`)
- `POST /api/documents`: Create a document (requires authentication). Broadcasts `document_created` over SSE.
- `GET /api/documents/:id`: Retrieve a single document and atomically increment view counter.
- `PATCH /api/documents/:id`: Partially update title, content, status, or file attachment (owner or admin). Broadcasts `document_updated` over SSE.
- `DELETE /api/documents/:id`: Delete a document (owner or admin). Broadcasts `document_deleted` over SSE.

### Files Storage (`/api/files`)

- `POST /api/files/upload`: Multipart form-data file upload (`file=@image.png`). Validates magic bytes, writes to storage, and indexes metadata. Broadcasts `file_uploaded` over SSE.
- `GET /api/files/:key`: Stream stored file with ETag validation (`If-None-Match` -> `304 Not Modified`) and immutable caching headers.
- `DELETE /api/files/:id`: Remove file from storage and collection (owner or admin). Broadcasts `file_deleted` over SSE.

### Realtime Change Feed (`/api/realtime`)

- `GET /api/realtime`: Server-Sent Events stream emitting mutation events:
  - `ready`: Connection established.
  - `document_created`: Emits created `DocumentRecord`.
  - `document_updated`: Emits updated `DocumentRecord`.
  - `document_deleted`: Emits `{ id: "..." }`.
  - `file_uploaded`: Emits uploaded `FileRecord`.
  - `file_deleted`: Emits `{ id: "..." }`.

---

## Docker Packaging (Distroless Native AOT)

Build the minimal distroless Native AOT Linux container:

```bash
docker build -f deploy/Dockerfile --build-arg APP_DIR=examples/baas -t reptar-baas .
```

Run in production:

```bash
docker run -d \
  -p 8787:8787 \
  -e PORT=8787 \
  -v $(pwd)/data:/app/data \
  -e BAAS_DB=/app/data/reptarbase.db \
  -e STORAGE_DIR=/app/data/uploads \
  --name reptarbase \
  reptar-baas
```

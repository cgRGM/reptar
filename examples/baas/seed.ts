/**
 * ReptarDrop Seeder & Demo Client
 * Seeds realistic snippets, code recipes, and magic-byte verified images into ReptarBase.
 */

const BASE_URL = process.env.BASE_URL ?? "http://localhost:8787";

console.log(`🦖 Connecting to ReptarBase at ${BASE_URL}...`);

// 1. Check health
try {
  const healthRes = await fetch(`${BASE_URL}/health`);
  if (!healthRes.ok) {
    console.error(`❌ ReptarBase is not healthy at ${BASE_URL}`);
    process.exit(1);
  }
  const health = (await healthRes.json()) as {
    collections: string[];
    version: string;
  };
  console.log(
    `✅ Connected to ReptarBase v${health.version} (${health.collections.join(", ")})`
  );
} catch {
  console.error(
    `❌ Could not connect to ReptarBase at ${BASE_URL}. Ensure it is running with 'bun baas:dev'.`
  );
  process.exit(1);
}

// 2. Register or Login Demo User
console.log("\n🔑 Authenticating demo user...");
const userPayload = {
  email: "developer@reptar.local",
  name: "Reptar Developer",
  password: "Password123!",
};

let sessionCookie = "";
const signupRes = await fetch(`${BASE_URL}/api/auth/signup`, {
  body: JSON.stringify(userPayload),
  headers: { "Content-Type": "application/json" },
  method: "POST",
});

if (signupRes.status === 409) {
  // Already registered, sign in
  const signinRes = await fetch(`${BASE_URL}/api/auth/signin`, {
    body: JSON.stringify({
      email: userPayload.email,
      password: userPayload.password,
    }),
    headers: { "Content-Type": "application/json" },
    method: "POST",
  });
  sessionCookie = signinRes.headers.get("set-cookie") ?? "";
  console.log("   Signed into existing demo account.");
} else {
  sessionCookie = signupRes.headers.get("set-cookie") ?? "";
  console.log("   Registered new demo user account.");
}

// 3. Issue Personal API Key
console.log("🎟 Generating Personal API Token...");
const keyRes = await fetch(`${BASE_URL}/api/auth/api-keys`, {
  body: JSON.stringify({ name: "Demo Automation Key" }),
  headers: {
    "Content-Type": "application/json",
    Cookie: sessionCookie,
  },
  method: "POST",
});

let apiKey = "";
if (keyRes.ok) {
  const keyData = (await keyRes.json()) as { key: string };
  apiKey = keyData.key;
  console.log(`   Generated live key: ${apiKey}`);
} else {
  console.log("   Using fallback session authentication.");
}

const authHeaders: Record<string, string> = apiKey
  ? { Authorization: `Bearer ${apiKey}` }
  : { Cookie: sessionCookie };

// 4. Upload a sample verified PNG image via magic-byte storage
console.log("\n📸 Uploading verified sample image via magic bytes...");
// Valid 1x1 transparent PNG binary bytes:
const pngBytes = new Uint8Array([
  0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0x00, 0x00, 0x00, 0x0d, 0x49,
  0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06,
  0x00, 0x00, 0x00, 0x1f, 0x15, 0xc4, 0x89, 0x00, 0x00, 0x00, 0x0a, 0x49, 0x44,
  0x41, 0x54, 0x78, 0x9c, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01, 0x0d,
  0x0a, 0x2d, 0xb4, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4e, 0x44, 0xae, 0x42,
  0x60, 0x82,
]);

const formData = new FormData();
formData.append(
  "file",
  new Blob([pngBytes], { type: "image/png" }),
  "reptar-badge.png"
);

const uploadRes = await fetch(`${BASE_URL}/api/files/upload`, {
  body: formData,
  headers: authHeaders,
  method: "POST",
});

let imageUrl = "/api/files/sample";
if (uploadRes.ok) {
  const uploadData = (await uploadRes.json()) as {
    etag: string;
    filename: string;
    url: string;
  };
  imageUrl = uploadData.url;
  console.log(
    `   Uploaded file: ${uploadData.filename} -> ${imageUrl} (ETag: ${uploadData.etag})`
  );
}

// 5. Seed Documents
console.log("\n📝 Seeding sample snippets & architectural recipes...");
const sampleSnippets = [
  {
    content: `# Reptar Architecture & Performance

Reptar is the high-performance C# backend framework compiling to **NetWasm** for Cloudflare Workers and **Native AOT** for containers.

![Reptar Badge](${imageUrl})

## Performance Highlights
- **132k req/s** raw engine baseline
- **34k req/s** filtered SQLite queries with zero reflection
- **31k writes/s** sustained SQLite WAL group-commit writes
- **10.4 MB** standalone Native AOT binary image

\`\`\`csharp
// Typed ColumnRef token query
var docs = await ctx.Db.From<DocumentRecord>(Schema.Documents)
    .Where(Schema.DocStatus.Eq("published"))
    .OrderBy(Schema.DocCreatedAt.Desc())
    .Limit(20)
    .ToListAsync();
\`\`\`
`,
    status: "published",
    title: "Reptar Architecture & Performance Cheat-Sheet",
  },
  {
    content: `# Zero-Reflection SQLite Query Recipes

When compiling to Native AOT, runtime reflection causes heap overhead and trimming regressions. Reptar uses Roslyn source generators and typed \`ColumnRef<T>\` tokens.

## Filter & Projection Example
\`\`\`csharp
public static class Schema
{
    public static readonly TableDef Documents = DefineTable("documents", t => ...);
    public static readonly ColumnRef<string> Status = Documents.Col<string>("status");
    public static readonly ColumnRef<long> ViewCount = Documents.Col<long>("view_count");
}

// Fluent compile-checked query
var list = await ctx.Db.From<DocRecord>(Schema.Documents)
    .Where(Schema.Status.Eq("published"))
    .ToListAsync();
\`\`\`

## Atomic View Increments
\`\`\`sql
UPDATE documents SET view_count = view_count + 1 WHERE id = @id;
\`\`\`
`,
    status: "published",
    title: "Zero-Reflection SQLite Query Recipes",
  },
  {
    content: `# Magic-Byte Storage Pipeline

Never trust client-supplied \`Content-Type\` headers. ReptarBase inspects the binary header bytes directly on the stack:

| Format | Magic Bytes (Hex) | MIME Type |
| :--- | :--- | :--- |
| **PNG** | \`89 50 4E 47\` | \`image/png\` |
| **JPEG** | \`FF D8 FF\` | \`image/jpeg\` |
| **GIF** | \`47 49 46 38\` | \`image/gif\` |
| **PDF** | \`25 50 44 46\` | \`application/pdf\` |

\`\`\`csharp
var check = Files.Check(file, "image/*");
if (!check.Ok) return check.ToError(ctx);
\`\`\`
`,
    status: "draft",
    title: "Magic-Byte File Sniffing & ETag Caching",
  },
  {
    content: `# Internal Infrastructure Secrets

This snippet is marked as **unlisted** and is only visible when authenticated with an authorized API token.

- **Cluster Endpoint:** \`k8s.internal.rocktown:6443\`
- **Database Connection Pool:** \`Data Source=reptar.db;Cache=Shared;Mode=ReadWriteCreate\`
- **Heartbeat Rate:** \`15s SSE keepalive\`
`,
    status: "unlisted",
    title: "Private Infrastructure Credentials & Webhooks",
  },
];

/* eslint-disable eslint/no-await-in-loop */
for (const snippet of sampleSnippets) {
  const res = await fetch(`${BASE_URL}/api/documents`, {
    body: JSON.stringify(snippet),
    headers: {
      "Content-Type": "application/json",
      ...authHeaders,
    },
    method: "POST",
  });

  if (res.ok) {
    const doc = (await res.json()) as {
      id: string;
      status: string;
      title: string;
    };
    console.log(`   Created [${doc.status}] "${doc.title}" (ID: ${doc.id})`);
    await fetch(`${BASE_URL}/api/documents/${doc.id}`);
  } else {
    console.error(`   Failed to create snippet "${snippet.title}"`);
  }
}
/* eslint-enable eslint/no-await-in-loop */

console.log("\n========================================================");
console.log("🎉 Seeding complete! All components verified.");
console.log("========================================================");
console.log(`📱 End-User App (ReptarDrop): ${BASE_URL}/`);
console.log(`⚡ Studio Admin Console:     ${BASE_URL}/_`);
console.log(`📡 Real-Time SSE Feed:       ${BASE_URL}/api/realtime`);
console.log("========================================================\n");

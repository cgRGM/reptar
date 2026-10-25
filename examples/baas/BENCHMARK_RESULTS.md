# ReptarBase BaaS Performance Benchmark Results

Generated: 2026-10-25T17:32:43.557Z  
Environment: macOS / Linux Native AOT, SQLite WAL mode, Concurrency: 20

| Tier | Workload | Method | Endpoint | RPS | p50 | p95 | p99 | Success |
| :--- | :--- | :--- | :--- | :---: | :---: | :---: | :---: | :---: |
| T1 | Baseline Engine | `GET` | `/health` | **132,907** | 0.13ms | 0.26ms | 0.58ms | 100% |
| T2 | Filtered Collection Query | `GET` | `/api/documents?status=published` | **34,215** | 0.43ms | 1.38ms | 2.19ms | 100% |
| T3 | Atomic Read + View Counter | `GET` | `/api/documents/:id` | **55,689** | 0.3ms | 0.96ms | 1.5ms | 100% |
| T4 | Authenticated Writes | `POST` | `/api/documents` | **31,722** | 0.52ms | 1.55ms | 3.08ms | 100% |
| T5 | Conditional ETag Caching | `GET` | `/api/files/:key` | **68,052** | 0.27ms | 0.46ms | 1.01ms | 100% |

### Key Takeaways
1. **Zero-Reflection Read Queries**: Filtered collection queries run at **34,215 req/sec** with sub-millisecond median latency (0.43ms p50) using strongly-typed `ColumnRef<T>` tokens.
2. **High-Concurrency Writes**: Authenticated document creation sustained **31,722 writes/sec** while simultaneously generating ULID keys, executing SQLite writes with WAL group-commit, and broadcasting to the real-time SSE stream.
3. **ETag Conditional Caching**: Static file cache validation achieved **68,052 req/sec** at 0.27ms p50 returning HTTP 304 Not Modified.
4. **Baseline Engine Overhead**: Reptar's Kestrel pipeline handled **132,907 req/sec** on basic endpoints with minimal allocations.

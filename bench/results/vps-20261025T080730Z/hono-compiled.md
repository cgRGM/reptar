## hono-compiled

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 49540 | 1.9 ms | 2.5 ms | 3.9 ms | 0 | 0.020 | 38.5 MB |
| T1 | json | 39597 | 2.5 ms | 2.8 ms | 3.3 ms | 0 | 0.025 | 36.8 MB |
| T2 | router-params | 36135 | 2.7 ms | 3.1 ms | 3.4 ms | 0 | 0.028 | 39.2 MB |
| T3 | validate | 17807 | 5.5 ms | 6.1 ms | 7.4 ms | 0 | 0.057 | 47.9 MB |
| T4 | task-detail | 17905 | 5.5 ms | 5.9 ms | 6.3 ms | 0 | 0.056 | 44.3 MB |
| T5 | task-list | 6908 | 14.4 ms | 15.2 ms | 16.0 ms | 0 | 0.145 | 46.2 MB |
| T6 | task-comments | 9944 | 9.9 ms | 10.6 ms | 11.6 ms | 0 | 0.102 | 45.2 MB |
| T7 | create-task | 4314 | 24.6 ms | 32.4 ms | 36.3 ms | 0 | 0.186 | 53.3 MB |
| T8 | me | 12026 | 8.1 ms | 10.3 ms | 12.5 ms | 0 | 0.084 | 55.9 MB |
| T9 | login | 22 | 445.0 ms | 711.0 ms | 800.0 ms | 0 | 47.547 | 47.7 MB |
| T10 | update-task | 5248 | 17.2 ms | 28.7 ms | 32.0 ms | 0 | 0.173 | 56.9 MB |

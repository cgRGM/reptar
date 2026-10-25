## hono-local

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 113696 | 0.4 ms | 0.8 ms | 0.9 ms | 49 | n/a | n/a |
| T1 | json | 105114 | 0.4 ms | 0.8 ms | 0.9 ms | 48 | n/a | n/a |
| T2 | router-params | 98268 | 0.5 ms | 0.9 ms | 1.0 ms | 48 | n/a | n/a |
| T3 | validate | 65927 | 0.7 ms | 1.3 ms | 1.5 ms | 49 | n/a | n/a |
| T4 | task-detail | 53040 | 0.9 ms | 1.7 ms | 1.9 ms | 48 | n/a | n/a |
| T5 | task-list | 18542 | 2.6 ms | 3.1 ms | 5.2 ms | 49 | n/a | n/a |
| T6 | task-comments | 28510 | 1.7 ms | 2.0 ms | 3.4 ms | 49 | n/a | n/a |
| T8 | me | 31995 | 1.4 ms | 2.7 ms | 3.5 ms | 50 | n/a | n/a |
| T9 | login | 503 | 89.7 ms | 213.6 ms | 310.5 ms | 50 | n/a | n/a |

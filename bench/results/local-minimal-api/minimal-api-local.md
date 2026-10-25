## minimal-api-local

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 111416 | 0.4 ms | 0.9 ms | 2.2 ms | 46 | n/a | n/a |
| T1 | json | 115661 | 0.3 ms | 0.9 ms | 2.0 ms | 44 | n/a | n/a |
| T2 | router-params | 94560 | 0.4 ms | 1.2 ms | 3.3 ms | 41 | n/a | n/a |
| T3 | validate | 81471 | 0.4 ms | 1.6 ms | 4.6 ms | 43 | n/a | n/a |
| T4 | task-detail | 64891 | 0.7 ms | 1.4 ms | 2.6 ms | 50 | n/a | n/a |
| T5 | task-list | 30317 | 1.5 ms | 2.5 ms | 4.3 ms | 46 | n/a | n/a |
| T6 | task-comments | 70186 | 0.6 ms | 1.1 ms | 2.6 ms | 48 | n/a | n/a |
| T8 | me | 96383 | 0.4 ms | 1.0 ms | 3.3 ms | 43 | n/a | n/a |
| T9 | login | 288 | 149.0 ms | 304.8 ms | 309.5 ms | 50 | n/a | n/a |

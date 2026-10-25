## hono-compiled

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 48798 | 2.0 ms | 2.5 ms | 4.0 ms | 0 | 0.021 | 33.7 MB |
| T1 | json | 39726 | 2.5 ms | 2.8 ms | 3.2 ms | 0 | 0.025 | 37.1 MB |
| T2 | router-params | 32693 | 2.9 ms | 3.4 ms | 3.8 ms | 0 | 0.030 | 37.9 MB |
| T3 | validate | 18358 | 5.4 ms | 5.9 ms | 6.3 ms | 0 | 0.055 | 44.8 MB |
| T4 | task-detail | 17403 | 5.6 ms | 6.2 ms | 7.2 ms | 0 | 0.058 | 45.1 MB |
| T5 | task-list | 6899 | 14.4 ms | 15.7 ms | 16.8 ms | 0 | 0.149 | 46.4 MB |
| T6 | task-comments | 9958 | 9.9 ms | 10.5 ms | 11.9 ms | 0 | 0.100 | 46.4 MB |
| T7 | create-task | 297 | 335.4 ms | 517.8 ms | 596.9 ms | 0 | 0.459 | 49.6 MB |
| T8 | me | 11905 | 8.1 ms | 10.4 ms | 12.8 ms | 0 | 0.085 | 54.9 MB |
| T9 | login | 22 | 449.1 ms | 716.1 ms | 857.1 ms | 0 | 49.587 | 46.2 MB |
| T10 | update-task | 303 | 329.0 ms | 484.1 ms | 580.2 ms | 0 | 0.452 | 50.0 MB |

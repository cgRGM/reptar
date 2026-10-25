## minimal-api

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 37612 | 2.6 ms | 3.4 ms | 3.6 ms | 0 | 0.026 | 39.8 MB |
| T1 | json | 41052 | 2.5 ms | 2.9 ms | 3.2 ms | 0 | 0.025 | 40.6 MB |
| T2 | router-params | 41072 | 2.5 ms | 2.9 ms | 3.2 ms | 0 | 0.024 | 40.8 MB |
| T3 | validate | 33234 | 3.1 ms | 3.7 ms | 4.6 ms | 0 | 0.030 | 40.9 MB |
| T4 | task-detail | 13727 | 7.1 ms | 9.8 ms | 11.6 ms | 0 | 0.075 | 43.8 MB |
| T5 | task-list | 6003 | 15.9 ms | 24.8 ms | 26.4 ms | 0 | 0.168 | 42.4 MB |
| T6 | task-comments | 10496 | 9.5 ms | 12.9 ms | 14.4 ms | 0 | 0.097 | 45.9 MB |
| T7 | create-task | 296 | 338.3 ms | 351.8 ms | 565.1 ms | 0 | 0.714 | 50.5 MB |
| T8 | me | 15617 | 6.5 ms | 9.0 ms | 11.6 ms | 0 | 0.065 | 52.8 MB |
| T9 | login | 20 | 8054.3 ms | 8054.7 ms | 8054.8 ms | 0 | 100.569 | 52.4 MB |
| T10 | update-task | 291 | 343.7 ms | 357.7 ms | 595.0 ms | 0 | 1.379 | 82.8 MB |

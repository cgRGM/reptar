## minimal-api

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 41852 | 2.4 ms | 2.8 ms | 3.2 ms | 0 | 0.024 | 40.1 MB |
| T1 | json | 41675 | 2.4 ms | 2.8 ms | 3.1 ms | 0 | 0.024 | 41.9 MB |
| T2 | router-params | 40954 | 2.4 ms | 2.9 ms | 3.2 ms | 0 | 0.024 | 41.9 MB |
| T3 | validate | 34427 | 3.0 ms | 3.5 ms | 3.9 ms | 0 | 0.029 | 42.0 MB |
| T4 | task-detail | 12973 | 7.9 ms | 10.6 ms | 13.0 ms | 0 | 0.077 | 43.5 MB |
| T5 | task-list | 5884 | 16.3 ms | 24.4 ms | 26.4 ms | 0 | 0.171 | 42.9 MB |
| T6 | task-comments | 10126 | 9.9 ms | 13.2 ms | 15.4 ms | 0 | 0.099 | 45.6 MB |
| T7 | create-task | 3628 | 29.2 ms | 37.8 ms | 40.9 ms | 0 | 0.235 | 52.8 MB |
| T8 | me | 14765 | 6.8 ms | 10.1 ms | 14.2 ms | 0 | 0.069 | 53.7 MB |
| T9 | login | 15 | 732.9 ms | 1026.2 ms | 1248.0 ms | 0 | 76.463 | 53.7 MB |
| T10 | update-task | 2150 | 46.2 ms | 53.7 ms | 57.5 ms | 0 | 0.362 | 81.2 MB |

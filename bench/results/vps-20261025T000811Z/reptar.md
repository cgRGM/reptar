## reptar

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 42898 | 2.3 ms | 2.8 ms | 3.0 ms | 0 | 0.023 | 45.8 MB |
| T1 | json | 41895 | 2.4 ms | 2.8 ms | 3.2 ms | 0 | 0.024 | 46.5 MB |
| T2 | router-params | 39331 | 2.5 ms | 3.2 ms | 3.5 ms | 0 | 0.025 | 46.7 MB |
| T3 | validate | 34548 | 2.9 ms | 3.6 ms | 4.2 ms | 0 | 0.029 | 49.2 MB |
| T4 | task-detail | 11645 | 8.0 ms | 12.8 ms | 14.3 ms | 0 | 0.085 | 50.1 MB |
| T5 | task-list | 5304 | 18.5 ms | 24.4 ms | 29.4 ms | 0 | 0.189 | 50.2 MB |
| T6 | task-comments | 9658 | 9.3 ms | 15.4 ms | 16.9 ms | 0 | 0.107 | 50.4 MB |
| T7 | create-task | 3640 | 28.8 ms | 37.6 ms | 40.8 ms | 0 | 0.235 | 58.7 MB |
| T8 | me | 11709 | 8.5 ms | 13.1 ms | 17.7 ms | 0 | 0.085 | 59.6 MB |
| T10 | update-task | 3738 | 25.5 ms | 37.6 ms | 41.8 ms | 0 | 0.263 | 58.8 MB |

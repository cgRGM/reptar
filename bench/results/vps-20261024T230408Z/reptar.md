## reptar

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 39667 | 2.6 ms | 3.0 ms | 3.5 ms | 0 | 0.025 | 39.7 MB |
| T1 | json | 35585 | 2.9 ms | 3.5 ms | 3.9 ms | 0 | 0.028 | 41.2 MB |
| T2 | router-params | 36777 | 2.8 ms | 3.3 ms | 3.8 ms | 0 | 0.027 | 42.1 MB |
| T3 | validate | 29753 | 3.5 ms | 4.2 ms | 4.8 ms | 0 | 0.034 | 42.5 MB |
| T4 | task-detail | 11521 | 8.7 ms | 11.7 ms | 12.9 ms | 0 | 0.088 | 44.1 MB |
| T5 | task-list | 5035 | 18.4 ms | 30.9 ms | 32.9 ms | 0 | 0.199 | 46.3 MB |
| T6 | task-comments | 9313 | 10.7 ms | 14.8 ms | 16.1 ms | 0 | 0.111 | 45.4 MB |
| T7 | create-task | 3282 | 30.3 ms | 40.8 ms | 43.6 ms | 0 | 0.264 | 53.4 MB |
| T8 | me | 10463 | 9.7 ms | 13.1 ms | 14.4 ms | 0 | 0.097 | 53.4 MB |
| T9 | login | 15 | 700.0 ms | 1069.4 ms | 1189.8 ms | 0 | 76.633 | 53.5 MB |
| T10 | update-task | 3459 | 27.8 ms | 39.0 ms | 44.1 ms | 0 | 0.278 | 52.8 MB |

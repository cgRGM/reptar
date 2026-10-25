## hono-jit

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 49280 | 2.0 ms | 2.5 ms | 3.9 ms | 0 | 0.020 | 40.5 MB |
| T1 | json | 39289 | 2.5 ms | 2.8 ms | 3.5 ms | 0 | 0.026 | 44.1 MB |
| T2 | router-params | 34401 | 2.8 ms | 3.2 ms | 4.6 ms | 0 | 0.029 | 44.4 MB |
| T3 | validate | 18256 | 5.4 ms | 6.0 ms | 6.4 ms | 0 | 0.055 | 51.0 MB |
| T4 | task-detail | 17899 | 5.5 ms | 6.0 ms | 7.7 ms | 0 | 0.058 | 50.5 MB |
| T5 | task-list | 6917 | 14.3 ms | 15.1 ms | 16.0 ms | 0 | 0.146 | 52.4 MB |
| T6 | task-comments | 9806 | 10.0 ms | 11.1 ms | 12.0 ms | 0 | 0.104 | 50.8 MB |
| T7 | create-task | 4235 | 25.1 ms | 33.5 ms | 37.2 ms | 0 | 0.187 | 59.2 MB |
| T8 | me | 12052 | 8.1 ms | 10.3 ms | 12.7 ms | 0 | 0.083 | 61.1 MB |
| T9 | login | 22 | 444.2 ms | 725.1 ms | 879.3 ms | 0 | 47.116 | 53.5 MB |
| T10 | update-task | 5230 | 17.1 ms | 28.8 ms | 32.3 ms | 0 | 0.173 | 62.7 MB |

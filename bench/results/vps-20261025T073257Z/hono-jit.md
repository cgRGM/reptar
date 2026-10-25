## hono-jit

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 48525 | 2.0 ms | 2.5 ms | 4.0 ms | 0 | 0.020 | 47.2 MB |
| T1 | json | 39463 | 2.5 ms | 2.8 ms | 3.3 ms | 0 | 0.025 | 43.4 MB |
| T2 | router-params | 36122 | 2.7 ms | 3.1 ms | 3.8 ms | 0 | 0.028 | 48.3 MB |
| T3 | validate | 17629 | 5.6 ms | 6.2 ms | 8.0 ms | 0 | 0.058 | 51.3 MB |
| T4 | task-detail | 18023 | 5.5 ms | 6.0 ms | 6.5 ms | 0 | 0.055 | 50.0 MB |
| T5 | task-list | 6817 | 14.6 ms | 15.6 ms | 17.3 ms | 0 | 0.154 | 52.1 MB |
| T6 | task-comments | 9950 | 9.9 ms | 10.6 ms | 11.3 ms | 0 | 0.101 | 50.6 MB |
| T7 | create-task | 4264 | 25.1 ms | 32.9 ms | 36.6 ms | 0 | 0.189 | 61.2 MB |
| T8 | me | 12019 | 8.1 ms | 10.3 ms | 12.9 ms | 0 | 0.087 | 60.3 MB |
| T9 | login | 22 | 442.3 ms | 721.9 ms | 890.5 ms | 0 | 46.517 | 53.3 MB |
| T10 | update-task | 5281 | 17.0 ms | 28.5 ms | 33.1 ms | 0 | 0.176 | 62.5 MB |

## hono-jit

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 48764 | 2.0 ms | 2.5 ms | 4.0 ms | 0 | 0.021 | 45.0 MB |
| T1 | json | 39315 | 2.5 ms | 2.8 ms | 3.3 ms | 0 | 0.025 | 49.3 MB |
| T2 | router-params | 35877 | 2.8 ms | 3.1 ms | 3.7 ms | 0 | 0.028 | 44.9 MB |
| T3 | validate | 17504 | 5.6 ms | 6.3 ms | 8.1 ms | 0 | 0.059 | 54.0 MB |
| T4 | task-detail | 18555 | 5.3 ms | 5.8 ms | 6.3 ms | 0 | 0.054 | 52.5 MB |
| T5 | task-list | 6785 | 14.7 ms | 15.7 ms | 16.7 ms | 0 | 0.149 | 54.3 MB |
| T6 | task-comments | 10101 | 9.8 ms | 10.5 ms | 11.2 ms | 0 | 0.101 | 52.5 MB |
| T7 | create-task | 4415 | 24.2 ms | 31.2 ms | 34.7 ms | 0 | 0.184 | 63.4 MB |
| T8 | me | 12069 | 8.1 ms | 10.2 ms | 12.7 ms | 0 | 0.085 | 62.9 MB |
| T9 | login | 22 | 443.6 ms | 709.9 ms | 800.8 ms | 0 | 46.880 | 55.8 MB |
| T10 | update-task | 5273 | 17.0 ms | 28.2 ms | 32.3 ms | 0 | 0.180 | 64.0 MB |

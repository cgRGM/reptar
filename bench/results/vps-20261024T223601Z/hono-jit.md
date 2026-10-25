## hono-jit

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 48814 | 2.0 ms | 2.4 ms | 4.0 ms | 0 | 0.020 | 43.9 MB |
| T1 | json | 38939 | 2.5 ms | 2.9 ms | 3.4 ms | 0 | 0.026 | 44.3 MB |
| T2 | router-params | 34023 | 2.8 ms | 3.8 ms | 6.0 ms | 0 | 0.029 | 48.8 MB |
| T3 | validate | 17803 | 5.5 ms | 6.2 ms | 7.5 ms | 0 | 0.056 | 51.0 MB |
| T4 | task-detail | 17849 | 5.5 ms | 6.0 ms | 7.7 ms | 0 | 0.057 | 51.8 MB |
| T5 | task-list | 6889 | 14.5 ms | 15.3 ms | 16.2 ms | 0 | 0.145 | 53.0 MB |
| T6 | task-comments | 9810 | 10.0 ms | 11.3 ms | 13.3 ms | 0 | 0.105 | 52.9 MB |
| T7 | create-task | 296 | 336.7 ms | 505.1 ms | 601.9 ms | 0 | 0.422 | 56.9 MB |
| T8 | me | 12208 | 8.0 ms | 10.2 ms | 12.9 ms | 0 | 0.084 | 61.7 MB |
| T9 | login | 24 | 3186.5 ms | 7850.9 ms | 8739.3 ms | 0 | 69.103 | 57.0 MB |
| T10 | update-task | 304 | 327.7 ms | 500.9 ms | 590.1 ms | 0 | 0.748 | 60.8 MB |

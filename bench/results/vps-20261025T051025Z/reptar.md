## reptar

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 43468 | 2.3 ms | 2.7 ms | 3.0 ms | 0 | 0.023 | 47.3 MB |
| T1 | json | 42291 | 2.4 ms | 2.8 ms | 3.1 ms | 0 | 0.024 | 49.7 MB |
| T2 | router-params | 40351 | 2.5 ms | 3.0 ms | 3.4 ms | 0 | 0.026 | 51.9 MB |
| T3 | validate | 35370 | 2.9 ms | 3.4 ms | 3.9 ms | 0 | 0.028 | 52.0 MB |
| T4 | task-detail | 12107 | 7.7 ms | 11.8 ms | 13.0 ms | 0 | 0.084 | 52.8 MB |
| T5 | task-list | 5316 | 18.5 ms | 24.7 ms | 30.8 ms | 0 | 0.190 | 53.0 MB |
| T6 | task-comments | 9846 | 9.2 ms | 14.9 ms | 16.2 ms | 0 | 0.102 | 53.3 MB |
| T7 | create-task | 3316 | 30.2 ms | 40.1 ms | 63.8 ms | 0 | 0.261 | 75.2 MB |
| T8 | me | 11720 | 7.7 ms | 12.1 ms | 13.9 ms | 0 | 0.086 | 81.4 MB |
| T9 | login | 15 | 699.7 ms | 1052.2 ms | 1259.8 ms | 0 | 74.406 | 81.5 MB |
| T10 | update-task | 3517 | 27.5 ms | 39.0 ms | 43.2 ms | 0 | 0.276 | 84.5 MB |

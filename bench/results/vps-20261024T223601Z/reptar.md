## reptar

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 29224 | 2.9 ms | 6.1 ms | 8.1 ms | 0 | 0.028 | 40.2 MB |
| T1 | json | 35065 | 2.9 ms | 3.7 ms | 5.4 ms | 0 | 0.028 | 40.7 MB |
| T2 | router-params | 36540 | 2.8 ms | 3.3 ms | 3.8 ms | 0 | 0.027 | 41.9 MB |
| T3 | validate | 29483 | 3.6 ms | 4.3 ms | 4.9 ms | 0 | 0.034 | 43.4 MB |
| T4 | task-detail | 11290 | 8.6 ms | 12.1 ms | 13.3 ms | 0 | 0.089 | 44.7 MB |
| T5 | task-list | 4906 | 19.7 ms | 31.2 ms | 34.7 ms | 0 | 0.208 | 46.2 MB |
| T6 | task-comments | 6800 | 14.5 ms | 23.2 ms | 27.4 ms | 0 | 0.134 | 47.0 MB |
| T7 | create-task | 3090 | 33.0 ms | 43.1 ms | 49.3 ms | 0 | 0.285 | 53.6 MB |
| T8 | me | 10370 | 9.5 ms | 13.6 ms | 16.4 ms | 0 | 0.099 | 52.9 MB |
| T9 | login | 20 | 8067.8 ms | 8068.2 ms | 8068.3 ms | 0 | 137.260 | 52.9 MB |
| T10 | update-task | 3310 | 29.3 ms | 44.2 ms | 55.0 ms | 0 | 0.304 | 53.7 MB |

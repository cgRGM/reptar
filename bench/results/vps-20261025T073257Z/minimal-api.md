## minimal-api

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 41133 | 2.4 ms | 2.9 ms | 3.3 ms | 0 | 0.025 | 40.0 MB |
| T1 | json | 41704 | 2.4 ms | 2.8 ms | 3.1 ms | 0 | 0.024 | 41.0 MB |
| T2 | router-params | 40996 | 2.5 ms | 2.9 ms | 3.1 ms | 0 | 0.024 | 41.0 MB |
| T3 | validate | 33763 | 3.0 ms | 3.6 ms | 4.0 ms | 0 | 0.030 | 41.1 MB |
| T4 | task-detail | 13162 | 7.7 ms | 10.0 ms | 13.2 ms | 0 | 0.077 | 43.1 MB |
| T5 | task-list | 5859 | 16.1 ms | 25.1 ms | 26.7 ms | 0 | 0.175 | 43.4 MB |
| T6 | task-comments | 9994 | 10.0 ms | 13.8 ms | 15.7 ms | 0 | 0.101 | 44.3 MB |
| T7 | create-task | 3515 | 30.1 ms | 38.6 ms | 41.0 ms | 0 | 0.236 | 50.8 MB |
| T8 | me | 13989 | 7.2 ms | 9.9 ms | 12.3 ms | 0 | 0.072 | 51.8 MB |
| T9 | login | 15 | 693.9 ms | 826.3 ms | 1043.0 ms | 0 | 72.560 | 51.8 MB |
| T10 | update-task | 2145 | 46.1 ms | 53.5 ms | 57.5 ms | 0 | 0.369 | 73.3 MB |

## minimal-api

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 41340 | 2.4 ms | 2.9 ms | 3.3 ms | 0 | 0.024 | 40.5 MB |
| T1 | json | 41030 | 2.4 ms | 2.9 ms | 3.2 ms | 0 | 0.025 | 42.2 MB |
| T2 | router-params | 41009 | 2.4 ms | 2.9 ms | 3.2 ms | 0 | 0.024 | 42.2 MB |
| T3 | validate | 33236 | 3.1 ms | 3.7 ms | 4.1 ms | 0 | 0.031 | 42.5 MB |
| T4 | task-detail | 14163 | 7.2 ms | 9.2 ms | 12.8 ms | 0 | 0.071 | 44.4 MB |
| T5 | task-list | 9864 | 9.8 ms | 15.1 ms | 16.7 ms | 0 | 0.101 | 43.8 MB |
| T6 | task-comments | 10263 | 9.7 ms | 13.4 ms | 16.4 ms | 0 | 0.099 | 43.8 MB |
| T7 | create-task | 7478 | 12.4 ms | 17.9 ms | 21.0 ms | 0 | 0.133 | 47.2 MB |
| T8 | me | 15449 | 6.6 ms | 8.6 ms | 10.1 ms | 0 | 0.066 | 48.2 MB |
| T9 | login | 15 | 699.8 ms | 980.0 ms | 1064.5 ms | 0 | 73.833 | 48.2 MB |
| T10 | update-task | 2182 | 45.4 ms | 52.5 ms | 56.0 ms | 0 | 0.350 | 74.7 MB |

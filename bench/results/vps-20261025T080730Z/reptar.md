## reptar

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 41877 | 2.4 ms | 2.8 ms | 3.1 ms | 0 | 0.024 | 39.7 MB |
| T1 | json | 42239 | 2.4 ms | 2.7 ms | 3.1 ms | 0 | 0.024 | 39.9 MB |
| T2 | router-params | 41355 | 2.4 ms | 2.9 ms | 3.3 ms | 0 | 0.024 | 39.9 MB |
| T3 | validate | 36262 | 2.8 ms | 3.3 ms | 3.7 ms | 0 | 0.028 | 44.6 MB |
| T4 | task-detail | 13765 | 6.7 ms | 10.3 ms | 11.6 ms | 0 | 0.075 | 45.2 MB |
| T5 | task-list | 5945 | 16.2 ms | 21.8 ms | 25.8 ms | 0 | 0.170 | 45.5 MB |
| T6 | task-comments | 10707 | 8.3 ms | 13.7 ms | 14.8 ms | 0 | 0.094 | 45.6 MB |
| T7 | create-task | 6224 | 15.1 ms | 28.1 ms | 34.2 ms | 0 | 0.160 | 82.9 MB |
| T8 | me | 15683 | 6.4 ms | 9.2 ms | 11.3 ms | 0 | 0.064 | 93.0 MB |
| T9 | login | 14 | 693.8 ms | 1040.5 ms | 1249.9 ms | 0 | 74.198 | 92.7 MB |
| T10 | update-task | 4925 | 20.5 ms | 31.1 ms | 38.8 ms | 0 | 0.205 | 103.2 MB |

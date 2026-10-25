## reptar

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 43169 | 2.3 ms | 2.8 ms | 3.0 ms | 0 | 0.023 | 39.3 MB |
| T1 | json | 42300 | 2.4 ms | 2.8 ms | 3.1 ms | 0 | 0.024 | 41.8 MB |
| T2 | router-params | 42038 | 2.4 ms | 2.8 ms | 3.3 ms | 0 | 0.024 | 43.7 MB |
| T3 | validate | 35731 | 2.8 ms | 3.4 ms | 3.8 ms | 0 | 0.028 | 44.3 MB |
| T4 | task-detail | 14100 | 6.3 ms | 9.9 ms | 11.3 ms | 0 | 0.071 | 45.2 MB |
| T5 | task-list | 6054 | 16.1 ms | 21.4 ms | 26.1 ms | 0 | 0.166 | 45.4 MB |
| T6 | task-comments | 10772 | 8.4 ms | 13.5 ms | 14.6 ms | 0 | 0.095 | 45.5 MB |
| T7 | create-task | 6328 | 15.1 ms | 27.5 ms | 34.0 ms | 0 | 0.154 | 85.4 MB |
| T8 | me | 15426 | 6.5 ms | 9.3 ms | 11.7 ms | 0 | 0.066 | 88.4 MB |
| T9 | login | 15 | 693.8 ms | 885.6 ms | 1178.3 ms | 0 | 72.062 | 87.8 MB |
| T10 | update-task | 5055 | 20.1 ms | 30.3 ms | 36.7 ms | 0 | 0.200 | 101.1 MB |

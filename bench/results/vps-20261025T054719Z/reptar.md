## reptar

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 43228 | 2.3 ms | 2.6 ms | 2.9 ms | 0 | 0.023 | 40.0 MB |
| T1 | json | 41395 | 2.4 ms | 2.8 ms | 3.3 ms | 0 | 0.024 | 40.7 MB |
| T2 | router-params | 37665 | 2.6 ms | 3.4 ms | 3.6 ms | 0 | 0.026 | 40.8 MB |
| T3 | validate | 36105 | 2.8 ms | 3.4 ms | 3.8 ms | 0 | 0.028 | 45.0 MB |
| T4 | task-detail | 11802 | 7.9 ms | 12.9 ms | 16.9 ms | 0 | 0.085 | 45.7 MB |
| T5 | task-list | 5368 | 18.3 ms | 24.4 ms | 30.6 ms | 0 | 0.188 | 46.0 MB |
| T6 | task-comments | 10058 | 9.0 ms | 14.7 ms | 15.9 ms | 0 | 0.101 | 46.1 MB |
| T7 | create-task | 3521 | 29.7 ms | 38.3 ms | 41.0 ms | 0 | 0.252 | 70.5 MB |
| T8 | me | 12112 | 7.9 ms | 11.7 ms | 13.7 ms | 0 | 0.083 | 72.2 MB |
| T9 | login | 15 | 699.3 ms | 1054.3 ms | 1189.9 ms | 0 | 74.039 | 72.2 MB |
| T10 | update-task | 3653 | 26.0 ms | 38.3 ms | 42.3 ms | 0 | 0.262 | 77.7 MB |

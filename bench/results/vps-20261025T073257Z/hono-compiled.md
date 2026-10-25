## hono-compiled

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 49164 | 2.0 ms | 2.6 ms | 3.9 ms | 0 | 0.020 | 32.5 MB |
| T1 | json | 40153 | 2.5 ms | 2.7 ms | 3.1 ms | 0 | 0.025 | 35.6 MB |
| T2 | router-params | 35854 | 2.8 ms | 3.1 ms | 3.7 ms | 0 | 0.028 | 40.5 MB |
| T3 | validate | 17275 | 5.7 ms | 6.5 ms | 6.9 ms | 0 | 0.058 | 43.8 MB |
| T4 | task-detail | 17797 | 5.6 ms | 6.0 ms | 6.4 ms | 0 | 0.056 | 43.2 MB |
| T5 | task-list | 6752 | 14.7 ms | 16.3 ms | 21.5 ms | 0 | 0.151 | 45.4 MB |
| T6 | task-comments | 9956 | 10.0 ms | 10.5 ms | 11.1 ms | 0 | 0.100 | 43.7 MB |
| T7 | create-task | 4125 | 26.5 ms | 34.2 ms | 37.6 ms | 0 | 0.189 | 54.2 MB |
| T8 | me | 12159 | 8.1 ms | 10.1 ms | 12.4 ms | 0 | 0.082 | 54.4 MB |
| T9 | login | 22 | 441.4 ms | 711.0 ms | 802.1 ms | 0 | 46.661 | 46.2 MB |
| T10 | update-task | 5185 | 17.2 ms | 29.6 ms | 34.4 ms | 0 | 0.184 | 56.5 MB |

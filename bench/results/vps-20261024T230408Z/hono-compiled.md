## hono-compiled

| Tier | Scenario | Req/s | p50 | p95 | p99 | Errors | CPU ms/req | RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 48737 | 2.0 ms | 2.6 ms | 4.0 ms | 0 | 0.021 | 39.3 MB |
| T1 | json | 39796 | 2.5 ms | 2.8 ms | 3.3 ms | 0 | 0.026 | 45.1 MB |
| T2 | router-params | 35970 | 2.8 ms | 3.1 ms | 3.5 ms | 0 | 0.028 | 45.0 MB |
| T3 | validate | 17788 | 5.5 ms | 6.1 ms | 8.8 ms | 0 | 0.056 | 51.9 MB |
| T4 | task-detail | 18812 | 5.3 ms | 5.7 ms | 6.1 ms | 0 | 0.053 | 50.7 MB |
| T5 | task-list | 6722 | 14.3 ms | 16.2 ms | 18.1 ms | 0 | 0.150 | 52.5 MB |
| T6 | task-comments | 10142 | 9.8 ms | 10.4 ms | 10.9 ms | 0 | 0.098 | 50.6 MB |
| T7 | create-task | 4341 | 24.4 ms | 32.1 ms | 35.3 ms | 0 | 0.183 | 63.2 MB |
| T8 | me | 12178 | 8.0 ms | 10.2 ms | 12.4 ms | 0 | 0.088 | 62.5 MB |
| T9 | login | 22 | 444.2 ms | 709.6 ms | 887.1 ms | 0 | 46.517 | 54.6 MB |
| T10 | update-task | 5066 | 17.2 ms | 30.0 ms | 37.7 ms | 0 | 0.178 | 64.0 MB |

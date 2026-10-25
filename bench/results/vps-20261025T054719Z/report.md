# Reptar Bench — vps-20261025T054719Z

Generated: 2026-10-25T05:56:53.009Z

## Environment

```json
{
  "stamp": "20261025T054719Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       722Mi       1.9Gi       2.8Mi       1.5Gi       3.0Gi",
  "bun": "1.4.4",
  "dotnet": "10.0.402",
  "oha": "oha 1.16.0",
  "gitCommit": "unknown",
  "binarySizes": {
    "reptar": 11454808,
    "minimalApi": 10920632,
    "hono": 109365424
  },
  "config": {
    "connections": 100,
    "duration": 10,
    "warmup": 3,
    "reps": 3,
    "only": "T0,T1,T2,T3,T4,T5,T6,T7,T8,T9,T10"
  }
}
```

## Startup and binary size

| Implementation | Startup to first 200 | Binary size |
| :--- | ---: | ---: |
| Reptar | 356 ms | 10.9 MB |

## Throughput (req/s)

| Tier | Scenario | Reptar | Reptar vs Minimal API |
| :--- | :--- | ---: | ---: |
| T0 | plaintext | 43228 | n/a |
| T1 | json | 41395 | n/a |
| T2 | router-params | 37665 | n/a |
| T3 | validate | 36105 | n/a |
| T4 | task-detail | 11802 | n/a |
| T5 | task-list | 5368 | n/a |
| T6 | task-comments | 10058 | n/a |
| T7 | create-task | 3521 | n/a |
| T8 | me | 12112 | n/a |
| T9 | login | 15 | n/a |
| T10 | update-task | 3653 | n/a |

## Latency p50 (ms)

| Tier | Scenario | Reptar |
| :--- | :--- | ---: |
| T0 | plaintext | 2.3 |
| T1 | json | 2.4 |
| T2 | router-params | 2.6 |
| T3 | validate | 2.8 |
| T4 | task-detail | 7.9 |
| T5 | task-list | 18.3 |
| T6 | task-comments | 9.0 |
| T7 | create-task | 29.7 |
| T8 | me | 7.9 |
| T9 | login | 699.3 |
| T10 | update-task | 26.0 |

## Latency p99 (ms)

| Tier | Scenario | Reptar |
| :--- | :--- | ---: |
| T0 | plaintext | 2.9 |
| T1 | json | 3.3 |
| T2 | router-params | 3.6 |
| T3 | validate | 3.8 |
| T4 | task-detail | 16.9 |
| T5 | task-list | 30.6 |
| T6 | task-comments | 15.9 |
| T7 | create-task | 41.0 |
| T8 | me | 13.7 |
| T9 | login | 1189.9 |
| T10 | update-task | 42.3 |

## CPU and memory

| Tier | Scenario | Reptar CPU ms/req | Reptar RSS |
| :--- | :--- | ---: | ---: |
| T0 | plaintext | 0.023 | 40.0 MB |
| T1 | json | 0.024 | 40.7 MB |
| T2 | router-params | 0.026 | 40.8 MB |
| T3 | validate | 0.028 | 45.0 MB |
| T4 | task-detail | 0.085 | 45.7 MB |
| T5 | task-list | 0.188 | 46.0 MB |
| T6 | task-comments | 0.101 | 46.1 MB |
| T7 | create-task | 0.252 | 70.5 MB |
| T8 | me | 0.083 | 72.2 MB |
| T9 | login | 74.039 | 72.2 MB |
| T10 | update-task | 0.262 | 77.7 MB |

## T7 write-path breakdown (ms)
### Reptar

| Stage | Count | Avg | p50 | p95 | p99 | Max |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| auth | 111234 | 0.073 | 0.031 | 0.169 | 0.924 | 53.667 |
| writeGate | 111234 | 26.274 | 27.028 | 37.319 | 51.187 | 124.024 |
| insert | 111234 | 0.223 | 0.064 | 0.709 | 2.176 | 24.864 |
| assignee | 111234 | 0.070 | 0.030 | 0.065 | 1.590 | 29.884 |
| handler | 111234 | 26.581 | 27.569 | 37.687 | 51.729 | 127.525 |


## Headline
- **T0 plaintext**: Reptar 43228 req/s. p50 2.3 ms, p99 2.9 ms.
- **T1 json**: Reptar 41395 req/s. p50 2.4 ms, p99 3.3 ms.
- **T2 router-params**: Reptar 37665 req/s. p50 2.6 ms, p99 3.6 ms.
- **T3 validate**: Reptar 36105 req/s. p50 2.8 ms, p99 3.8 ms.
- **T4 task-detail**: Reptar 11802 req/s. p50 7.9 ms, p99 16.9 ms.
- **T5 task-list**: Reptar 5368 req/s. p50 18.3 ms, p99 30.6 ms.
- **T6 task-comments**: Reptar 10058 req/s. p50 9.0 ms, p99 15.9 ms.
- **T7 create-task**: Reptar 3521 req/s. p50 29.7 ms, p99 41.0 ms.
- **T8 me**: Reptar 12112 req/s. p50 7.9 ms, p99 13.7 ms.
- **T9 login**: Reptar 15 req/s. p50 699.3 ms, p99 1189.9 ms.
- **T10 update-task**: Reptar 3653 req/s. p50 26.0 ms, p99 42.3 ms.

## Mixed workload (T11)

| Implementation | Req/s | p50 | p95 | p99 | Requests | Statuses |
| :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| Reptar | 5665 | 16.3 ms | 30.3 ms | 66.0 ms | 85066 | 200:76570, 201:8496 |

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

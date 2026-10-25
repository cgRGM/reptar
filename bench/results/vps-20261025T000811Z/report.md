# Reptar Bench — vps-20261025T000811Z

Generated: 2026-10-25T00:23:14.640Z

## Environment

```json
{
  "stamp": "20261025T000811Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       715Mi       2.0Gi       2.8Mi       1.3Gi       3.0Gi",
  "bun": "1.4.4",
  "dotnet": "10.0.402",
  "oha": "oha 1.16.0",
  "gitCommit": "unknown",
  "binarySizes": {
    "reptar": 19216264,
    "minimalApi": 10920632,
    "hono": 109365424
  },
  "config": {
    "connections": 100,
    "duration": 10,
    "warmup": 3,
    "reps": 3,
    "only": "T0,T1,T2,T3,T4,T5,T6,T7,T8,T10"
  }
}
```

## Startup and binary size

| Implementation | Startup to first 200 | Binary size |
| :------------- | -------------------: | ----------: |
| Reptar         |               258 ms |     18.3 MB |

## Throughput (req/s)

| Tier | Scenario      | Reptar | Reptar vs Minimal API |
| :--- | :------------ | -----: | --------------------: |
| T0   | plaintext     |  42898 |                   n/a |
| T1   | json          |  41895 |                   n/a |
| T2   | router-params |  39331 |                   n/a |
| T3   | validate      |  34548 |                   n/a |
| T4   | task-detail   |  11645 |                   n/a |
| T5   | task-list     |   5304 |                   n/a |
| T6   | task-comments |   9658 |                   n/a |
| T7   | create-task   |   3640 |                   n/a |
| T8   | me            |  11709 |                   n/a |
| T10  | update-task   |   3738 |                   n/a |

## Latency p50 (ms)

| Tier | Scenario      | Reptar |
| :--- | :------------ | -----: |
| T0   | plaintext     |    2.3 |
| T1   | json          |    2.4 |
| T2   | router-params |    2.5 |
| T3   | validate      |    2.9 |
| T4   | task-detail   |    8.0 |
| T5   | task-list     |   18.5 |
| T6   | task-comments |    9.3 |
| T7   | create-task   |   28.8 |
| T8   | me            |    8.5 |
| T10  | update-task   |   25.5 |

## Latency p99 (ms)

| Tier | Scenario      | Reptar |
| :--- | :------------ | -----: |
| T0   | plaintext     |    3.0 |
| T1   | json          |    3.2 |
| T2   | router-params |    3.5 |
| T3   | validate      |    4.2 |
| T4   | task-detail   |   14.3 |
| T5   | task-list     |   29.4 |
| T6   | task-comments |   16.9 |
| T7   | create-task   |   40.8 |
| T8   | me            |   17.7 |
| T10  | update-task   |   41.8 |

## CPU and memory

| Tier | Scenario      | Reptar CPU ms/req | Reptar RSS |
| :--- | :------------ | ----------------: | ---------: |
| T0   | plaintext     |             0.023 |    45.8 MB |
| T1   | json          |             0.024 |    46.5 MB |
| T2   | router-params |             0.025 |    46.7 MB |
| T3   | validate      |             0.029 |    49.2 MB |
| T4   | task-detail   |             0.085 |    50.1 MB |
| T5   | task-list     |             0.189 |    50.2 MB |
| T6   | task-comments |             0.107 |    50.4 MB |
| T7   | create-task   |             0.235 |    58.7 MB |
| T8   | me            |             0.085 |    59.6 MB |
| T10  | update-task   |             0.263 |    58.8 MB |

## Headline

- **T0 plaintext**: Reptar 42898 req/s. p50 2.3 ms, p99 3.0 ms.
- **T1 json**: Reptar 41895 req/s. p50 2.4 ms, p99 3.2 ms.
- **T2 router-params**: Reptar 39331 req/s. p50 2.5 ms, p99 3.5 ms.
- **T3 validate**: Reptar 34548 req/s. p50 2.9 ms, p99 4.2 ms.
- **T4 task-detail**: Reptar 11645 req/s. p50 8.0 ms, p99 14.3 ms.
- **T5 task-list**: Reptar 5304 req/s. p50 18.5 ms, p99 29.4 ms.
- **T6 task-comments**: Reptar 9658 req/s. p50 9.3 ms, p99 16.9 ms.
- **T7 create-task**: Reptar 3640 req/s. p50 28.8 ms, p99 40.8 ms.
- **T8 me**: Reptar 11709 req/s. p50 8.5 ms, p99 17.7 ms.
- **T10 update-task**: Reptar 3738 req/s. p50 25.5 ms, p99 41.8 ms.

## Mixed workload (T11)

| Implementation | Req/s | p50 | p95 | p99 | Requests | Statuses |
| :-- | --: | --: | --: | --: | --: | :-- |
| Reptar | 5450 | 17.3 ms | 27.7 ms | 45.8 ms | 81841 | 200:73671, 201:8170 |

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

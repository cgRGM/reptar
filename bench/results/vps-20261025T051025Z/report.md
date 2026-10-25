# Reptar Bench — vps-20261025T051025Z

Generated: 2026-10-25T05:19:32.211Z

## Environment

```json
{
  "stamp": "20261025T051025Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       708Mi       2.1Gi       2.8Mi       1.3Gi       3.1Gi",
  "bun": "1.4.4",
  "dotnet": "10.0.402",
  "oha": "oha 1.16.0",
  "gitCommit": "unknown",
  "binarySizes": {
    "reptar": 19299624,
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
| :------------- | -------------------: | ----------: |
| Reptar         |               258 ms |     18.4 MB |

## Throughput (req/s)

| Tier | Scenario      | Reptar | Reptar vs Minimal API |
| :--- | :------------ | -----: | --------------------: |
| T0   | plaintext     |  43468 |                   n/a |
| T1   | json          |  42291 |                   n/a |
| T2   | router-params |  40351 |                   n/a |
| T3   | validate      |  35370 |                   n/a |
| T4   | task-detail   |  12107 |                   n/a |
| T5   | task-list     |   5316 |                   n/a |
| T6   | task-comments |   9846 |                   n/a |
| T7   | create-task   |   3316 |                   n/a |
| T8   | me            |  11720 |                   n/a |
| T9   | login         |     15 |                   n/a |
| T10  | update-task   |   3517 |                   n/a |

## Latency p50 (ms)

| Tier | Scenario      | Reptar |
| :--- | :------------ | -----: |
| T0   | plaintext     |    2.3 |
| T1   | json          |    2.4 |
| T2   | router-params |    2.5 |
| T3   | validate      |    2.9 |
| T4   | task-detail   |    7.7 |
| T5   | task-list     |   18.5 |
| T6   | task-comments |    9.2 |
| T7   | create-task   |   30.2 |
| T8   | me            |    7.7 |
| T9   | login         |  699.7 |
| T10  | update-task   |   27.5 |

## Latency p99 (ms)

| Tier | Scenario      | Reptar |
| :--- | :------------ | -----: |
| T0   | plaintext     |    3.0 |
| T1   | json          |    3.1 |
| T2   | router-params |    3.4 |
| T3   | validate      |    3.9 |
| T4   | task-detail   |   13.0 |
| T5   | task-list     |   30.8 |
| T6   | task-comments |   16.2 |
| T7   | create-task   |   63.8 |
| T8   | me            |   13.9 |
| T9   | login         | 1259.8 |
| T10  | update-task   |   43.2 |

## CPU and memory

| Tier | Scenario      | Reptar CPU ms/req | Reptar RSS |
| :--- | :------------ | ----------------: | ---------: |
| T0   | plaintext     |             0.023 |    47.3 MB |
| T1   | json          |             0.024 |    49.7 MB |
| T2   | router-params |             0.026 |    51.9 MB |
| T3   | validate      |             0.028 |    52.0 MB |
| T4   | task-detail   |             0.084 |    52.8 MB |
| T5   | task-list     |             0.190 |    53.0 MB |
| T6   | task-comments |             0.102 |    53.3 MB |
| T7   | create-task   |             0.261 |    75.2 MB |
| T8   | me            |             0.086 |    81.4 MB |
| T9   | login         |            74.406 |    81.5 MB |
| T10  | update-task   |             0.276 |    84.5 MB |

## T7 write-path breakdown (ms)

### Reptar

| Stage     |  Count |    Avg |    p50 |    p95 |    p99 |     Max |
| :-------- | -----: | -----: | -----: | -----: | -----: | ------: |
| auth      | 108611 |  0.088 |  0.036 |  0.142 |  1.214 |  91.271 |
| writeGate | 108611 | 27.073 | 27.857 | 38.471 | 55.414 | 255.124 |
| insert    | 108611 |  0.228 |  0.064 |  0.744 |  2.331 |  44.110 |
| assignee  | 108611 |  0.093 |  0.032 |  0.068 |  2.144 | 102.093 |
| handler   | 108611 | 27.408 | 28.332 | 38.825 | 56.062 | 305.592 |

## Headline

- **T0 plaintext**: Reptar 43468 req/s. p50 2.3 ms, p99 3.0 ms.
- **T1 json**: Reptar 42291 req/s. p50 2.4 ms, p99 3.1 ms.
- **T2 router-params**: Reptar 40351 req/s. p50 2.5 ms, p99 3.4 ms.
- **T3 validate**: Reptar 35370 req/s. p50 2.9 ms, p99 3.9 ms.
- **T4 task-detail**: Reptar 12107 req/s. p50 7.7 ms, p99 13.0 ms.
- **T5 task-list**: Reptar 5316 req/s. p50 18.5 ms, p99 30.8 ms.
- **T6 task-comments**: Reptar 9846 req/s. p50 9.2 ms, p99 16.2 ms.
- **T7 create-task**: Reptar 3316 req/s. p50 30.2 ms, p99 63.8 ms.
- **T8 me**: Reptar 11720 req/s. p50 7.7 ms, p99 13.9 ms.
- **T9 login**: Reptar 15 req/s. p50 699.7 ms, p99 1259.8 ms.
- **T10 update-task**: Reptar 3517 req/s. p50 27.5 ms, p99 43.2 ms.

## Mixed workload (T11)

_No mixed-workload results in this directory._

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

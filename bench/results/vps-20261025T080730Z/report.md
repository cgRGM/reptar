# Reptar Bench — vps-20261025T080730Z

Generated: 2026-10-25T09:10:10.317Z

## Environment

```json
{
  "stamp": "20261025T080730Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       737Mi       1.6Gi       2.8Mi       1.7Gi       3.0Gi",
  "bun": "1.4.4",
  "dotnet": "10.0.402",
  "oha": "oha 1.16.0",
  "gitCommit": "unknown",
  "binarySizes": {
    "reptar": 10946088,
    "minimalApi": 10920632,
    "hono": 109365424
  },
  "config": {
    "connections": 100,
    "duration": 10,
    "warmup": 3,
    "reps": 3,
    "only": ""
  }
}
```

## Startup and binary size

| Implementation  | Startup to first 200 | Binary size |
| :-------------- | -------------------: | ----------: |
| Reptar          |               124 ms |     10.4 MB |
| Minimal API     |               127 ms |     10.4 MB |
| Hono (JIT)      |               240 ms |    104.3 MB |
| Hono (compiled) |               134 ms |    104.3 MB |

## Throughput (req/s)

| Tier | Scenario | Reptar | Minimal API | Hono (JIT) | Hono (compiled) | Reptar vs Minimal API |
| :-- | :-- | --: | --: | --: | --: | --: |
| T0 | plaintext | 41877 | 41852 | 49280 | 49540 | +0% |
| T1 | json | 42239 | 41675 | 39289 | 39597 | +1% |
| T2 | router-params | 41355 | 40954 | 34401 | 36135 | +1% |
| T3 | validate | 36262 | 34427 | 18256 | 17807 | +5% |
| T4 | task-detail | 13765 | 12973 | 17899 | 17905 | +6% |
| T5 | task-list | 5945 | 5884 | 6917 | 6908 | +1% |
| T6 | task-comments | 10707 | 10126 | 9806 | 9944 | +6% |
| T7 | create-task | 6224 | 3628 | 4235 | 4314 | +72% |
| T8 | me | 15683 | 14765 | 12052 | 12026 | +6% |
| T9 | login | 14 | 15 | 22 | 22 | -1% |
| T10 | update-task | 4925 | 2150 | 5230 | 5248 | +129% |

## Latency p50 (ms)

| Tier | Scenario      | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :------------ | -----: | ----------: | ---------: | --------------: |
| T0   | plaintext     |    2.4 |         2.4 |        2.0 |             1.9 |
| T1   | json          |    2.4 |         2.4 |        2.5 |             2.5 |
| T2   | router-params |    2.4 |         2.4 |        2.8 |             2.7 |
| T3   | validate      |    2.8 |         3.0 |        5.4 |             5.5 |
| T4   | task-detail   |    6.7 |         7.9 |        5.5 |             5.5 |
| T5   | task-list     |   16.2 |        16.3 |       14.3 |            14.4 |
| T6   | task-comments |    8.3 |         9.9 |       10.0 |             9.9 |
| T7   | create-task   |   15.1 |        29.2 |       25.1 |            24.6 |
| T8   | me            |    6.4 |         6.8 |        8.1 |             8.1 |
| T9   | login         |  693.8 |       732.9 |      444.2 |           445.0 |
| T10  | update-task   |   20.5 |        46.2 |       17.1 |            17.2 |

## Latency p99 (ms)

| Tier | Scenario      | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :------------ | -----: | ----------: | ---------: | --------------: |
| T0   | plaintext     |    3.1 |         3.2 |        3.9 |             3.9 |
| T1   | json          |    3.1 |         3.1 |        3.5 |             3.3 |
| T2   | router-params |    3.3 |         3.2 |        4.6 |             3.4 |
| T3   | validate      |    3.7 |         3.9 |        6.4 |             7.4 |
| T4   | task-detail   |   11.6 |        13.0 |        7.7 |             6.3 |
| T5   | task-list     |   25.8 |        26.4 |       16.0 |            16.0 |
| T6   | task-comments |   14.8 |        15.4 |       12.0 |            11.6 |
| T7   | create-task   |   34.2 |        40.9 |       37.2 |            36.3 |
| T8   | me            |   11.3 |        14.2 |       12.7 |            12.5 |
| T9   | login         | 1249.9 |      1248.0 |      879.3 |           800.0 |
| T10  | update-task   |   38.8 |        57.5 |       32.3 |            32.0 |

## CPU and memory

| Tier | Scenario | Reptar CPU ms/req | Minimal API CPU ms/req | Hono (JIT) CPU ms/req | Hono (compiled) CPU ms/req | Reptar RSS | Minimal API RSS | Hono (JIT) RSS | Hono (compiled) RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 0.024 | 0.024 | 0.020 | 0.020 | 39.7 MB | 40.1 MB | 40.5 MB | 38.5 MB |
| T1 | json | 0.024 | 0.024 | 0.026 | 0.025 | 39.9 MB | 41.9 MB | 44.1 MB | 36.8 MB |
| T2 | router-params | 0.024 | 0.024 | 0.029 | 0.028 | 39.9 MB | 41.9 MB | 44.4 MB | 39.2 MB |
| T3 | validate | 0.028 | 0.029 | 0.055 | 0.057 | 44.6 MB | 42.0 MB | 51.0 MB | 47.9 MB |
| T4 | task-detail | 0.075 | 0.077 | 0.058 | 0.056 | 45.2 MB | 43.5 MB | 50.5 MB | 44.3 MB |
| T5 | task-list | 0.170 | 0.171 | 0.146 | 0.145 | 45.5 MB | 42.9 MB | 52.4 MB | 46.2 MB |
| T6 | task-comments | 0.094 | 0.099 | 0.104 | 0.102 | 45.6 MB | 45.6 MB | 50.8 MB | 45.2 MB |
| T7 | create-task | 0.160 | 0.235 | 0.187 | 0.186 | 82.9 MB | 52.8 MB | 59.2 MB | 53.3 MB |
| T8 | me | 0.064 | 0.069 | 0.083 | 0.084 | 93.0 MB | 53.7 MB | 61.1 MB | 55.9 MB |
| T9 | login | 74.198 | 76.463 | 47.116 | 47.547 | 92.7 MB | 53.7 MB | 53.5 MB | 47.7 MB |
| T10 | update-task | 0.205 | 0.362 | 0.173 | 0.173 | 103.2 MB | 81.2 MB | 62.7 MB | 56.9 MB |

## T7 write-path breakdown (ms)

### Reptar

| Stage     |  Count |    Avg |    p50 |    p95 |    p99 |    Max |
| :-------- | -----: | -----: | -----: | -----: | -----: | -----: |
| auth      | 196241 |  0.098 |  0.027 |  0.072 |  2.873 | 54.463 |
| writeGate | 196241 |  2.784 |  2.023 |  7.764 | 17.017 | 65.295 |
| insert    | 196241 |  0.143 |  0.114 |  0.287 |  0.660 | 27.412 |
| assignee  | 196241 |  0.066 |  0.015 |  0.044 |  0.608 | 23.100 |
| handler   | 196241 | 11.541 | 11.243 | 23.777 | 31.921 | 99.871 |

## Headline

- **T0 plaintext**: Reptar 41877 req/s, +0% vs Minimal API, -15% vs Hono (JIT). p50 2.4 ms, p99 3.1 ms.
- **T1 json**: Reptar 42239 req/s, +1% vs Minimal API, +8% vs Hono (JIT). p50 2.4 ms, p99 3.1 ms.
- **T2 router-params**: Reptar 41355 req/s, +1% vs Minimal API, +20% vs Hono (JIT). p50 2.4 ms, p99 3.3 ms.
- **T3 validate**: Reptar 36262 req/s, +5% vs Minimal API, +99% vs Hono (JIT). p50 2.8 ms, p99 3.7 ms.
- **T4 task-detail**: Reptar 13765 req/s, +6% vs Minimal API, -23% vs Hono (JIT). p50 6.7 ms, p99 11.6 ms.
- **T5 task-list**: Reptar 5945 req/s, +1% vs Minimal API, -14% vs Hono (JIT). p50 16.2 ms, p99 25.8 ms.
- **T6 task-comments**: Reptar 10707 req/s, +6% vs Minimal API, +9% vs Hono (JIT). p50 8.3 ms, p99 14.8 ms.
- **T7 create-task**: Reptar 6224 req/s, +72% vs Minimal API, +47% vs Hono (JIT). p50 15.1 ms, p99 34.2 ms.
- **T8 me**: Reptar 15683 req/s, +6% vs Minimal API, +30% vs Hono (JIT). p50 6.4 ms, p99 11.3 ms.
- **T9 login**: Reptar 14 req/s, -1% vs Minimal API, -34% vs Hono (JIT). p50 693.8 ms, p99 1249.9 ms.
- **T10 update-task**: Reptar 4925 req/s, +129% vs Minimal API, -6% vs Hono (JIT). p50 20.5 ms, p99 38.8 ms.

## Mixed workload (T11)

| Implementation | Req/s | p50 | p95 | p99 | Requests | Statuses |
| :-- | --: | --: | --: | --: | --: | :-- |
| Reptar | 6143 | 16.0 ms | 21.7 ms | 32.5 ms | 92239 | 200:83172, 201:9067 |
| Minimal API | 6242 | 15.8 ms | 22.0 ms | 31.9 ms | 93757 | 200:84336, 201:9421 |
| Hono (JIT) | 7272 | 12.3 ms | 23.6 ms | 37.6 ms | 109188 | 200:98373, 201:10815 |
| Hono (compiled) | 7218 | 12.4 ms | 23.3 ms | 42.2 ms | 108396 | 200:97517, 201:10879 |

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

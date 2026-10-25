# Reptar Bench — vps-20261024T223601Z

Generated: 2026-10-24T23:04:10.532Z

## Environment

```json
{
  "stamp": "20261024T223601Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       701Mi       1.1Gi       2.8Mi       2.2Gi       3.1Gi",
  "bun": "1.4.4",
  "dotnet": "10.0.402",
  "oha": "oha 1.16.0",
  "gitCommit": "unknown",
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
| Reptar          |               n/a ms |         n/a |
| Minimal API     |               n/a ms |         n/a |
| Hono (JIT)      |               n/a ms |         n/a |
| Hono (compiled) |               n/a ms |         n/a |

## Throughput (req/s)

| Tier | Scenario | Reptar | Minimal API | Hono (JIT) | Hono (compiled) | Reptar vs Minimal API |
| :-- | :-- | --: | --: | --: | --: | --: |
| T0 | plaintext | 29224 | 37612 | 48814 | 48798 | -22% |
| T1 | json | 35065 | 41052 | 38939 | 39726 | -15% |
| T2 | router-params | 36540 | 41072 | 34023 | 32693 | -11% |
| T3 | validate | 29483 | 33234 | 17803 | 18358 | -11% |
| T4 | task-detail | 11290 | 13727 | 17849 | 17403 | -18% |
| T5 | task-list | 4906 | 6003 | 6889 | 6899 | -18% |
| T6 | task-comments | 6800 | 10496 | 9810 | 9958 | -35% |
| T7 | create-task | 3090 | 296 | 296 | 297 | +943% |
| T8 | me | 10370 | 15617 | 12208 | 11905 | -34% |
| T9 | login | 20 | 20 | 24 | 22 | +0% |
| T10 | update-task | 3310 | 291 | 304 | 303 | +1039% |

## Latency p50 (ms)

| Tier | Scenario      | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :------------ | -----: | ----------: | ---------: | --------------: |
| T0   | plaintext     |    2.9 |         2.6 |        2.0 |             2.0 |
| T1   | json          |    2.9 |         2.5 |        2.5 |             2.5 |
| T2   | router-params |    2.8 |         2.5 |        2.8 |             2.9 |
| T3   | validate      |    3.6 |         3.1 |        5.5 |             5.4 |
| T4   | task-detail   |    8.6 |         7.1 |        5.5 |             5.6 |
| T5   | task-list     |   19.7 |        15.9 |       14.5 |            14.4 |
| T6   | task-comments |   14.5 |         9.5 |       10.0 |             9.9 |
| T7   | create-task   |   33.0 |       338.3 |      336.7 |           335.4 |
| T8   | me            |    9.5 |         6.5 |        8.0 |             8.1 |
| T9   | login         | 8067.8 |      8054.3 |     3186.5 |           449.1 |
| T10  | update-task   |   29.3 |       343.7 |      327.7 |           329.0 |

## Latency p99 (ms)

| Tier | Scenario      | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :------------ | -----: | ----------: | ---------: | --------------: |
| T0   | plaintext     |    8.1 |         3.6 |        4.0 |             4.0 |
| T1   | json          |    5.4 |         3.2 |        3.4 |             3.2 |
| T2   | router-params |    3.8 |         3.2 |        6.0 |             3.8 |
| T3   | validate      |    4.9 |         4.6 |        7.5 |             6.3 |
| T4   | task-detail   |   13.3 |        11.6 |        7.7 |             7.2 |
| T5   | task-list     |   34.7 |        26.4 |       16.2 |            16.8 |
| T6   | task-comments |   27.4 |        14.4 |       13.3 |            11.9 |
| T7   | create-task   |   49.3 |       565.1 |      601.9 |           596.9 |
| T8   | me            |   16.4 |        11.6 |       12.9 |            12.8 |
| T9   | login         | 8068.3 |      8054.8 |     8739.3 |           857.1 |
| T10  | update-task   |   55.0 |       595.0 |      590.1 |           580.2 |

## CPU and memory

| Tier | Scenario | Reptar CPU ms/req | Minimal API CPU ms/req | Hono (JIT) CPU ms/req | Hono (compiled) CPU ms/req | Reptar RSS | Minimal API RSS | Hono (JIT) RSS | Hono (compiled) RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 0.028 | 0.026 | 0.020 | 0.021 | 40.2 MB | 39.8 MB | 43.9 MB | 33.7 MB |
| T1 | json | 0.028 | 0.025 | 0.026 | 0.025 | 40.7 MB | 40.6 MB | 44.3 MB | 37.1 MB |
| T2 | router-params | 0.027 | 0.024 | 0.029 | 0.030 | 41.9 MB | 40.8 MB | 48.8 MB | 37.9 MB |
| T3 | validate | 0.034 | 0.030 | 0.056 | 0.055 | 43.4 MB | 40.9 MB | 51.0 MB | 44.8 MB |
| T4 | task-detail | 0.089 | 0.075 | 0.057 | 0.058 | 44.7 MB | 43.8 MB | 51.8 MB | 45.1 MB |
| T5 | task-list | 0.208 | 0.168 | 0.145 | 0.149 | 46.2 MB | 42.4 MB | 53.0 MB | 46.4 MB |
| T6 | task-comments | 0.134 | 0.097 | 0.105 | 0.100 | 47.0 MB | 45.9 MB | 52.9 MB | 46.4 MB |
| T7 | create-task | 0.285 | 0.714 | 0.422 | 0.459 | 53.6 MB | 50.5 MB | 56.9 MB | 49.6 MB |
| T8 | me | 0.099 | 0.065 | 0.084 | 0.085 | 52.9 MB | 52.8 MB | 61.7 MB | 54.9 MB |
| T9 | login | 137.260 | 100.569 | 69.103 | 49.587 | 52.9 MB | 52.4 MB | 57.0 MB | 46.2 MB |
| T10 | update-task | 0.304 | 1.379 | 0.748 | 0.452 | 53.7 MB | 82.8 MB | 60.8 MB | 50.0 MB |

## Headline

- **T0 plaintext**: Reptar 29224 req/s, -22% vs Minimal API, -40% vs Hono (JIT). p50 2.9 ms, p99 8.1 ms.
- **T1 json**: Reptar 35065 req/s, -15% vs Minimal API, -10% vs Hono (JIT). p50 2.9 ms, p99 5.4 ms.
- **T2 router-params**: Reptar 36540 req/s, -11% vs Minimal API, +7% vs Hono (JIT). p50 2.8 ms, p99 3.8 ms.
- **T3 validate**: Reptar 29483 req/s, -11% vs Minimal API, +66% vs Hono (JIT). p50 3.6 ms, p99 4.9 ms.
- **T4 task-detail**: Reptar 11290 req/s, -18% vs Minimal API, -37% vs Hono (JIT). p50 8.6 ms, p99 13.3 ms.
- **T5 task-list**: Reptar 4906 req/s, -18% vs Minimal API, -29% vs Hono (JIT). p50 19.7 ms, p99 34.7 ms.
- **T6 task-comments**: Reptar 6800 req/s, -35% vs Minimal API, -31% vs Hono (JIT). p50 14.5 ms, p99 27.4 ms.
- **T7 create-task**: Reptar 3090 req/s, +943% vs Minimal API, +942% vs Hono (JIT). p50 33.0 ms, p99 49.3 ms.
- **T8 me**: Reptar 10370 req/s, -34% vs Minimal API, -15% vs Hono (JIT). p50 9.5 ms, p99 16.4 ms.
- **T9 login**: Reptar 20 req/s, +0% vs Minimal API, -17% vs Hono (JIT). p50 8067.8 ms, p99 8068.3 ms.
- **T10 update-task**: Reptar 3310 req/s, +1039% vs Minimal API, +987% vs Hono (JIT). p50 29.3 ms, p99 55.0 ms.

## Mixed workload (T11)

_No mixed-workload results in this directory._

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

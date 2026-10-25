# Reptar Bench — vps-20261024T230408Z

Generated: 2026-10-25T00:22:13.511Z

## Environment

```json
{
  "stamp": "20261024T230408Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       738Mi       1.3Gi       2.8Mi       2.0Gi       3.0Gi",
  "bun": "1.4.4",
  "dotnet": "10.0.402",
  "oha": "oha 1.16.0",
  "gitCommit": "unknown",
  "binarySizes": {
    "reptar": 11142184,
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
| Reptar          |               372 ms |     10.6 MB |
| Minimal API     |               570 ms |     10.4 MB |
| Hono (JIT)      |               130 ms |    104.3 MB |
| Hono (compiled) |               131 ms |    104.3 MB |

## Throughput (req/s)

| Tier | Scenario | Reptar | Minimal API | Hono (JIT) | Hono (compiled) | Reptar vs Minimal API |
| :-- | :-- | --: | --: | --: | --: | --: |
| T0 | plaintext | 39667 | 41340 | 48764 | 48737 | -4% |
| T1 | json | 35585 | 41030 | 39315 | 39796 | -13% |
| T2 | router-params | 36777 | 41009 | 35877 | 35970 | -10% |
| T3 | validate | 29753 | 33236 | 17504 | 17788 | -10% |
| T4 | task-detail | 11521 | 14163 | 18555 | 18812 | -19% |
| T5 | task-list | 5035 | 9864 | 6785 | 6722 | -49% |
| T6 | task-comments | 9313 | 10263 | 10101 | 10142 | -9% |
| T7 | create-task | 3282 | 7478 | 4415 | 4341 | -56% |
| T8 | me | 10463 | 15449 | 12069 | 12178 | -32% |
| T9 | login | 15 | 15 | 22 | 22 | -0% |
| T10 | update-task | 3459 | 2182 | 5273 | 5066 | +59% |

## Latency p50 (ms)

| Tier | Scenario      | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :------------ | -----: | ----------: | ---------: | --------------: |
| T0   | plaintext     |    2.6 |         2.4 |        2.0 |             2.0 |
| T1   | json          |    2.9 |         2.4 |        2.5 |             2.5 |
| T2   | router-params |    2.8 |         2.4 |        2.8 |             2.8 |
| T3   | validate      |    3.5 |         3.1 |        5.6 |             5.5 |
| T4   | task-detail   |    8.7 |         7.2 |        5.3 |             5.3 |
| T5   | task-list     |   18.4 |         9.8 |       14.7 |            14.3 |
| T6   | task-comments |   10.7 |         9.7 |        9.8 |             9.8 |
| T7   | create-task   |   30.3 |        12.4 |       24.2 |            24.4 |
| T8   | me            |    9.7 |         6.6 |        8.1 |             8.0 |
| T9   | login         |  700.0 |       699.8 |      443.6 |           444.2 |
| T10  | update-task   |   27.8 |        45.4 |       17.0 |            17.2 |

## Latency p99 (ms)

| Tier | Scenario      | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :------------ | -----: | ----------: | ---------: | --------------: |
| T0   | plaintext     |    3.5 |         3.3 |        4.0 |             4.0 |
| T1   | json          |    3.9 |         3.2 |        3.3 |             3.3 |
| T2   | router-params |    3.8 |         3.2 |        3.7 |             3.5 |
| T3   | validate      |    4.8 |         4.1 |        8.1 |             8.8 |
| T4   | task-detail   |   12.9 |        12.8 |        6.3 |             6.1 |
| T5   | task-list     |   32.9 |        16.7 |       16.7 |            18.1 |
| T6   | task-comments |   16.1 |        16.4 |       11.2 |            10.9 |
| T7   | create-task   |   43.6 |        21.0 |       34.7 |            35.3 |
| T8   | me            |   14.4 |        10.1 |       12.7 |            12.4 |
| T9   | login         | 1189.8 |      1064.5 |      800.8 |           887.1 |
| T10  | update-task   |   44.1 |        56.0 |       32.3 |            37.7 |

## CPU and memory

| Tier | Scenario | Reptar CPU ms/req | Minimal API CPU ms/req | Hono (JIT) CPU ms/req | Hono (compiled) CPU ms/req | Reptar RSS | Minimal API RSS | Hono (JIT) RSS | Hono (compiled) RSS |
| :-- | :-- | --: | --: | --: | --: | --: | --: | --: | --: |
| T0 | plaintext | 0.025 | 0.024 | 0.021 | 0.021 | 39.7 MB | 40.5 MB | 45.0 MB | 39.3 MB |
| T1 | json | 0.028 | 0.025 | 0.025 | 0.026 | 41.2 MB | 42.2 MB | 49.3 MB | 45.1 MB |
| T2 | router-params | 0.027 | 0.024 | 0.028 | 0.028 | 42.1 MB | 42.2 MB | 44.9 MB | 45.0 MB |
| T3 | validate | 0.034 | 0.031 | 0.059 | 0.056 | 42.5 MB | 42.5 MB | 54.0 MB | 51.9 MB |
| T4 | task-detail | 0.088 | 0.071 | 0.054 | 0.053 | 44.1 MB | 44.4 MB | 52.5 MB | 50.7 MB |
| T5 | task-list | 0.199 | 0.101 | 0.149 | 0.150 | 46.3 MB | 43.8 MB | 54.3 MB | 52.5 MB |
| T6 | task-comments | 0.111 | 0.099 | 0.101 | 0.098 | 45.4 MB | 43.8 MB | 52.5 MB | 50.6 MB |
| T7 | create-task | 0.264 | 0.133 | 0.184 | 0.183 | 53.4 MB | 47.2 MB | 63.4 MB | 63.2 MB |
| T8 | me | 0.097 | 0.066 | 0.085 | 0.088 | 53.4 MB | 48.2 MB | 62.9 MB | 62.5 MB |
| T9 | login | 76.633 | 73.833 | 46.880 | 46.517 | 53.5 MB | 48.2 MB | 55.8 MB | 54.6 MB |
| T10 | update-task | 0.278 | 0.350 | 0.180 | 0.178 | 52.8 MB | 74.7 MB | 64.0 MB | 64.0 MB |

## Headline

- **T0 plaintext**: Reptar 39667 req/s, -4% vs Minimal API, -19% vs Hono (JIT). p50 2.6 ms, p99 3.5 ms.
- **T1 json**: Reptar 35585 req/s, -13% vs Minimal API, -9% vs Hono (JIT). p50 2.9 ms, p99 3.9 ms.
- **T2 router-params**: Reptar 36777 req/s, -10% vs Minimal API, +3% vs Hono (JIT). p50 2.8 ms, p99 3.8 ms.
- **T3 validate**: Reptar 29753 req/s, -10% vs Minimal API, +70% vs Hono (JIT). p50 3.5 ms, p99 4.8 ms.
- **T4 task-detail**: Reptar 11521 req/s, -19% vs Minimal API, -38% vs Hono (JIT). p50 8.7 ms, p99 12.9 ms.
- **T5 task-list**: Reptar 5035 req/s, -49% vs Minimal API, -26% vs Hono (JIT). p50 18.4 ms, p99 32.9 ms.
- **T6 task-comments**: Reptar 9313 req/s, -9% vs Minimal API, -8% vs Hono (JIT). p50 10.7 ms, p99 16.1 ms.
- **T7 create-task**: Reptar 3282 req/s, -56% vs Minimal API, -26% vs Hono (JIT). p50 30.3 ms, p99 43.6 ms.
- **T8 me**: Reptar 10463 req/s, -32% vs Minimal API, -13% vs Hono (JIT). p50 9.7 ms, p99 14.4 ms.
- **T9 login**: Reptar 15 req/s, -0% vs Minimal API, -34% vs Hono (JIT). p50 700.0 ms, p99 1189.8 ms.
- **T10 update-task**: Reptar 3459 req/s, +59% vs Minimal API, -34% vs Hono (JIT). p50 27.8 ms, p99 44.1 ms.

## Mixed workload (T11)

| Implementation | Req/s | p50 | p95 | p99 | Requests | Statuses |
| :-- | --: | --: | --: | --: | --: | :-- |
| Reptar | 5778 | 14.8 ms | 35.3 ms | 56.0 ms | 86756 | 200:78069, 201:8687 |
| Minimal API | 5281 | 16.7 ms | 28.1 ms | 47.5 ms | 79300 | 200:71432, 201:7868 |
| Hono (JIT) | 7742 | 12.1 ms | 22.3 ms | 27.5 ms | 116326 | 200:104724, 201:11602 |
| Hono (compiled) | 7654 | 12.2 ms | 22.8 ms | 27.9 ms | 114912 | 200:103160, 201:11752 |

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

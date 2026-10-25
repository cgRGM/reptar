# Reptar Bench — vps-20261025T001815Z

Generated: 2026-10-25T00:21:13.105Z

## Environment

```json
{
  "stamp": "20261025T001815Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       740Mi       2.1Gi       2.8Mi       1.3Gi       3.0Gi",
  "bun": "1.4.4",
  "dotnet": "10.0.402",
  "oha": "oha 1.16.0",
  "gitCommit": "unknown",
  "binarySizes": {
    "reptar": 19245304,
    "minimalApi": 10920632,
    "hono": 109365424
  },
  "config": {
    "connections": 100,
    "duration": 10,
    "warmup": 3,
    "reps": 3,
    "only": "T4,T5,T6"
  }
}
```

## Startup and binary size

| Implementation | Startup to first 200 | Binary size |
| :--- | ---: | ---: |
| Reptar | 258 ms | 18.4 MB |

## Throughput (req/s)

| Tier | Scenario | Reptar | Reptar vs Minimal API |
| :--- | :--- | ---: | ---: |
| T4 | task-detail | 39214 | n/a |
| T5 | task-list | 29874 | n/a |
| T6 | task-comments | 35485 | n/a |

## Latency p50 (ms)

| Tier | Scenario | Reptar |
| :--- | :--- | ---: |
| T4 | task-detail | 2.6 |
| T5 | task-list | 3.3 |
| T6 | task-comments | 2.7 |

## Latency p99 (ms)

| Tier | Scenario | Reptar |
| :--- | :--- | ---: |
| T4 | task-detail | 3.5 |
| T5 | task-list | 4.2 |
| T6 | task-comments | 3.8 |

## CPU and memory

| Tier | Scenario | Reptar CPU ms/req | Reptar RSS |
| :--- | :--- | ---: | ---: |
| T4 | task-detail | 0.025 | 48.5 MB |
| T5 | task-list | 0.033 | 48.9 MB |
| T6 | task-comments | 0.028 | 49.0 MB |

## Headline
- **T4 task-detail**: Reptar 39214 req/s. p50 2.6 ms, p99 3.5 ms.
- **T5 task-list**: Reptar 29874 req/s. p50 3.3 ms, p99 4.2 ms.
- **T6 task-comments**: Reptar 35485 req/s. p50 2.7 ms, p99 3.8 ms.

## Mixed workload (T11)

_No mixed-workload results in this directory._

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

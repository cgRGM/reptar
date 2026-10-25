# Reptar Bench — vps-20261025T073257Z

Generated: 2026-10-25T08:01:59.457Z

## Environment

```json
{
  "stamp": "20261025T073257Z",
  "hostname": "ip-172-26-11-201",
  "kernel": "Linux 7.0.0-1012-aws x86_64",
  "cpu": "CPU(s):                                  2|Model name:                              Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz|Thread(s) per core:                      2|Core(s) per socket:                      1|Socket(s):                               1|",
  "memory": "Mem:           3.7Gi       728Mi       1.9Gi       2.8Mi       1.5Gi       3.0Gi",
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

| Implementation | Startup to first 200 | Binary size |
| :--- | ---: | ---: |
| Reptar | 504 ms | 10.4 MB |
| Minimal API | 236 ms | 10.4 MB |
| Hono (JIT) | 248 ms | 104.3 MB |
| Hono (compiled) | 366 ms | 104.3 MB |

## Throughput (req/s)

| Tier | Scenario | Reptar | Minimal API | Hono (JIT) | Hono (compiled) | Reptar vs Minimal API |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 43169 | 41133 | 48525 | 49164 | +5% |
| T1 | json | 42300 | 41704 | 39463 | 40153 | +1% |
| T2 | router-params | 42038 | 40996 | 36122 | 35854 | +3% |
| T3 | validate | 35731 | 33763 | 17629 | 17275 | +6% |
| T4 | task-detail | 14100 | 13162 | 18023 | 17797 | +7% |
| T5 | task-list | 6054 | 5859 | 6817 | 6752 | +3% |
| T6 | task-comments | 10772 | 9994 | 9950 | 9956 | +8% |
| T7 | create-task | 6328 | 3515 | 4264 | 4125 | +80% |
| T8 | me | 15426 | 13989 | 12019 | 12159 | +10% |
| T9 | login | 15 | 15 | 22 | 22 | -1% |
| T10 | update-task | 5055 | 2145 | 5281 | 5185 | +136% |

## Latency p50 (ms)

| Tier | Scenario | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :--- | ---: | ---: | ---: | ---: |
| T0 | plaintext | 2.3 | 2.4 | 2.0 | 2.0 |
| T1 | json | 2.4 | 2.4 | 2.5 | 2.5 |
| T2 | router-params | 2.4 | 2.5 | 2.7 | 2.8 |
| T3 | validate | 2.8 | 3.0 | 5.6 | 5.7 |
| T4 | task-detail | 6.3 | 7.7 | 5.5 | 5.6 |
| T5 | task-list | 16.1 | 16.1 | 14.6 | 14.7 |
| T6 | task-comments | 8.4 | 10.0 | 9.9 | 10.0 |
| T7 | create-task | 15.1 | 30.1 | 25.1 | 26.5 |
| T8 | me | 6.5 | 7.2 | 8.1 | 8.1 |
| T9 | login | 693.8 | 693.9 | 442.3 | 441.4 |
| T10 | update-task | 20.1 | 46.1 | 17.0 | 17.2 |

## Latency p99 (ms)

| Tier | Scenario | Reptar | Minimal API | Hono (JIT) | Hono (compiled) |
| :--- | :--- | ---: | ---: | ---: | ---: |
| T0 | plaintext | 3.0 | 3.3 | 4.0 | 3.9 |
| T1 | json | 3.1 | 3.1 | 3.3 | 3.1 |
| T2 | router-params | 3.3 | 3.1 | 3.8 | 3.7 |
| T3 | validate | 3.8 | 4.0 | 8.0 | 6.9 |
| T4 | task-detail | 11.3 | 13.2 | 6.5 | 6.4 |
| T5 | task-list | 26.1 | 26.7 | 17.3 | 21.5 |
| T6 | task-comments | 14.6 | 15.7 | 11.3 | 11.1 |
| T7 | create-task | 34.0 | 41.0 | 36.6 | 37.6 |
| T8 | me | 11.7 | 12.3 | 12.9 | 12.4 |
| T9 | login | 1178.3 | 1043.0 | 890.5 | 802.1 |
| T10 | update-task | 36.7 | 57.5 | 33.1 | 34.4 |

## CPU and memory

| Tier | Scenario | Reptar CPU ms/req | Minimal API CPU ms/req | Hono (JIT) CPU ms/req | Hono (compiled) CPU ms/req | Reptar RSS | Minimal API RSS | Hono (JIT) RSS | Hono (compiled) RSS |
| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| T0 | plaintext | 0.023 | 0.025 | 0.020 | 0.020 | 39.3 MB | 40.0 MB | 47.2 MB | 32.5 MB |
| T1 | json | 0.024 | 0.024 | 0.025 | 0.025 | 41.8 MB | 41.0 MB | 43.4 MB | 35.6 MB |
| T2 | router-params | 0.024 | 0.024 | 0.028 | 0.028 | 43.7 MB | 41.0 MB | 48.3 MB | 40.5 MB |
| T3 | validate | 0.028 | 0.030 | 0.058 | 0.058 | 44.3 MB | 41.1 MB | 51.3 MB | 43.8 MB |
| T4 | task-detail | 0.071 | 0.077 | 0.055 | 0.056 | 45.2 MB | 43.1 MB | 50.0 MB | 43.2 MB |
| T5 | task-list | 0.166 | 0.175 | 0.154 | 0.151 | 45.4 MB | 43.4 MB | 52.1 MB | 45.4 MB |
| T6 | task-comments | 0.095 | 0.101 | 0.101 | 0.100 | 45.5 MB | 44.3 MB | 50.6 MB | 43.7 MB |
| T7 | create-task | 0.154 | 0.236 | 0.189 | 0.189 | 85.4 MB | 50.8 MB | 61.2 MB | 54.2 MB |
| T8 | me | 0.066 | 0.072 | 0.087 | 0.082 | 88.4 MB | 51.8 MB | 60.3 MB | 54.4 MB |
| T9 | login | 72.062 | 72.560 | 46.517 | 46.661 | 87.8 MB | 51.8 MB | 53.3 MB | 46.2 MB |
| T10 | update-task | 0.200 | 0.369 | 0.176 | 0.184 | 101.1 MB | 73.3 MB | 62.5 MB | 56.5 MB |

## T7 write-path breakdown (ms)
### Reptar

| Stage | Count | Avg | p50 | p95 | p99 | Max |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: |
| auth | 203373 | 0.094 | 0.027 | 0.069 | 2.791 | 19.950 |
| writeGate | 203336 | 2.501 | 1.726 | 8.178 | 15.637 | 25.819 |
| insert | 203336 | 0.136 | 0.104 | 0.255 | 0.694 | 18.337 |
| assignee | 203336 | 0.059 | 0.015 | 0.042 | 0.507 | 17.066 |
| handler | 203336 | 10.304 | 10.310 | 21.993 | 28.886 | 42.673 |


## Headline
- **T0 plaintext**: Reptar 43169 req/s, +5% vs Minimal API, -11% vs Hono (JIT). p50 2.3 ms, p99 3.0 ms.
- **T1 json**: Reptar 42300 req/s, +1% vs Minimal API, +7% vs Hono (JIT). p50 2.4 ms, p99 3.1 ms.
- **T2 router-params**: Reptar 42038 req/s, +3% vs Minimal API, +16% vs Hono (JIT). p50 2.4 ms, p99 3.3 ms.
- **T3 validate**: Reptar 35731 req/s, +6% vs Minimal API, +103% vs Hono (JIT). p50 2.8 ms, p99 3.8 ms.
- **T4 task-detail**: Reptar 14100 req/s, +7% vs Minimal API, -22% vs Hono (JIT). p50 6.3 ms, p99 11.3 ms.
- **T5 task-list**: Reptar 6054 req/s, +3% vs Minimal API, -11% vs Hono (JIT). p50 16.1 ms, p99 26.1 ms.
- **T6 task-comments**: Reptar 10772 req/s, +8% vs Minimal API, +8% vs Hono (JIT). p50 8.4 ms, p99 14.6 ms.
- **T7 create-task**: Reptar 6328 req/s, +80% vs Minimal API, +48% vs Hono (JIT). p50 15.1 ms, p99 34.0 ms.
- **T8 me**: Reptar 15426 req/s, +10% vs Minimal API, +28% vs Hono (JIT). p50 6.5 ms, p99 11.7 ms.
- **T9 login**: Reptar 15 req/s, -1% vs Minimal API, -33% vs Hono (JIT). p50 693.8 ms, p99 1178.3 ms.
- **T10 update-task**: Reptar 5055 req/s, +136% vs Minimal API, -4% vs Hono (JIT). p50 20.1 ms, p99 36.7 ms.

## Mixed workload (T11)

| Implementation | Req/s | p50 | p95 | p99 | Requests | Statuses |
| :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| Reptar | 6449 | 15.6 ms | 20.5 ms | 25.0 ms | 96788 | 200:87163, 201:9582, 500:43 |
| Minimal API | 6216 | 15.7 ms | 22.0 ms | 31.4 ms | 93313 | 200:84152, 201:9161 |
| Hono (JIT) | 7675 | 12.1 ms | 23.3 ms | 29.2 ms | 115242 | 200:103807, 201:11435 |
| Hono (compiled) | 7529 | 12.3 ms | 24.1 ms | 31.1 ms | 113038 | 200:101828, 201:11210 |

## Notes

- Server and load generator run on the same Lightsail box (2 vCPU = 2 SMT threads on one physical core). The server is pinned to CPU 0 and `oha` to CPU 1, so these are **relative** comparisons, not absolute capacity numbers.
- Logging and Reptar telemetry are off for the main run. The existing RealWorld measurements showed default logging roughly halves in-memory throughput, so leaving it on would measure stdout, not the framework.
- Reptar uses a source-generated `JsonSerializerContext` (`BenchJsonCodec`) because the default reflection codec trims nested DTO properties under Native AOT. The .NET Minimal API control also uses source-generated JSON.
- `Rule<T>` and `ctx.Json<T>` with the default codec are not AOT-safe today; the benchmark app uses manual validation and a source-gen codec. That is a finding, not a hidden advantage.
- Hono runs both the JIT path (`bun run src/index.ts`) and the compiled bundle (`bun build --compile`). The compiled bundle is ~105 MB because it embeds the Bun runtime; it is not AOT.

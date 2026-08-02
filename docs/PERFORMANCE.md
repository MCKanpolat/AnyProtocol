# Performance testing

AnyProtocol separates deterministic microbenchmarks from broker-backed load tests. Results are comparative diagnostics for a controlled environment, not universal capacity claims.

## What is measured

BenchmarkDotNet isolates binary envelope encode/decode, Text.Json and MessagePack serialization, filter-pipeline depth, direct/interface dispatch, and InMemory event/request-reply paths. Payload-sensitive benchmarks use 256, 4,096, and 65,536 bytes and include allocation/GC diagnostics.

The load runner measures three scenarios (`event`, `request-reply`, `competing-consumers`) across InMemory, RabbitMQ, and Kafka at concurrency 1, 8, and 32. The full cross-product is 81 scenario keys. Each key is `(transport, scenario, payloadBytes, concurrency)`.

Before timing, the runner creates topology/subscriptions, performs a correctness operation, and executes warm-up operations. The measured region records per-operation elapsed time, throughput, nearest-rank p50/p95/p99, process allocations, GC collections, errors, and timeouts. Broker startup and warm-up are excluded.

## Run locally

```shell
dotnet test tests/AnyProtocol.Performance.Tests/AnyProtocol.Performance.Tests.csproj -c Release
dotnet run --project benchmarks/AnyProtocol.Benchmarks -c Release -- --job short --filter "*CodecBenchmarks*" --artifacts artifacts/benchmarks-smoke
dotnet run --project performance/AnyProtocol.LoadTests -c Release -- --matrix smoke --operations 1000 --warmup 100 --json artifacts/performance/smoke.json --markdown artifacts/performance/smoke.md
```

For the broker matrix:

```shell
docker compose -f samples/docker-compose.yml up -d
dotnet run --project performance/AnyProtocol.LoadTests -c Release -- --matrix full --rabbitmq-uri "$ANYPROTOCOL_RABBITMQ_URI" --kafka-bootstrap localhost:9092 --operations 10000 --warmup 1000 --baseline performance/baselines/linux-x64.json --threshold 0.20 --confirm-regressions --json artifacts/performance/full.json --markdown artifacts/performance/full.md
```

Run Release builds outside a debugger on an otherwise quiet machine. Preserve JSON and Markdown from the same run.

## Schema and exit codes

Schema version 1 includes UTC start time, framework, OS, architecture, processor metadata, and ordered scenarios. Generated files live under `artifacts/performance` and are not committed automatically.

| Exit | Meaning |
|---:|---|
| 0 | Correct run; no confirmed regression |
| 2 | Invalid CLI configuration |
| 3 | Correctness/setup failure, error, or timeout |
| 4 | Regression exceeded the threshold on both initial and confirmation runs |

## Regression rule

Correctness dominates timing: any error, timeout, incomplete delivery, or invalid report fails regardless of speed. A timing candidate regresses when throughput is more than 20% lower or p95 is more than 20% higher than the matching baseline. Exact 20% boundaries do not fail. With `--confirm-regressions`, only regressed keys rerun, and exit 4 requires the confirmation to exceed the threshold too. p50 and p99 remain diagnostic and do not gate.

Hosted runners have noisy neighbors, changing processor generations, and runtime-image updates. Compare only equivalent metadata and use dedicated hardware for release decisions.

## Baseline status

The committed `performance/baselines/linux-x64.json` is an explicit empty bootstrap baseline. The first full run on the chosen Linux x64 runner must be reviewed for zero errors/timeouts and then adopted without hand-editing measurements. Until that happens, the comparison classifies all results as new scenarios and applies no timing gate.

Raw benchmark and load reports are uploaded by the Performance smoke/full GitHub Actions workflows. Timestamped runs remain artifacts; only a deliberately reviewed normalized baseline belongs in source control.

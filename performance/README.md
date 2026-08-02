# AnyProtocol performance testing

Performance validation has two layers:

- `AnyProtocol.Benchmarks` isolates codec, serializer, pipeline, proxy-dispatch, and InMemory costs with BenchmarkDotNet.
- `AnyProtocol.LoadTests` measures end-to-end event, request/reply, and competing-consumer scenarios for InMemory, RabbitMQ, and Kafka.

Run measurements in Release mode outside a debugger. Broker startup, topology setup, correctness checks, and warm-up are excluded from the measured region.

## Local smoke commands

```powershell
dotnet test tests/AnyProtocol.Performance.Tests/AnyProtocol.Performance.Tests.csproj -c Release
dotnet run --project benchmarks/AnyProtocol.Benchmarks -c Release -- --job short --filter "*CodecBenchmarks*" --artifacts artifacts/benchmarks-smoke
dotnet run --project performance/AnyProtocol.LoadTests -c Release -- --matrix smoke --operations 1000 --warmup 100 --json artifacts/performance/smoke.json --markdown artifacts/performance/smoke.md
```

For the complete broker matrix, start `samples/docker-compose.yml` and run:

```powershell
dotnet run --project performance/AnyProtocol.LoadTests -c Release -- --matrix full --rabbitmq-uri amqp://guest:guest@localhost:5672/ --kafka-bootstrap localhost:9092 --operations 10000 --warmup 1000 --baseline performance/baselines/linux-x64.json --threshold 0.20 --confirm-regressions --json artifacts/performance/full.json --markdown artifacts/performance/full.md
```

The full matrix contains three transports, three scenarios, payloads of 256/4,096/65,536 bytes, and concurrency levels 1/8/32: 81 results in total.

## Results and regression policy

JSON uses schema version 1 and records runtime metadata, throughput, p50/p95/p99 latency, allocations, collections, errors, and timeouts. Markdown is a deterministic view of the same scenario keys.

Any error or timeout is a correctness failure. A timing regression requires throughput to fall by more than 20% or p95 latency to rise by more than 20% on both the initial run and a confirmation rerun. Short runs are useful for plumbing checks, not performance conclusions. Compare results only on controlled, equivalent hardware and runtime images.

`performance/baselines/linux-x64.json` is intentionally an empty bootstrap baseline until the first reviewed run on the target GitHub-hosted Linux image. Replace it with the normalized output of that environment; never hand-edit measured values or commit timestamped artifact runs.

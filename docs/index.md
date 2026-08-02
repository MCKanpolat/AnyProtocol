# AnyProtocol

AnyProtocol is a .NET 10 contract-first messaging library. Define one interface, register it as a client or server, and carry the same contract over local, HTTP, gRPC, Kafka, RabbitMQ, or ZeroMQ transports according to each transport's declared capabilities.

![AnyProtocol overview: one contract, any transport](assets/AnyProtocol_overview.png)

## Start here

- [Getting started](GETTING_STARTED.md)
- [Configuration](CONFIGURATION.md)
- [Transport semantics](TRANSPORT_SEMANTICS.md)
- [RabbitMQ](RABBITMQ.md)
- [Performance](PERFORMANCE.md)
- [Deployment](DEPLOYMENT.md)
- [Observability](OBSERVABILITY.md)
- [Security](SECURITY.md)
- [API reference](xref:AnyProtocol)

AnyProtocol validates selected protocols against contract operations during startup. Review transport semantics before production use: request/reply and streaming may be native or emulated, and delivery, durability, ordering, cancellation, and backpressure guarantees differ by transport.

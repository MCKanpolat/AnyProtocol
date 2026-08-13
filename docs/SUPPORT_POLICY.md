# Support policy

AnyProtocol core, abstraction, serializer, encoder, and InMemory runtime packages target
`net8.0` and `net10.0`.

- Minimum core runtime: .NET 8.
- Preferred baseline: .NET 10 LTS.
- Current SDK feature band: the version pinned in `global.json`.
- .NET 8 assets are maintained while .NET 8 remains supported.
- .NET 10 assets follow the .NET 10 support horizon, subject to the authoritative
  [.NET releases and support](https://learn.microsoft.com/dotnet/core/releases-and-support)
  schedule.
- Review date: at least six months before a supported target reaches end of support.

Transport and hosting adapters that still target only `net10.0` publish that restriction in their
package assets. Expanding an adapter requires dependency compatibility, operation conformance,
AOT/trimming where applicable, test-matrix cost, and a removal policy.

Packable projects run .NET package validation. Historical API baseline comparison is intentionally
disabled while the public API permits breaking changes; package asset and compatible-framework
validation still run during `dotnet pack`.

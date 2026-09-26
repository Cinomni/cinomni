# Source

Modular monolith backend (.NET 10 + ASP.NET Core). Solution: `Cinomni.slnx`.

## Layout

```
src/
  Directory.Build.props        # shared build config (net10.0, nullable, warnings-as-errors)
  Platform/
    Cinomni.Kernel/        # shared kernel: ids (UUIDv7), Result, message markers
    Cinomni.Operations/    # platform: command/event bus, outbox, jobs, health
  Modules/
    Identity/                  # reference module — the pattern every module follows
      Cinomni.Identity.Contracts/   # public interfaces + event DTOs (what others depend on)
      Cinomni.Identity/             # implementation (domain + persistence), owns its tables
  Host/
    Cinomni.Host/          # ASP.NET Core composition root
tests/
  Cinomni.Kernel.Tests/    # xUnit
```

## Dependency rules (enforced — project references fail the build if broken)

- The **Kernel** depends on nothing; everything may depend on it.
- A module's **`.Contracts`** project holds only its public surface (interfaces + event DTOs). Other modules reference *contracts*, never the implementation.
- A module **owns its tables** (one PostgreSQL schema) and never touches another module's tables. Cross-module communication is by interface or event only.
- The **Host** wires everything together (composition root) and is the only project that references module implementations.

## Adding a module (follow the Identity pattern)

1. `Cinomni.<Name>.Contracts` (references `Kernel`) — public interfaces + events.
2. `Cinomni.<Name>` (references its `.Contracts`, `Kernel`, `Operations`) — implementation + `Add<Name>Module()` DI extension.
3. Register it in `Host/Program.cs`.
4. Add a `tests/Cinomni.<Name>.Tests` project.

A module with nothing to publish gets no `.Contracts` project. `RealTime` is the one such case: it
consumes other modules' events and pushes signals to browsers, so nothing depends on it and every
arrow in the graph points inwards.

## Build & test

```bash
dotnet build          # from src/ (auto-detects the solution)
dotnet test
```

Code, comments, XML docs, platform documentation and commit messages are written in English.

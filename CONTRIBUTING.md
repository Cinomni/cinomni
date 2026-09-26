# Contributing to Cinomni

Welcome. This guide is everything you need to get productive on Cinomni: the architecture,
the module pattern, and how to build, run and test. It is self-contained: you don't need any
other document to start contributing.

Cinomni is a unified self-hosted media platform (discovery, acquisition, library, subtitles
and playback) built as a **modular monolith**.

See **[ROADMAP.md](./ROADMAP.md)** for what's built and what's next, and **[SECURITY.md](./SECURITY.md)**
for security practices and vulnerability reporting.

Original Cinomni code is licensed under [AGPL-3.0-or-later](./LICENSE). Contributions are
submitted under the same license; contributors retain copyright in their own work. A different
license for an existing contribution requires permission from its copyright holder.

---

## 1. Tech stack

| Area | Choice |
|------|--------|
| Runtime | .NET 10 (ASP.NET Core) |
| Language | C# (`Nullable` enabled, `TreatWarningsAsErrors`; the build must be clean) |
| Database | PostgreSQL 16 via EF Core 10 + Npgsql (one schema per module, `snake_case`) |
| Messaging | In-process command/event bus with a transactional **outbox** |
| Downloads | libtorrent **sidecar** (separate process) over gRPC |
| External processes | FFmpeg / ffprobe invoked by `argv` (never a shell) |
| Solution | `src/Cinomni.slnx` (the newer XML solution format) |

---

## 2. Prerequisites

- **.NET 10 SDK**
- **Docker** (for PostgreSQL, and optionally the libtorrent sidecar)

---

## 3. Getting started

```bash
# Clone
git clone https://github.com/cinomni/cinomni.git
cd cinomni

# 1. Start development dependencies (PostgreSQL on host port 5442)
docker compose -f docker-compose.dev.yml up -d

# 2. Restore local tools (dotnet-ef) and build
dotnet tool restore
dotnet build src/Cinomni.slnx

# 3. Run the tests (they need PostgreSQL running — see §7)
dotnet test src/Cinomni.slnx

# 4. Run the API
dotnet run --project src/Host/Cinomni.Host
# then check http://localhost:5xxx/health  (the port is printed on startup)
```

The development connection string targets `Database=cinomni`; the development user and
credentials live in `docker-compose.dev.yml` and are **never** used in production. The
libtorrent sidecar is optional for most work:

```bash
docker compose -f docker-compose.sidecar.yml up -d --build   # only if you touch Downloads
```

There are three compose files and they are not interchangeable. `docker-compose.dev.yml`
(PostgreSQL) and `docker-compose.sidecar.yml` (the libtorrent sidecar, published on localhost) are
**development only**: the credentials in them are development credentials and never leave your
machine. `docker-compose.yml` is the production stack, described in
**[DEPLOYMENT.md](./DEPLOYMENT.md)**; do not start it for day-to-day development.

In development the web client runs under Vite on port 5173 and proxies `/api` and `/health` to the
Host. In production there is no Vite: the built bundle is copied into the Host's `wwwroot` and served
from the same origin, with any unmatched non-API path answered by `index.html` so a deep link
survives a reload. An unknown `/api/...` path is answered by a 404 in the standard
`{ error, message }` envelope, never by the web shell.

---

## 4. Architecture in one page

Cinomni is a **modular monolith**: a single deployable process composed of independent
modules. Each module is a bounded context that **owns its own PostgreSQL schema** and never
reads or writes another module's tables.

```
┌──────────────────────────────────────────────────────────────┐
│  Host  (src/Host/Cinomni.Host)  — composition root, HTTP API   │
│  wires every module, runs migrations, exposes /health          │
└───────────────┬──────────────────────────────────────────────┘
                │ references module implementations (only the Host may)
   ┌────────────┴───────────────────────────────────────────────┐
   │  Modules (src/Modules/*)                                     │
   │  each = <Module>.Contracts  +  <Module>                      │
   │  talk to each other ONLY through *.Contracts and events      │
   └────────────┬───────────────────────────────────────────────┘
                │ every module depends on the platform kernel
   ┌────────────┴───────────────────────────────────────────────┐
   │  Platform (src/Platform)                                     │
   │   Cinomni.Kernel      — ids (UUIDv7), Result, message markers│
   │   Cinomni.Operations  — command/event bus, outbox, jobs      │
   └──────────────────────────────────────────────────────────────┘
```

**Dependency rules (enforced by project references; CI fails if broken):**

1. **Kernel** depends on nothing; everything may depend on it.
2. A module's **`.Contracts`** project holds only its public surface: interfaces, strongly-typed
   ids, enums, DTOs, **and its integration-event DTOs**. Other modules reference *contracts*,
   never the implementation.
3. A module **owns its tables** (one schema) and never touches another module's tables.
   Cross-module communication is by **interface or event only**.
4. The **Host** is the only project that references module implementations; it is the
   composition root.

Writes and the events they emit are committed **atomically** through a unit of work into the
outbox; a relay then dispatches events at-least-once. This is why modules never call each other
synchronously to mutate state: they publish an event or enqueue a command.

---

## 5. Repository layout

```
src/
  Cinomni.slnx                # the solution
  Directory.Build.props       # shared build config (net10.0, nullable, warnings-as-errors)
  Platform/
    Cinomni.Kernel/           # shared kernel — depends on nothing
    Cinomni.Operations/       # command/event bus, outbox, scheduler, unit of work
  Modules/<Name>/
    Cinomni.<Name>.Contracts/ # public interfaces + event DTOs (what others depend on)
    Cinomni.<Name>/           # implementation: Persistence / Application / Messaging /
                              # EventHandlers / Api  +  <Name>Module.cs
  Shared/
    Cinomni.Search.Contracts/ # contracts shared by more than one module (references Kernel only)
  Host/
    Cinomni.Host/             # ASP.NET Core composition root
tests/
  Cinomni.<Name>.Tests/       # xUnit (integration tests run against real PostgreSQL)
sidecar/
  torrent/                    # Python libtorrent sidecar (proto + server + Dockerfile)
```

Modules present today, roughly in the order a slice traverses them: Identity, Catalog,
Metadata, Monitoring, Discovery, ReleaseParsing, Decision, Acquisition, Downloads, Import, Library,
Subtitles, Playback. There are also two that sit outside the slice: Requests, which feeds it (an approved
request catalogues a title, and the spine takes over from `WorkAdded`), and Notifications, a terminal
consumer of the events worth surfacing.

Movies and series share that one spine. The difference is what a step is *about*: every event and
command on the acquisition path carries the **catalog unit ids** it concerns:
`UnitIds = [WorkId]` for a movie, one season or episode id per unit for a series. That is what lets a
single season-pack download satisfy N episodes without any module learning a second code path.

---

## 6. Adding a module (the pattern every module follows)

Use an existing module as a template: **Identity** is the reference, **Catalog** is the
smallest complete example. A new module `Foo` looks like this:

1. **Two projects.**
   - `Cinomni.Foo.Contracts` (references `Kernel`): strongly-typed ids, enums, DTOs, public
     interfaces, **and the integration-event DTOs**. Integration events live in `.Contracts`
     so other modules can consume them without referencing your implementation.
   - `Cinomni.Foo` (references its `.Contracts`, `Kernel`, `Operations`): `Persistence/`,
     `Application/`, `Messaging/`, `EventHandlers/`, `Api/`, and `FooModule.cs`.

2. **One `DbContext`, one schema.** Register it with `AddModuleDbContext<FooDbContext>()` so it
   shares the kernel's scoped connection and enlists in the unit of work.

3. **`FooModule.cs`** exposes a DI extension `AddFooModule()` and a `MigrateFooAsync()`. It wires
   up integration events, commands, queries and handlers. For reference, this is Catalog's:

   ```csharp
   public static IServiceCollection AddCatalogModule(this IServiceCollection services)
   {
       services.AddModuleDbContext<CatalogDbContext>();

       services.AddIntegrationEvent<WorkAdded>(CatalogEventNames.WorkAdded);
       services.AddIntegrationEvent<WorkAvailable>(CatalogEventNames.WorkAvailable);
       services.AddCommand<MarkWorkAvailableCommand>(CatalogCommandNames.MarkWorkAvailable);

       services.AddScoped<ICatalogCommands, CatalogCommands>();
       services.AddScoped<ICatalogQuery, CatalogQuery>();
       services.AddScoped<ICommandHandler<MarkWorkAvailableCommand>, MarkWorkAvailableCommandHandler>();

       // Consume another module's event → enqueue our own command.
       services.AddScoped<IEventHandler<MediaAvailable>, MediaAvailableHandler>();
       return services;
   }
   ```

4. **Atomicity.** Persist writes and publish events together inside `IUnitOfWork.ExecuteAsync`.
   Events carry plain `Guid`/`string` in their payload (not typed ids) for wire stability.

5. **Gotcha: event handlers that write to the database.** The outbox relay dispatches
   `IEventHandler<>` inside *its own* transaction on `OperationsDbContext`; your module's
   `DbContext` is **not** enlisted there. If a handler needs to write its own tables, it must
   **enqueue a command** (`ICommandQueue`), and the command handler does the write under its own
   unit of work in a second phase. See `MediaAvailableHandler` → `MarkWorkAvailable` command.

6. **Wire it into the Host** (`src/Host/Cinomni.Host/Program.cs`): `AddFooModule()` in the
   composition root, `MigrateFooAsync()` before `RecoverOperationsAsync()`, and
   `MapFooEndpoints()`.

7. **Add the projects and a `tests/Cinomni.Foo.Tests` project to `src/Cinomni.slnx`.**

---

## 7. Database & migrations

Each module owns one schema (`snake_case` naming). Migrations use the module's own
`IDesignTimeDbContextFactory`, which connects using the `CINOMNI_DB` environment variable when
set (otherwise the local development default).

```bash
dotnet tool restore   # once, to get the pinned dotnet-ef

# Add a migration for a module (project == startup-project == the module itself):
dotnet ef migrations add <Name> \
  --project src/Modules/Catalog/Cinomni.Catalog \
  --startup-project src/Modules/Catalog/Cinomni.Catalog \
  --output-dir Persistence/Migrations

# Platform migrations live under src/Platform/Cinomni.Operations.
```

You rarely apply migrations by hand: the Host applies every schema's pending migrations on
startup (see `Program.cs`).

Every installation upgrades a database that already holds the previous schema and its data, so:

- **Prefer expand/contract** for an incompatible change: add the new shape, move the data, and
  remove the old shape in a later release, rather than one destructive step.
- **Never edit a migration that has shipped.** An installation that already applied it would never
  see the edit, and its database and the code would drift apart silently. Add a corrective
  migration instead.

---

## 8. Testing

Integration tests run against a **real PostgreSQL** (the one from `docker-compose.dev.yml`,
host port 5442), so start it before running the suite. Each module has a `TestHost` that
registers the platform plus the module(s) under test and migrates the schemas from scratch;
tests drive `OutboxRelay.ProcessBatchAsync` and `CommandProcessor.ProcessBatchAsync` **manually**
so behaviour is deterministic (hosted services do not auto-start under a test `ServiceProvider`).

```bash
dotnet test src/Cinomni.slnx                                   # everything
dotnet test tests/Cinomni.Catalog.Tests/Cinomni.Catalog.Tests.csproj   # one module
```

New behaviour needs tests. Prefer pure unit tests for domain logic (FSMs, planners, parsers)
and integration tests for the event/command flow end-to-end.

### The validation scenarios

[`tests/VALIDATION-SCENARIOS.md`](./tests/VALIDATION-SCENARIOS.md) is the authoritative list of the
fifteen end-to-end behaviours the MVP is judged by, and it says for each one which test settles it and
at what scope. `Cinomni.Recovery.Tests.ValidationScenarios` carries the same list as data, and
`ValidationScenarioCatalogueTests` fails the build when the prose and the data disagree, when a cited
suite no longer exists, or when a test class claims a scenario number that is not in the list, so the
catalogue cannot quietly drift from what the suite proves.

`Cinomni.Recovery.Tests` is where a scenario needs something no other suite can do: a **real restart**.
`RecoveryHost.CreateAsync(..., reset: false)` and `RestartAsync()` dispose the service provider and
rebuild it over the *same* database, then run the Host's own recovery sequence. The engine and
filesystem fakes deliberately outlive the provider, because a sidecar and a disk outlive a backend
restart. If you add recovery behaviour, prove it there and check the obvious way: delete the mechanism
and confirm the test goes red.

---

## 9. Coding conventions

- **Language: English, everywhere**: identifiers, comments, XML docs, user-facing strings,
  documentation and commit messages.
- **Warnings are errors.** `dotnet build` must be clean before you push.
- **Enums travel by name** on the wire (a global `JsonStringEnumConverter`).
- **Strongly-typed ids** live in the owning module's `.Contracts`; integration events use plain
  `Guid`/`string` payloads.
- **External effects run outside the unit of work; persistence happens inside it.** Never mix a
  network/process/filesystem call into the transaction.
- **Adapter configuration is bound key by key** in `src/Host/Cinomni.Host/ModuleConfiguration.cs`,
  never with a blanket `Bind`. A typo in a configuration key must leave the code default in place
  rather than silently reshape an adapter; `ModuleConfigurationTests` pins that behaviour.
- **Security is not optional:**
  - **Authorization:** an endpoint that operates the installation (adds titles, configures indexers,
    controls downloads, manages accounts, collections or delivery channels) requires the administrator
    policy: `.RequireAuthorization(AuthorizationPolicies.Administrator)` (`Cinomni.Kernel.Security`).
    Regular accounts browse, play and request. `Cinomni.Host.Tests/ApiAuthorizationTests` enumerates the
    whole surface, so a new endpoint fails the build until you classify it there.
  - **Content access:** a work sits in a collection, and a collection is open or granted. Catalog's
    `IContentAccess` is the one authority; consult it, never re-implement it. A read model that answers
    for a person takes a `Viewer`; the unscoped twin is for event and command handlers only, and
    `ScopedReadModelTests` fails the build if an endpoint binds one. Hidden must read exactly like
    missing (404, empty list), never a distinct "forbidden".
  - Outbound HTTP to third parties (indexers, subtitle/metadata providers, `.torrent` links)
    goes through the SSRF-hardened client (`SsrfGuard` in `Cinomni.Kernel.Net`): validates the
    real connected IP (anti-rebinding), no redirects, no proxy, bounded response size; hostile
    XML is parsed with DTD processing prohibited.
  - External processes (ffprobe, FFmpeg) are launched with `argv` (`ProcessStartInfo.ArgumentList`,
    `UseShellExecute=false`), never through a shell, with a closed codec whitelist.
  - Every filesystem path is confined to its root: no traversal, no UNC/device paths. Import does
    it with `PathGuard` (`Cinomni.Import/Files/PathGuard.cs`, module-local, not in the kernel);
    Playback confines HLS segment names in `PlaybackStreamer`. Reuse the closest one; if a third
    module needs it, lift `PathGuard` into the kernel instead of re-implementing it.

  See **[SECURITY.md](./SECURITY.md)** for the full security guide and how to report a vulnerability.

### Clean-room implementation

- Implement Cinomni independently, from neutral requirements and observable behaviour. Do not copy
  or port source code, tests, database schemas, migrations, repository structure, UI text, visual
  design or internal names from other products, whatever their licence.
- Do not keep another product's source open while writing the equivalent Cinomni component: write a
  neutral requirement and its acceptance criteria first, close the source, then design.
- Do not use other products' names, logos or trade dress in product-facing code or UI.
- Third-party libraries (libtorrent, for example) are linked as dependencies, never used as a source
  of Cinomni code. **Every new or upgraded dependency records its licence and provenance** in the
  change that adds it.
- If provenance is uncertain, stop and reimplement from the neutral requirement.

---

## 10. Commits & pull requests

- **Conventional commits**, in English: `feat:`, `fix:`, `refactor:`, `docs:`, `test:`,
  `chore:`, `perf:`, `ci:`, optionally scoped, e.g. `feat(catalog): ...`.
- Keep PRs small and focused; make sure the build and tests are green.
- Comments state their rationale in place rather than citing documents outside this repository;
  this guide and the code are the source of truth.

---

## 11. Continuous integration

Every pull request and every push to `main` runs `.github/workflows/ci.yml` on Linux. Four
jobs, all reproducible on your machine. A CI step you cannot run locally is a defect.

| Job | What it proves | Run it locally |
|-----|----------------|----------------|
| `web` | The SPA typechecks, lints, passes its unit tests and builds | `cd web && npm ci && npm run typecheck && npm run lint && npm run test && npm run build` |
| `gates` | Module boundaries, solution completeness, a warnings-as-errors build, and the test projects that need no database (Kernel, RealTime, Host, including the authorization-surface gate) | `bash .github/scripts/check-module-boundaries.sh`, `bash .github/scripts/check-solution-completeness.sh`, `dotnet build src/Cinomni.slnx -c Release -warnaserror` |
| `backend-integration` | The whole suite against a real PostgreSQL 16 | `docker compose -f docker-compose.dev.yml up -d --wait && dotnet test src/Cinomni.slnx -c Release` |
| `migrations` | Every schema applies to an empty database, applies again as a no-op, applies on top of an existing installation, declares any destructive DDL, has no model drift, owns its schema with no cross-schema foreign key, and the real Host starts twice in a row | see below |

### The database contract

The integration tests read no environment variable: every `*TestHost.cs` hardcodes
`Host=localhost;Port=5442;…;Username=cinomni;Password=cinomni_dev`. CI therefore starts the database
with the very compose file you use (`docker compose -f docker-compose.dev.yml up -d --wait`)
instead of a GitHub service container. Those credentials are development-only, they are already
committed, and they are deliberately **not** GitHub Secrets: storing them as secrets would imply they
are production credentials. No workflow references a secret at all.

### Reproducing the `migrations` job

```bash
docker compose -f docker-compose.dev.yml up -d --wait
dotnet tool restore
docker compose -f docker-compose.dev.yml exec -T postgres \
  psql -U cinomni -d postgres -c 'CREATE DATABASE cinomni_ci_migrations;'

export CINOMNI_DB='Host=localhost;Port=5442;Database=cinomni_ci_migrations;Username=cinomni;Password=cinomni_dev'
bash .github/scripts/check-migration-safety.sh   # destructive DDL must declare a reason
bash .github/scripts/migrate-all.sh update       # applies every schema from empty
bash .github/scripts/migrate-all.sh update       # must apply nothing the second time
bash .github/scripts/migrate-all.sh check-drift  # no model changed without a migration
bash .github/scripts/migration-upgrade.sh <base-ref>   # applies your change over an existing install
bash .github/scripts/check-schemas.sh cinomni_ci_migrations
bash .github/scripts/host-smoke.sh cinomni_ci_host     # run it twice; the Host must be re-entrant
```

Set `CINOMNI_EF_NO_BUILD=1` after a Release build to skip 16 redundant project builds. Point
`CINOMNI_PSQL` elsewhere if your database is not the compose one (a git worktree resolves to a
different compose project name, so use `CINOMNI_PSQL='docker exec -i cinomni-postgres-dev psql -U cinomni'`).

### The scripts, and what they exist to catch

- `check-module-boundaries.sh`: a `<ProjectReference>` reaching into another module's
  implementation, a `.Contracts` project pulling in `Cinomni.Operations`, a reference to the Host, or
  a kernel that grew a dependency. §4 of this guide claimed CI enforced the dependency rules; now it
  does.
- `check-solution-completeness.sh`: a project on disk that nobody added to `src/Cinomni.slnx`, which
  would silently drop a whole module or test assembly out of CI.
- `check-migration-safety.sh`: a migration that drops a table or column, or adds a `NOT NULL` column
  with no default, without an explicit `cinomni:destructive-migration <reason>` line. An empty
  database can never show you that data loss; this reads the migration instead.
- `migrate-all.sh` / `ef-projects.sh`: applies or drift-checks every EF project, cross-checking the
  `Persistence/Migrations` convention against the design-time factories so a module cannot fall out
  of the gate unnoticed.
- `migration-upgrade.sh`: installs the base ref's migrations, then applies yours on top: the upgrade
  path every existing installation actually takes. It also refuses a migration that was edited after
  being applied, since that desynchronises every installation that already ran it.
- `check-schemas.sh`: every schema is claimed by exactly one module, no table lands in `public`, and
  no foreign key crosses a schema boundary.
- `host-smoke.sh`: the real composition root reaches `/health`. Run twice, it is the restart test.
  It starts the Host in Production, which is the environment where the storage roots are checked, so
  it points all three at `${RUNNER_TEMP:-${TMPDIR:-/tmp}}/cinomni-host-smoke` (override with
  `CINOMNI_SMOKE_STORAGE`) instead of the packaged `/data` mounts, which no ordinary account owns.
  The directory is outside the working tree and is left behind, so both runs see one installation.

### Node and SDK pins

`global.json` pins the .NET SDK feature band (`10.0.300`, `rollForward: latestFeature`), which keeps
`dotnet-ef 10.0.10` (`rollForward: false` in `.config/dotnet-tools.json`) resolvable. `web/.nvmrc`
pins Node 22. Both apply to your machine as well as to CI, on purpose.

### Dependency updates

`.github/dependabot.yml` keeps GitHub Actions, npm, NuGet, pip and the sidecar base image current.
Actions are pinned by commit SHA, so Dependabot is what stops those pins going stale. A dependency
bump still needs its licence and provenance recorded (see *Clean-room implementation* in §9).

> **These checks do not block a merge yet.** The repository is private on a plan without branch
> protection, so the jobs advise rather than gate until required status checks can be configured.

Thanks for contributing to Cinomni.

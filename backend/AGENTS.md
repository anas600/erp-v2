# Backend DOX — .NET 8 Root

## Purpose
- Own the .NET 8 backend tree, its DI registration, middleware pipeline, and feature module registry.
- Provide a single entry point (`Program.cs`) and consistent conventions for every feature module.

## Project
- Target framework: **net8.0**.
- Top-level statements live in `Program.cs` (no `Main` method).
- DI is registered in `Program.cs`; services are constructor-injected.
- The HTTP pipeline order is: `UseCors` → `UseAuthentication` → `UseAuthorization` → `UseSwagger` → `UseSwaggerUI` → feature endpoints.
- Migrations run automatically on startup via `IMigrationRunner.MigrateUp()`.

## Ownership
- `Program.cs` owns the service registration, middleware order, and Swagger configuration.
- `appsettings.json` owns non-secret configuration (JWT issuer, CORS, connection string template). Secrets must come from env vars, not this file.
- `ErpV2.csproj` owns the NuGet dependency set. Add new packages here with a comment explaining the why.
- `Dockerfile` owns the multi-stage build; do not change the runtime tag without testing.
- `Common/` holds services used by more than one feature (see `Common/AGENTS.md`).
- `Features/` holds business modules; each module is a vertical slice (see `Features/AGENTS.md`).
- `Migrations/` holds FluentMigrator scripts; ordered by numeric prefix in the attribute (see `Migrations/AGENTS.md`).

## Local Contracts
- **Endpoints** are exposed via Minimal API `MapGroup("/api/<resource>")`. Each feature folder exposes a static `Map(WebApplication app)` method called from `Program.cs`.
- **Models** are immutable C# `record` types with the suffix `Dto` for read DTOs and `Request` for write payloads. Example: `CompanyDto`, `CreateCompanyRequest`.
- **Services** are plain classes registered as `Singleton` in `Program.cs`. They depend on `IDbConnectionFactory` and other singletons; no scoped state.
- **Repositories are not separate types** in this MVP. Data access lives in services via Dapper; split out only when a service grows beyond ~300 lines.
- **Errors** are returned as `Results.BadRequest(new { error = "..." })` or `Results.Unauthorized()`. Error messages are user-facing Arabic strings, not exception messages.
- **Auth** is required on every endpoint except `/`, `/health`, and `/api/auth/login`. Use `.RequireAuthorization()` on the group, not per-endpoint.

## Work Guidance
- To add a new feature module:
  1. Create `backend/Features/<Name>/` with `Models.cs`, `Service.cs`, `Endpoints.cs`.
  2. Register the service in `Program.cs` and call `<Name>Endpoints.Map(app)`.
  3. Add a `AGENTS.md` inside the new folder following the standard section order.
  4. If the feature requires a schema change, add a new migration in `Migrations/`.
- To change a public DTO: search for all references in `frontend/src/lib` and update both ends together; do not let them drift.
- To add a new package: add a `<PackageReference>` with the version, and a one-line comment justifying it.

## Verification
- `dotnet build` must succeed without warnings introduced by your change.
- `dotnet run` should start on port 8080 and execute all migrations on boot.
- `curl http://localhost:5000/health` must return `{"status":"healthy"}` once the stack is up via Docker Compose.
- A `GET /swagger` UI must list the new endpoints after a code change.

## Child DOX Index
- `Common/AGENTS.md` — `IDbConnectionFactory`, `IPasswordHasher`, `JwtTokenService`, `CurrentContext` helpers.
- `Features/AGENTS.md` — module registry.
- `Migrations/AGENTS.md` — schema and seed migrations.

## Intentionally Unindexed
`bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`.

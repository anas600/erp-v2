# Features — Module Registry

## Purpose
- Each subfolder is one vertical slice of business functionality.
- Every feature is self-contained: it owns its models, service, and endpoints.

## Ownership
Each module folder owns:
- `Models.cs` — DTOs and request records.
- `Service.cs` — business logic and data access (Dapper).
- `Endpoints.cs` — minimal API route registration via a static `Map(WebApplication)` method.

Modules currently in this tree:
- `Auth/` — login, JWT issuance, company switching.
- `Companies/` — Holding + subsidiaries CRUD.
- `Accounts/` — chart of accounts CRUD.
- `Journal/` — manual journal entries and the **Posting Engine**.
- `Rules/` — business rules engine, evaluator, rule templates.
- `Reports/` — trial balance, income statement, balance sheet.

## Local Contracts
- A module never reaches into another module's files. Cross-module access goes through DI or events.
- `Service.cs` is the only type that uses `IDbConnectionFactory`. Endpoints delegate everything to the service.
- Endpoint URLs follow the pattern `/api/<resource>`, e.g. `/api/journal`. Sub-resources use nested paths, e.g. `/api/journal/{id}/post`.
- Mutation endpoints return the created or updated resource. Errors return `Results.BadRequest` / `Results.NotFound` / `Results.Unauthorized` with a short Arabic message.
- Every service method that mutates state is wrapped in an explicit Dapper transaction when the change spans multiple tables.

## Work Guidance
- To add a new feature:
  1. Create `Features/<Name>/` with the three files above.
  2. Register the service as a singleton in `Program.cs`.
  3. Call `<Name>Endpoints.Map(app)` after the existing endpoint mappings.
  4. Add a sibling `AGENTS.md` describing Purpose, Ownership, Local Contracts, Work Guidance, Verification.
  5. Update this file's "Modules currently in this tree" list.
- Keep services small. If a service exceeds 300 lines, split it (e.g. `CompanyService.Query.cs` / `CompanyService.Mutation.cs`).
- Use C# 12 syntax (file-scoped namespaces, primary constructors, collection expressions where it reads clearly).

## Verification
- `dotnet build` must succeed.
- After adding a module, the Swagger UI at `/swagger` must list the new endpoints.
- A focused `curl` against each new endpoint (with a valid JWT) must return the expected shape.

## Child DOX Index
- `Auth/AGENTS.md`
- `Companies/AGENTS.md`
- `Accounts/AGENTS.md`
- `Journal/AGENTS.md` — **contains the Posting Engine, read this carefully.**
- `Rules/AGENTS.md` — **contains the Rules Engine, read this carefully.**
- `Reports/AGENTS.md`

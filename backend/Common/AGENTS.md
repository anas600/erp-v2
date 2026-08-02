# Common — Cross-Cutting Services

## Purpose
- Hold services used by more than one feature module.
- No business logic lives here; only infrastructure plumbing.

## Ownership
- `NpgsqlConnectionFactory.cs` — creates and opens a PostgreSQL connection from a connection string. Registered as `IDbConnectionFactory` singleton.
- `PasswordHasher.cs` — BCrypt-based password hashing (cost factor 12). Registered as `IPasswordHasher`.
- `JwtTokenService.cs` — issues JWTs with the full claim set (user, companies, roles, permissions). Also defines `CurrentContext` extension methods for reading claims and headers in endpoints.

## Local Contracts
- All types in this folder are `public` and registered as singletons in `Program.cs`.
- Methods that touch the database open a connection inside a `using` block and dispose it; do not store connections in fields.
- `JwtTokenService.GenerateToken` is the **only** place where JWTs are minted. Do not duplicate this logic.
- `CurrentContext.GetActiveCompanyIdFromHeader` is the canonical way to resolve the active company. Call it instead of reading `HttpContext.User` directly.

## Work Guidance
- When you add a new shared service, place it in this folder only if two or more features need it. Otherwise keep it inside the feature.
- Password hasher and JWT key both come from configuration; never hardcode them.
- The default JWT expiry is 24 hours (`Jwt:ExpiryMinutes = 1440`); change via env var only.

## Verification
- After changing `JwtTokenService`, regenerate a token and verify the `company_ids` and `active_company_id` claims are present and valid (use `jwt.io` or `dotnet user-jwts`).
- After changing the password hasher, existing hashes must still verify. Do not change the cost factor on a deployed system without a migration plan.

## Child DOX Index
- *(No child folders; this is a leaf.)*

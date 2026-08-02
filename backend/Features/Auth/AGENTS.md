# Auth Feature

## Purpose
- Authenticate users, issue JWTs, and let a user switch their active company.
- This module never touches company data directly; it only deals with identity and tokens.

## Ownership
- `AuthModels.cs` — `LoginRequest`, `LoginResponse`, `UserInfo`, `CompanyInfo`, `SwitchCompanyRequest`, `SwitchCompanyResponse`.
- `AuthService.cs` — verifies credentials, loads user companies and permissions, calls `JwtTokenService` to mint tokens.
- `AuthEndpoints.cs` — `POST /api/auth/login`, `POST /api/auth/switch-company`, `GET /api/auth/me`.

## Local Contracts
- Login returns `LoginResponse` with `accessToken`, `refreshToken`, `user`, and `companies` (the list the user can access).
- `switch-company` accepts `{ companyId }` and returns a fresh token whose `active_company_id` claim is the new company.
- The `/me` endpoint requires a valid token; it returns the user record from the DB.
- Tokens are HS256, signed with `Jwt:Key`, with issuer `Jwt:Issuer` and audience `Jwt:Audience`.
- Passwords are checked with `IPasswordHasher.Verify`, never with string comparison.

## Work Guidance
- To add a "forgot password" flow: create a new service method, do not modify `LoginAsync`.
- To add MFA: extend `UserInfo` and add a `mfa_required` flag to the login response; do not break the existing shape.
- The `permissions` claim is the union of permissions across all the user's roles. Do not pre-filter by company; the company context is applied per-request.

## Verification
- `curl -X POST /api/auth/login` with valid creds returns a token.
- The same with bad creds returns `401`.
- `GET /api/auth/me` with the token returns the user; without the token returns `401`.
- After `switch-company`, the new token's `active_company_id` claim matches the request body.

## Child DOX Index
- *(No child folders; this is a leaf.)*

# Companies Feature

## Purpose
- Manage the Holding company and its subsidiaries.
- Enforce the "one Holding per deployment" rule from `CONSTITUTION.md` Article 3.

## Ownership
- `CompanyModels.cs` — `CompanyDto`, `CreateCompanyRequest`, `UpdateCompanyRequest`.
- `CompanyService.cs` — CRUD plus `GetForUserAsync(userId, isSuperAdmin)`.
- `CompanyEndpoints.cs` — `GET/POST/PUT/DELETE /api/companies`, `GET /api/companies/{id}`.

## Local Contracts
- The `companies` table is self-referencing via `parent_id`. The Holding is the row with `is_holding = true` and `parent_id IS NULL`.
- `GetForUserAsync` returns the full list for super admins, or only the rows in `user_companies` for everyone else.
- `Delete` is a soft delete (sets `is_active = false`); the row stays in the DB.
- Subsidiary creation requires a Holding to exist first; the system refuses to start without one.
- `code` is unique across all companies.

## Work Guidance
- Adding a new company: validate `code` uniqueness, set `base_currency` default to `LYD`, leave `is_holding = false` for subsidiaries.
- The Holding row is created by the seed migration; application code must not create a second one.
- Renaming a company is allowed; renaming a Holding requires a Constitution amendment (Article 3).
- To link a user to a company, write to the `user_companies` table directly from `AuthService` or a future `MembershipService`; do not extend this module.

## Verification
- `GET /api/companies` returns at least the Holding row and any subsidiaries from the seed.
- Creating a subsidiary with a `parent_id` pointing to the Holding succeeds; with a missing parent it returns `400`.
- A non-super-admin user only sees their assigned companies in the list.

## Child DOX Index
- *(No child folders; this is a leaf.)*

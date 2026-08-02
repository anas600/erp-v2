# Users Feature

## Purpose
- Manage user accounts and their company-role memberships.
- Let a user change their own password without going through an admin.

## Ownership
- `UserModels.cs` — `UserDto`, `UserCompanyMembership`, request records.
- `UserService.cs` — CRUD, soft-delete (set `is_active = false`), `ChangePasswordAsync`.
- `UserEndpoints.cs` — `GET/POST/PUT/DELETE /api/users`, `POST /api/users/me/change-password`.

## Local Contracts
- Only super admins can list, create, edit, or delete other users.
- A user can change their own password via `POST /api/users/me/change-password` with `{ currentPassword, newPassword }`.
- `newPassword` must be at least 6 characters; `currentPassword` is verified against the stored BCrypt hash.
- Soft delete: a deleted user has `is_active = false` and cannot log in, but their data is preserved for audit.

## Work Guidance
- Adding password reset via email: introduce a `password_resets` table with a single-use token and a TTL. The endpoint would email a link that opens a page calling `POST /api/users/reset-password`.
- Adding "last login" tracking: add a `last_login_at` column and update it on each successful `AuthService.LoginAsync`.
- Adding audit log entries for user CRUD: write to `audit_logs` from `UserService.CreateAsync` / `UpdateAsync` / `DeleteAsync`.

## Verification
- A non-super-admin calling `GET /api/users` gets `403`.
- A user calling `POST /api/users/me/change-password` with a wrong current password gets `400` with the Arabic error message.
- A deactivated user (`is_active = false`) cannot log in.

## Child DOX Index
- *(No child folders; this is a leaf.)*

# Shared Libraries

## Purpose
- Hold the pieces of code shared by more than one page: the API client, the auth context, formatting helpers.

## Ownership
- `api.ts` — Axios singleton with request interceptor (adds `Authorization` and `X-Company-Id`) and response interceptor (clears cookies on 401). Also exports `getErrorMessage` for consistent error toasts.
- `auth-context.tsx` — `AuthProvider`, `useAuth`, and the `User` / `Company` types. Stores the token, user, and companies in cookies so a hard reload keeps the user signed in.
- `company-context.tsx` — thin wrapper around the auth context that exposes only the `activeCompanyId`. Useful for components that only need the current company.
- `utils.ts` — `cn` (Tailwind class merge), `formatNumber` (English digits, two decimals), `formatDate` / `formatDateTime` (UK-style `dd/mm/yyyy`).

## Local Contracts
- All cookies are non-`httpOnly` and expire after 1 day; the backend issues an `accessToken` that lasts the same duration. The trade-off is "no XSS-safe token storage" in exchange for "easy reload on shared machines." The project is a private deployment, so this is acceptable.
- The auth context decodes the JWT payload (no signature check) to read the `permission` claim for client-side hiding of UI elements. **Security decisions always happen on the backend.**
- `getErrorMessage` accepts any thrown value and returns a user-friendly Arabic string when the backend provided one, otherwise the raw error message.

## Work Guidance
- To add a new shared utility: place it in `utils.ts` or a new sibling file. Update this `AGENTS.md` to list it.
- To add a new React context: create a sibling file in this folder and wrap it inside `AuthProvider` in `app/layout.tsx`.
- Avoid putting business logic in this folder; it is for plumbing only.

## Verification
- After editing `api.ts`, run a request from the browser dev tools to confirm the `Authorization` header is present.
- After editing `auth-context.tsx`, log in, refresh, and confirm the user stays signed in.
- After editing `utils.ts`, confirm the format strings match the project style (English digits, `dd/mm/yyyy`).

## Child DOX Index
- *(No child folders; this is a leaf.)*

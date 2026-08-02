# App Router Routes

## Purpose
- Map URLs to React Server Components (or `'use client'` pages) under the App Router.
- Define the global layout, the auth flow, and the protected dashboard shell.

## Ownership
- `layout.tsx` — root layout: sets `<html lang="ar" dir="rtl">`, loads `globals.css`, wraps children in `AuthProvider` and `CompanyProvider`.
- `page.tsx` — root index; redirects to `/dashboard`.
- `globals.css` — Tailwind directives plus project-specific classes (`btn-primary`, `input`, `card`, `table`, `badge-*`).
- `auth/login/page.tsx` — login form; calls `useAuth().login`.
- `dashboard/layout.tsx` — protected shell: sidebar, top bar, company switcher, user menu; reads `useAuth` and redirects to `/auth/login` when unauthenticated.
- `dashboard/page.tsx` — main dashboard: total assets, liabilities, equity, net income, trial balance summary.
- `dashboard/companies/page.tsx` — list of companies; super admin can add a new company.
- `dashboard/accounts/page.tsx` — chart of accounts; create / list with type, nature, balance.
- `dashboard/journal/page.tsx` — journal entry list, expandable rows, draft creation with line-by-line balance check, post and reverse actions.
- `dashboard/rules/page.tsx` — rule list, JSON editor, test sandbox, enable/disable, delete.
- `dashboard/reports/trial-balance/page.tsx` — trial balance report with balanced indicator.

## Local Contracts
- The dashboard layout is `'use client'` because it reads the auth context. Pages inside the dashboard may also be `'use client'` when they need state or effects.
- The login page is `'use client'` for the same reason.
- API calls always go through `lib/api.ts`. The base URL is `process.env.NEXT_PUBLIC_API_URL` (defaults to `http://localhost:5000`).
- Loading states show a centered spinner (`<Loader2 className="animate-spin" />`).
- Error messages are Arabic and short; they come from the backend's `error` field via `getErrorMessage` from `lib/api.ts`.

## Work Guidance
- New sidebar entries: update the `navItems` array in `dashboard/layout.tsx`. Each item needs an icon, label, and optional permission key.
- New forms: use controlled inputs with `useState`. Keep forms under 200 lines; if they grow, extract custom hooks.
- New tables: use the `table` class from `globals.css` for consistent styling.
- New modals: use the inline modal pattern (`fixed inset-0 bg-black/40 ...`) seen in `dashboard/accounts/page.tsx`.

## Verification
- All dashboard pages render without console errors when the backend is up.
- Switching companies from the top bar reloads the page and the new company's data appears.
- Creating a journal entry with unbalanced lines shows the "غير متوازن" badge.

## Child DOX Index
- *(No child `AGENTS.md`; route folders are leaves with their own concerns documented above.)*

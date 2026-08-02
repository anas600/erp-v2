# Frontend DOX — Next.js 15 Root

## Purpose
- Own the Next.js 15 frontend tree, its build configuration, and routing conventions.
- Provide a single RTL Arabic experience for the client.

## Project
- Framework: **Next.js 15** with the App Router (`src/app/`).
- Language: **TypeScript 5.6** with `strict: true`.
- UI: **Tailwind CSS 3.4** plus utility classes defined in `src/app/globals.css`.
- Icons: **lucide-react**.
- HTTP: **axios** with a single instance in `src/lib/api.ts`.
- Auth state: React Context in `src/lib/auth-context.tsx`.

## Ownership
- `package.json` owns the dependency set; bump with care (lockfile is generated).
- `next.config.js` owns the `rewrites` from `/api/*` to the backend, plus `output: 'standalone'` for the Docker build.
- `tailwind.config.js` owns the design tokens (font family, primary color scale).
- `Dockerfile` owns the multi-stage Node 20 build.
- `tsconfig.json` owns the strict TypeScript settings and the `@/*` path alias to `src/*`.
- `src/app/` owns the routes (see `src/app/AGENTS.md`).
- `src/lib/` owns the shared libraries (see `src/lib/AGENTS.md`).

## Local Contracts
- The app is **RTL-first**: every page declares `dir="rtl"` via the root `layout.tsx`. The default font is Tajawal.
- Every page under `src/app/dashboard/` is protected; the dashboard layout reads the auth context and redirects to `/auth/login` if the user is missing.
- API calls go through the singleton `api` instance; do not import `axios` directly in components.
- Numeric formatting uses `formatNumber` from `src/lib/utils.ts` with English digits per project owner preference.
- Date formatting uses `formatDate` / `formatDateTime` with `dd/mm/yyyy` style.

## Work Guidance
- Adding a new page:
  1. Create the file under `src/app/dashboard/<feature>/page.tsx`.
  2. The dashboard layout handles auth and the sidebar; you only need to export the default page component.
  3. Use the existing UI patterns (cards, tables, modals) defined in `globals.css` (`btn-primary`, `input`, `card`, `table`).
- Adding a new sidebar entry: update the `navItems` array in `src/app/dashboard/layout.tsx`.
- Translations stay in Arabic. If a string needs to be a code or an enum value, keep it in English and translate the label separately.

## Verification
- `npm run build` must succeed with no TypeScript errors.
- After build, the standalone output lives in `.next/standalone`; the Docker build copies it.
- A logged-in user can reach `/dashboard`; a logged-out user is redirected to `/auth/login`.
- The login page demo accounts (`admin@holding.ly / admin123`, etc.) work against the running backend.

## Child DOX Index
- `src/AGENTS.md` — frontend source root.
- `src/app/AGENTS.md` — App Router routes.
- `src/lib/AGENTS.md` — shared libraries (API client, auth context, utilities).

## Intentionally Unindexed
`node_modules/`, `.next/`, `out/`, `.env*.local`, `*.log`, `.DS_Store`.

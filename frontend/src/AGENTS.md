# Frontend Source Root

## Purpose
- Hold all authored source for the frontend.
- Provide two stable roots: `app/` for routes and `lib/` for shared libraries.

## Ownership
- `app/` — App Router routes; the URL path mirrors the folder structure.
- `lib/` — pure TypeScript modules (no React state) plus the auth context provider.

## Local Contracts
- Imports use the `@/*` alias defined in `tsconfig.json`; do not use long relative paths.
- `lib/` modules export pure functions or React providers; they do not import from `app/`.
- `app/` routes import freely from `lib/` but never the other way around.

## Work Guidance
- When adding a new shared helper: place it in `lib/` and document the function in this file's "Verification" if it has non-obvious behavior.
- When adding a new route: see `app/AGENTS.md`.

## Verification
- `npm run build` passes; type errors fail the build before runtime.

## Child DOX Index
- `app/AGENTS.md` — App Router routes.
- `lib/AGENTS.md` — shared libraries.

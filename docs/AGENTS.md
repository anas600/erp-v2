# Docs — Human-Facing Guides

## Purpose
- Hold every document the client (and the future maintainer) reads.
- Keep this folder in Arabic for the user guide; English is allowed for technical reference.

## Ownership
- `architecture.md` — system architecture, design decisions, and how the rules engine fits in.
- `user-guide.md` — operator manual in Arabic: how to log in, how to create a company, how to post a journal entry, how to use the rules engine.
- `deployment.md` — production deployment on Hostinger VPS 2 (port mapping, reverse proxy hints, backup strategy).
- `deploy-render.md` — free deployment on Render.com (Blueprint, cold starts, sleep avoidance).
- `deploy-hf.md` — single-container deployment on Hugging Face Spaces (paid PRO plan).

## Local Contracts
- Markdown only; no binaries.
- Diagrams are Mermaid inside fenced code blocks so they render on GitHub.
- Avoid duplicating information from the root `README.md`; link to it instead.

## Work Guidance
- When you add a new feature in the backend, add a "How to use it" section in `user-guide.md`.
- When you change the deployment story, update `deployment.md` in the same PR.
- Architecture changes belong in `architecture.md`.

## Verification
- The user guide covers every menu item in the dashboard sidebar.
- The architecture doc matches the current state of the code; update it when you change a contract.

## Child DOX Index
- *(No child folders; each doc is a leaf file.)*

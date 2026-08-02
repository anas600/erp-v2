# Deploy to Hugging Face Spaces

This guide shows how to deploy ERP-V2 as a **single-container** app on a Hugging Face Space. It is meant for demo and preview, not production.

## What is different on HF Spaces?

| | Docker Compose (Hostinger) | HF Space |
|---|---|---|
| Containers | 3 (db + backend + frontend) | 1 (all-in-one) |
| Database | PostgreSQL 17 (separate container) | PostgreSQL 15 (inside the same container) |
| Exposed port | 3000, 5000, 5432 | 7860 only |
| Migrations | Run on every container start | Run once at first boot |
| Process manager | Docker Compose | `supervisord` |

The `Dockerfile.hf`, `supervisord-hf.conf`, and `start-hf.sh` files at the project root implement this single-container shape. The application code, the chart of accounts, the seed data, and the rules templates are identical to the docker-compose setup.

## Prerequisites

- A Hugging Face account: <https://huggingface.co/join>
- Git installed on your local machine.
- HF CLI (optional but convenient): `pip install -U "huggingface_hub[cli]"`

## Step 1 — Create a new Space

1. Go to <https://huggingface.co/new-space>.
2. **Space name**: `erp-v2` (or whatever you like).
3. **License**: `MIT` (or your client's preferred license).
4. **SDK**: choose **Docker** — this is critical; it lets you provide your own `Dockerfile`.
5. **Space hardware**: CPU basic (free) is enough for the demo. For a smoother experience pick the small upgrade (paid).
6. Click **Create Space**.

The Space repo will be at `https://huggingface.co/spaces/<your-username>/erp-v2`.

## Step 2 — Clone the Space repo

```bash
git clone https://huggingface.co/spaces/<your-username>/erp-v2
cd erp-v2
```

## Step 3 — Replace the placeholder with ERP-V2

You have two options.

### Option A — Copy from the ERP-V2 zip (recommended)

Unzip `erp-v2.zip` somewhere temporary, then copy the HF-specific files into your Space repo:

```bash
# from the unzipped erp-v2 directory
cp Dockerfile.hf                <space-repo>/Dockerfile
cp supervisord-hf.conf          <space-repo>/supervisord-hf.conf
cp start-hf.sh                  <space-repo>/start-hf.sh
chmod +x <space-repo>/start-hf.sh
```

The `Dockerfile` (no extension) is what HF looks for. The `Dockerfile.hf` is the same content; copy it as `Dockerfile` into the Space root.

### Option B — Initialize a git submodule

```bash
git submodule add https://huggingface.co/spaces/<your-username>/erp-v2
# then add Dockerfile.hf, supervisord-hf.conf, start-hf.sh at the root
```

## Step 4 — Push and let HF build

```bash
git add Dockerfile supervisord-hf.conf start-hf.sh README.md
git commit -m "Deploy ERP-V2 single-container image"
git push
```

HF will start building the image. Watch the **Logs** tab in the Space UI.

## Step 5 — Wait for the build

The first build pulls:
- `mcr.microsoft.com/dotnet/sdk:8.0` (~700 MB)
- `node:20-alpine` (~50 MB)
- `debian:bookworm-slim` updates + PostgreSQL 15 + .NET runtime (~400 MB)

Total image size: ~1.5–2 GB. Build time: 5–10 minutes on a fresh Space.

## Step 6 — Open the Space

Once the build finishes, the Space URL becomes live:
```
https://<your-username>-erp-v2.hf.space
```

Log in with the demo credentials:
- `admin@holding.ly` / `admin123`
- `accountant@company-a.ly` / `acc123`
- `engineer@company-a.ly` / `eng123`

## What you give up on HF vs Hostinger

- **No file persistence**: every time the Space restarts, the database is reset to the seed state. This is fine for a demo but not for real data.
- **No SMTP / external integrations**: HF Spaces cannot open arbitrary outbound ports.
- **Slower cold starts**: the first request after a sleep takes 5–10 seconds.
- **Resource limits**: the free tier caps CPU and RAM. For 10–20 users the basic paid tier is enough.

## When to use HF vs Hostinger

- **Use HF Space** for: client demos, internal previews, marketing screenshots.
- **Use Hostinger VPS** for: real production, persistent data, more than a handful of users.

## Troubleshooting on HF

| Symptom | Fix |
|---|---|
| Build fails with "permission denied" on `start-hf.sh` | The `chmod +x` did not stick; re-run it before commit |
| "Address already in use" | The Space is restarting; wait 30 s and refresh |
| Login returns 401 | Migrations did not run; check the Space logs for "Running FluentMigrator" |
| Frontend shows "Network Error" | The Next.js rewrite is pointing at the wrong backend URL; rebuild after editing `next.config.js` |
| Database is empty after restart | This is expected — HF Spaces are stateless. Use the seed accounts again. |

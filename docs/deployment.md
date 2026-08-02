# Deployment Guide — Hostinger VPS 2

This guide covers the production deployment of ERP-V2 on a Hostinger VPS 2 (2 vCPU, 2 GB RAM, Ubuntu 22.04 LTS).

## 1. Prerequisites

- A Hostinger VPS 2 (or any VPS with 2 GB RAM and Docker support).
- A registered domain (optional but recommended for HTTPS).
- SSH access to the VPS.

## 2. Initial Server Setup

```bash
# Update packages
sudo apt update && sudo apt upgrade -y

# Install Docker
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
sudo usermod -aG docker $USER
newgrp docker

# Install Docker Compose (if not bundled)
sudo apt install -y docker-compose-plugin

# Verify
docker --version
docker compose version
```

## 3. Firewall

Open only the ports the app actually uses:

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80/tcp    # HTTP (for reverse proxy or direct)
sudo ufw allow 443/tcp   # HTTPS
sudo ufw enable
```

> The app ports `3000`, `5000`, and `5432` should **not** be exposed publicly. Use a reverse proxy (Caddy or Nginx) and expose only `80` and `443`.

## 4. Upload the Application

```bash
# On your local machine
scp erp-v2.zip user@<vps-ip>:/home/user/

# On the VPS
ssh user@<vps-ip>
cd /home/user
unzip erp-v2.zip
cd erp-v2
cp .env.example .env
nano .env   # edit POSTGRES_PASSWORD, JWT_KEY, etc.
```

## 5. Environment Variables (Production)

Generate strong values for these:

```bash
# Generate a strong JWT key
openssl rand -base64 48

# Generate a strong DB password
openssl rand -base64 24 | tr -d '/+=' | head -c 32
```

In `.env`:
```
POSTGRES_VERSION=17
POSTGRES_PASSWORD=<generated-strong-password>
JWT_KEY=<generated-strong-jwt-key>
CORS_ORIGINS=https://yourdomain.com
NEXT_PUBLIC_API_URL=https://yourdomain.com
```

## 6. Start the Stack

```bash
docker compose up -d --build
docker compose logs -f   # watch until migrations finish
```

## 7. Reverse Proxy with Caddy (Recommended)

```bash
sudo apt install -y caddy
```

`/etc/caddy/Caddyfile`:
```
yourdomain.com {
    reverse_proxy localhost:3000
    encode gzip
}
```

```bash
sudo systemctl reload caddy
```

Caddy provisions a Let's Encrypt certificate automatically.

## 8. Backups

### Database backup (cron)

```bash
# Add to crontab
0 2 * * * docker exec erp-v2-db pg_dump -U erp erp | gzip > /home/user/backups/db-$(date +\%F).sql.gz
```

Keep at least 30 days of backups. Copy them off-server (e.g. to Backblaze B2 or S3).

### Volume backup

The Postgres data lives in the `erp_db_data` named volume. The `pg_dump` cron above covers logical backups; for a full volume snapshot, stop the stack first.

## 9. Updates

```bash
cd /home/user/erp-v2
git pull    # or re-upload the zip
docker compose pull
docker compose up -d --build
```

## 10. Monitoring

- Logs: `docker compose logs -f`
- Resource usage: `docker stats`
- Health check: `curl http://localhost:5000/health` (from inside the VPS)

For external uptime monitoring, point a service like UptimeRobot at `https://yourdomain.com/health`.

## 11. Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Backend won't start | DB password mismatch | Re-check `.env`; rebuild the backend image |
| Login fails immediately | CORS misconfiguration | Update `CORS_ORIGINS` to match the frontend URL |
| Frontend shows blank page | `NEXT_PUBLIC_API_URL` wrong | Rebuild the frontend with the correct value |
| Migrations fail | Postgres version mismatch | Set `POSTGRES_VERSION=15` if running PG 15 locally |
| Token keeps expiring | `JWT_EXPIRY_MINUTES` too low | Default 1440 (24h) is recommended |

## 12. Security Checklist

- [ ] `JWT_KEY` is at least 32 random characters.
- [ ] `POSTGRES_PASSWORD` is strong and unique.
- [ ] `CORS_ORIGINS` lists only the production domain.
- [ ] Only ports 80 and 443 are open publicly.
- [ ] HTTPS is enforced (via Caddy or Nginx).
- [ ] Backups run daily and are stored off-server.
- [ ] Demo seed passwords have been changed or removed (see `Migrations/002_SeedData.cs`).

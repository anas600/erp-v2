# ===========================================
# ERP-V2 Frontend (Next.js 14)
# Multi-stage build
# ===========================================

FROM node:20-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
# --legacy-peer-deps tolerates React 19 / Next.js peer ranges that npm 10+ rejects.
# Safe to drop once the entire dependency tree is React 19 aware.
RUN npm install --no-audit --no-fund --legacy-peer-deps

FROM node:20-alpine AS builder
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
# Ensure the public directory exists. Next.js's standalone build expects
# /app/public to be COPY-able. If the project has no static files in
# public/, the directory is missing and the next stage fails with
# "failed to calculate checksum of ref ... /app/public: not found".
RUN mkdir -p ./public
ARG NEXT_PUBLIC_API_URL
ENV NEXT_PUBLIC_API_URL=$NEXT_PUBLIC_API_URL
RUN npm run build

FROM node:20-alpine AS runner
WORKDIR /app
ENV NODE_ENV=production
# Render free tier expects services to listen on port 10000 by default.
# Setting PORT=10000 in the env is what the env var name says, but the
# actual value must match what Render's reverse proxy will hit. The
# `EXPOSE 3000` is documentation only; the runtime PORT env var
# determines where Node.js actually binds.
ENV PORT=10000
EXPOSE 10000
RUN addgroup --system --gid 1001 nodejs && adduser --system --uid 1001 nextjs

COPY --from=builder /app/public ./public
COPY --from=builder --chown=nextjs:nodejs /app/.next/standalone ./
COPY --from=builder --chown=nextjs:nodejs /app/.next/static ./.next/static

USER nextjs
EXPOSE 3000
CMD ["node", "server.js"]

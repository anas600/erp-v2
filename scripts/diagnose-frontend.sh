#!/usr/bin/env bash
# Frontend diagnostic — paste output in chat
echo "=== Working dir ==="
pwd
ls -la
echo ""
echo "=== App dir ==="
ls -la /app 2>/dev/null | head -20
echo ""
echo "=== Server.js exists? ==="
ls -la /app/server.js 2>/dev/null || echo "MISSING"
echo ""
echo "=== Public dir ==="
ls -la /app/public 2>/dev/null || echo "MISSING"
echo ""
echo "=== Process check ==="
ps aux | grep -E "node|next" | grep -v grep || echo "no node process"
echo ""
echo "=== Port check ==="
netstat -tlnp 2>/dev/null | grep -E ":3000|:10000|:8080" || echo "no listening ports"
echo ""
echo "=== Env vars (filtered) ==="
env | grep -E "PORT|NODE_ENV|NEXT_PUBLIC|BACKEND" | sort
echo ""
echo "=== Try manual start ==="
timeout 5 node server.js 2>&1 || echo "(exited or timed out)"

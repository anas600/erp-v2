/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  output: "standalone",
  experimental: {
    serverActions: { allowedOrigins: ["localhost:3000", "localhost:5000"] }
  },
  async rewrites() {
    // Use the public HTTPS URL as the rewrite target. The public
    // URL is what we know works (it has DNS, it has TLS, it has
    // a working backend listening on it). The internal DNS
    // (http://erp-v2-backend:8080) does NOT resolve from inside
    // the frontend container on Render — confirmed via
    // 'getaddrinfo ENOTFOUND erp-v2-backend' in production logs.
    //
    // Going via the public URL means an extra hop (browser → Next.js
    // → Render edge → backend), but the added latency is negligible
    // and the trade-off is worth it for a working demo. The env
    // var BACKEND_INTERNAL_URL still wins if set, so production
    // users with a working internal DNS can override.
    const apiUrl = process.env.BACKEND_INTERNAL_URL
      || "https://erp-v2-backend-mkyg.onrender.com";
    return [
      { source: "/api/:path*", destination: `${apiUrl}/api/:path*` }
    ];
  }
};

module.exports = nextConfig;

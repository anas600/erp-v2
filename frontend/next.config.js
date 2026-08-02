/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  output: "standalone",
  experimental: {
    serverActions: { allowedOrigins: ["localhost:3000", "localhost:5000"] }
  },
  async rewrites() {
    // Hardcoded fallback matches the render.yaml default
    // (BACKEND_INTERNAL_URL=http://erp-v2-backend:8080). The env
    // var is the source of truth, but if the Blueprint sync drops
    // it, we still proxy to a sensible address instead of an
    // unresolvable "backend" hostname.
    const apiUrl = process.env.BACKEND_INTERNAL_URL
      || "http://erp-v2-backend:8080";
    return [
      { source: "/api/:path*", destination: `${apiUrl}/api/:path*` }
    ];
  }
};

module.exports = nextConfig;

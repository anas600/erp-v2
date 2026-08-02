/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  output: "standalone",
  experimental: {
    serverActions: { allowedOrigins: ["localhost:3000", "localhost:5000"] }
  },
  async rewrites() {
    const apiUrl = process.env.BACKEND_INTERNAL_URL || "http://backend:8080";
    return [
      { source: "/api/:path*", destination: `${apiUrl}/api/:path*` }
    ];
  }
};

module.exports = nextConfig;

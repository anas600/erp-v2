/**
 * Shared Axios instance.
 *
 * Every API call from the frontend goes through this singleton. The request
 * interceptor attaches the JWT (from the `erp_token` cookie) and the active
 * company id (from `erp_active_company`) to every request, so individual
 * call sites never need to think about headers.
 *
 * The response interceptor clears the session on 401 and redirects to login,
 * which is why the auth context decodes the JWT for UI hints only — real
 * security checks always happen on the backend.
 *
 * IMPORTANT: we use a *relative* baseURL ("/api") so that all API calls
 * stay on the same origin. The Next.js rewrite (configured in
 * next.config.mjs as `/api/:path*` -> BACKEND_INTERNAL_URL) then forwards
 * them to the .NET backend. This means we don't need to bake
 * NEXT_PUBLIC_API_URL at build time, which would break every time Render
 * re-creates the backend with a new URL suffix (e.g. -86pf). See the
 * comment in render.yaml for the full rationale.
 */
import axios, { AxiosError, AxiosInstance } from "axios";
import Cookies from "js-cookie";

/** Base Axios instance used by every page. */
export const api: AxiosInstance = axios.create({
  baseURL: "/api",
  headers: { "Content-Type": "application/json" }
});

// Add auth token and active company header
api.interceptors.request.use((config) => {
  const token = Cookies.get("erp_token");
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  const activeCompany = Cookies.get("erp_active_company");
  if (activeCompany) {
    config.headers["X-Company-Id"] = activeCompany;
  }
  return config;
});

// Handle 401 globally
api.interceptors.response.use(
  (r) => r,
  (err: AxiosError) => {
    if (err.response?.status === 401) {
      Cookies.remove("erp_token");
      Cookies.remove("erp_user");
      if (typeof window !== "undefined" && !window.location.pathname.startsWith("/auth/login")) {
        window.location.href = "/auth/login";
      }
    }
    return Promise.reject(err);
  }
);

/**
 * Extracts a user-friendly Arabic error message from an Axios or generic error.
 * Looks for the backend's `error` field first, then falls back to the raw error.
 */
export function getErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data as any;
    return data?.error?.message || data?.error || err.message;
  }
  return (err as Error)?.message || "حدث خطأ غير متوقع";
}

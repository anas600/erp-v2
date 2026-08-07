/**
 * Shared Axios instance.
 *
 * Every API call from the frontend goes through this singleton. The request
 * interceptor attaches the JWT (from the `erp_token` cookie) and the active
 * company id (from `erp_active_company`) to every request, so individual
 * call sites never need to think about headers.
 *
 * Sprint 33 hotfix v3 (2026-08-07): the user reported the cold-start retry
 * mechanism (added in Sprint 33) was actually slowing the system down and
 * blocking pages from loading. The retry added 4+8=12s of waiting on top
 * of the cold-start delay, and the WakeupBanner was overlapping the page
 * content. The user explicitly said: "نحتاج اليه" → "we don't need it".
 *
 * Decision: remove the retry logic entirely. The trade-off is that the
 * user might see a brief 502 on the first request after Render's free
 * tier spins down the backend (~15 min idle). A simple page refresh fixes
 * it. This is much better than making the user wait 12+ seconds on every
 * request, or having a banner block the page.
 *
 * What stays:
 *  - 401 → clear session + redirect to login (security-critical)
 *  - Request interceptor (auth + company header)
 *  - Friendly error message helper
 *
 * What goes away:
 *  - Retry on 502/503/504 (with delays 4s+8s)
 *  - Wakeup event dispatching
 *  - WakeupBanner overlay
 *  - Slow request detection
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
  headers: { "Content-Type": "application/json" },
  // 30s timeout — long enough for a typical request, short enough that
  // a stuck connection doesn't lock the page forever. Render cold-start
  // can take 30-60s, so the user may need to refresh once after a long
  // idle period. That's an acceptable trade-off for fast normal use.
  timeout: 30000
});

// Request interceptor: attach auth + active company header
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

// Response interceptor: 401 → clear session + redirect to login
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
 * Extracts a user-friendly Arabic error message from an Axios or generic
 * error. Looks for the backend's `error` field first, then falls back to
 * the raw error. Special-cases common HTTP statuses with Arabic messages
 * so the user always knows what went wrong.
 */
export function getErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data as any;
    const status = err.response?.status ?? 0;
    if (status === 502 || status === 503 || status === 504) {
      return "الخادم في وضع السكون. يرجى تحديث الصفحة بعد 30 ثانية";
    }
    if (status === 404) {
      return "العنصر المطلوب غير موجود";
    }
    if (status === 403) {
      return "ليس لديك صلاحية لتنفيذ هذه العملية";
    }
    if (status === 400) {
      return data?.error?.message || data?.error || "بيانات غير صحيحة";
    }
    if (status >= 500) {
      return "خطأ في الخادم. يرجى المحاولة لاحقاً";
    }
    if (err.code === "ECONNABORTED") {
      return "انتهت مهلة الاتصال. يرجى المحاولة مجدداً";
    }
    return data?.error?.message || data?.error || err.message;
  }
  return (err as Error)?.message || "حدث خطأ غير متوقع";
}

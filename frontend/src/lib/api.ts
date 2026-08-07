/**
 * Shared Axios instance.
 *
 * Every API call from the frontend goes through this singleton. The request
 * interceptor attaches the JWT (from the `erp_token` cookie) and the active
 * company id (from `erp_active_company`) to every request, so individual
 * call sites never need to think about headers.
 *
 * Sprint 34 hotfix v4 (2026-08-07): the user reported the pre-warm + silent
 * retry was causing extra requests against Render's free tier (which has
 * hard monthly usage limits). Each pre-warm on dashboard mount was an
 * additional GET to /api/health, and any cold-start 502 was doubled by
 * the silent retry. The cleanest fix: remove all proactive mechanisms.
 * Let the error show, let the user refresh.
 *
 * What this file does (final):
 *  - Request interceptor: attach auth + company header
 *  - Response interceptor: 401 → clear session + redirect to login
 *  - Timeout: 30s (long enough for normal requests, short enough that
 *    a stuck connection doesn't lock the page for a minute)
 *  - Error helper: friendly Arabic messages for common statuses
 *
 * What it doesn't do (intentionally):
 *  - No pre-warm (was: GET /api/health on page mount)
 *  - No retry on 502/503/504 (was: silent retry after 3s)
 *  - No background pings or polling
 *
 * The user is happy with: see an error → refresh the page → see the
 * data. That's the simplest model and the safest for the free tier.
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

// Response interceptor: 401 → clear session + redirect to login.
//
// Sprint 34 hotfix v4 (2026-08-07): removed pre-warm + silent retry.
// The user reported: "لا تعمل اسكربتات لكي تزعج الخادم اعتقد ان
// هناك حدود استخدام للخطه المجانيه" — every pre-warm call + every
// retry = extra request against Render's free tier, which has hard
// monthly limits. The pre-warm itself can fail with 502 and cause
// the same error the user was seeing. The cleanest fix: just let
// errors happen and show them. The user can refresh manually.
//
// What stayed:
//  - 401 handling (security-critical)
//  - Friendly Arabic error messages
//  - 30s axios timeout
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

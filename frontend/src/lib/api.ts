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

// Sprint 59 — Render Free Tier rate-limit fix: global cooldown flag.
// When any 429 fires, we set this to Date.now() + 30_000. The request
// interceptor below sees it and delays subsequent requests instead of
// letting them pile up and re-trigger 429s.
let globalCooldownUntil = 0;

// Request interceptor: attach auth + active company header
api.interceptors.request.use(async (config) => {
  // Sprint 59 — wait out the global cooldown if one is active.
  // Without this, when the dashboard or a page fires 5 parallel
  // requests after a 429, all 5 get 429 and all 5 retry, multiplying
  // the rate-limit pressure by 6. The cooldown makes them wait.
  const now = Date.now();
  if (globalCooldownUntil > now) {
    const waitMs = globalCooldownUntil - now;
    console.warn(`[api] global cooldown active — waiting ${waitMs}ms`);
    await new Promise((r) => setTimeout(r, waitMs));
  }
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
//                     429 → wait + retry (Render/Cloudflare rate limit)
//
// Sprint 59 — Render Free Tier rate-limit fix:
// Added a GLOBAL cooldown. When any request gets 429, ALL subsequent
// requests are blocked for 30 seconds. This prevents a "retry
// cascade" where multiple in-flight requests each get 429 and each
// retry, multiplying the load and making the rate limit recovery
// take even longer. The global flag is checked by the request
// interceptor below — see GLOBAL_COOLDOWN_UNTIL.
//
//
// Sprint 34 hotfix v4 (2026-08-07): removed pre-warm + silent retry.
// Sprint 45 (2026-08-13): added 429 retry. The user hit 429 across
// most pages — Render's free tier + Cloudflare rate-limit during
// burst navigation. We retry 429 (rate limit) up to 2 times with
// exponential backoff (3s, 6s), using the Retry-After header if
// present, otherwise the default 3s. We do NOT retry other errors
// (502/503/504 are kept as "show and let the user refresh" because
// those typically need a fresh page state — pre-warm retry was a
// footgun, as the Sprint 34 comment explains).
api.interceptors.response.use(
  (r) => r,
  async (err: AxiosError) => {
    if (err.response?.status === 401) {
      Cookies.remove("erp_token");
      Cookies.remove("erp_user");
      if (typeof window !== "undefined" && !window.location.pathname.startsWith("/auth/login")) {
        window.location.href = "/auth/login";
      }
      return Promise.reject(err);
    }

    // 429 retry: wait then retry. Cap at 1 retry.
    // Sprint 59 — Render Free Tier rate-limit fix.
    // The previous retry waited 3s then 6s = 9s total. That's not
    // long enough for the Render per-minute rate limit window to
    // reset, so the retry itself would get 429 too, doubling the
    // pressure on the rate limit. New strategy: wait 30s (half the
    // per-minute window) and cap at 1 retry. If still 429, surface
    // the error and let the user refresh — better than hammering
    // the rate limit.
    //
    // Also sets a GLOBAL cooldown so other in-flight requests stop
    // hammering the limit. Without this, a single 429 on a 5-request
    // parallel batch becomes 15 requests (5 × 3 with retries) which
    // makes the rate limit recovery take minutes.
    if (err.response?.status === 429) {
      // Set the global cooldown for 30 seconds
      globalCooldownUntil = Date.now() + 30_000;
      const config = err.config as any;
      config.__retryCount = config.__retryCount ?? 0;
      if (config.__retryCount >= 1) {
        return Promise.reject(err);
      }
      config.__retryCount += 1;
      // Honor Retry-After header if Cloudflare/Render sends one
      const retryAfter = parseInt(err.response.headers["retry-after"] ?? "", 10);
      const waitMs = Number.isFinite(retryAfter) && retryAfter > 0
        ? retryAfter * 1000
        : 30000; // 30s — half the per-minute rate-limit window
      await new Promise((r) => setTimeout(r, waitMs));
      return axios.request(config);
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
    if (status === 429) {
      return "تم تجاوز حد الاستخدام. جاري إعادة المحاولة تلقائياً...";
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

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
 * The response interceptor also implements a single cold-start retry: when
 * Render's free tier spins the backend down (after 15min of inactivity), the
 * first request on a fresh page load often hits a still-spinning container
 * and gets 502 / 503 / 504 / ECONNREFUSED. We detect that, warm the backend
 * with a /health probe, wait 2s, and retry the original request once. The
 * retry is capped at 1 to avoid loops. See the cold-start section below.
 *
 * IMPORTANT: we use a *relative* baseURL ("/api") so that all API calls
 * stay on the same origin. The Next.js rewrite (configured in
 * next.config.mjs as `/api/:path*` -> BACKEND_INTERNAL_URL) then forwards
 * them to the .NET backend. This means we don't need to bake
 * NEXT_PUBLIC_API_URL at build time, which would break every time Render
 * re-creates the backend with a new URL suffix (e.g. -86pf). See the
 * comment in render.yaml for the full rationale.
 */
import axios, { AxiosError, AxiosInstance, InternalAxiosRequestConfig } from "axios";
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

// =====================================================================
// Cold-start handling (Render free tier)
// =====================================================================
//
// Render's free tier spins the backend container down after 15 minutes
// of inactivity. When a user opens the app, the first request hits a
// still-spinning container and gets 502 / 503 / 504 / ECONNREFUSED. By
// the time the user retries, the backend is up — so the error is
// transient and confusing.
//
// To make this transparent, the response interceptor below detects
// cold-start failures and does ONE automatic retry:
//   1. Probe the backend's /health endpoint (this *blocks* until the
//      cold container is ready, so it acts as a wake-up).
//   2. Wait 2s for any final init steps.
//   3. Retry the original request.
//
// Cap: 1 retry. The flag `__coldStartRetryDone` on the request config
// marks the second attempt so we never loop.
//
// /health probe URL: relative to the backend host. In dev the backend
// is on :5000; in production on Render the public URL is
// https://erp-v2-backend-mkyg.onrender.com (matches the hardcoded
// fallback in next.config.mjs — keep both in sync if Render ever
// changes the URL).
const BACKEND_HEALTH_URL =
  typeof window !== "undefined" && window.location.hostname === "localhost"
    ? "http://localhost:5000/health"
    : "https://erp-v2-backend-mkyg.onrender.com/health";

/** Arabic message shown to the user when even the retry fails. */
const COLD_START_MESSAGE_AR = "الخادم يستيقظ — يرجى الانتظار ثانيتين";

/** A request is considered "cold-start" if the response is one of these or absent. */
export function isColdStartError(err: unknown): boolean {
  if (!axios.isAxiosError(err)) return false;
  const status = err.response?.status;
  if (status === 502 || status === 503 || status === 504) return true;
  // Network error: no response at all (ECONNREFUSED, DNS, CORS, etc.).
  if (!err.response) return true;
  return false;
}

// Combined interceptor: 401 (auth) + cold-start retry.
// Kept as one function so the order is explicit — auth redirect must
// never be skipped by the cold-start branch.
api.interceptors.response.use(
  (r) => r,
  async (err: AxiosError) => {
    // ---- 401 handling: clear session, redirect to login ----
    if (err.response?.status === 401) {
      Cookies.remove("erp_token");
      Cookies.remove("erp_user");
      if (typeof window !== "undefined" && !window.location.pathname.startsWith("/auth/login")) {
        window.location.href = "/auth/login";
      }
      return Promise.reject(err);
    }

    // ---- Cold-start retry ----
    // We tag the original request with `__coldStartRetryDone = true` on
    // the retry attempt, so the second failure falls through with the
    // friendly Arabic message instead of looping.
    const config = err.config as (InternalAxiosRequestConfig & { __coldStartRetryDone?: boolean }) | undefined;
    const isHealthProbe = config?.url?.includes("/health");
    const alreadyRetried = config?.__coldStartRetryDone === true;

    if (config && !isHealthProbe && !alreadyRetried && isColdStartError(err)) {
      // Mark before retrying so any nested interceptor re-entry on the
      // retry attempt is a no-op.
      config.__coldStartRetryDone = true;

      // Step 1: warm the backend via /health. The /health endpoint
      // returns 200 only after the container is fully up, so this
      // call effectively blocks until the cold start is done.
      try {
        await axios.get(BACKEND_HEALTH_URL, {
          timeout: 30_000, // worst-case cold start on Render free tier
          // Mark so any future interceptor on this exact call won't
          // also try to retry it. We bypass the `api` instance here
          // intentionally — the /health endpoint must not be sent
          // through the rewrite proxy and must not pick up auth
          // headers.
          headers: { "X-Cold-Start-Probe": "1" }
        });
      } catch {
        // /health itself failed (rare). Fall through to retry the
        // original request anyway — there's a small chance the
        // original endpoint is up even if /health is flaky.
      }

      // Step 2: brief extra delay, then retry the original request.
      await new Promise((res) => setTimeout(res, 2000));

      try {
        // Use api.request so the retry goes through the same
        // interceptors (auth header gets re-attached, etc.) but
        // __coldStartRetryDone is set so we won't loop.
        return await api.request(config);
      } catch (retryErr) {
        // Even the retry failed. Annotate the error so the UI can
        // show a friendly Arabic message instead of the raw axios
        // "Request failed with status code 502" red banner. Pages
        // can read err.coldStartMessage to differentiate this from
        // a true error.
        (retryErr as AxiosError & { coldStartMessage?: string }).coldStartMessage = COLD_START_MESSAGE_AR;
        return Promise.reject(retryErr);
      }
    }

    // Not a cold-start error (or already retried) — propagate as-is.
    return Promise.reject(err);
  }
);

/**
 * Extracts a user-friendly Arabic error message from an Axios or generic error.
 * Looks for the backend's `error` field first, then falls back to the raw error.
 * For cold-start errors that exhausted the retry, returns the Arabic cold-start
 * message set by the interceptor.
 */
export function getErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    // Cold-start retry exhausted — show the friendly Arabic message.
    const coldMsg = (err as AxiosError & { coldStartMessage?: string }).coldStartMessage;
    if (coldMsg) return coldMsg;
    const data = err.response?.data as any;
    return data?.error?.message || data?.error || err.message;
  }
  return (err as Error)?.message || "حدث خطأ غير متوقع";
}

/**
 * Shared Axios instance.
 *
 * Every API call from the frontend goes through this singleton. The request
 * interceptor attaches the JWT (from the `erp_token` cookie) and the active
 * company id (from `erp_active_company`) to every request, so individual
 * call sites never need to think about headers.
 *
 * The response interceptor:
 *  - Clears the session on 401 and redirects to login
 *  - Retries on 502/503/504 from Render cold-start (GET requests only!)
 *  - Fires "wakeup" events on the document so UI can show feedback
 *
 * Sprint 33 hotfix v2: the first version of this script was too aggressive.
 * 5 retries with delays of 1+2+4+8+16 = 31 seconds, plus 30-60s cold start,
 * meant the user could wait 90+ seconds for a single request to either
 * succeed or fail. The user reported "loading takes too long" and
 * "wrong info" after the retry script was added. Root cause was the retry
 * triggering on POST requests (login) which can create duplicate side
 * effects, AND triggering on timeouts (status 0) which meant the user saw
 * the spinner freeze for the full timeout window on every request.
 *
 * New behaviour:
 *  - Only GET requests are retried (POST/PUT/DELETE never retry — they
 *    could create duplicate records / double-charges)
 *  - Only 502/503/504 from Render are retryable, NOT network errors (0)
 *  - Max 2 retries (3 total attempts) with 4s + 8s delays
 *  - Axios timeout is 90s so the first cold-start attempt doesn't get
 *    killed before the backend wakes up
 *  - Slow requests (>5s) fire wakeup events even if they don't fail
 *
 * IMPORTANT: we use a *relative* baseURL ("/api") so that all API calls
 * stay on the same origin. The Next.js rewrite (configured in
 * next.config.mjs as `/api/:path*` -> BACKEND_INTERNAL_URL) then forwards
 * them to the .NET backend. This means we don't need to bake
 * NEXT_PUBLIC_API_URL at build time, which would break every time Render
 * re-creates the backend with a new URL suffix (e.g. -86pf). See the
 * comment in render.yaml for the full rationale.
 */
import axios, { AxiosError, AxiosInstance, InternalAxiosRequestConfig, Method } from "axios";
import Cookies from "js-cookie";

// =====================================================================
// Constants
// =====================================================================
const MAX_RETRIES = 2; // 3 total attempts max
const RETRY_DELAYS = [4000, 8000]; // 4s, 8s — covers Render cold start window
const RETRYABLE_STATUSES = new Set([502, 503, 504]); // Render cold-start only
const SAFE_METHODS: Method[] = ["get", "head", "options"]; // never retry POST/PUT/DELETE
const SLOW_REQUEST_MS = 5000; // show wakeup banner if request takes > 5s
const AXIOS_TIMEOUT_MS = 90000; // 90s — survive Render cold start

// =====================================================================
// Helpers
// =====================================================================
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

// Global "wakeup in progress" indicator
let wakeupInProgress: { count: number } | null = null;

const notifyWakeupStart = () => {
  if (!wakeupInProgress) {
    wakeupInProgress = { count: 0 };
    document.dispatchEvent(new CustomEvent("erp:wakeup-start"));
  }
};

const notifyWakeupRetry = (attempt: number) => {
  if (wakeupInProgress) {
    wakeupInProgress.count = attempt;
    document.dispatchEvent(new CustomEvent("erp:wakeup-retry", { detail: { count: attempt } }));
  }
};

const notifyWakeupEnd = () => {
  if (wakeupInProgress) {
    document.dispatchEvent(new CustomEvent("erp:wakeup-end"));
    wakeupInProgress = null;
  }
};

declare module "axios" {
  export interface InternalAxiosRequestConfig {
    __retryCount?: number;
    __startTime?: number;
  }
}

// =====================================================================
// Axios instance
// =====================================================================
export const api: AxiosInstance = axios.create({
  baseURL: "/api",
  headers: { "Content-Type": "application/json" },
  timeout: AXIOS_TIMEOUT_MS
});

// Request interceptor: attach auth + stamp start time
api.interceptors.request.use((config) => {
  const token = Cookies.get("erp_token");
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  const activeCompany = Cookies.get("erp_active_company");
  if (activeCompany) {
    config.headers["X-Company-Id"] = activeCompany;
  }
  // Stamp the start time so the response interceptor can detect
  // cold-start via long request duration (not just 502 status).
  (config as any).__startTime = Date.now();
  return config;
});

// =====================================================================
// Response interceptor #1: success / generic error timing
// =====================================================================
api.interceptors.response.use(
  (r) => {
    // If a request succeeds, we're definitely awake
    if (wakeupInProgress) notifyWakeupEnd();
    const start = (r.config as any).__startTime;
    if (start) {
      const elapsed = Date.now() - start;
      if (elapsed >= SLOW_REQUEST_MS) {
        // eslint-disable-next-line no-console
        console.log(`[api] slow request ${r.config.url} (${elapsed}ms) — backend was waking up`);
      }
    }
    return r;
  },
  (err) => {
    // Pass through to the retry handler. Don't return a rejected promise
    // here — let the next interceptor handle it.
    return Promise.reject(err);
  }
);

// =====================================================================
// Response interceptor #2: 401 + cold-start retry handler
// =====================================================================
api.interceptors.response.use(
  (r) => r, // success is already handled above
  async (err: AxiosError) => {
    // 401 → clear session
    if (err.response?.status === 401) {
      Cookies.remove("erp_token");
      Cookies.remove("erp_user");
      if (typeof window !== "undefined" && !window.location.pathname.startsWith("/auth/login")) {
        window.location.href = "/auth/login";
      }
      return Promise.reject(err);
    }

    const config = err.config as InternalAxiosRequestConfig | undefined;
    if (!config) return Promise.reject(err);

    // Safety: NEVER retry non-safe methods (POST/PUT/DELETE/PATCH).
    // Retrying a POST could create duplicate records / double-charge the
    // user. Only GET/HEAD/OPTIONS are safe to retry.
    const method = (config.method || "get").toLowerCase() as Method;
    if (!SAFE_METHODS.includes(method)) {
      notifyWakeupEnd();
      return Promise.reject(err);
    }

    // Only retry on Render cold-start signals (502/503/504).
    // Do NOT retry on:
    //   - status 0 (network error / timeout) — usually client-side
    //   - 4xx errors (caller's fault, retrying won't help)
    const status = err.response?.status ?? 0;
    if (!RETRYABLE_STATUSES.has(status)) {
      notifyWakeupEnd();
      return Promise.reject(err);
    }

    const retryCount = config.__retryCount ?? 0;
    if (retryCount >= MAX_RETRIES) {
      notifyWakeupEnd();
      return Promise.reject(err);
    }

    // Wait, then retry
    const delay = RETRY_DELAYS[retryCount] ?? 8000;
    config.__retryCount = retryCount + 1;
    if (retryCount === 0) notifyWakeupStart();
    notifyWakeupRetry(retryCount + 1);
    await sleep(delay);

    // eslint-disable-next-line no-console
    console.log(
      `[api] cold-start retry ${config.__retryCount}/${MAX_RETRIES} for ${config.url} (status=${status}, delay=${delay}ms)`
    );

    return api.request(config);
  }
);

// =====================================================================
// Error message helper
// =====================================================================
/**
 * Extracts a user-friendly Arabic error message from an Axios or generic error.
 * Looks for the backend's `error` field first, then falls back to the raw error.
 * Special-cases cold-start 502 with a friendlier Arabic message.
 */
export function getErrorMessage(err: unknown): string {
  if (axios.isAxiosError(err)) {
    const data = err.response?.data as any;
    const status = err.response?.status ?? 0;
    if (status === 502 || status === 503 || status === 504) {
      return "الخادم في وضع السكون (Cold Start). يرجى الانتظار 30-60 ثانية والمحاولة مجدداً";
    }
    if (err.code === "ECONNABORTED") {
      return "انتهت مهلة الاتصال. يرجى المحاولة مجدداً";
    }
    return data?.error?.message || data?.error || err.message;
  }
  return (err as Error)?.message || "حدث خطأ غير متوقع";
}

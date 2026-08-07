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
 *  - Retries on 502/503/504 (Render cold-start) with exponential backoff
 *
 * This is critical because Render's free tier spins down the backend
 * after ~15 min of inactivity. The first request after that gets a
 * 502 "Bad Gateway" while Render wakes the service. We retry up to 5
 * times (1s → 2s → 4s → 8s → 16s) which covers the 30-60s cold-start
 * window without making the user wait forever.
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

// Track retry attempts per request via a custom config field
declare module "axios" {
  export interface InternalAxiosRequestConfig {
    __retryCount?: number;
  }
}

const MAX_RETRIES = 5;
const RETRY_DELAYS = [1000, 2000, 4000, 8000, 16000]; // ms, exponential
const RETRYABLE_STATUSES = new Set([502, 503, 504, 0]); // 0 = network error

// Helper: wait
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

// Global "wakeup in progress" indicator — only one spinner at a time
let wakeupInProgress: { count: number; listener?: () => void } | null = null;
const WAKEUP_THRESHOLD = 2; // Show "waking up" UI after 2nd retry

const notifyWakeupStart = () => {
  if (!wakeupInProgress) {
    wakeupInProgress = { count: 0 };
    document.dispatchEvent(new CustomEvent("erp:wakeup-start"));
  }
  wakeupInProgress.count += 1;
  if (wakeupInProgress.count >= WAKEUP_THRESHOLD) {
    document.dispatchEvent(new CustomEvent("erp:wakeup-retry", { detail: { count: wakeupInProgress.count } }));
  }
};

const notifyWakeupEnd = () => {
  if (wakeupInProgress) {
    document.dispatchEvent(new CustomEvent("erp:wakeup-end"));
    wakeupInProgress = null;
  }
};

// Handle 401 + cold-start retries
api.interceptors.response.use(
  (r) => {
    // If a request succeeds, we're definitely awake
    if (wakeupInProgress) notifyWakeupEnd();
    return r;
  },
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

    // Cold-start retry: 502, 503, 504, or network error (status 0)
    const status = err.response?.status ?? 0;
    if (!RETRYABLE_STATUSES.has(status)) {
      return Promise.reject(err);
    }

    const config = err.config as InternalAxiosRequestConfig | undefined;
    if (!config) return Promise.reject(err);

    const retryCount = config.__retryCount ?? 0;
    if (retryCount >= MAX_RETRIES) {
      notifyWakeupEnd();
      return Promise.reject(err);
    }

    // Wait, then retry
    const delay = RETRY_DELAYS[retryCount] ?? 16000;
    config.__retryCount = retryCount + 1;
    if (retryCount === 0) notifyWakeupStart();
    await sleep(delay);

    // eslint-disable-next-line no-console
    console.log(
      `[api] cold-start retry ${config.__retryCount}/${MAX_RETRIES} for ${config.url} (status=${status}, delay=${delay}ms)`
    );

    return api.request(config);
  }
);

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
    return data?.error?.message || data?.error || err.message;
  }
  return (err as Error)?.message || "حدث خطأ غير متوقع";
}

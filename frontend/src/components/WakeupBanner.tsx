"use client";

/**
 * WakeupBanner — global "server is waking up" indicator.
 *
 * Listens to the `erp:wakeup-start`, `erp:wakeup-retry`, and
 * `erp:wakeup-end` CustomEvents dispatched by the api.ts response
 * interceptor.
 *
 * Rendered once in the root layout so it's visible on every page.
 *
 * Behaviour:
 *  - First retry (after 1s): banner appears
 *  - Subsequent retries: shows retry count
 *  - Request succeeds: banner disappears
 *  - Max retries hit: banner stays but switches to "failed" tone
 */

import { useEffect, useState } from "react";
import { Loader2, Server } from "lucide-react";

export default function WakeupBanner() {
  const [visible, setVisible] = useState(false);
  const [retryCount, setRetryCount] = useState(0);

  useEffect(() => {
    const onStart = () => {
      setVisible(true);
      setRetryCount(1);
    };
    const onRetry = (e: Event) => {
      const ce = e as CustomEvent<{ count: number }>;
      setVisible(true);
      setRetryCount(ce.detail?.count ?? retryCount + 1);
    };
    const onEnd = () => {
      setVisible(false);
      setRetryCount(0);
    };

    document.addEventListener("erp:wakeup-start", onStart);
    document.addEventListener("erp:wakeup-retry", onRetry as EventListener);
    document.addEventListener("erp:wakeup-end", onEnd);

    return () => {
      document.removeEventListener("erp:wakeup-start", onStart);
      document.removeEventListener("erp:wakeup-retry", onRetry as EventListener);
      document.removeEventListener("erp:wakeup-end", onEnd);
    };
  }, [retryCount]);

  if (!visible) return null;

  return (
    <div
      role="status"
      aria-live="polite"
      className="fixed top-0 left-0 right-0 z-50 bg-amber-50 border-b border-amber-300 text-amber-900 px-4 py-2 flex items-center justify-center gap-3 text-sm shadow-md"
      style={{ direction: "rtl" }}
    >
      <Loader2 className="animate-spin" size={16} />
      <Server size={16} className="text-amber-700" />
      <span className="font-medium">
        الخادم في وضع السكون — جاري إعادة الاتصال...
      </span>
      <span className="text-xs text-amber-700 bg-amber-100 px-2 py-0.5 rounded-full">
        محاولة {retryCount} من 5
      </span>
    </div>
  );
}

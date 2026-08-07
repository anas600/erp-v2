"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { Building2, LogIn, AlertCircle, Loader2, Server } from "lucide-react";

export default function LoginPage() {
  const { login, user, loading } = useAuth();
  const router = useRouter();
  const [email, setEmail] = useState("admin@holding.ly");
  const [password, setPassword] = useState("admin123");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  // Tracks whether the backend is currently waking up (Render cold start).
  // Set by the api.ts interceptor via the global erp:wakeup-start event.
  const [wakeup, setWakeup] = useState(false);
  const [elapsed, setElapsed] = useState(0);

  useEffect(() => {
    if (!loading && user) router.push("/dashboard");
  }, [user, loading, router]);

  // Listen for global wakeup events
  useEffect(() => {
    const onStart = () => { setWakeup(true); setElapsed(0); };
    const onEnd = () => { setWakeup(false); setElapsed(0); };
    document.addEventListener("erp:wakeup-start", onStart);
    document.addEventListener("erp:wakeup-end", onEnd);
    return () => {
      document.removeEventListener("erp:wakeup-start", onStart);
      document.removeEventListener("erp:wakeup-end", onEnd);
    };
  }, []);

  // Tick the elapsed timer once a second while wakeup is showing
  useEffect(() => {
    if (!wakeup) return;
    const t = setInterval(() => setElapsed((e) => e + 1), 1000);
    return () => clearInterval(t);
  }, [wakeup]);

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      const res = await login(email, password);
      if (!res.ok) {
        setError(res.error || "فشل تسجيل الدخول");
      }
    } catch (err: any) {
      setError(err?.message || "فشل تسجيل الدخول");
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="min-h-screen bg-gradient-to-br from-primary-50 to-primary-100 flex items-center justify-center p-4">
      <div className="w-full max-w-md">
        <div className="text-center mb-6">
          <div className="inline-flex items-center justify-center w-16 h-16 bg-primary-600 text-white rounded-full mb-3">
            <Building2 size={32} />
          </div>
          <h1 className="text-3xl font-bold text-gray-900">ERP-V2</h1>
          <p className="text-gray-600 mt-1">نظام إدارة الشركات المتعددة</p>
        </div>

        <div className="card">
          <h2 className="text-xl font-semibold mb-4 text-center">تسجيل الدخول</h2>

          {/* Cold-start banner — shown inside the login form so the
              user gets explicit feedback during a 30-60s wake-up */}
          {wakeup && (
            <div className="mb-4 p-3 bg-amber-50 border border-amber-200 rounded-md flex items-start gap-2">
              <Server size={18} className="text-amber-700 mt-0.5 flex-shrink-0" />
              <div className="text-sm text-amber-900 flex-1">
                <p className="font-semibold">جاري إيقاظ الخادم...</p>
                <p className="text-xs text-amber-800 mt-1">
                  الخدمة في وضع السكون — يستيقظ خلال 30-60 ثانية. يرجى الانتظار وعدم تحديث الصفحة.
                  ({elapsed} ثانية)
                </p>
              </div>
            </div>
          )}

          {error && (
            <div className="mb-4 p-3 bg-red-50 border border-red-200 rounded-md flex items-start gap-2">
              <AlertCircle size={18} className="text-red-600 mt-0.5" />
              <span className="text-sm text-red-700">{error}</span>
            </div>
          )}

          <form onSubmit={onSubmit} className="space-y-4">
            <div>
              <label className="block text-sm font-medium mb-1">البريد الإلكتروني</label>
              <input
                type="email"
                className="input"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                placeholder="admin@holding.ly"
                required
                dir="ltr"
                disabled={submitting}
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">كلمة المرور</label>
              <input
                type="password"
                className="input"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder="••••••••"
                required
                dir="ltr"
                disabled={submitting}
              />
            </div>
            <button type="submit" disabled={submitting} className="btn-primary w-full">
              {submitting ? (
                <>
                  <Loader2 size={18} className="animate-spin" />
                  {wakeup ? "جاري إيقاظ الخادم..." : "جاري الدخول..."}
                </>
              ) : (
                <>
                  <LogIn size={18} />
                  دخول
                </>
              )}
            </button>
          </form>

          <div className="mt-6 pt-4 border-t border-gray-200">
            <p className="text-xs text-gray-500 mb-2 font-semibold">حسابات تجريبية:</p>
            <div className="space-y-1 text-xs text-gray-600">
              <div className="flex justify-between">
                <span>مدير عام:</span>
                <code className="text-gray-800">admin@holding.ly / admin123</code>
              </div>
              <div className="flex justify-between">
                <span>محاسب:</span>
                <code className="text-gray-800">accountant@company-a.ly / acc123</code>
              </div>
              <div className="flex justify-between">
                <span>مهندس:</span>
                <code className="text-gray-800">engineer@company-a.ly / eng123</code>
              </div>
            </div>
          </div>
        </div>

        <p className="text-center text-xs text-gray-500 mt-6">
          © 2026 ERP-V2 — جميع الحقوق محفوظة
        </p>
      </div>
    </div>
  );
}

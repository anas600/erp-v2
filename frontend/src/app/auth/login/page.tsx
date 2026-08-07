"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { Building2, LogIn, AlertCircle, Loader2, Server } from "lucide-react";
import { prewarmBackend } from "@/lib/api";

export default function LoginPage() {
  const { login, user, loading } = useAuth();
  const router = useRouter();
  const [email, setEmail] = useState("admin@holding.ly");
  const [password, setPassword] = useState("admin123");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  // Sprint 34 hotfix — when the backend is cold-starting, show
  // a small inline hint that the login may take a few extra seconds.
  // The pre-warm tries to avoid this, but it's not a guarantee.
  const [wakeup, setWakeup] = useState(false);

  useEffect(() => {
    if (!loading && user) router.push("/dashboard");
  }, [user, loading, router]);

  // Pre-warm on mount so the first POST /auth/login is more likely
  // to land on a warm backend.
  useEffect(() => {
    prewarmBackend();
  }, []);

  const onSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    setWakeup(false);
    // Pre-warm just before the login attempt — if the backend is
    // cold, this wakes it up so the login request succeeds.
    await prewarmBackend();
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
                  جاري الدخول...
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

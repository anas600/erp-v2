"use client";

import { useState, useEffect } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { Building2, LogIn, AlertCircle, Loader2 } from "lucide-react";

/**
 * Login page — Sprint 37 design refresh.
 *
 * The login page is the "hero" of the app — first impression for
 * every user. We use the brand-gradient as a left panel on wide
 * screens (lg+) and a soft teal-tinted background on mobile.
 *
 * Layout: split-pane.
 *   - Left  (1/2, hidden on mobile): the gradient panel with
 *           brand mark + tagline.
 *   - Right (1/2, full width on mobile): the actual login form.
 *
 * Both panes must work in dark mode. The left panel stays dark
 * regardless of theme (it's already a dark gradient). The right
 * panel uses the bg-primary semantic token.
 */
export default function LoginPage() {
  const { login, user, loading } = useAuth();
  const router = useRouter();
  const [email, setEmail] = useState("admin@holding.ly");
  const [password, setPassword] = useState("admin123");
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    if (!loading && user) router.push("/dashboard");
  }, [user, loading, router]);

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
    <div className="min-h-screen flex flex-col lg:flex-row bg-canvas dark:bg-neutral-950">
      {/* Left panel — hero (hidden on mobile) */}
      <div className="hidden lg:flex lg:w-1/2 bg-hero-gradient items-center justify-center p-12 text-white">
        <div className="max-w-md">
          <div className="inline-flex items-center justify-center w-16 h-16 bg-white/10 backdrop-blur rounded-card mb-6 border border-white/20">
            <Building2 size={32} />
          </div>
          <h1 className="text-4xl font-bold mb-3">ERP-V2</h1>
          <p className="text-lg text-white/80 mb-6">
            نظام إدارة الشركات المتعددة
          </p>
          <ul className="space-y-2 text-white/70 text-sm">
            <li className="flex items-center gap-2">
              <span className="w-1.5 h-1.5 bg-primary-300 rounded-full"></span>
              محاسبة متعددة الشركات
            </li>
            <li className="flex items-center gap-2">
              <span className="w-1.5 h-1.5 bg-primary-300 rounded-full"></span>
              فوترة وقيود يومية
            </li>
            <li className="flex items-center gap-2">
              <span className="w-1.5 h-1.5 bg-primary-300 rounded-full"></span>
              تقارير مالية لحظية
            </li>
          </ul>
        </div>
      </div>

      {/* Right panel — form */}
      <div className="flex-1 flex items-center justify-center p-6 lg:p-12">
        <div className="w-full max-w-md">
          {/* Mobile-only logo */}
          <div className="lg:hidden text-center mb-8">
            <div className="inline-flex items-center justify-center w-14 h-14 bg-primary-700 text-white rounded-full mb-3">
              <Building2 size={28} />
            </div>
            <h1 className="text-2xl font-bold text-ink-strong">ERP-V2</h1>
            <p className="text-ink-muted text-sm mt-1">نظام إدارة الشركات المتعددة</p>
          </div>

          <div className="bg-canvas dark:bg-neutral-900 border border-edge rounded-card p-6 sm:p-8 shadow-sm">
            <h2 className="text-xl font-semibold mb-6 text-center text-ink-strong">تسجيل الدخول</h2>

            {error && (
              <div className="mb-4 p-3 bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-800 rounded-md flex items-start gap-2">
                <AlertCircle size={18} className="text-red-600 dark:text-red-400 mt-0.5" />
                <span className="text-sm text-red-700 dark:text-red-300">{error}</span>
              </div>
            )}

            <form onSubmit={onSubmit} className="space-y-4">
              <div>
                <label className="block text-sm font-medium mb-1.5 text-ink-strong">البريد الإلكتروني</label>
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
                <label className="block text-sm font-medium mb-1.5 text-ink-strong">كلمة المرور</label>
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

            <div className="mt-6 pt-4 border-t border-edge">
              <p className="text-xs text-ink-muted mb-2 font-semibold">حسابات تجريبية:</p>
              <div className="space-y-1 text-xs text-ink-muted">
                <div className="flex justify-between">
                  <span>مدير عام:</span>
                  <code className="text-ink-strong">admin@holding.ly / admin123</code>
                </div>
                <div className="flex justify-between">
                  <span>محاسب:</span>
                  <code className="text-ink-strong">accountant@company-a.ly / acc123</code>
                </div>
                <div className="flex justify-between">
                  <span>مهندس:</span>
                  <code className="text-ink-strong">engineer@company-a.ly / eng123</code>
                </div>
              </div>
            </div>
          </div>

          <p className="text-center text-xs text-ink-subtle mt-6">
            © 2026 ERP-V2 — جميع الحقوق محفوظة
          </p>
        </div>
      </div>
    </div>
  );
}

"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import {
  Wrench, Trash2, Database, AlertTriangle, Loader2, CheckCircle2,
  AlertCircle, RefreshCw, BarChart3, Lock, TreePine
} from "lucide-react";

/**
 * Admin tools — Sprint 26.
 *
 * Three super-admin-only actions, all guarded behind a confirm
 * dialog because they touch the database directly:
 *
 *   1. تنظيف البيانات (cleanup)
 *      POST /api/admin/cleanup-transactions — wipes all invoices,
 *      vouchers, journal entries, and zeroes out account balances.
 *      Keeps the structure (companies, accounts L1-L3, contacts).
 *
 *   2. بيانات تجريبية (seed)
 *      POST /api/admin/seed-demo-data — creates 5 customers +
 *      3 suppliers + 10 invoices + 5 receipts + 2 payments.
 *      Sprint 26's backend may or may not implement the seed
 *      endpoint yet; the UI handles a 404 gracefully.
 *
 *   3. إعادة تعيين (reset = cleanup + seed)
 *      Runs both in sequence with the same dialogs.
 *
 * We also surface a live db-stats card so the super-admin can
 * see row counts before/after each action.
 *
 * The endpoints require the `is_super_admin` claim in the JWT.
 * If the current user is not a super-admin, the action buttons
 * are disabled and a clear message is shown.
 */
export default function AdminPage() {
  const { user, activeCompany } = useAuth();
  const isSuperAdmin = !!user?.isSuperAdmin;

  const [stats, setStats] = useState<Record<string, number> | null>(null);
  const [statsLoading, setStatsLoading] = useState(false);

  const [cleanupBusy, setCleanupBusy] = useState(false);
  const [cleanupMsg, setCleanupMsg] = useState<{ ok: boolean; text: string } | null>(null);

  const [seedBusy, setSeedBusy] = useState(false);
  const [seedMsg, setSeedMsg] = useState<{ ok: boolean; text: string } | null>(null);

  const [resetBusy, setResetBusy] = useState(false);
  const [resetMsg, setResetMsg] = useState<{ ok: boolean; text: string } | null>(null);

  // Sprint 31 — full COA reseed (drops all accounts + journals, re-inserts the standard 4-level COA)
  const [coaBusy, setCoaBusy] = useState(false);
  const [coaMsg, setCoaMsg] = useState<{ ok: boolean; text: string } | null>(null);

  const loadStats = async () => {
    if (!isSuperAdmin) return;
    setStatsLoading(true);
    try {
      const res = await api.get("/admin/db-stats");
      setStats(res.data);
    } catch (err) {
      // Stats are nice-to-have; don't fail the whole page.
      console.warn("Failed to load db-stats:", err);
    } finally {
      setStatsLoading(false);
    }
  };

  useEffect(() => { loadStats(); }, [isSuperAdmin]);

  // ─── Action: cleanup ─────────────────────────────────────────────────
  const doCleanup = async () => {
    if (!confirm(
      "⚠️ تنظيف البيانات سيمسح كل الفواتير والسندات والقيود. " +
      "الشركات والحسابات (L1-L3) وجهات الاتصال ستبقى سليمة. " +
      "هل أنت متأكد؟"
    )) return;
    setCleanupBusy(true);
    setCleanupMsg(null);
    try {
      const res = await api.post("/admin/cleanup-transactions");
      // Backend returns a counts object like
      //   { invoices: 10, invoice_lines: 25, journal_entries: 12, ... }
      const counts = res.data || {};
      const parts = Object.entries(counts)
        .filter(([_, v]) => typeof v === "number" && v > 0)
        .map(([k, v]) => `${k}: ${v}`)
        .join("، ");
      setCleanupMsg({
        ok: true,
        text: parts
          ? `تم مسح ${parts} صف`
          : "تم مسح البيانات بنجاح"
      });
      await loadStats();
    } catch (err) {
      setCleanupMsg({ ok: false, text: getErrorMessage(err) });
    } finally {
      setCleanupBusy(false);
    }
  };

  // ─── Action: seed ────────────────────────────────────────────────────
  const doSeed = async () => {
    if (!confirm(
      "سيتم إنشاء 5 عملاء + 3 موردين + 10 فواتير + 5 سندات قبض + 2 سندات صرف. " +
      "تابع؟"
    )) return;
    setSeedBusy(true);
    setSeedMsg(null);
    try {
      const res = await api.post("/admin/seed-demo-data");
      const counts = res.data || {};
      const parts = Object.entries(counts)
        .filter(([_, v]) => typeof v === "number" && v > 0)
        .map(([k, v]) => `${k}: ${v}`)
        .join("، ");
      setSeedMsg({
        ok: true,
        text: parts
          ? `تم إنشاء: ${parts}`
          : "تم إنشاء البيانات التجريبية"
      });
      await loadStats();
    } catch (err) {
      const msg = getErrorMessage(err);
      // If the endpoint is not yet implemented (404), surface a
      // clear "coming soon" message rather than a stack trace.
      if (msg.includes("404") || msg.toLowerCase().includes("not found")) {
        setSeedMsg({
          ok: false,
          text: "endpoint غير متاح بعد — سيُفعّل في Sprint 26 backend"
        });
      } else {
        setSeedMsg({ ok: false, text: msg });
      }
    } finally {
      setSeedBusy(false);
    }
  };

  // ─── Action: reset (cleanup + seed) ───────────────────────────────────
  const doReset = async () => {
    if (!confirm(
      "⚠️ إعادة تعيين كاملة: سيتم مسح كل البيانات الحالية ثم إنشاء البيانات التجريبية. " +
      "هذه العملية لا يمكن التراجع عنها. هل أنت متأكد؟"
    )) return;
    setResetBusy(true);
    setResetMsg(null);
    try {
      // 1) Cleanup
      const cleanupRes = await api.post("/admin/cleanup-transactions");
      // 2) Seed
      const seedRes = await api.post("/admin/seed-demo-data");
      const cl = cleanupRes.data || {};
      const sd = seedRes.data || {};
      setResetMsg({
        ok: true,
        text: `تم المسح (${Object.keys(cl).length} جدول) ثم الإنشاء (${Object.keys(sd).length} جدول)`
      });
      await loadStats();
    } catch (err) {
      const msg = getErrorMessage(err);
      setResetMsg({ ok: false, text: msg });
    } finally {
      setResetBusy(false);
    }
  };

  // ─── Action: reseed COA (Sprint 31) ──────────────────────────────────
  const doReseedCoa = async () => {
    if (!confirm(
      "⚠️ إعادة بناء دليل الحسابات: سيتم حذف جميع الحسابات (L1-L4) وجميع القيود اليومية المرتبطة بها.\n\n" +
      "سيتم بعد ذلك إعادة إدراج الهيكل الموحد الجديد (4 مستويات) مع الأكواد الصحيحة:\n" +
      "  - 6 حسابات L1 (الأصول، الخصوم، إلخ)\n" +
      "  - 13 حساب L2 (تصنيفات فرعية)\n" +
      "  - 50 حساب L3 (الحسابات العامة)\n" +
      "  - 0 حسابات L4 (تُنشأ لاحقاً من جهات الاتصال والمشاريع)\n\n" +
      "⚠️ هذه العملية لا يمكن التراجع عنها. هل أنت متأكد؟"
    )) return;
    if (!activeCompany) return;
    setCoaBusy(true);
    setCoaMsg(null);
    try {
      const res = await api.post(`/admin/reseed-coa?companyId=${activeCompany.id}`);
      const { l1Count, l2Count, l3Count } = res.data;
      setCoaMsg({
        ok: true,
        text: `تم بنجاح: L1=${l1Count}، L2=${l2Count}، L3=${l3Count}`
      });
      await loadStats();
    } catch (err) {
      setCoaMsg({ ok: false, text: getErrorMessage(err) });
    } finally {
      setCoaBusy(false);
    }
  };

  // ─── Render ──────────────────────────────────────────────────────────
  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Wrench size={24} className="text-primary-600" />
            أدوات المدير
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            إجراءات حساسة على قاعدة البيانات — تتطلب صلاحية المدير العام
          </p>
        </div>
        {!isSuperAdmin && (
          <div className="flex items-center gap-2 text-amber-700 bg-amber-50 px-3 py-2 rounded-md text-sm">
            <Lock size={14} />
            هذه الصفحة للمدير العام فقط
          </div>
        )}
      </div>

      {/* DB stats card */}
      <div className="card mb-4">
        <div className="flex items-center justify-between mb-3">
          <h2 className="text-sm font-semibold text-gray-700 flex items-center gap-2">
            <BarChart3 size={16} />
            إحصائيات قاعدة البيانات
          </h2>
          <button
            onClick={loadStats}
            disabled={statsLoading || !isSuperAdmin}
            className="text-xs flex items-center gap-1 text-primary-600 hover:text-primary-800 disabled:opacity-50"
          >
            <RefreshCw size={12} className={statsLoading ? "animate-spin" : ""} />
            تحديث
          </button>
        </div>
        {stats ? (
          <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-3 text-sm">
            {Object.entries(stats).map(([k, v]) => (
              <div key={k} className="bg-gray-50 rounded-md px-3 py-2">
                <div className="text-xs text-gray-500" dir="ltr">{k}</div>
                <div className="text-lg font-bold text-gray-900" dir="ltr">{v}</div>
              </div>
            ))}
          </div>
        ) : (
          <p className="text-sm text-gray-500">
            {isSuperAdmin ? "جاري التحميل..." : "غير متاح — يلزم صلاحية المدير العام"}
          </p>
        )}
      </div>

      {/* Action cards */}
      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        {/* Card 1: cleanup */}
        <div className="card border-red-200">
          <div className="flex items-start gap-3">
            <div className="w-10 h-10 rounded-md bg-red-100 text-red-700 flex items-center justify-center flex-shrink-0">
              <Trash2 size={20} />
            </div>
            <div className="flex-1">
              <h2 className="text-base font-semibold text-gray-900">تنظيف البيانات</h2>
              <p className="text-sm text-gray-600 mt-1">
                يمسح كل الفواتير والقيود والسندات. يُصفّر أرصدة الحسابات.
                يحتفظ بالهيكل (الشركات، الحسابات L1-L3، جهات الاتصال).
              </p>
              <button
                onClick={doCleanup}
                disabled={!isSuperAdmin || cleanupBusy}
                className="mt-3 inline-flex items-center gap-1 px-3 py-1.5 rounded-md bg-red-600 text-white text-sm font-medium hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {cleanupBusy ? (
                  <Loader2 className="animate-spin" size={14} />
                ) : (
                  <Trash2 size={14} />
                )}
                تنظيف البيانات
              </button>
              {cleanupMsg && (
                <div
                  className={`mt-2 text-xs flex items-start gap-1 ${
                    cleanupMsg.ok ? "text-green-700" : "text-red-700"
                  }`}
                >
                  {cleanupMsg.ok ? (
                    <CheckCircle2 size={12} className="mt-0.5" />
                  ) : (
                    <AlertCircle size={12} className="mt-0.5" />
                  )}
                  <span>{cleanupMsg.text}</span>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Card 2: seed */}
        <div className="card border-green-200">
          <div className="flex items-start gap-3">
            <div className="w-10 h-10 rounded-md bg-green-100 text-green-700 flex items-center justify-center flex-shrink-0">
              <Database size={20} />
            </div>
            <div className="flex-1">
              <h2 className="text-base font-semibold text-gray-900">بيانات تجريبية</h2>
              <p className="text-sm text-gray-600 mt-1">
                ينشئ 5 عملاء + 3 موردين + 10 فواتير + 5 سندات قبض + 2 سندات صرف.
                مفيد للعروض التوضيحية والاختبار.
              </p>
              <button
                onClick={doSeed}
                disabled={!isSuperAdmin || seedBusy}
                className="mt-3 inline-flex items-center gap-1 px-3 py-1.5 rounded-md bg-green-600 text-white text-sm font-medium hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {seedBusy ? (
                  <Loader2 className="animate-spin" size={14} />
                ) : (
                  <Database size={14} />
                )}
                إنشاء البيانات
              </button>
              {seedMsg && (
                <div
                  className={`mt-2 text-xs flex items-start gap-1 ${
                    seedMsg.ok ? "text-green-700" : "text-red-700"
                  }`}
                >
                  {seedMsg.ok ? (
                    <CheckCircle2 size={12} className="mt-0.5" />
                  ) : (
                    <AlertCircle size={12} className="mt-0.5" />
                  )}
                  <span>{seedMsg.text}</span>
                </div>
              )}
            </div>
          </div>
        </div>

        {/* Card 3: reset (cleanup + seed) */}
        <div className="card md:col-span-2 border-amber-200 bg-amber-50/30">
          <div className="flex items-start gap-3">
            <div className="w-10 h-10 rounded-md bg-amber-100 text-amber-700 flex items-center justify-center flex-shrink-0">
              <AlertTriangle size={20} />
            </div>
            <div className="flex-1">
              <h2 className="text-base font-semibold text-gray-900">
                إعادة تعيين للبيانات التجريبية
              </h2>
              <p className="text-sm text-gray-600 mt-1">
                يدمج العمليتين: يمسح كل البيانات الحالية ثم ينشئ البيانات التجريبية.
                استخدم هذا الإجراء بعد العرض التوضيحي لإعادة النظام إلى حالة قابلة للاختبار.
              </p>
              <button
                onClick={doReset}
                disabled={!isSuperAdmin || resetBusy}
                className="mt-3 inline-flex items-center gap-1 px-3 py-1.5 rounded-md bg-amber-600 text-white text-sm font-medium hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed"
              >
                {resetBusy ? (
                  <Loader2 className="animate-spin" size={14} />
                ) : (
                  <AlertTriangle size={14} />
                )}
                إعادة تعيين كاملة
              </button>
              {resetMsg && (
                <div
                  className={`mt-2 text-xs flex items-start gap-1 ${
                    resetMsg.ok ? "text-green-700" : "text-red-700"
                  }`}
                >
                  {resetMsg.ok ? (
                    <CheckCircle2 size={12} className="mt-0.5" />
                  ) : (
                    <AlertCircle size={12} className="mt-0.5" />
                  )}
                  <span>{resetMsg.text}</span>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Sprint 31 — COA Reseed */}
      <div className="card border-2 border-amber-200 bg-amber-50/30 mt-4">
        <h2 className="text-lg font-semibold mb-3 flex items-center gap-2 text-amber-900">
          <TreePine size={20} />
          إعادة بناء دليل الحسابات الموحد
        </h2>
        <p className="text-sm text-gray-700 mb-3">
          يحذف جميع الحسابات (L1-L4) وجميع القيود اليومية المرتبطة ويُعيد بناء الهيكل الموحد الجديد
          (L1: 6، L2: 13، L3: 50 حساب) بأكواد صحيحة. استخدمه مرة واحدة لتطبيق الهيكل المقفل.
        </p>
        <div className="flex items-center gap-3 flex-wrap">
          <button
            onClick={doReseedCoa}
            disabled={!isSuperAdmin || coaBusy}
            className="btn-primary bg-amber-600 hover:bg-amber-700"
          >
            {coaBusy ? <Loader2 className="animate-spin" size={16} /> : <TreePine size={16} />}
            إعادة بناء COA
          </button>
          {coaMsg && (
            <div
              className={`text-xs flex items-center gap-1 ${
                coaMsg.ok ? "text-green-700" : "text-red-700"
              }`}
            >
              {coaMsg.ok ? <CheckCircle2 size={12} /> : <AlertCircle size={12} />}
              <span>{coaMsg.text}</span>
            </div>
          )}
        </div>
      </div>

      {/* Warning footer */}
      <div className="mt-4 p-3 bg-amber-50 text-amber-900 rounded-md text-sm flex items-start gap-2 border border-amber-200">
        <AlertTriangle size={16} className="mt-0.5 flex-shrink-0" />
        <div>
          <strong>تحذير:</strong> جميع الإجراءات هنا تمس قاعدة البيانات مباشرة. لا يمكن التراجع عنها.
          يُنصح بأخذ نسخة احتياطية قبل الاستخدام في بيئة الإنتاج.
        </div>
      </div>
    </div>
  );
}

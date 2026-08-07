"use client";

import { useEffect, useState, useCallback } from "react";
import { useRouter } from "next/navigation";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { BarChart3, Loader2, CheckCircle, XCircle, RefreshCw, BookOpen } from "lucide-react";
import { formatNumber } from "@/lib/utils";

interface TrialBalance {
  companyId: string;
  companyName: string;
  asOfDate: string;
  lines: Array<{
    code: string;
    name: string;
    accountType: string;
    nature: string;
    debitBalance: number;
    creditBalance: number;
  }>;
  totalDebit: number;
  totalCredit: number;
  balanced: boolean;
}

export default function TrialBalancePage() {
  const { activeCompany } = useAuth();
  const router = useRouter();
  const [report, setReport] = useState<TrialBalance | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Code → accountId map for the GL drill-down. The TB lines
  // don't include the id, so we look it up here.
  const [accountIdByCode, setAccountIdByCode] = useState<Record<string, string>>({});

  const load = useCallback(async (isManualRefresh = false) => {
    if (!activeCompany) return;
    try {
      if (isManualRefresh) setRefreshing(true);
      else setLoading(true);
      const res = await api.get(`/reports/trial-balance?companyId=${activeCompany.id}`);
      setReport(res.data);
      setError(null);

      // Build a code→id map for the GL drill-down. The TB doesn't
      // include account ids, so we fetch the chart of accounts
      // separately and flatten the tree.
      try {
        const accRes = await api.get(`/accounts?companyId=${activeCompany.id}`);
        const raw = Array.isArray(accRes.data) ? accRes.data : (accRes.data?.data || []);
        const map: Record<string, string> = {};
        const walk = (n: any) => {
          if (n?.code && n?.id) map[n.code] = n.id;
          (n?.children || []).forEach(walk);
        };
        raw.forEach(walk);
        setAccountIdByCode(map);
      } catch {
        // Non-fatal: GL drill-down won't work but the page still loads
      }
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [activeCompany]);

  /**
   * Sprint 33: clicking a TB row navigates to the General Ledger
   * for that account. The full fiscal year is the default range
   * so the user sees the entire history. They can adjust later.
   */
  const openGL = (code: string) => {
    const accountId = accountIdByCode[code];
    if (!accountId) return;
    const yearStart = `${new Date().getFullYear()}-01-01`;
    const today = new Date().toISOString().slice(0, 10);
    router.push(
      `/dashboard/reports/general-ledger?accountId=${accountId}&from=${yearStart}&to=${today}`
    );
  };

  // Re-fetch when the page mounts OR when the user comes back to the
  // tab/window. This solves the "stale data after creating a journal
  // entry" UX issue — Next.js App Router caches visited routes, so
  // useEffect-on-mount doesn't re-fire when you navigate away and back.
  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    const onFocus = () => load(true);
    window.addEventListener("focus", onFocus);
    return () => window.removeEventListener("focus", onFocus);
  }, [load]);

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <Loader2 className="animate-spin text-primary-500" size={32} />
      </div>
    );
  }

  if (!report) {
    return <div className="text-center text-gray-500">لا توجد بيانات</div>;
  }

  return (
    <div>
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <BarChart3 size={24} className="text-primary-600" />
            ميزان المراجعة
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            {report.companyName} •截至 {new Date(report.asOfDate).toLocaleDateString("en-GB")}
          </p>
        </div>
        <div className="flex items-center gap-3">
          <button
            onClick={() => load(true)}
            disabled={refreshing}
            className="btn-secondary flex items-center gap-1 text-sm"
            title="إعادة تحميل البيانات"
          >
            <RefreshCw size={14} className={refreshing ? "animate-spin" : ""} />
            {refreshing ? "جاري التحديث..." : "تحديث"}
          </button>
          {report.balanced ? (
            <span className="badge badge-success text-base px-3 py-1">
              <CheckCircle size={14} className="ml-1" /> متوازن
            </span>
          ) : (
            <span className="badge badge-danger text-base px-3 py-1">
              <XCircle size={14} className="ml-1" /> غير متوازن
            </span>
          )}
        </div>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      <div className="card">
        <table className="table">
          <thead>
            <tr>
              <th>الكود</th>
              <th>الحساب</th>
              <th>النوع</th>
              <th>الطبيعة</th>
              <th className="text-left">مدين</th>
              <th className="text-left">دائن</th>
            </tr>
          </thead>
          <tbody>
            {report.lines.map((l, idx) => {
              // A row is "drillable" if it has a balance AND we know
              // the account id. L3 and L4 accounts are drillable.
              // L1/L2 (the category headers we may show) are not
              // clickable since they have no own journal lines.
              const isDrillable = accountIdByCode[l.code] && (
                Math.abs(l.debitBalance) > 0.01 || Math.abs(l.creditBalance) > 0.01
              );
              return (
                <tr
                  key={idx}
                  onClick={isDrillable ? () => openGL(l.code) : undefined}
                  className={isDrillable ? "cursor-pointer hover:bg-primary-50 transition-colors" : ""}
                  title={isDrillable ? "اضغط لعرض دفتر الأستاذ" : undefined}
                >
                  <td className="font-mono">
                    {isDrillable && <BookOpen size={11} className="inline ml-1 text-primary-500" />}
                    {l.code}
                  </td>
                  <td>{l.name}</td>
                  <td>
                    <span className="text-xs text-gray-600">
                      {l.accountType === "Asset" ? "أصول" :
                       l.accountType === "Liability" ? "خصوم" :
                       l.accountType === "Equity" ? "حقوق ملكية" :
                       l.accountType === "Revenue" ? "إيرادات" : "مصروفات"}
                    </span>
                  </td>
                  <td>
                    {l.nature === "Debit" ? (
                      <span className="text-xs text-blue-700">مدين</span>
                    ) : (
                      <span className="text-xs text-orange-700">دائن</span>
                    )}
                  </td>
                  <td className="font-mono" dir="ltr">{formatNumber(l.debitBalance)}</td>
                  <td className="font-mono" dir="ltr">{formatNumber(l.creditBalance)}</td>
                </tr>
              );
            })}
          </tbody>
          <tfoot>
            <tr className="font-bold bg-gray-100">
              <td colSpan={4} className="text-left py-3">الإجمالي</td>
              <td className="font-mono py-3" dir="ltr">{formatNumber(report.totalDebit)}</td>
              <td className="font-mono py-3" dir="ltr">{formatNumber(report.totalCredit)}</td>
            </tr>
          </tfoot>
        </table>
      </div>

      <div className="mt-4 p-4 bg-blue-50 border border-blue-200 rounded-md text-sm text-blue-900 space-y-1">
        <p>
          💡 <strong>قاعدة محاسبية:</strong> في كل ميزان مراجعة، إجمالي المدين يجب أن يساوي إجمالي الدائن
          (A = L + E). هذا يتحقق تلقائياً من Posting Engine عند ترحيل أي قيد.
        </p>
        <p>
          📖 <strong>للتدقيق:</strong> اضغط على أي صف به رصيد (الأيقونة <BookOpen size={11} className="inline mx-1" />)
          لفتح <em>دفتر الأستاذ</em> الخاص بهذا الحساب وعرض الحركات التفصيلية.
        </p>
      </div>
    </div>
  );
}

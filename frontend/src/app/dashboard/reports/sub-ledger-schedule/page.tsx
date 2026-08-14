/**
 * Sub-Ledger Schedule — Cycle 2.
 *
 * Route: /dashboard/reports/sub-ledger-schedule
 *
 * Shows the L3 control account (e.g. "1103 Accounts Receivable")
 * broken down into its L4 sub-ledgers (e.g. "1103-CUST-001",
 * "1103-CUST-002", ...). This is the reconciliation view that
 * accountants need when they want to see "what makes up the
 * control account's balance".
 *
 * Why a separate page (not just inside the GL):
 *   - The GL is per-account per-period. The sub-ledger schedule
 *     is a point-in-time view of one L3 control + all its children.
 *   - Auditors use this for the "Schedule of Accounts Receivable"
 *     footnote in the financials.
 *   - The reconciliation (L3 NET == Σ L4) is automatic.
 *
 * Usage:
 *   1. Pick an L3 control account from the dropdown
 *   2. Pick an as-of date (defaults to today)
 *   3. The table shows every L4 sub-ledger with its balance
 *   4. The header shows the reconciliation: parent NET vs sum
 *
 * Deep links:
 *   /dashboard/reports/sub-ledger-schedule?accountId=<uuid>&asOf=YYYY-MM-DD
 *   (used by the trial balance to drill into L3 controls)
 */

"use client";

import { useEffect, useState, useMemo, useCallback, Suspense } from "react";
import { useSearchParams } from "next/navigation";
import { Layers, Loader2, CheckCircle, XCircle, Calendar, BookOpen, Printer, AlertTriangle } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate } from "@/lib/utils";

// ─── Types ────────────────────────────────────────────────────────────────

interface Account {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  accountType: string;
  nature: string;
  level: number;
  isPostable: boolean;
  isActive: boolean;
  children?: Account[];
}

interface SubLedgerLine {
  accountId: string;
  accountCode: string;
  accountName: string;
  contactId: string | null;
  contactCode: string | null;
  contactName: string | null;
  balance: number;
}

interface SubLedgerReport {
  companyId: string;
  companyName: string;
  asOfDate: string;
  parentAccountId: string;
  parentCode: string;
  parentName: string;
  accountType: string;
  nature: string;
  parentBalance: number;
  lines: SubLedgerLine[];
  subLedgerCount: number;
}

const TOLERANCE = 0.01;

// ─── Inner page (uses useSearchParams → needs Suspense wrapper) ──────────

function SubLedgerScheduleInner() {
  const { activeCompany } = useAuth();
  const searchParams = useSearchParams();

  // URL-driven state (deep link friendly)
  const initialAccountId = searchParams.get("accountId") || "";
  const initialAsOf = searchParams.get("asOf") || new Date().toISOString().slice(0, 10);

  const [accounts, setAccounts] = useState<Account[]>([]);
  const [accountsLoading, setAccountsLoading] = useState(true);

  const [selectedAccountId, setSelectedAccountId] = useState<string>(initialAccountId);
  const [asOf, setAsOf] = useState<string>(initialAsOf);

  const [report, setReport] = useState<SubLedgerReport | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // ─── Load the flat account list once (for the dropdown) ───────────────
  useEffect(() => {
    if (!activeCompany) return;
    (async () => {
      try {
        setAccountsLoading(true);
        const res = await api.get(`/accounts?companyId=${activeCompany.id}`);
        const raw = Array.isArray(res.data) ? res.data : (res.data?.data || []);
        setAccounts(raw as Account[]);
      } catch (err) {
        console.error("Failed to load accounts:", err);
      } finally {
        setAccountsLoading(false);
      }
    })();
  }, [activeCompany]);

  // ─── Load the report when both inputs are set ─────────────────────────
  const load = useCallback(async () => {
    if (!activeCompany || !selectedAccountId) return;
    try {
      setLoading(true);
      setError(null);
      const res = await api.get(
        `/reports/sub-ledger-schedule?companyId=${activeCompany.id}&accountId=${selectedAccountId}&asOf=${asOf}`
      );
      setReport(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
      setReport(null);
    } finally {
      setLoading(false);
    }
  }, [activeCompany, selectedAccountId, asOf]);

  useEffect(() => {
    if (selectedAccountId) load();
  }, [load, selectedAccountId]);

  // ─── Derived: flat list of L3 control accounts for the dropdown ──────
  const l3Accounts = useMemo(() => {
    const flat: Account[] = [];
    const walk = (nodes: Account[]) => {
      for (const a of nodes) {
        if (a.level === 3 && a.isActive) flat.push(a);
        if (a.children) walk(a.children);
      }
    };
    walk(accounts);
    return flat.sort((a, b) => a.code.localeCompare(b.code));
  }, [accounts]);

  // ─── Derived: reconciliation check ─────────────────────────────────────
  const reconciliation = useMemo(() => {
    if (!report) return null;
    const sum = report.lines.reduce((acc, l) => acc + l.balance, 0);
    const diff = report.parentBalance - sum;
    const balanced = Math.abs(diff) < TOLERANCE;
    return { sum, diff, balanced };
  }, [report]);

  return (
    <div>
      {/* Header */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
          <Layers size={24} className="text-brand-700" />
          جدول الأستاذ المساعد
        </h1>
        <p className="text-sm text-ink-muted mt-1">
          تفصيل حساب التحكم (L3) إلى حساباته الفرعية (L4). للتسوية المحاسبية.
        </p>
      </div>

      {/* Filters */}
      <div className="card mb-4">
        <div className="flex flex-col md:flex-row md:items-end gap-3">
          {/* L3 account selector */}
          <div className="flex-1 min-w-[200px]">
            <label className="block text-xs text-ink-muted mb-1">حساب التحكم (L3)</label>
            <select
              value={selectedAccountId}
              onChange={(e) => setSelectedAccountId(e.target.value)}
              disabled={accountsLoading}
              className="input w-full"
            >
              <option value="">— اختر الحساب —</option>
              {l3Accounts.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.code} — {a.nameAr || a.name}
                </option>
              ))}
            </select>
            {accountsLoading && (
              <p className="text-xs text-ink-muted mt-1">جاري تحميل الحسابات...</p>
            )}
            {!accountsLoading && l3Accounts.length === 0 && (
              <p className="text-xs text-amber-700 mt-1">
                لا توجد حسابات L3 (حسابات تحكم) في هذه الشركة.
              </p>
            )}
          </div>

          {/* As-of date */}
          <div>
            <label className="block text-xs text-ink-muted mb-1">
              <Calendar size={12} className="inline ml-1" />
              كما في تاريخ
            </label>
            <input
              type="date"
              value={asOf}
              onChange={(e) => setAsOf(e.target.value)}
              className="input"
            />
          </div>

          {/* Submit button — mostly for clarity, since useEffect auto-loads */}
          <button
            type="button"
            onClick={load}
            disabled={!selectedAccountId || loading}
            className="btn-primary"
          >
            {loading ? "جاري التحميل..." : "عرض"}
          </button>
        </div>
      </div>

      {/* Empty state */}
      {!selectedAccountId && !loading && (
        <div className="card text-center text-ink-muted py-12">
          <BookOpen size={40} className="mx-auto mb-3 opacity-40" />
          <p className="font-medium">اختر حساب تحكم (L3) لعرض حساباته الفرعية</p>
          <p className="text-xs mt-1">مثال: 1103 — الذمم المدينة، أو 2101 — الذمم الدائنة</p>
        </div>
      )}

      {/* Loading state */}
      {loading && (
        <div className="card flex items-center justify-center h-48">
          <Loader2 className="animate-spin text-brand-700" size={32} />
        </div>
      )}

      {/* Error state */}
      {error && !loading && (
        <div className="card border-red-200 bg-red-50">
          <div className="flex items-start gap-3">
            <AlertTriangle size={20} className="text-red-600 mt-0.5" />
            <div>
              <p className="font-semibold text-red-700">فشل تحميل التقرير</p>
              <p className="text-sm text-red-600 mt-1">{error}</p>
            </div>
          </div>
        </div>
      )}

      {/* Report */}
      {report && reconciliation && !loading && (
        <SubLedgerReport
          report={report}
          reconciliation={reconciliation}
        />
      )}
    </div>
  );
}

// ─── Sub-components ──────────────────────────────────────────────────────

function SubLedgerReport({
  report,
  reconciliation,
}: {
  report: SubLedgerReport;
  reconciliation: { sum: number; diff: number; balanced: boolean };
}) {
  return (
    <>
      {/* Reconciliation banner */}
      <div
        className={`card mb-4 ${
          reconciliation.balanced
            ? "bg-green-50 border-green-200"
            : "bg-red-50 border-red-200"
        }`}
      >
        <div className="flex items-center justify-between flex-wrap gap-3">
          <div className="flex items-center gap-3">
            {reconciliation.balanced ? (
              <CheckCircle size={24} className="text-green-600" />
            ) : (
              <XCircle size={24} className="text-red-600" />
            )}
            <div>
              <div className="font-bold text-lg">
                {reconciliation.balanced ? "متوازن ✅" : "غير متوازن ❌"}
              </div>
              <div className="text-sm text-ink-muted">
                {report.parentCode} ({report.parentName}) • {report.companyName} • كما في {formatDate(report.asOfDate)}
              </div>
            </div>
          </div>
          <div className="text-left">
            <table className="text-sm">
              <tbody>
                <tr>
                  <td className="text-ink-muted pl-4">رصيد L3:</td>
                  <td className="font-mono font-bold" dir="ltr">{formatNumber(report.parentBalance)}</td>
                </tr>
                <tr>
                  <td className="text-ink-muted pl-4">مجموع L4:</td>
                  <td className="font-mono" dir="ltr">{formatNumber(reconciliation.sum)}</td>
                </tr>
                <tr>
                  <td className="text-ink-muted pl-4">الفرق:</td>
                  <td className={`font-mono font-bold ${reconciliation.balanced ? "text-green-700" : "text-red-700"}`} dir="ltr">
                    {formatNumber(reconciliation.diff)}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </div>

      {/* Sub-ledgers table */}
      <div className="card p-0 overflow-hidden">
        <div className="px-4 py-3 border-b border-default flex items-center justify-between">
          <h2 className="font-semibold text-ink-strong">
            الحسابات الفرعية ({report.subLedgerCount})
          </h2>
          <button
            type="button"
            onClick={() => window.print()}
            className="text-xs text-ink-muted hover:text-brand-700 flex items-center gap-1"
          >
            <Printer size={14} /> طباعة
          </button>
        </div>
        {report.lines.length === 0 ? (
          <div className="text-center py-12 text-ink-muted">
            لا توجد حسابات L4 فرعية لهذا الحساب.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>الرمز</th>
                  <th>الحساب الفرعي</th>
                  <th>الجهة</th>
                  <th>كود الجهة</th>
                  <th className="text-left">الرصيد</th>
                </tr>
              </thead>
              <tbody>
                {report.lines.map((line) => (
                  <tr key={line.accountId}>
                    <td className="font-mono text-xs">{line.accountCode}</td>
                    <td>{line.accountName}</td>
                    <td>
                      {line.contactName ? (
                        <span className="text-sm">{line.contactName}</span>
                      ) : (
                        <span className="text-xs text-ink-muted">—</span>
                      )}
                    </td>
                    <td className="font-mono text-xs">
                      {line.contactCode || <span className="text-ink-muted">—</span>}
                    </td>
                    <td
                      className="font-mono font-semibold"
                      dir="ltr"
                      style={{ color: line.balance < 0 ? "var(--text-danger, #DC2626)" : undefined }}
                    >
                      {formatNumber(line.balance)}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="font-bold bg-surface">
                  <td colSpan={4}>المجموع</td>
                  <td className="font-mono" dir="ltr">
                    {formatNumber(reconciliation.sum)}
                  </td>
                </tr>
              </tfoot>
            </table>
          </div>
        )}
      </div>

      <p className="text-xs text-ink-muted mt-4 text-center">
        💡 هذا التقرير مفيد للمدقق الخارجي: يوضح كيف يتوزع رصيد L3 على الحسابات الفرعية.
      </p>
    </>
  );
}

// ─── Wrapper (Suspense for useSearchParams) ──────────────────────────────

export default function SubLedgerSchedulePage() {
  return (
    <Suspense
      fallback={
        <div className="flex items-center justify-center h-64">
          <Loader2 className="animate-spin text-brand-700" size={32} />
        </div>
      }
    >
      <SubLedgerScheduleInner />
    </Suspense>
  );
}

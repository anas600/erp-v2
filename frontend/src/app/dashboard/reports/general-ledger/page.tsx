"use client";

/**
 * General Ledger (دفتر الأستاذ) per account.
 *
 * Shows every POSTED journal line that touched a single
 * account, within a date range, with a running balance. This
 * is the standard drill-down view in any accounting system:
 * the trial balance tells you "what's the balance" — the
 * general ledger tells you "what are the transactions that
 * make up this balance".
 *
 * Posted-only (no drafts/pending): those don't affect the
 * books yet. Non-reversed: a reversed entry's reversing
 * counterpart already undoes it.
 *
 * The running balance is always in the account's natural sign:
 *   - For debit-nature accounts (assets, expenses): positive
 *     means debit balance
 *   - For credit-nature accounts (liabilities, equity, revenue):
 *     positive means credit balance
 *
 * To get here from elsewhere in the app, you can pass
 *   ?accountId=<uuid>&from=YYYY-MM-DD&to=YYYY-MM-DD
 * in the URL. The trial balance will link here with the
 * account's id and the current month as the date range.
 */

import { useEffect, useState, useCallback, useMemo } from "react";
import { useSearchParams } from "next/navigation";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { BookOpen, Loader2, Printer } from "lucide-react";
import { formatDate, formatNumber } from "@/lib/utils";

interface Account {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  accountType: string;
  nature: string;
  /** 1=L1 type, 2=L2 category, 3=L3 operational, 4=L4 sub-ledger. */
  level?: number;
  /** Whether this account accepts direct journal postings (L4 only). */
  isPostable?: boolean;
}

interface LedgerEntry {
  entryId: string;
  entryNumber: string;
  entryDate: string;
  narration?: string;
  source?: string;
  reference?: string;
  debit: number;
  credit: number;
  runningBalance: number;
}

interface LedgerReport {
  companyId: string;
  companyName: string;
  accountId: string;
  accountCode: string;
  accountName: string;
  accountNature: string;
  fromDate: string;
  toDate: string;
  openingBalance: number;
  totalDebit: number;
  totalCredit: number;
  closingBalance: number;
  entries: LedgerEntry[];
}

export default function GeneralLedgerPage() {
  const { activeCompany } = useAuth();
  const searchParams = useSearchParams();

  const today = new Date().toISOString().slice(0, 10);
  const firstOfYear = `${new Date().getFullYear()}-01-01`;

  const [accounts, setAccounts] = useState<Account[]>([]);
  const [selectedAccountId, setSelectedAccountId] = useState<string>(searchParams.get("accountId") ?? "");
  const [from, setFrom] = useState<string>(searchParams.get("from") ?? firstOfYear);
  const [to, setTo] = useState<string>(searchParams.get("to") ?? today);
  const [report, setReport] = useState<LedgerReport | null>(null);
  const [loadingAccounts, setLoadingAccounts] = useState(true);
  const [loadingReport, setLoadingReport] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Load the chart of accounts (for the account picker)
  //
  // Sprint 33 hotfix: the previous code took r.data directly, which
  // for our tree API means only the 6 L1 roots. The dropdown showed
  // only L1 accounts and the accountant couldn't drill down into L3
  // (operational accounts) or L4 (sub-ledgers) — which is exactly
  // what they need to audit.
  //
  // Now we flatten the nested tree response, then filter out L1
  // (no own activity) and present L2/L3/L4 grouped by level with
  // indentation so the hierarchy is obvious in the dropdown.
  useEffect(() => {
    if (!activeCompany) return;
    setLoadingAccounts(true);
    api.get(`/accounts?companyId=${activeCompany.id}`)
      .then((r) => {
        const raw = Array.isArray(r.data) ? r.data : (r.data?.data || []);
        const flat = flattenAccounts(raw);
        // Group by level for an organised dropdown:
        //   L2  (category headers, e.g. "11 أصول متداولة")
        //   L3  (operational control accounts, e.g. "1103 المدينون")
        //   L4  (sub-ledgers, e.g. "1103-CUST-001")
        // We skip L1 entirely (it's a top-level classification, no
        // own activity). An L2 with no L3 children is also excluded
        // since it has nothing to show. `level` is optional in the
        // Account type (we define it for this page) but the API
        // always sends it, so we use a default of 0 for type safety.
        const grouped = flat
          .filter((a) => (a.level ?? 0) >= 2 && (a.level ?? 0) <= 4)
          .sort((a, b) =>
            a.code.localeCompare(b.code, undefined, { numeric: true })
          );
        setAccounts(grouped);
        // Auto-select the first L3 (most useful) when none is set.
        if (!searchParams.get("accountId") && grouped.length > 0) {
          const firstL3 = grouped.find((a) => a.level === 3);
          setSelectedAccountId((firstL3 ?? grouped[0]).id);
        }
      })
      .catch((err) => setError(getErrorMessage(err)))
      .finally(() => setLoadingAccounts(false));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeCompany]);

  // Load the ledger when account or dates change
  const loadReport = useCallback(async () => {
    if (!activeCompany || !selectedAccountId) return;
    setLoadingReport(true);
    setError(null);
    try {
      const r = await api.get(
        `/reports/general-ledger?companyId=${activeCompany.id}&accountId=${selectedAccountId}&from=${from}&to=${to}`
      );
      setReport(r.data);
    } catch (err) {
      setError(getErrorMessage(err));
      setReport(null);
    } finally {
      setLoadingReport(false);
    }
  }, [activeCompany, selectedAccountId, from, to]);

  useEffect(() => { loadReport(); }, [loadReport]);

  // Build a code→Account map so the table can show the
  // account name even when the API returns just ids.
  const accountById = useMemo(() => {
    const m = new Map<string, Account>();
    accounts.forEach((a) => m.set(a.id, a));
    return m;
  }, [accounts]);

  const selectedAccount = selectedAccountId ? accountById.get(selectedAccountId) : null;

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
            <BookOpen size={24} className="text-primary-600" />
            دفتر الأستاذ
          </h1>
          <p className="text-sm text-ink-muted mt-1">
            كل الحركات على حساب معين في فترة محددة، مع رصيد جاري
          </p>
        </div>
        {report && (
          <button
            onClick={() => window.print()}
            className="btn-secondary flex items-center gap-1 text-sm"
            title="طباعة / حفظ PDF"
          >
            <Printer size={14} />
            طباعة
          </button>
        )}
      </div>

      {/* Filters */}
      <div className="card mb-4">
        <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">الحساب *</label>
            {loadingAccounts ? (
              <Loader2 className="animate-spin" size={16} />
            ) : (
              <select
                className="input"
                value={selectedAccountId}
                onChange={(e) => setSelectedAccountId(e.target.value)}
              >
                <option value="">— اختر حساب —</option>
                {accounts.map((a) => {
                  // Indent deeper levels so the hierarchy is obvious
                  // in the dropdown: L3 → no indent, L4 → 2 spaces, L2 → 4 spaces
                  const lvl = a.level ?? 3;
                  const indent = lvl === 4 ? "↳ " : lvl === 2 ? "  ‖ " : "";
                  const levelLabel = LEVEL_LABEL[lvl] ?? `L${lvl}`;
                  return (
                    <option key={a.id} value={a.id}>
                      {indent}{a.code} — {a.nameAr || a.name}  [{levelLabel}]
                    </option>
                  );
                })}
              </select>
            )}
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">من تاريخ</label>
            <input
              type="date"
              className="input"
              value={from}
              onChange={(e) => setFrom(e.target.value)}
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">إلى تاريخ</label>
            <input
              type="date"
              className="input"
              value={to}
              onChange={(e) => setTo(e.target.value)}
            />
          </div>
        </div>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>
      )}

      {/* Report */}
      {!selectedAccountId ? (
        <div className="card text-center py-12 text-ink-muted">
          <BookOpen size={48} className="mx-auto mb-3 text-ink-subtle" />
          <p className="text-canvas font-medium">اختر حساب لعرض حركاته</p>
        </div>
      ) : loadingReport ? (
        <div className="card flex justify-center py-8">
          <Loader2 className="animate-spin text-primary-500" size={32} />
        </div>
      ) : report ? (
        <div className="card">
          {/* Account header */}
          <div className="border-b border-edge pb-3 mb-3">
            <div className="flex items-baseline justify-between">
              <div>
                <h2 className="text-lg font-semibold">
                  حساب: {report.accountCode} — {report.accountName}
                </h2>
                <p className="text-sm text-ink-muted">
                  {report.companyName} • الفترة: {formatDate(report.fromDate)} → {formatDate(report.toDate)} •
                  الطبيعة: <span className="font-mono">{report.accountNature}</span>
                </p>
              </div>
            </div>
          </div>

          {/* Period summary */}
          <div className="grid grid-cols-4 gap-3 mb-3 text-sm">
            <Summary label="رصيد افتتاحي" value={report.openingBalance} />
            <Summary label="إجمالي مدين" value={report.totalDebit} />
            <Summary label="إجمالي دائن" value={report.totalCredit} />
            <Summary label="رصيد ختامي" value={report.closingBalance} bold />
          </div>

          {/* Transactions table */}
          {report.entries.length === 0 ? (
            <p className="text-center text-ink-muted py-6">لا توجد حركات في هذه الفترة</p>
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th>التاريخ</th>
                  <th>رقم القيد</th>
                  <th>البيان</th>
                  <th>المصدر</th>
                  <th className="text-left">مدين</th>
                  <th className="text-left">دائن</th>
                  <th className="text-left">الرصيد</th>
                </tr>
              </thead>
              <tbody>
                {report.entries.map((e) => (
                  <tr key={e.entryId}>
                    <td>{formatDate(e.entryDate)}</td>
                    <td className="font-mono text-xs">{e.entryNumber}</td>
                    <td className="text-sm">{e.narration || "—"}</td>
                    <td className="text-xs">
                      <SourceBadge source={e.source} />
                    </td>
                    <td className="font-mono" dir="ltr">
                      {e.debit > 0 ? formatNumber(e.debit) : "—"}
                    </td>
                    <td className="font-mono" dir="ltr">
                      {e.credit > 0 ? formatNumber(e.credit) : "—"}
                    </td>
                    <td
                      className="font-mono font-semibold"
                      dir="ltr"
                    >
                      {formatNumber(e.runningBalance)}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="border-t-2 font-bold bg-raised">
                  <td colSpan={4} className="py-2">الإجماليات</td>
                  <td className="font-mono py-2" dir="ltr">{formatNumber(report.totalDebit)}</td>
                  <td className="font-mono py-2" dir="ltr">{formatNumber(report.totalCredit)}</td>
                  <td className="font-mono py-2 text-primary-700" dir="ltr">
                    {formatNumber(report.closingBalance)}
                  </td>
                </tr>
              </tfoot>
            </table>
          )}
        </div>
      ) : null}
    </div>
  );
}

function Summary({ label, value, bold }: { label: string; value: number; bold?: boolean }) {
  return (
    <div className={`p-2 rounded ${bold ? "bg-primary-50" : "bg-raised"}`}>
      <p className="text-xs text-ink-muted">{label}</p>
      <p className={`font-mono ${bold ? "text-lg font-bold text-primary-700" : "text-sm"}`} dir="ltr">
        {formatNumber(value)}
      </p>
    </div>
  );
}

function SourceBadge({ source }: { source?: string }) {
  if (!source) return <span className="text-ink-subtle">—</span>;
  if (source.startsWith("rule:"))
    return <span className="badge badge-info text-xs">قاعدة</span>;
  if (source.startsWith("reverse:"))
    return <span className="badge badge-warning text-xs">عكس</span>;
  if (source === "manual")
    return <span className="badge badge-secondary text-xs">يدوي</span>;
  return <span className="badge text-xs">{source}</span>;
}

/**
 * Flatten the nested account tree response into a flat list.
 * Each node's `children` field is stripped (we only need it
 * in the chart-of-accounts tree view, not the GL picker).
 */
function flattenAccounts(nodes: any[]): Account[] {
  const out: Account[] = [];
  const walk = (n: any) => {
    const { children, ...rest } = n;
    out.push(rest as Account);
    if (Array.isArray(children)) children.forEach(walk);
  };
  nodes.forEach(walk);
  return out;
}

/** Visual label for the level badge in the account picker. */
const LEVEL_LABEL: Record<number, string> = {
  1: "نوع",
  2: "فئة",
  3: "تشغيلي",
  4: "تفصيلي"
};

const LEVEL_BADGE: Record<number, string> = {
  2: "bg-slate-100 text-slate-600",
  3: "bg-emerald-100 text-emerald-700",
  4: "bg-amber-100 text-amber-700"
};

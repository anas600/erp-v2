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
 * Sprint 44 — L3 control account handling. When the user picks
 * an L3 control account (e.g. 1103 Accounts Receivable), the GL
 * is empty because L3 is not postable — all postings go to L4
 * sub-ledgers. Instead of showing "no movements" (which is
 * technically correct but useless), we now detect the L3
 * control case and render a "Sub-ledger Schedule" — the
 * reconciliation view that shows every L4 sub-ledger under
 * the L3 control with its current balance. The L3 control's
 * NET balance equals the sum of its sub-ledgers by construction
 * (Sprint 41 rebuild-balances writes the NET to the L3 control),
 * so the reconciliation is automatic.
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
import { BookOpen, Loader2, Printer, Layers, Users } from "lucide-react";
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

/**
 * Sprint 44 — Sub-ledger Schedule (كشف الحسابات التحليلية).
 * Returned by GET /api/reports/sub-ledger-schedule for an L3
 * control account. Each line is one L4 sub-ledger with its
 * current balance. The parent (L3) balance is the NET.
 */
interface SubLedgerScheduleLine {
  accountId: string;
  accountCode: string;       // e.g. "1103-CUST-001"
  accountName: string;
  contactId: string | null;
  contactCode: string | null;
  contactName: string | null;
  balance: number;            // signed per account nature
}

interface SubLedgerScheduleReport {
  companyId: string;
  companyName: string;
  asOfDate: string;
  parentAccountId: string;
  parentCode: string;        // e.g. "1103"
  parentName: string;
  accountType: string;
  nature: string;
  parentBalance: number;     // L3 NET
  subLedgerCount: number;
  lines: SubLedgerScheduleLine[];
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
  const [schedule, setSchedule] = useState<SubLedgerScheduleReport | null>(null);
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

  // Build a code→Account map so the table can show the
  // account name even when the API returns just ids.
  // MUST be declared before loadReport because loadReport's
  // useCallback depends on it (to detect L3 control accounts).
  const accountById = useMemo(() => {
    const m = new Map<string, Account>();
    accounts.forEach((a) => m.set(a.id, a));
    return m;
  }, [accounts]);

  // Load the ledger when account or dates change
  //
  // Sprint 44 — when the selected account is an L3 control,
  // we also fetch the sub-ledger schedule. The GL itself returns
  // "no movements" for L3 (which is correct — L3 is not
  // postable), but the schedule shows the L4 breakdown that
  // makes the L3 balance meaningful. We render whichever is
  // appropriate based on the account's level.
  const loadReport = useCallback(async () => {
    if (!activeCompany || !selectedAccountId) return;
    setLoadingReport(true);
    setError(null);
    setReport(null);
    setSchedule(null);
    try {
      const r = await api.get(
        `/reports/general-ledger?companyId=${activeCompany.id}&accountId=${selectedAccountId}&from=${from}&to=${to}`
      );
      setReport(r.data);
      // If this is an L3 account, also pull the sub-ledger schedule.
      const acc = accountById.get(selectedAccountId);
      if (acc?.level === 3) {
        try {
          const sr = await api.get(
            `/reports/sub-ledger-schedule?companyId=${activeCompany.id}&accountId=${selectedAccountId}`
          );
          setSchedule(sr.data);
        } catch {
          // Sub-ledger schedule not available (no sub-ledgers under
          // this L3) — that's fine, the GL still shows.
          setSchedule(null);
        }
      }
    } catch (err) {
      setError(getErrorMessage(err));
      setReport(null);
    } finally {
      setLoadingReport(false);
    }
  }, [activeCompany, selectedAccountId, from, to, accountById]);

  useEffect(() => { loadReport(); }, [loadReport]);

  const selectedAccount = selectedAccountId ? accountById.get(selectedAccountId) : null;
  const showSchedule = selectedAccount?.level === 3 && schedule !== null;

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
      ) : showSchedule && schedule ? (
        // Sprint 44 — L3 control account view: show the sub-ledger
        // schedule instead of "no movements". This is the
        // reconciliation view: L4 sub-ledgers + their balances +
        // the L3 NET, so the reader can verify they match.
        <div className="card">
          <div className="border-b border-edge pb-3 mb-3">
            <div className="flex items-baseline justify-between">
              <div>
                <h2 className="text-lg font-semibold flex items-center gap-2">
                  <Layers size={18} className="text-primary-600" />
                  حساب: {schedule.parentCode} — {schedule.parentName}
                  <span className="badge badge-info text-xs">كشف حساب تجميعي</span>
                </h2>
                <p className="text-sm text-ink-muted mt-1">
                  {schedule.companyName} • حتى تاريخ: {formatDate(schedule.asOfDate)} •
                  النوع: <span className="font-mono">{schedule.accountType}</span> •
                  الطبيعة: <span className="font-mono">{schedule.nature}</span>
                </p>
              </div>
            </div>
          </div>

          {/* Sub-ledger count + parent balance summary */}
          <div className="grid grid-cols-3 gap-3 mb-3 text-sm">
            <Summary label="عدد الحسابات التحليلية" value={schedule.subLedgerCount} isInt />
            <Summary label="رصيد الحساب التجميعي (L3)" value={schedule.parentBalance} bold />
            <Summary label="مجموع أرصدة L4" value={schedule.lines.reduce((s, l) => s + l.balance, 0)} bold />
          </div>

          {schedule.lines.length === 0 ? (
            <p className="text-center text-ink-muted py-6">
              لا توجد حسابات تحليلية تحت هذا الحساب
            </p>
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th>الحساب التحليلي</th>
                  <th>الجهة</th>
                  <th className="text-left">الرصيد</th>
                </tr>
              </thead>
              <tbody>
                {schedule.lines.map((l) => (
                  <tr key={l.accountId} className="hover:bg-raised">
                    <td>
                      <div className="font-mono text-sm">{l.accountCode}</div>
                      <div className="text-xs text-ink-muted">{l.accountName}</div>
                    </td>
                    <td>
                      {l.contactId ? (
                        <a
                          href={`/dashboard/contacts/${l.contactId}`}
                          className="text-primary-700 hover:underline flex items-center gap-1"
                        >
                          <Users size={12} />
                          {l.contactName || l.contactCode}
                        </a>
                      ) : (
                        <span className="text-ink-subtle text-sm">—</span>
                      )}
                    </td>
                    <td
                      className="font-mono text-left font-semibold"
                      dir="ltr"
                    >
                      {formatNumber(l.balance)}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="border-t-2 font-bold bg-raised">
                  <td colSpan={2} className="py-2">مجموع أرصدة L4</td>
                  <td className="font-mono text-left py-2 text-primary-700" dir="ltr">
                    {formatNumber(schedule.lines.reduce((s, l) => s + l.balance, 0))}
                  </td>
                </tr>
              </tfoot>
            </table>
          )}

          <div className="mt-4 p-3 bg-emerald-50 text-emerald-800 rounded-md text-sm flex items-start gap-2">
            <Layers size={16} className="mt-0.5 flex-shrink-0" />
            <div>
              <strong>كشف الحساب التجميعي (Sub-ledger Schedule):</strong>
              <p className="mt-1">
                هذا حساب <strong>تجميعي</strong> (L3) — كل الترحيلات تتم على الحسابات التحليلية (L4) أدناه.
                رصيد الحساب التجميعي = مجموع أرصدة الحسابات التحليلية (مطابقة تلقائية).
              </p>
              <p className="mt-2 text-xs">
                <strong>للوصول للحركات التفصيلية:</strong> اضغط على اسم الجهة (إن وُجد) لفتح كشف حسابها،
                أو اختر حساب L4 من القائمة أعلاه لعرض قيوده المحاسبية.
              </p>
            </div>
          </div>
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

function Summary({ label, value, bold, isInt }: { label: string; value: number; bold?: boolean; isInt?: boolean }) {
  return (
    <div className={`p-2 rounded ${bold ? "bg-primary-50" : "bg-raised"}`}>
      <p className="text-xs text-ink-muted">{label}</p>
      <p className={`font-mono ${bold ? "text-lg font-bold text-primary-700" : "text-sm"}`} dir="ltr">
        {isInt ? value : formatNumber(value)}
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

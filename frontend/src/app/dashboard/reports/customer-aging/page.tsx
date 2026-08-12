"use client";

/**
 * Customer Aging (أعمار المدينين)
 *
 * Sprint 44 — added Tab 2: "كشف حساب تفصيلي" (Per-contact Statement).
 * The original table (Tab 1) is the aggregated aging view (10 customers
 * × 4 age buckets). When the user clicks a customer row, the page
 * switches to Tab 2 and shows the contact's full statement of account
 * (every invoice + receipt with a running balance). This is the
 * natural drill-down: "CUST-001 has 95K outstanding" → "show me why".
 *
 * Tab 2 also includes a contact selector so the user can switch
 * customers without going back to Tab 1.
 */

import { useEffect, useState, useCallback } from "react";
import { useSearchParams } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import { Loader2, Users, AlertCircle, ArrowRight, FileText, ChevronRight } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface AgingLine {
  contactId: string;
  contactCode: string;
  contactName: string;
  buckets: number[];  // [0-30, 31-60, 61-90, 91+]
  total: number;      // outstanding total
  /**
   * Sprint 25 — total amount the customer has already paid toward
   * these outstanding invoices. Optional in the wire format because
   * the backend may return it from the new settlement table or not.
   * Frontend falls back to 0 if absent.
   */
  paid?: number;
}

interface AgingReport {
  companyId: string;
  asOfDate: string;
  lines: AgingLine[];
  totals: number[];
  grandTotal: number;
  totalPaid?: number;
}

/**
 * Sprint 44 — Per-contact Statement of Account (Tab 2).
 * The line items are invoices + receipts merged into one timeline
 * with a running balance.
 */
interface ContactStatementLine {
  date: string;
  docType: string;        // "فاتورة" | "سند قبض"
  docNumber: string;
  description?: string;
  debit: number;
  credit: number;
  runningBalance: number;
}

interface ContactStatementReport {
  companyId: string;
  companyName: string;
  contactId: string;
  contactCode: string;
  contactName: string;
  contactType: string;
  fromDate: string;
  toDate: string;
  openingBalance: number;
  totalDebit: number;
  totalCredit: number;
  closingBalance: number;
  lines: ContactStatementLine[];
}

const BUCKET_LABELS = ["0-30 يوم", "31-60 يوم", "61-90 يوم", "+90 يوم"];
const BUCKET_CLASSES = ["text-green-700", "text-amber-700", "text-amber-700", "text-red-700"];

export default function CustomerAgingPage() {
  const { activeCompany } = useAuth();
  const searchParams = useSearchParams();

  // Sprint 44 — Tab state. Default to "summary" (the original
  // aging table). URL ?tab=detail&contact=<id> opens Tab 2
  // with that contact pre-selected (deep-link from anywhere).
  const initialTab = (searchParams.get("tab") === "detail" ? "detail" : "summary") as "summary" | "detail";
  const initialContact = searchParams.get("contact") ?? "";
  const [tab, setTab] = useState<"summary" | "detail">(initialTab);
  const [selectedContactId, setSelectedContactId] = useState<string>(initialContact);

  const [report, setReport] = useState<AgingReport | null>(null);
  const [statement, setStatement] = useState<ContactStatementReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingStatement, setLoadingStatement] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const today = new Date().toISOString().slice(0, 10);
  const firstOfYear = `${new Date().getFullYear()}-01-01`;

  const load = useCallback(async () => {
    if (!activeCompany) return;
    setLoading(true);
    try {
      const r = await api.get(`/reports/customer-aging?companyId=${activeCompany.id}`);
      const payload: AgingReport = r.data?.data || r.data;
      setReport(payload);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [activeCompany]);

  useEffect(() => { load(); }, [load]);

  // When Tab 2 is open and a contact is selected, load the statement.
  const loadStatement = useCallback(async () => {
    if (!activeCompany || !selectedContactId) return;
    setLoadingStatement(true);
    try {
      const r = await api.get(
        `/reports/contact-statement?companyId=${activeCompany.id}&contactId=${selectedContactId}&from=${firstOfYear}&to=${today}`
      );
      const payload: ContactStatementReport = r.data?.data || r.data;
      setStatement(payload);
    } catch (err) {
      setError(getErrorMessage(err));
      setStatement(null);
    } finally {
      setLoadingStatement(false);
    }
  }, [activeCompany, selectedContactId, firstOfYear, today]);

  useEffect(() => {
    if (tab === "detail" && selectedContactId) {
      loadStatement();
    }
  }, [tab, selectedContactId, loadStatement]);

  const totalPaid = report?.totalPaid ?? report?.lines.reduce((s, l) => s + (l.paid || 0), 0) ?? 0;

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
            <Users size={24} className="text-amber-600" />
            أعمار المدينين
          </h1>
          <p className="text-sm text-ink-muted mt-1">
            أرصدة العملاء موزعة حسب مدة التأخر في السداد
          </p>
        </div>
        {report && (
          <div className="text-sm text-ink-muted">
            حتى تاريخ: <span className="font-mono font-semibold">{formatDate(report.asOfDate)}</span>
          </div>
        )}
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {/* Sprint 44 — Tab switcher */}
      <div className="card mb-4 p-1 inline-flex gap-1">
        <button
          onClick={() => setTab("summary")}
          className={`px-4 py-2 rounded text-sm font-medium ${
            tab === "summary"
              ? "bg-primary-600 text-white"
              : "text-ink-muted hover:bg-raised"
          }`}
        >
          ملخص الأعمار
        </button>
        <button
          onClick={() => setTab("detail")}
          className={`px-4 py-2 rounded text-sm font-medium ${
            tab === "detail"
              ? "bg-primary-600 text-white"
              : "text-ink-muted hover:bg-raised"
          }`}
        >
          كشف حساب تفصيلي
        </button>
      </div>

      {tab === "summary" ? (
        <div className="card">
          {loading ? (
            <div className="flex justify-center py-8">
              <Loader2 className="animate-spin text-primary-500" size={32} />
            </div>
          ) : !report || report.lines.length === 0 ? (
            <div className="text-center py-12 text-ink-muted">
              <Users size={48} className="mx-auto mb-3 text-ink-subtle" />
              <p>لا توجد أرصدة مدينة مستحقة</p>
              <p className="text-sm mt-1">جميع الفواتير المُرحّلة تم سدادها</p>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="table">
                <thead>
                  <tr>
                    <th>العميل</th>
                    {BUCKET_LABELS.map((label, i) => (
                      <th key={i} className="text-left">{label}</th>
                    ))}
                    <th className="text-left text-green-700">مدفوع</th>
                    <th className="text-left">الإجمالي المستحق</th>
                  </tr>
                </thead>
                <tbody>
                  {report.lines.map((line) => (
                    <tr key={line.contactId} className="hover:bg-raised">
                      <td>
                        <button
                          onClick={() => {
                            setSelectedContactId(line.contactId);
                            setTab("detail");
                          }}
                          className="text-right hover:text-primary-700"
                        >
                          <div className="font-semibold flex items-center gap-1">
                            {line.contactName}
                            <ChevronRight size={12} className="text-ink-subtle" />
                          </div>
                          <div className="text-xs text-ink-muted">{line.contactCode}</div>
                        </button>
                      </td>
                      {line.buckets.map((amt, i) => (
                        <td key={i} className={`font-mono text-left ${amt > 0 ? BUCKET_CLASSES[i] : 'text-ink-subtle'}`} dir="ltr">
                          {amt > 0 ? formatNumber(amt) : '—'}
                        </td>
                      ))}
                      <td className="font-mono text-left text-green-700" dir="ltr">
                        {line.paid != null ? formatNumber(line.paid) : <span className="text-ink-subtle">—</span>}
                      </td>
                      <td className="font-mono text-left font-bold" dir="ltr">
                        {formatNumber(line.total)}
                      </td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr className="border-t-2 font-bold bg-raised">
                    <td className="py-2">الإجمالي</td>
                    {report.totals.map((t, i) => (
                      <td key={i} className={`font-mono text-left py-2 ${BUCKET_CLASSES[i]}`} dir="ltr">
                        {formatNumber(t)}
                      </td>
                    ))}
                    <td className="font-mono text-left py-2 text-green-700" dir="ltr">
                      {formatNumber(totalPaid)}
                    </td>
                    <td className="font-mono text-left py-2 text-amber-700" dir="ltr">
                      {formatNumber(report.grandTotal)}
                    </td>
                  </tr>
                </tfoot>
              </table>
              <div className="mt-4 p-3 bg-amber-50 text-amber-800 rounded-md text-sm flex items-start gap-2">
                <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
                <div>
                  <strong>كيفية القراءة:</strong> المبالغ في الأعمدة = المستحق حالياً (بعد خصم المدفوع).
                  <ul className="mt-1 list-disc list-inside">
                    <li>0-30 يوم: الدين لم يتأخر بعد</li>
                    <li>31-60 يوم: متأخر شهر إلى شهرين</li>
                    <li>61-90 يوم: متأخر ربع سنة (إنذار)</li>
                    <li>+90 يوم: متأخر بشدة (تحصيل صعب)</li>
                  </ul>
                  <p className="mt-2 text-xs">
                    اضغط على اسم العميل لفتح <strong>كشف حسابه التفصيلي</strong> (فواتير + سندات قبض) في Tab 2.
                  </p>
                </div>
              </div>
            </div>
          )}
        </div>
      ) : (
        // Tab 2 — Per-contact Statement of Account
        <div className="card">
          {/* Contact selector */}
          <div className="mb-4 flex items-center gap-3">
            <label className="text-sm font-medium">العميل:</label>
            <select
              className="input flex-1 max-w-md"
              value={selectedContactId}
              onChange={(e) => setSelectedContactId(e.target.value)}
            >
              <option value="">— اختر عميل —</option>
              {report?.lines.map((l) => (
                <option key={l.contactId} value={l.contactId}>
                  {l.contactCode} — {l.contactName}
                </option>
              ))}
            </select>
            {statement && (
              <span className="text-sm text-ink-muted">
                الفترة: {formatDate(statement.fromDate)} → {formatDate(statement.toDate)}
              </span>
            )}
          </div>

          {!selectedContactId ? (
            <div className="text-center py-12 text-ink-muted">
              <FileText size={48} className="mx-auto mb-3 text-ink-subtle" />
              <p className="text-canvas font-medium">اختر عميل لعرض كشف حسابه</p>
              <p className="text-sm mt-1">أو ارجع لـ Tab 1 واضغط على اسم العميل</p>
            </div>
          ) : loadingStatement ? (
            <div className="flex justify-center py-8">
              <Loader2 className="animate-spin text-primary-500" size={32} />
            </div>
          ) : statement ? (
            <>
              <div className="border-b border-edge pb-3 mb-3">
                <h2 className="text-lg font-semibold flex items-center gap-2">
                  <FileText size={18} className="text-primary-600" />
                  كشف حساب: {statement.contactCode} — {statement.contactName}
                </h2>
                <p className="text-sm text-ink-muted mt-1">
                  {statement.companyName} • {statement.contactType === "customer" ? "عميل" : "مورد"}
                </p>
              </div>

              <div className="grid grid-cols-4 gap-3 mb-3 text-sm">
                <Summary label="رصيد افتتاحي" value={statement.openingBalance} />
                <Summary label="إجمالي مدين" value={statement.totalDebit} />
                <Summary label="إجمالي دائن" value={statement.totalCredit} />
                <Summary label="رصيد ختامي" value={statement.closingBalance} bold />
              </div>

              {statement.lines.length === 0 ? (
                <p className="text-center text-ink-muted py-6">
                  لا توجد حركات لهذا العميل في الفترة المحددة
                </p>
              ) : (
                <table className="table">
                  <thead>
                    <tr>
                      <th>التاريخ</th>
                      <th>المستند</th>
                      <th>الرقم</th>
                      <th>البيان</th>
                      <th className="text-left">مدين</th>
                      <th className="text-left">دائن</th>
                      <th className="text-left">الرصيد</th>
                    </tr>
                  </thead>
                  <tbody>
                    {statement.lines.map((l, i) => (
                      <tr key={i} className="hover:bg-raised">
                        <td>{formatDate(l.date)}</td>
                        <td>
                          <span className="badge text-xs">{l.docType}</span>
                        </td>
                        <td className="font-mono text-xs">{l.docNumber}</td>
                        <td className="text-sm">{l.description || "—"}</td>
                        <td className="font-mono text-left" dir="ltr">
                          {l.debit > 0 ? formatNumber(l.debit) : "—"}
                        </td>
                        <td className="font-mono text-left" dir="ltr">
                          {l.credit > 0 ? formatNumber(l.credit) : "—"}
                        </td>
                        <td
                          className={`font-mono font-semibold text-left ${
                            l.runningBalance > 0 ? "text-amber-700" : l.runningBalance < 0 ? "text-green-700" : ""
                          }`}
                          dir="ltr"
                        >
                          {formatNumber(l.runningBalance)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                  <tfoot>
                    <tr className="border-t-2 font-bold bg-raised">
                      <td colSpan={4} className="py-2">الإجماليات</td>
                      <td className="font-mono py-2 text-left" dir="ltr">{formatNumber(statement.totalDebit)}</td>
                      <td className="font-mono py-2 text-left" dir="ltr">{formatNumber(statement.totalCredit)}</td>
                      <td className="font-mono py-2 text-left text-primary-700" dir="ltr">
                        {formatNumber(statement.closingBalance)}
                      </td>
                    </tr>
                  </tfoot>
                </table>
              )}

              <div className="mt-4 p-3 bg-amber-50 text-amber-800 rounded-md text-sm flex items-start gap-2">
                <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
                <div>
                  <strong>قراءة الكشف:</strong>
                  <ul className="mt-1 list-disc list-inside">
                    <li><strong>الفاتورة</strong> (مدين): تزيد رصيد العميل — عليه يدفع</li>
                    <li><strong>سند القبض</strong> (دائن): ينقص رصيد العميل — دفع جزء أو كل</li>
                    <li><strong>الرصيد الختامي</strong> الموجب = العميل ما يزال مدين</li>
                    <li>الرصيد الختامي يجب أن يطابق <strong>الإجمالي المستحق</strong> في Tab 1</li>
                  </ul>
                </div>
              </div>
            </>
          ) : null}
        </div>
      )}
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

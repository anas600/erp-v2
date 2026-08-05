"use client";

/**
 * Pending Journal Entries (Sprint 15)
 *
 * Lists every entry with status='pending' — these are auto-generated
 * by the Business Rules engine and need the accountant's sign-off
 * before they hit the financial reports.
 *
 * The accountant can:
 *   - Approve a pending entry (transitions to 'posted' → affects reports)
 *   - Reject a pending entry (transitions to 'draft' → editable, not in reports)
 *   - Drill into a single entry to review its lines
 *
 * Why this page exists:
 *   Before Sprint 15, rule-generated entries went straight from
 *   "draft" to "posted" with no human in the loop. This is fine
 *   for trusted automations but dangerous for anything that affects
 *   financial statements — a buggy rule or a wrong account mapping
 *   would silently corrupt the books.
 *
 *   The DRAFT-APPROVE workflow fixes that: the rule proposes,
 *   the accountant disposes.
 */

import { useEffect, useState, useCallback } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { CheckCircle, XCircle, Loader2, FileText, Inbox, ChevronDown, ChevronUp } from "lucide-react";
import { formatDateTime } from "@/lib/utils";

interface JournalLine {
  id: string;
  accountId: string;
  accountCode?: string;
  accountName?: string;
  debit: number;
  credit: number;
  description?: string;
  lineNumber: number;
}

interface JournalEntry {
  id: string;
  companyId: string;
  entryNumber: string;
  entryDate: string;
  narration?: string;
  status: "draft" | "pending" | "posted" | "reversed";
  source?: string;
  ruleId?: string;
  createdAt: string;
  postedAt?: string;
  lines: JournalLine[];
}

export default function PendingJournalPage() {
  const { activeCompany } = useAuth();
  const [entries, setEntries] = useState<JournalEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [actionInProgress, setActionInProgress] = useState<string | null>(null);
  const [rejectReason, setRejectReason] = useState<{ id: string; reason: string } | null>(null);

  const load = useCallback(async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(`/journal/pending?companyId=${activeCompany.id}`);
      setEntries(res.data);
      setError(null);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [activeCompany]);

  useEffect(() => { load(); }, [load]);

  // Re-fetch when the user tabs back — same pattern as the
  // trial-balance refresh.
  useEffect(() => {
    const onFocus = () => load();
    window.addEventListener("focus", onFocus);
    return () => window.removeEventListener("focus", onFocus);
  }, [load]);

  const approve = async (id: string) => {
    if (!confirm("اعتماد هذا القيد؟ سيدخل حيز التنفيذ في التقارير المالية فوراً.")) return;
    setActionInProgress(id);
    try {
      await api.post(`/journal/${id}/approve`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    } finally {
      setActionInProgress(null);
    }
  };

  const submitReject = async () => {
    if (!rejectReason) return;
    setActionInProgress(rejectReason.id);
    try {
      await api.post(`/journal/${rejectReason.id}/reject`, { reason: rejectReason.reason || null });
      setRejectReason(null);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    } finally {
      setActionInProgress(null);
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Inbox size={24} className="text-amber-600" />
            القيود المعلقة
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            قيود أنشأها محرك قواعد العمل بانتظار مراجعتك واعتمادك
          </p>
        </div>
        <div className="text-sm text-gray-500">
          {entries.length > 0 ? (
            <span className="badge badge-info text-base px-3 py-1">
              {entries.length} قيد بانتظار الاعتماد
            </span>
          ) : (
            <span className="badge badge-success text-base px-3 py-1">
              <CheckCircle size={14} className="ml-1" /> لا توجد قيود معلقة
            </span>
          )}
        </div>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>
      )}

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : entries.length === 0 ? (
          <div className="text-center py-12 text-gray-500">
            <Inbox size={48} className="mx-auto mb-3 text-gray-300" />
            <p className="text-base font-medium">لا توجد قيود معلقة</p>
            <p className="text-sm mt-1">جميع القيود المولّدة من القواعد تم اعتمادها أو رفضها</p>
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>الرقم</th>
                <th>التاريخ</th>
                <th>البيان</th>
                <th>المصدر</th>
                <th>المبلغ</th>
                <th>أُنشئ</th>
                <th>الإجراءات</th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => {
                // Total = Σ debit (which by construction equals Σ credit
                // for any balanced entry). Don't use Math.max(debit, credit)
                // per line — that would sum the debit AND credit sides
                // independently, doubling the displayed amount for any
                // multi-line entry (e.g. a 3-line sales entry showed
                // 8,160 instead of 4,080 because 4080 + 4000 + 80 = 8160).
                const total = e.lines.reduce((s, l) => s + l.debit, 0);
                const isExpanded = expanded === e.id;
                return (
                  <PendingRow
                    key={e.id}
                    entry={e}
                    isExpanded={isExpanded}
                    total={total}
                    isProcessing={actionInProgress === e.id}
                    onToggle={() => setExpanded(isExpanded ? null : e.id)}
                    onApprove={() => approve(e.id)}
                    onReject={() => setRejectReason({ id: e.id, reason: "" })}
                  />
                );
              })}
            </tbody>
          </table>
        )}
      </div>

      {rejectReason && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-md p-6">
            <h3 className="text-lg font-semibold mb-3">رفض القيد</h3>
            <p className="text-sm text-gray-600 mb-3">
              سيتحول القيد إلى "مسودة" (للتعديل) ولن يدخل التقارير. أضف سبب الرفض (اختياري):
            </p>
            <textarea
              className="input min-h-[80px]"
              value={rejectReason.reason}
              onChange={(e) => setRejectReason({ ...rejectReason, reason: e.target.value.slice(0, 500) })}
              placeholder="مثال: الحساب غير صحيح، أعد التكوين"
              dir="rtl"
              maxLength={500}
            />
            <div className="flex items-center justify-between mt-1">
              <span className="text-xs text-gray-400">
                {rejectReason.reason.length} / 500 حرف
              </span>
              {rejectReason.reason.length > 450 && (
                <span className="text-xs text-amber-600">
                  ⚠ يقترب من الحد الأقصى
                </span>
              )}
            </div>
            <div className="flex gap-2 mt-3">
              <button
                onClick={submitReject}
                disabled={actionInProgress === rejectReason.id}
                className="btn-secondary flex-1 bg-red-50 hover:bg-red-100 text-red-700"
              >
                {actionInProgress === rejectReason.id ? "جاري الرفض..." : "تأكيد الرفض"}
              </button>
              <button
                onClick={() => setRejectReason(null)}
                className="btn-secondary"
              >
                إلغاء
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function PendingRow({
  entry, isExpanded, total, isProcessing,
  onToggle, onApprove, onReject
}: {
  entry: JournalEntry;
  isExpanded: boolean;
  total: number;
  isProcessing: boolean;
  onToggle: () => void;
  onApprove: () => void;
  onReject: () => void;
}) {
  const sourceLabel = entry.source?.startsWith("rule:")
    ? "قاعدة عمل"
    : entry.source === "manual" ? "يدوي" : (entry.source ?? "—");

  return (
    <>
      <tr className="cursor-pointer hover:bg-gray-50" onClick={onToggle}>
        <td className="font-mono font-semibold">{entry.entryNumber}</td>
        <td>{formatDateTime(entry.entryDate)}</td>
        <td className="max-w-md truncate">{entry.narration || "—"}</td>
        <td>
          <span className="badge badge-info text-xs">{sourceLabel}</span>
        </td>
        <td className="font-mono" dir="ltr">{total.toLocaleString("en-US", { minimumFractionDigits: 2 })}</td>
        <td className="text-xs text-gray-500">{formatDateTime(entry.createdAt)}</td>
        <td>
          <div className="flex items-center gap-1">
            <button
              onClick={(e) => { e.stopPropagation(); onApprove(); }}
              disabled={isProcessing}
              className="text-green-600 hover:bg-green-50 p-1.5 rounded text-sm flex items-center gap-1 disabled:opacity-50"
              title="اعتماد"
            >
              <CheckCircle size={16} />
            </button>
            <button
              onClick={(e) => { e.stopPropagation(); onReject(); }}
              disabled={isProcessing}
              className="text-red-600 hover:bg-red-50 p-1.5 rounded disabled:opacity-50"
              title="رفض"
            >
              <XCircle size={16} />
            </button>
            <button
              onClick={(e) => { e.stopPropagation(); onToggle(); }}
              className="text-gray-400 hover:bg-gray-50 p-1 rounded"
            >
              {isExpanded ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
            </button>
          </div>
        </td>
      </tr>
      {isExpanded && (
        <tr>
          <td colSpan={7} className="bg-gray-50 p-4">
            <table className="w-full text-sm">
              <thead>
                <tr className="text-xs text-gray-600">
                  <th className="text-right py-1">الحساب</th>
                  <th className="text-right py-1">البيان</th>
                  <th className="text-left py-1">مدين</th>
                  <th className="text-left py-1">دائن</th>
                </tr>
              </thead>
              <tbody>
                {entry.lines.map((l) => (
                  <tr key={l.id}>
                    <td className="py-1">
                      <span className="font-mono text-xs text-gray-500">{l.accountCode}</span>{" "}
                      {l.accountName}
                    </td>
                    <td className="py-1">{l.description || "—"}</td>
                    <td className="py-1 font-mono" dir="ltr">{l.debit > 0 ? l.debit.toFixed(2) : "—"}</td>
                    <td className="py-1 font-mono" dir="ltr">{l.credit > 0 ? l.credit.toFixed(2) : "—"}</td>
                  </tr>
                ))}
                <tr className="border-t font-semibold">
                  <td colSpan={2} className="py-1">الإجمالي</td>
                  <td className="py-1 font-mono" dir="ltr">
                    {entry.lines.reduce((s, l) => s + l.debit, 0).toFixed(2)}
                  </td>
                  <td className="py-1 font-mono" dir="ltr">
                    {entry.lines.reduce((s, l) => s + l.credit, 0).toFixed(2)}
                  </td>
                </tr>
              </tbody>
            </table>
          </td>
        </tr>
      )}
    </>
  );
}

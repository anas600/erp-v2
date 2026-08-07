"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { FileText, Plus, Loader2, X, CheckCircle, Send, Trash2, RotateCcw, FolderKanban } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";
import ProjectPicker from "../projects/components/ProjectPicker";

interface Account {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  accountType: string;
  nature: string;
}

interface JournalLine {
  id: string;
  accountId: string;
  accountCode?: string;
  accountName?: string;
  debit: number;
  credit: number;
  description?: string;
  lineNumber: number;
  costCenterId?: string;
}

interface JournalEntry {
  id: string;
  companyId: string;
  entryNumber: string;
  entryDate: string;
  narration?: string;
  status: string;
  source?: string;
  ruleId?: string;
  /**
   * FK back to the original entry this one reverses (set on
   * reverse-entries, null on every other entry). Populated by
   * PostingEngine.GetByIdAsync via the journal_entries.reverses_entry_id
   * FK (Sprint 18 — see Migrations/010_JournalEntryReversal).
   */
  reversesEntryId?: string;
  /**
   * Human-readable form of `reversesEntryId` (e.g. "JV-2026-0001").
   * The UI uses this to show the "يعكس JV-2026-0001" badge.
   */
  reversesEntryNumber?: string;
  lines: JournalLine[];
  createdAt: string;
  postedAt?: string;
}

export default function JournalPage() {
  const { activeCompany } = useAuth();
  const [entries, setEntries] = useState<JournalEntry[]>([]);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);

  const [form, setForm] = useState({
    entryDate: new Date().toISOString().slice(0, 10),
    narration: "",
    // Sprint 35 — optional project tag on the journal header.
    // Cost-centers live on lines (per-line granularity), but
    // projects are header-level: one project per entry.
    projectId: "" as string,
    lines: [
      { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" },
      { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" }
    ]
  });

  const [costCenters, setCostCenters] = useState<
    { id: string; code: string; nameAr?: string; name: string }[]
  >([]);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const [entriesRes, accountsRes, ccRes] = await Promise.all([
        api.get(`/journal?companyId=${activeCompany.id}`),
        api.get(`/accounts?companyId=${activeCompany.id}`),
        api.get(`/cost-centers?companyId=${activeCompany.id}`).catch(() => ({ data: [] }))
      ]);
      setEntries(entriesRes.data);
      setAccounts(accountsRes.data);
      setCostCenters(ccRes.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  // Auto-refresh every 30s. The user reported a manual draft
  // they saved that "didn't appear" — in their case the row
  // was in the DB, but the list state was stale because they
  // hadn't tabbed back to the journal page. Polling eliminates
  // that ambiguity: any change made elsewhere (e.g. another
  // tab, or the rule engine auto-creating entries) shows up
  // within 30s without a manual refresh.
  useEffect(() => {
    if (!activeCompany) return;
    const interval = setInterval(() => load(), 30_000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeCompany]);

  const addLine = () => {
    setForm({
      ...form,
      lines: [
        ...form.lines,
        { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" }
      ]
    });
  };

  const removeLine = (idx: number) => {
    setForm({ ...form, lines: form.lines.filter((_, i) => i !== idx) });
  };

  const updateLine = (idx: number, field: string, value: any) => {
    const newLines = [...form.lines];
    newLines[idx] = { ...newLines[idx], [field]: value };
    setForm({ ...form, lines: newLines });
  };

  const totalDebit = form.lines.reduce((sum, l) => sum + (Number(l.debit) || 0), 0);
  const totalCredit = form.lines.reduce((sum, l) => sum + (Number(l.credit) || 0), 0);
  const isBalanced = Math.abs(totalDebit - totalCredit) < 0.01 && totalDebit > 0;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    if (!isBalanced) {
      setError("القيد غير متوازن - إجمالي المدين يجب أن يساوي إجمالي الدائن");
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      const res = await api.post("/journal", {
        companyId: activeCompany.id,
        entryDate: form.entryDate,
        narration: form.narration,
        // Sprint 35 — optional project tag for cost-center reports.
        projectId: form.projectId || null,
        lines: form.lines
          .filter((l) => l.accountId && (l.debit > 0 || l.credit > 0))
          .map((l) => ({
            accountId: l.accountId,
            debit: Number(l.debit) || 0,
            credit: Number(l.credit) || 0,
            description: l.description,
            costCenterId: l.costCenterId || null
          }))
      });
      // Brief inline confirmation so the user knows the save worked
      // (especially helpful when the entry isn't immediately visible
      // in the table due to large lists or sort order).
      setSuccessMessage(`تم حفظ القيد ${res.data.entryNumber} كمسودة`);
      setForm({ entryDate: new Date().toISOString().slice(0, 10), narration: "", projectId: "", lines: [
        { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" },
        { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" }
      ]});
      // Force reload BEFORE closing the modal so the new entry
      // is in `entries` state by the time the user looks at the
      // table. Without this, the modal closes immediately and
      // the user might miss the row if `load()` is slow or throws.
      await load();
      setShowForm(false);
      // Auto-dismiss the success message after 3s
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const postEntry = async (id: string) => {
    try {
      await api.post(`/journal/${id}/post`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  // Sprint 30 — delete a PENDING or DRAFT entry. The backend will:
  //   1. Refuse if status is 'posted' or 'reversed' (use Reverse instead)
  //   2. Cascade-restore the source document (invoice/voucher) to draft
  //      so the data-entry accountant can re-edit and re-post.
  // Safe because PENDING/DRAFT never touched `accounts.balance`.
  const deleteEntry = async (id: string) => {
    if (!confirm(
      "حذف هذا القيد؟\n\n" +
      "سيُحذف القيد وستُرجع الفاتورة/السند الأصلي إلى 'مسودة' لتتمكن من تعديله.\n\n" +
      "هذا آمن لأن القيد لم يدخل التقارير المالية بعد."
    )) return;
    try {
      await api.delete(`/journal/${id}`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  // Reverse a posted entry. This is the ONLY way to "undo" a
  // posted entry in accounting: the original stays in the
  // books (audit trail) but a new "reversing" entry with the
  // opposite debits/credits neutralizes the effect. The
  // original is marked status='reversed' for display.
  const reverseEntry = async (id: string) => {
    if (!confirm(
      "عكس هذا القيد المرحّل؟\n\n" +
      "سيُنشأ قيد عكسي جديد يلغي تأثير القيد الأصلي (دون حذفه) — هذا هو الإجراء المحاسبي الصحيح.\n\n" +
      "في حال ارتبط القيد بسند قبض/صرف، سيُعاد السند إلى مسودة وسيُعاد المبلغ إلى الفاتورة الأصلية (بحالة 'مدفوعة جزئياً' أو 'مفتوحة').\n\n" +
      "هذا الإجراء لا يحذف أي بيانات — كل شيء قابل للمراجعة من دفتر الأستاذ."
    )) return;
    try {
      await api.post(`/journal/${id}/reverse`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong">القيود اليومية</h1>
          <p className="text-sm text-ink-muted mt-1">إنشاء وإدارة و ترحيل القيود المحاسبية</p>
        </div>
        <button onClick={() => setShowForm(true)} className="btn-primary">
          <Plus size={18} />
          قيد جديد
        </button>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {successMessage && (
        <div className="mb-4 p-3 bg-green-50 text-green-700 rounded-md text-sm flex items-center gap-2">
          <CheckCircle size={16} />
          {successMessage}
        </div>
      )}

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>الرقم</th>
                <th>التاريخ</th>
                <th>البيان</th>
                <th>المبلغ</th>
                <th>الحالة</th>
                <th>المصدر</th>
                <th>يعكس</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {entries.map((e) => {
                const total = e.lines.reduce((s, l) => s + Number(l.debit), 0);
                return (
                  <>
                    <tr key={e.id} className="cursor-pointer hover:bg-raised" onClick={() => setExpanded(expanded === e.id ? null : e.id)}>
                      <td className="font-mono font-semibold">{e.entryNumber}</td>
                      <td>{formatDate(e.entryDate)}</td>
                      <td>{e.narration || "-"}</td>
                      <td className="font-mono" dir="ltr">{formatNumber(total)}</td>
                      <td>
                        {e.status === "posted" && <span className="badge badge-success">مرحّل</span>}
                        {e.status === "pending" && <span className="badge badge-info">معلّق</span>}
                        {e.status === "draft" && <span className="badge badge-warning">مسودة</span>}
                        {e.status === "reversed" && <span className="badge badge-danger">معكوس</span>}
                      </td>
                      <td className="text-xs text-ink-muted">{e.source || "يدوي"}</td>
                      <td className="text-xs">
                        {e.reversesEntryNumber ? (
                          // "يعكس JV-2026-0001" — links the reversing
                          // entry to the original in the user's mind.
                          <span className="badge badge-warning" title="يعكس القيد الأصلي">
                            ↩ يعكس {e.reversesEntryNumber}
                          </span>
                        ) : e.status === "reversed" ? (
                          <span className="text-xs text-ink-subtle italic" title="تم عكس هذا القيد بقيد لاحق">
                            تم عكسه
                          </span>
                        ) : (
                          <span className="text-ink-subtle">—</span>
                        )}
                      </td>
                      <td>
                        <div className="flex items-center gap-1">
                          {e.status === "draft" && (
                            <>
                              <button
                                onClick={(ev) => { ev.stopPropagation(); postEntry(e.id); }}
                                className="text-primary-600 hover:bg-primary-50 p-1 rounded text-sm"
                                title="ترحيل (نشر)"
                              >
                                <Send size={14} />
                              </button>
                              <button
                                onClick={(ev) => { ev.stopPropagation(); deleteEntry(e.id); }}
                                className="text-red-600 hover:bg-red-50 p-1 rounded text-sm"
                                title="حذف (المسودة فقط)"
                              >
                                <Trash2 size={14} />
                              </button>
                            </>
                          )}
                          {e.status === "pending" && (
                            <div className="flex items-center gap-1">
                              <a
                                href="/dashboard/journal/pending"
                                className="text-xs text-amber-600 hover:underline"
                              >
                                من صفحة المعلقة
                              </a>
                              <span className="text-ink-subtle">|</span>
                              <button
                                onClick={(ev) => { ev.stopPropagation(); deleteEntry(e.id); }}
                                className="text-red-500 hover:bg-red-50 p-1 rounded text-sm"
                                title="حذف القيد المعلّق (يُعيد المصدر لمسودة)"
                              >
                                <Trash2 size={14} />
                              </button>
                            </div>
                          )}
                          {e.status === "posted" && (
                            <button
                              onClick={(ev) => { ev.stopPropagation(); reverseEntry(e.id); }}
                              className="text-amber-600 hover:bg-amber-50 p-1 rounded text-sm"
                              title="عكس (قيد عكسي)"
                            >
                              <RotateCcw size={14} />
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                    {expanded === e.id && (
                      <tr key={e.id + "-detail"}>
                        <td colSpan={8} className="bg-raised p-4">
                          <table className="w-full text-sm">
                            <thead>
                              <tr className="text-xs text-ink-muted">
                                <th className="text-right py-1">الحساب</th>
                                <th className="text-right py-1">البيان</th>
                                <th className="text-left py-1">مدين</th>
                                <th className="text-left py-1">دائن</th>
                              </tr>
                            </thead>
                            <tbody>
                              {e.lines.map((l) => (
                                <tr key={l.id}>
                                  <td className="py-1">
                                    <span className="font-mono text-xs text-ink-muted">{l.accountCode}</span>{" "}
                                    {l.accountName}
                                  </td>
                                  <td className="py-1 text-ink-muted">{l.description || "-"}</td>
                                  <td className="py-1 font-mono" dir="ltr">{formatNumber(l.debit)}</td>
                                  <td className="py-1 font-mono" dir="ltr">{formatNumber(l.credit)}</td>
                                </tr>
                              ))}
                              <tr className="border-t font-semibold">
                                <td colSpan={2} className="py-1">الإجمالي</td>
                                <td className="py-1 font-mono" dir="ltr">{formatNumber(totalDebit)}</td>
                                <td className="py-1 font-mono" dir="ltr">{formatNumber(totalDebit)}</td>
                              </tr>
                            </tbody>
                          </table>
                        </td>
                      </tr>
                    )}
                  </>
                );
              })}
              {entries.length === 0 && (
                <tr>
                  <td colSpan={7} className="text-center text-ink-muted py-6">لا توجد قيود</td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4 overflow-y-auto">
          <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-4xl p-6 my-8">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">قيد يومية جديد</h2>
              <button onClick={() => setShowForm(false)} className="text-ink-subtle hover:text-ink-muted">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submit} className="space-y-3">
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">التاريخ *</label>
                  <input type="date" className="input" value={form.entryDate} onChange={(e) => setForm({ ...form, entryDate: e.target.value })} required />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">البيان</label>
                  <input className="input" value={form.narration} onChange={(e) => setForm({ ...form, narration: e.target.value })} placeholder="وصف القيد" />
                </div>
              </div>

              <div>
                {/* Sprint 35 — project tag (optional). One project
                    per journal entry. Cost-centers stay per-line
                    because accountants often need to split a single
                    entry across multiple cost-centers. */}
                <label className="block text-sm font-medium mb-1 flex items-center gap-1">
                  <FolderKanban size={12} />
                  المشروع
                  <span className="text-xs text-ink-muted mr-1">(اختياري — لتحميل التكلفة على مركز تكلفة المشروع)</span>
                </label>
                <ProjectPicker
                  companyId={activeCompany?.id}
                  value={form.projectId || null}
                  onChange={(id) => setForm({ ...form, projectId: id || "" })}
                  disabled={submitting}
                />
              </div>

              <div>
                <div className="flex items-center justify-between mb-2">
                  <h3 className="text-sm font-semibold">بنود القيد</h3>
                  <button type="button" onClick={addLine} className="text-sm text-primary-600 hover:underline">
                    + بند جديد
                  </button>
                </div>
                <div className="space-y-2">
                  {form.lines.map((line, idx) => (
                    <div key={idx} className="grid grid-cols-12 gap-2 items-center">
                      <select
                        className="input col-span-5"
                        value={line.accountId}
                        onChange={(e) => updateLine(idx, "accountId", e.target.value)}
                      >
                        <option value="">- اختر حساب -</option>
                        {accounts.map((a) => (
                          <option key={a.id} value={a.id}>
                            {a.code} - {a.nameAr || a.name} ({a.nature === "Debit" ? "مدين" : "دائن"})
                          </option>
                        ))}
                      </select>
                      <input
                        type="number"
                        step="0.01"
                        className="input col-span-2"
                        placeholder="مدين"
                        value={line.debit || ""}
                        onChange={(e) => updateLine(idx, "debit", e.target.value)}
                        dir="ltr"
                      />
                      <input
                        type="number"
                        step="0.01"
                        className="input col-span-2"
                        placeholder="دائن"
                        value={line.credit || ""}
                        onChange={(e) => updateLine(idx, "credit", e.target.value)}
                        dir="ltr"
                      />
                      <input
                        className="input col-span-2"
                        placeholder="بيان البند"
                        value={line.description}
                        onChange={(e) => updateLine(idx, "description", e.target.value)}
                      />
                      <select
                        className="input col-span-2"
                        value={line.costCenterId || ""}
                        onChange={(e) => updateLine(idx, "costCenterId", e.target.value)}
                        title="مركز التكلفة"
                      >
                        <option value="">- مركز التكلفة -</option>
                        {costCenters
                          .filter((c) => c.id && c.code)
                          .map((c) => (
                            <option key={c.id} value={c.id}>
                              {c.code} - {c.nameAr || c.name}
                            </option>
                          ))}
                      </select>
                      <button type="button" onClick={() => removeLine(idx)} className="text-red-500 hover:text-red-700 col-span-1">
                        <X size={16} />
                      </button>
                    </div>
                  ))}
                </div>

                <div className="mt-3 p-3 bg-raised rounded-md flex items-center justify-between text-sm">
                  <div className="flex gap-6">
                    <div>
                      <span className="text-ink-muted ml-2">إجمالي المدين:</span>
                      <span className="font-mono font-semibold" dir="ltr">{formatNumber(totalDebit)}</span>
                    </div>
                    <div>
                      <span className="text-ink-muted ml-2">إجمالي الدائن:</span>
                      <span className="font-mono font-semibold" dir="ltr">{formatNumber(totalCredit)}</span>
                    </div>
                  </div>
                  {isBalanced ? (
                    <span className="badge badge-success"><CheckCircle size={12} className="ml-1" /> متوازن</span>
                  ) : (
                    <span className="badge badge-danger">غير متوازن</span>
                  )}
                </div>
              </div>

              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting || !isBalanced} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : "حفظ كمسودة"}
                </button>
                <button type="button" onClick={() => setShowForm(false)} className="btn-secondary">
                  إلغاء
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}

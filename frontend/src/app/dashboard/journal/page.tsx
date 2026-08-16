"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { FileText, Plus, Loader2, X, CheckCircle, Send, Trash2, RotateCcw, FolderKanban, Pencil } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";
import ProjectPicker from "../projects/components/ProjectPicker";

interface Account {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  accountType: string;
  nature: string;
  level?: number;
  isPostable?: boolean;
  isActive?: boolean;
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
  // Sprint 41 — pagination state. The backend's GET /api/journal
  // returns {items, total, limit, offset} when ?limit and ?offset
  // are passed. We use a fixed page size of 50 and let the user
  // step forward/backward. The page navigator shows page N of
  // ceil(total / limit).
  const [page, setPage] = useState(0);
  const [pageSize] = useState(50);
  const [totalEntries, setTotalEntries] = useState(0);
  // Sprint 41 — bulk-action state. The two buttons at the top of
  // the table trigger POST /api/journal/bulk-approve and
  // POST /api/journal/bulk-post. The backend returns
  // {approved, posted, failed, succeededIds, failures}.
  const [bulkProcessing, setBulkProcessing] = useState(false);
  const [statusFilter, setStatusFilter] = useState<string>("all");

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

  // Sprint 59 — Edit mode state. When `editingId` is set, the form
  // is bound to an existing draft journal entry and submit() calls
  // PUT /api/journal/{id} instead of POST /api/journal. When null,
  // the form is in "create" mode.
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editingEntryNumber, setEditingEntryNumber] = useState<string | null>(null);

  const [costCenters, setCostCenters] = useState<
    { id: string; code: string; nameAr?: string; name: string }[]
  >([]);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      // Sprint 41 — paginated journal fetch. The backend's
      // GET /api/journal accepts ?limit= and ?offset= query
      // params and returns {items, total, limit, offset}.
      // We only show the active page; the page navigator at
      // the bottom uses total to know how many pages there are.
      const offset = page * pageSize;
      const statusQuery = statusFilter === "all" ? "" : `&status=${statusFilter}`;
      const [entriesRes, accountsRes, ccRes] = await Promise.all([
        api.get(`/journal?companyId=${activeCompany.id}&limit=${pageSize}&offset=${offset}${statusQuery}`),
        api.get(`/accounts?companyId=${activeCompany.id}`),
        api.get(`/cost-centers?companyId=${activeCompany.id}`).catch(() => ({ data: [] }))
      ]);
      // The backend returns either a flat array (legacy) or
      // {items, total, ...} (Sprint 41). Handle both.
      const data = entriesRes.data;
      if (Array.isArray(data)) {
        setEntries(data);
        setTotalEntries(data.length);
      } else {
        setEntries(data.items || []);
        setTotalEntries(data.total || 0);
      }
      // Sprint 59 — the accounts endpoint returns a TREE (L1 roots
      // with nested children). For the journal entry dropdown we
      // need every postable L4 account. Flatten the tree and keep
      // only isPostable=true rows so the user can pick any
      // sub-ledger (cash, bank, AR, AP, expense, project cost, etc.)
      // The general-ledger page has the same pattern with indented
      // labels (↳ L4, ‖ L2). We use a similar one here.
      const rawAccounts: any[] = Array.isArray(accountsRes.data)
        ? accountsRes.data
        : (accountsRes.data?.data || []);
      const flatten = (nodes: any[]): any[] => {
        const out: any[] = [];
        const walk = (n: any) => {
          const { children, ...rest } = n;
          out.push(rest);
          if (Array.isArray(children)) children.forEach(walk);
        };
        nodes.forEach(walk);
        return out;
      };
      const flat = flatten(rawAccounts);
      // Keep only postable (L4) and active accounts. The L1/L2/L3
      // are headers/control accounts and can't be posted to
      // directly — the system would reject the journal entry.
      // Sort by code so the dropdown shows accounts in a natural
      // reading order (1101-CASH-001, 1102-BANK-001, 1103-CUS-001,
      // 2101-SUP-001, ...).
      const postable = flat
        .filter((a) => a.isPostable && a.isActive !== false)
        .sort((x, y) => String(x.code).localeCompare(String(y.code)));
      setAccounts(postable);
      setCostCenters(ccRes.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany, page, statusFilter]);

  // Sprint 41 — bulk approve all draft entries. Calls
  // POST /api/journal/bulk-approve?companyId=X which returns
  // {approved, failed, succeededIds, failures}. Useful when
  // the seeder has stacked up dozens of drafts (e.g. on first
  // deploy or after a migration).
  const bulkApproveAll = async () => {
    if (!activeCompany) return;
    if (!confirm("موافقة كل القيود المعلّقة لهذه الشركة؟")) return;
    try {
      setBulkProcessing(true);
      const res = await api.post(`/journal/bulk-approve?companyId=${activeCompany.id}`);
      const data = res.data;
      setSuccessMessage(`تمت الموافقة على ${data.approved} قيد${data.failed > 0 ? `، فشل ${data.failed}` : ""}`);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBulkProcessing(false);
      setTimeout(() => setSuccessMessage(null), 5000);
    }
  };

  // Sprint 41 — bulk post all approved entries. Calls
  // POST /api/journal/bulk-post?companyId=X. With the
  // trusted-accountant flow + auto-seed, this is rarely
  // needed; useful when migrating from a state where lots
  // of drafts accumulated (e.g. from a faulty old run).
  const bulkPostAll = async () => {
    if (!activeCompany) return;
    if (!confirm("ترحيل كل القيود المعلّقة لهذه الشركة؟ هذا الإجراء نهائي.")) return;
    try {
      setBulkProcessing(true);
      const res = await api.post(`/journal/bulk-post?companyId=${activeCompany.id}`);
      const data = res.data;
      setSuccessMessage(`تم ترحيل ${data.posted} قيد${data.failed > 0 ? `، فشل ${data.failed}` : ""}`);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBulkProcessing(false);
      setTimeout(() => setSuccessMessage(null), 5000);
    }
  };

  // Auto-refresh every 30s. The user reported a manual draft
  // they saved that "didn't appear" — in their case the row
  // was in the DB, but the list state was stale because they
  // hadn't tabbed back to the journal page. Polling eliminates
  // that ambiguity: any change made elsewhere (e.g. another
  // Sprint 45: removed the 30s polling. The user reported the auto-refresh
  // was annoying AND kept the server awake (preventing Render's free
  // tier from sleeping naturally). The user can refresh manually with
  // the browser refresh button or by clicking a row / filter.
  //
  // Background: Render free tier sleeps after ~15 min of inactivity.
  // The 30s poll from this page (plus journal, payments, receipts)
  // was waking the server every 30s and burning API quota. We removed
  // all three polls so the server can sleep naturally.
  //
  // If the user needs fresh data, they refresh the page (F5) or
  // navigate away and back. This is the same model as the rest of
  // the app (no polling anywhere).

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
      const payload = {
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
      };

      // Sprint 59 — Branch on create vs update. When `editingId` is
      // set, the form is bound to an existing draft entry, so we
      // call PUT /api/journal/{id} instead of POST /api/journal.
      // The payload shape is identical (CreateJournalEntryRequest
      // is reused server-side to keep the validation single-sourced).
      let res;
      if (editingId) {
        res = await api.put(`/journal/${editingId}`, payload);
        setSuccessMessage(`تم تحديث القيد ${res.data.entryNumber}`);
      } else {
        res = await api.post("/journal", payload);
        setSuccessMessage(`تم حفظ القيد ${res.data.entryNumber} كمسودة`);
      }

      setForm({ entryDate: new Date().toISOString().slice(0, 10), narration: "", projectId: "", lines: [
        { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" },
        { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" }
      ]});
      // Clear edit state so the next "+ قيد جديد" opens in create mode.
      setEditingId(null);
      setEditingEntryNumber(null);
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

  /**
   * Sprint 59 — Open the form in edit mode, pre-populated with the
   * selected draft entry's header + lines. The accountant can then
   * change the narration, the date, the project tag, or any line
   * (account, debit/credit, description, cost-center). On submit,
   * the form calls PUT /api/journal/{id}.
   *
   * Only draft entries are editable. If the entry is not a draft
   * (e.g. user clicks Edit on a posted row by mistake), we throw
   * — the button is hidden in the table for non-draft rows anyway.
   */
  const editEntry = async (entry: JournalEntry) => {
    if (entry.status !== "draft") {
      setError("لا يمكن تعديل قيد غير مسودة. اعكسه بقيد عكسي بدلاً من ذلك.");
      setTimeout(() => setError(null), 4000);
      return;
    }
    setError(null);
    setEditingId(entry.id);
    setEditingEntryNumber(entry.entryNumber);
    setForm({
      entryDate: entry.entryDate.slice(0, 10),
      narration: entry.narration || "",
      projectId: (entry as any).projectId || "",
      // The backend always returns at least 1 line for an existing
      // entry. We re-hydrate all of them; if there are more than
      // 2, the user will see all rows in the form.
      lines: entry.lines.length > 0
        ? entry.lines.map((l) => ({
            accountId: l.accountId,
            debit: l.debit,
            credit: l.credit,
            description: l.description || "",
            costCenterId: l.costCenterId || ""
          }))
        : [{ accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" }]
    });
    setShowForm(true);
  };

  /**
   * Sprint 59 — Close the form and clear edit state. Used by the
   * modal's "X" close button and the "إلغاء" cancel button so
   * that the next "+ قيد جديد" opens in create mode (not edit).
   */
  const closeForm = () => {
    setShowForm(false);
    setEditingId(null);
    setEditingEntryNumber(null);
    setError(null);
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
        <div className="flex items-center gap-2">
          {/* Sprint 41 — bulk actions. The two buttons trigger
              the bulk-approve and bulk-post endpoints. They
              only show when the user has at least one entry
              to act on (the backend returns 0 if nothing to
              do, but we still let the user try). */}
          <button
            onClick={bulkApproveAll}
            disabled={bulkProcessing}
            className="btn-secondary"
            title="موافقة كل القيود المعلّقة دفعة واحدة"
          >
            {bulkProcessing ? <Loader2 size={18} className="animate-spin" /> : <CheckCircle size={18} />}
            موافقة الكل
          </button>
          <button
            onClick={bulkPostAll}
            disabled={bulkProcessing}
            className="btn-secondary"
            title="ترحيل كل القيود المعلّقة دفعة واحدة"
          >
            {bulkProcessing ? <Loader2 size={18} className="animate-spin" /> : <Send size={18} />}
            ترحيل الكل
          </button>
          <button
            onClick={() => {
              // Sprint 59 — Reset edit state when opening the "new"
              // button, so the form is always in create mode.
              setEditingId(null);
              setEditingEntryNumber(null);
              setForm({
                entryDate: new Date().toISOString().slice(0, 10),
                narration: "",
                projectId: "",
                lines: [
                  { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" },
                  { accountId: "", debit: 0, credit: 0, description: "", costCenterId: "" }
                ]
              });
              setShowForm(true);
            }}
            className="btn-primary"
          >
            <Plus size={18} />
            قيد جديد
          </button>
        </div>
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
                              {/* Sprint 59 — Edit (only on drafts). Loads
                                  the entry into the create form so the
                                  accountant can change accounts, amounts,
                                  or narration before posting. */}
                              <button
                                onClick={(ev) => { ev.stopPropagation(); editEntry(e); }}
                                className="text-blue-600 hover:bg-blue-50 p-1 rounded text-sm"
                                title="تعديل القيد"
                              >
                                <Pencil size={14} />
                              </button>
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

        {/* Sprint 41 — pagination + status filter. The page
            navigator shows the current page and total count
            (from the backend's `total` field), with prev/next
            buttons. The status filter lets the user drill
            down to draft-only or posted-only without leaving
            the page. */}
        {!loading && totalEntries > 0 && (
          <div className="mt-4 flex items-center justify-between border-t border-ink-border pt-3">
            <div className="flex items-center gap-2 text-sm text-ink-muted">
              <label>تصفية:</label>
              <select
                value={statusFilter}
                onChange={(e) => { setPage(0); setStatusFilter(e.target.value); }}
                className="input py-1 text-sm"
              >
                <option value="all">الكل</option>
                <option value="draft">مسودة</option>
                <option value="posted">مرحّل</option>
                <option value="pending">معلّق</option>
                <option value="reversed">معكوس</option>
              </select>
              <span className="text-ink-subtle">|</span>
              <span>إجمالي: <span className="font-mono font-semibold text-ink-strong">{totalEntries}</span> قيد</span>
            </div>
            <div className="flex items-center gap-2">
              <button
                onClick={() => setPage(Math.max(0, page - 1))}
                disabled={page === 0}
                className="btn-secondary py-1 px-3 text-sm"
              >
                السابق
              </button>
              <span className="text-sm text-ink-muted">
                صفحة <span className="font-mono font-semibold">{page + 1}</span> / {Math.max(1, Math.ceil(totalEntries / pageSize))}
              </span>
              <button
                onClick={() => setPage(page + 1)}
                disabled={(page + 1) * pageSize >= totalEntries}
                className="btn-secondary py-1 px-3 text-sm"
              >
                التالي
              </button>
            </div>
          </div>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4 overflow-y-auto">
          <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-4xl p-6 my-8">
            <div className="flex items-center justify-between mb-4">
              {/* Sprint 59 — Modal title changes between "new" and "edit"
                  mode. In edit mode we show the original entry number
                  so the accountant can confirm which entry they're
                  modifying. */}
              <h2 className="text-lg font-semibold">
                {editingId ? `تعديل القيد ${editingEntryNumber}` : "قيد يومية جديد"}
              </h2>
              <button onClick={closeForm} className="text-ink-subtle hover:text-ink-muted">
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
                        {accounts.map((a) => {
                          // Sprint 59 — the user reported the
                          // dropdown only showed L1 accounts. We
                          // now feed it the flattened list of every
                          // L4 (postable) account, so the accountant
                          // can pick any cash/bank/AR/AP/expense
                          // sub-ledger. The nature suffix (مدين/
                          // دائن) makes the debit/credit side
                          // obvious at a glance.
                          const lvl = a.level ?? 4;
                          const indent = lvl === 4 ? "↳ " : lvl === 3 ? "  ‖ " : "    ";
                          return (
                            <option key={a.id} value={a.id}>
                              {indent}{a.code} — {a.nameAr || a.name} ({a.nature === "Debit" ? "مدين" : "دائن"})
                            </option>
                          );
                        })}
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
                  {submitting
                    ? (editingId ? "جاري التحديث..." : "جاري الحفظ...")
                    : (editingId ? "تحديث القيد" : "حفظ كمسودة")}
                </button>
                <button type="button" onClick={closeForm} className="btn-secondary">
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

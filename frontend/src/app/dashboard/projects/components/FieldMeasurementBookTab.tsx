"use client";

/**
 * Sprint 55 — Field Measurement Book tab.
 *
 * Shown in /dashboard/projects/[id] as a new tab "📏 الدفتر الفني".
 * Lists all FMBs (دفاتر المقاسات) for this project, with:
 *   - "دفتر جديد" button → modal to create a new FMB
 *   - Each FMB shows status, total amount, engineer + consultant names
 *   - "View" button → page/modal with the FMB's entries
 *
 * The entry editor (sub-rows with count × length × width × height)
 * lives in FieldMeasurementEntryForm.tsx and is shown inside the
 * create/edit modal.
 */
import { useEffect, useState } from "react";
import {
  Loader2, Plus, Eye, FileText, CheckCircle, Send, XCircle, AlertCircle, X
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatDate, formatNumber, cn } from "@/lib/utils";
import FieldMeasurementEntryForm, { type FieldMeasurementEntryDto } from "./FieldMeasurementEntryForm";

export interface MeasurementRow {
  label?: string | null;
  count?: number | null;
  length?: number | null;
  width?: number | null;
  height?: number | null;
  initialQty?: number | null;
  deduction?: number | null;
  notes?: string | null;
}

export interface FieldMeasurementBookDto {
  id: string;
  companyId: string;
  projectId: string;
  contractId?: string | null;
  bookNumber: string;
  measurementDate: string;
  periodFrom?: string | null;
  periodTo?: string | null;
  engineerUserId?: string | null;
  engineerName?: string | null;
  consultantUserId?: string | null;
  consultantName?: string | null;
  status: string;            // "DRAFT" | "SUBMITTED" | "APPROVED" | "CANCELLED"
  approvedAt?: string | null;
  notes?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  entries: FieldMeasurementEntryDto[];
}

export interface LineItemDto {
  id: string;
  lineNumber: number;
  description: string;
  unit: string;
  quantity: number;
  unitPrice: number;
}

interface Props {
  projectId: string;
  /** Optional pre-loaded line items (when parent has them). */
  lineItems?: LineItemDto[];
  /** Optional pre-loaded FMBs (when integrated with parent). */
  initialBooks?: FieldMeasurementBookDto[];
}

export default function FieldMeasurementBookTab({ projectId, lineItems: initialLineItems, initialBooks }: Props) {
  const [books, setBooks] = useState<FieldMeasurementBookDto[]>(initialBooks || []);
  const [loading, setLoading] = useState(!initialBooks);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [viewing, setViewing] = useState<FieldMeasurementBookDto | null>(null);
  // Sprint 58 — if the parent didn't pass line items, fetch them ourselves
  // by first getting the project contract, then the contract line items.
  const [lineItems, setLineItems] = useState<LineItemDto[]>(initialLineItems || []);

  const loadLineItems = async () => {
    try {
      // 1) Get the project's contract
      const contractRes = await api.get<{ id: string }>(
        `/projects/${projectId}/contract`
      );
      if (!contractRes.data?.id) {
        // No contract yet — leave lineItems empty
        return;
      }
      // 2) Get the contract's line items
      const itemsRes = await api.get<LineItemDto[]>(
        `/contracts/${contractRes.data.id}/line-items`
      );
      const list = Array.isArray(itemsRes.data) ? itemsRes.data : (itemsRes.data as any)?.items || [];
      setLineItems(list);
    } catch (err) {
      // No contract or no line items yet — that's OK
      setLineItems([]);
    }
  };

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get(`/projects/${projectId}/field-measurement-books`);
      setBooks(res.data || []);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (!initialBooks) load();
    if (!initialLineItems || initialLineItems.length === 0) loadLineItems();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const handleCreated = (b: FieldMeasurementBookDto) => {
    setBooks((prev) => [b, ...prev]);
    setCreating(false);
  };

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <div>
          <h3 className="text-base font-semibold">الدفاتر الفنية (المقاسات)</h3>
          <p className="text-xs text-ink-muted">
            دفتر المقاسات يوثق الكميات المنفذة في الموقع لكل بند من بنود العقد
          </p>
        </div>
        <button
          onClick={() => setCreating(true)}
          className="btn-primary flex items-center gap-2"
        >
          <Plus size={16} /> دفتر جديد
        </button>
      </div>

      {error && (
        <div className="p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 flex items-start gap-1">
          <AlertCircle size={12} className="mt-0.5 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {loading ? (
        <div className="card flex items-center justify-center py-8 text-ink-muted gap-2 text-sm">
          <Loader2 className="animate-spin" size={16} />
          جاري تحميل الدفاتر...
        </div>
      ) : books.length === 0 ? (
        <div className="card text-center text-ink-muted py-6 text-sm">
          لا توجد دفاتر فنية بعد. اضغط "دفتر جديد" للبدء.
        </div>
      ) : (
        <div className="card p-0 overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="bg-raised border-b border-edge">
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">رقم الدفتر</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">التاريخ</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">الفترة</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">المهندس</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">المستشار</th>
                <th className="text-left py-2 px-3 font-semibold text-ink-muted">الإجمالي</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">الحالة</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted w-24">إجراءات</th>
              </tr>
            </thead>
            <tbody>
              {books.map((b) => {
                const total = b.entries.reduce((s, e) => s + (e.amount || 0), 0);
                return (
                  <tr key={b.id} className="border-b border-edge">
                    <td className="py-2 px-3 font-mono text-xs">{b.bookNumber}</td>
                    <td className="py-2 px-3">{formatDate(b.measurementDate)}</td>
                    <td className="py-2 px-3 text-xs text-ink-muted">
                      {b.periodFrom ? formatDate(b.periodFrom) : "—"} → {b.periodTo ? formatDate(b.periodTo) : "—"}
                    </td>
                    <td className="py-2 px-3 text-xs">{b.engineerName || "—"}</td>
                    <td className="py-2 px-3 text-xs">{b.consultantName || "—"}</td>
                    <td className="py-2 px-3 text-left font-mono" dir="ltr">
                      {formatNumber(total, 3)}
                    </td>
                    <td className="py-2 px-3">
                      <StatusBadge status={b.status} />
                    </td>
                    <td className="py-2 px-3">
                      <button
                        onClick={() => setViewing(b)}
                        className="text-primary-600 hover:text-primary-800 inline-flex items-center gap-1 text-xs"
                        title="عرض الدفتر"
                      >
                        <Eye size={14} /> عرض
                      </button>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {creating && (
        <CreateFmbModal
          projectId={projectId}
          lineItems={lineItems}
          onClose={() => setCreating(false)}
          onCreated={handleCreated}
        />
      )}

      {viewing && (
        <ViewFmbModal
          book={viewing}
          onClose={() => setViewing(null)}
          onChanged={async () => {
            await load();
            const res = await api.get(`/projects/${projectId}/field-measurement-books`);
            const found = (res.data || []).find((b: FieldMeasurementBookDto) => b.id === viewing.id);
            if (found) setViewing(found);
          }}
        />
      )}
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const map: Record<string, { label: string; color: string }> = {
    DRAFT: { label: "مسودة", color: "bg-gray-100 text-gray-700" },
    SUBMITTED: { label: "مقدّم", color: "bg-blue-100 text-blue-700" },
    APPROVED: { label: "معتمد", color: "bg-green-100 text-green-700" },
    CANCELLED: { label: "ملغي", color: "bg-red-100 text-red-700" },
  };
  const m = map[status] || { label: status, color: "bg-gray-100 text-gray-700" };
  return (
    <span className={cn("px-2 py-0.5 rounded text-xs font-medium", m.color)}>
      {m.label}
    </span>
  );
}

function CreateFmbModal({
  projectId,
  lineItems,
  onClose,
  onCreated,
}: {
  projectId: string;
  lineItems: LineItemDto[];
  onClose: () => void;
  onCreated: (b: FieldMeasurementBookDto) => void;
}) {
  const [bookNumber, setBookNumber] = useState(`FMB-${new Date().getFullYear()}-001`);
  const [measurementDate, setMeasurementDate] = useState(
    new Date().toISOString().slice(0, 10)
  );
  const [periodFrom, setPeriodFrom] = useState("");
  const [periodTo, setPeriodTo] = useState("");
  const [engineerName, setEngineerName] = useState("م. أحمد الفيتوري");
  const [consultantName, setConsultantName] = useState("م. دار التقنية");
  const [notes, setNotes] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const res = await api.post(`/projects/${projectId}/field-measurement-books`, {
        bookNumber,
        measurementDate,
        periodFrom: periodFrom || null,
        periodTo: periodTo || null,
        engineerName,
        consultantName,
        notes: notes || null,
      });
      onCreated(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-2 sm:p-4">
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-2xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold flex items-center gap-2">
            <FileText size={18} className="text-primary-600" /> دفتر فني جديد
          </h2>
          <button onClick={onClose} className="text-ink-subtle hover:text-ink-muted">
            <X size={20} />
          </button>
        </div>
        {error && (
          <div className="p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 mb-3">
            {error}
          </div>
        )}
        <form onSubmit={submit} className="space-y-3 text-sm">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">رقم الدفتر</label>
              <input className="input" value={bookNumber} onChange={(e) => setBookNumber(e.target.value)} required dir="ltr" />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">تاريخ القياس</label>
              <input type="date" className="input" value={measurementDate} onChange={(e) => setMeasurementDate(e.target.value)} required />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">الفترة من</label>
              <input type="date" className="input" value={periodFrom} onChange={(e) => setPeriodFrom(e.target.value)} />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">الفترة إلى</label>
              <input type="date" className="input" value={periodTo} onChange={(e) => setPeriodTo(e.target.value)} />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">المهندس المنفذ</label>
              <input className="input" value={engineerName} onChange={(e) => setEngineerName(e.target.value)} />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">المهندس المشرف</label>
              <input className="input" value={consultantName} onChange={(e) => setConsultantName(e.target.value)} />
            </div>
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">ملاحظات</label>
            <textarea className="input" value={notes} onChange={(e) => setNotes(e.target.value)} rows={2} />
          </div>
          <div className="flex gap-2 justify-end pt-2">
            <button type="button" onClick={onClose} className="btn-secondary">إلغاء</button>
            <button type="submit" disabled={busy} className="btn-primary flex items-center gap-2">
              {busy && <Loader2 className="animate-spin" size={14} />}
              إنشاء
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

function ViewFmbModal({
  book,
  onClose,
  onChanged,
}: {
  book: FieldMeasurementBookDto;
  onClose: () => void;
  onChanged: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const total = book.entries.reduce((s, e) => s + (e.amount || 0), 0);
  const isDraft = book.status === "DRAFT";
  const isSubmitted = book.status === "SUBMITTED";

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post(`/field-measurement-books/${book.id}/submit`, { comments: null });
      await onChanged();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const approve = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.post(`/field-measurement-books/${book.id}/approve`, { comments: null });
      await onChanged();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const reject = async () => {
    const reason = window.prompt("سبب الرفض:");
    if (!reason) return;
    setBusy(true);
    setError(null);
    try {
      await api.post(`/field-measurement-books/${book.id}/reject`, { reason });
      await onChanged();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-2 sm:p-4">
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-5xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold flex items-center gap-2 flex-wrap">
            <FileText size={18} className="text-primary-600" /> دفتر فني #{book.bookNumber}
            <StatusBadge status={book.status} />
          </h2>
          <button onClick={onClose} className="text-ink-subtle hover:text-ink-muted">
            <X size={20} />
          </button>
        </div>
        {error && (
          <div className="p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 mb-3">
            {error}
          </div>
        )}
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-sm mb-4">
          <Row label="التاريخ" value={formatDate(book.measurementDate)} />
          <Row label="الفترة" value={`${book.periodFrom ? formatDate(book.periodFrom) : "—"} → ${book.periodTo ? formatDate(book.periodTo) : "—"}`} />
          <Row label="المهندس المنفذ" value={book.engineerName || "—"} />
          <Row label="المهندس المشرف" value={book.consultantName || "—"} />
        </div>
        {book.notes && (
          <div className="mb-3 text-sm bg-raised rounded p-2">
            <span className="text-ink-muted">ملاحظات: </span>{book.notes}
          </div>
        )}

        <h4 className="font-semibold text-sm mb-2">بنود الدفتر</h4>
        {book.entries.length === 0 ? (
          <div className="card text-center text-ink-muted py-4 text-sm">
            لا توجد بنود في هذا الدفتر بعد.
          </div>
        ) : (
          <div className="card p-0 overflow-x-auto mb-3">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-raised border-b border-edge">
                  <th className="text-right py-2 px-2 font-semibold text-ink-muted">#</th>
                  <th className="text-right py-2 px-2 font-semibold text-ink-muted">البند</th>
                  <th className="text-right py-2 px-2 font-semibold text-ink-muted">الوحدة</th>
                  <th className="text-left py-2 px-2 font-semibold text-ink-muted">ابتدائي</th>
                  <th className="text-left py-2 px-2 font-semibold text-ink-muted">تنزيلات</th>
                  <th className="text-left py-2 px-2 font-semibold text-ink-muted">نهائي</th>
                  <th className="text-left py-2 px-2 font-semibold text-ink-muted">سعر</th>
                  <th className="text-left py-2 px-2 font-semibold text-ink-muted">المبلغ</th>
                </tr>
              </thead>
              <tbody>
                {book.entries.map((e, idx) => (
                  <tr key={e.id} className="border-b border-edge">
                    <td className="py-2 px-2 font-mono text-xs">{e.lineNumber}</td>
                    <td className="py-2 px-2">{e.description}</td>
                    <td className="py-2 px-2 text-xs">{e.unit}</td>
                    <td className="py-2 px-2 text-left font-mono" dir="ltr">{formatNumber(e.initialTotal, 3)}</td>
                    <td className="py-2 px-2 text-left font-mono text-ink-muted" dir="ltr">{formatNumber(e.deductionsTotal, 3)}</td>
                    <td className="py-2 px-2 text-left font-mono font-semibold" dir="ltr">{formatNumber(e.finalTotal, 3)}</td>
                    <td className="py-2 px-2 text-left font-mono" dir="ltr">{formatNumber(e.unitPrice, 3)}</td>
                    <td className="py-2 px-2 text-left font-mono text-primary-700" dir="ltr">{formatNumber(e.amount, 3)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="bg-raised font-semibold">
                  <td colSpan={7} className="py-2 px-2 text-right">إجمالي الدفتر</td>
                  <td className="py-2 px-2 text-left font-mono text-primary-700" dir="ltr">{formatNumber(total, 3)}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        )}

        {/* Lifecycle actions */}
        {isDraft && (
          <div className="flex justify-end gap-2">
            <button onClick={submit} disabled={busy} className="btn-primary flex items-center gap-2">
              {busy ? <Loader2 className="animate-spin" size={14} /> : <Send size={14} />}
              تقديم للاعتماد
            </button>
          </div>
        )}
        {isSubmitted && (
          <div className="flex justify-end gap-2">
            <button onClick={reject} disabled={busy} className="btn-secondary flex items-center gap-2 text-red-700">
              <XCircle size={14} /> رفض
            </button>
            <button onClick={approve} disabled={busy} className="btn-primary flex items-center gap-2">
              {busy ? <Loader2 className="animate-spin" size={14} /> : <CheckCircle size={14} />}
              اعتماد
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

function Row({ label, value, mono }: { label: string; value: any; mono?: boolean }) {
  return (
    <div>
      <div className="text-xs text-ink-muted">{label}</div>
      <div className={mono ? "font-mono" : ""} dir="ltr">{value}</div>
    </div>
  );
}

"use client";

/**
 * Sprint 38 — Variation orders tab.
 *
 * Shows a list of variation orders for the project. Each card:
 *   - number, date, description, status badge
 *   - additions / deductions / net totals
 *   - line items table (read-only)
 *   - for DRAFT: edit, add item, delete, approve, reject
 *   - for APPROVED/REJECTED: read-only view
 *
 * State flow:
 *   - Top-level: list of variations (fetched once)
 *   - Per card: its own list of items (lazy)
 *   - All mutations call a `reload` callback so the parent can
 *     also refresh the effective contract value.
 *
 * The approve/reject actions are guarded by a confirm() to prevent
 * accidental state transitions. Once approved, a variation's items
 * are folded into the contract's effective value.
 */
import { useEffect, useState, useCallback } from "react";
import {
  Loader2,
  AlertCircle,
  Plus,
  Pencil,
  Trash2,
  CheckCircle2,
  XCircle,
  FilePlus,
  FileText,
  Calendar,
  ChevronDown,
  ChevronUp,
  Plus as PlusIcon,
  Ban,
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate, cn } from "@/lib/utils";
import VariationModal, { type VariationDto } from "./VariationModal";
import LineItemModal, { type LineItemDto } from "./LineItemModal";

interface Props {
  projectId: string;
  contractId: string;
  /** Notify parent when variation list changes (e.g. for the
   *  effective contract value card). */
  onVariationsChange?: (vs: VariationDto[]) => void;
}

const STATUS_META: Record<
  string,
  { label: string; cls: string }
> = {
  DRAFT: {
    label: "مسودة",
    cls: "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300",
  },
  APPROVED: {
    label: "معتمد",
    cls: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  },
  REJECTED: {
    label: "مرفوض",
    cls: "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300",
  },
};

export default function VariationTab({
  projectId,
  contractId,
  onVariationsChange,
}: Props) {
  const { activeCompany } = useAuth();
  const [variations, setVariations] = useState<VariationDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<VariationDto | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!activeCompany) return;
    setLoading(true);
    setError(null);
    try {
      const res = await api.get(`/contracts/${contractId}/variations`);
      const list = res.data || [];
      setVariations(list);
      onVariationsChange?.(list);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [contractId, activeCompany, onVariationsChange]);

  useEffect(() => {
    load();
  }, [load]);

  const suggestedNumber = (() => {
    if (variations.length === 0) return "V-1";
    const nums = variations
      .map((v) => {
        const m = v.variationNumber.match(/(\d+)\s*$/);
        return m ? Number(m[1]) : NaN;
      })
      .filter((n) => !isNaN(n));
    if (nums.length === 0) return "V-1";
    return `V-${Math.max(...nums) + 1}`;
  })();

  const handleSaved = (v: VariationDto) => {
    setVariations((prev) => {
      const exists = prev.find((x) => x.id === v.id);
      const next = exists
        ? prev.map((x) => (x.id === v.id ? v : x))
        : [...prev, v];
      onVariationsChange?.(next);
      return next;
    });
  };

  const handleApprove = async (v: VariationDto) => {
    if (
      !confirm(
        "سيتم اعتماد أمر التغيير وإضافة/خصم بنوده من قيمة العقد الفعّالة. متأكد؟"
      )
    )
      return;
    setBusyId(v.id);
    setError(null);
    try {
      const res = await api.post(`/contracts/${contractId}/variations/${v.id}/approve`);
      const updated = res.data || { ...v, status: "APPROVED" as const };
      setVariations((prev) => {
        const next = prev.map((x) => (x.id === v.id ? updated : x));
        onVariationsChange?.(next);
        return next;
      });
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  };

  const handleReject = async (v: VariationDto) => {
    if (!confirm("سيتم رفض أمر التغيير. متأكد؟")) return;
    setBusyId(v.id);
    setError(null);
    try {
      const res = await api.post(`/contracts/${contractId}/variations/${v.id}/reject`);
      const updated = res.data || { ...v, status: "REJECTED" as const };
      setVariations((prev) => {
        const next = prev.map((x) => (x.id === v.id ? updated : x));
        onVariationsChange?.(next);
        return next;
      });
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  };

  const handleDelete = async (v: VariationDto) => {
    if (v.status !== "DRAFT") {
      alert("لا يمكن حذف أمر تغيير معتمد أو مرفوض");
      return;
    }
    if (
      !confirm(
        "سيتم حذف أمر التغيير وكل بنوده. متأكد؟ (لا يمكن التراجع)"
      )
    )
      return;
    setBusyId(v.id);
    setError(null);
    try {
      await api.delete(`/contracts/${contractId}/variations/${v.id}`);
      setVariations((prev) => {
        const next = prev.filter((x) => x.id !== v.id);
        onVariationsChange?.(next);
        return next;
      });
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div className="space-y-3">
      {error && (
        <div className="p-3 bg-red-50 border border-red-200 rounded-md text-sm text-red-700 flex items-start gap-2">
          <AlertCircle size={16} className="mt-0.5 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      <div className="card">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div>
            <h3 className="font-semibold flex items-center gap-2">
              <FileText size={16} className="text-primary-600" />
              أوامر التغيير
              {variations.length > 0 && (
                <span className="text-xs text-ink-muted font-normal">
                  ({variations.length})
                </span>
              )}
            </h3>
            <p className="text-xs text-ink-muted mt-0.5">
              الإضافات والخصومات على العقد (تؤثر على القيمة الفعّالة بعد الاعتماد)
            </p>
          </div>
          <button
            type="button"
            onClick={() => setCreating(true)}
            className="btn-primary"
          >
            <Plus size={16} />
            <span className="hidden sm:inline">أمر تغيير جديد</span>
          </button>
        </div>
      </div>

      {loading ? (
        <div className="card flex items-center justify-center py-12 text-ink-muted gap-2">
          <Loader2 className="animate-spin" size={20} />
          جاري التحميل...
        </div>
      ) : variations.length === 0 ? (
        <div className="card text-center text-ink-muted py-12 text-sm">
          لا توجد أوامر تغيير بعد.
          <div className="mt-2">
            <button
              type="button"
              onClick={() => setCreating(true)}
              className="text-primary-600 hover:underline"
            >
              أنشئ أول أمر تغيير
            </button>
          </div>
        </div>
      ) : (
        <div className="space-y-3">
          {variations.map((v) => (
            <VariationCard
              key={v.id}
              variation={v}
              contractId={contractId}
              busy={busyId === v.id}
              onEdit={() => setEditing(v)}
              onApprove={() => handleApprove(v)}
              onReject={() => handleReject(v)}
              onDelete={() => handleDelete(v)}
            />
          ))}
        </div>
      )}

      <VariationModal
        open={creating}
        onClose={() => setCreating(false)}
        onSaved={handleSaved}
        contractId={contractId}
        variation={null}
        suggestedNumber={suggestedNumber}
      />
      <VariationModal
        open={!!editing}
        onClose={() => setEditing(null)}
        onSaved={handleSaved}
        contractId={contractId}
        variation={editing}
      />
    </div>
  );
}

// ============================================================
// Variation card (one per variation)
// ============================================================
function VariationCard({
  variation,
  contractId,
  busy,
  onEdit,
  onApprove,
  onReject,
  onDelete,
}: {
  variation: VariationDto;
  contractId: string;
  busy: boolean;
  onEdit: () => void;
  onApprove: () => void;
  onReject: () => void;
  onDelete: () => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const [items, setItems] = useState<LineItemDto[]>([]);
  const [itemsLoading, setItemsLoading] = useState(false);
  const [itemsError, setItemsError] = useState<string | null>(null);
  const [addingItem, setAddingItem] = useState(false);
  const [editingItem, setEditingItem] = useState<LineItemDto | null>(null);

  const status = STATUS_META[variation.status] || {
    label: variation.status,
    cls: "bg-raised text-ink-muted",
  };
  const isDraft = variation.status === "DRAFT";

  const loadItems = async () => {
    setItemsLoading(true);
    setItemsError(null);
    try {
      const res = await api.get(
        `/contracts/${contractId}/variations/${variation.id}/line-items`
      );
      setItems(res.data || []);
    } catch (err) {
      setItemsError(getErrorMessage(err));
    } finally {
      setItemsLoading(false);
    }
  };

  const toggle = () => {
    const next = !expanded;
    setExpanded(next);
    if (next && items.length === 0 && !itemsLoading) {
      loadItems();
    }
  };

  const handleItemSaved = (li: LineItemDto) => {
    setItems((prev) => {
      const exists = prev.find((x) => x.id === li.id);
      return exists
        ? prev.map((x) => (x.id === li.id ? li : x))
        : [...prev, li];
    });
  };

  const handleItemDelete = async (li: LineItemDto) => {
    if (!confirm("سيتم حذف هذا البند. متأكد؟")) return;
    try {
      await api.delete(
        `/contracts/${contractId}/variations/${variation.id}/line-items/${li.id}`
      );
      setItems((prev) => prev.filter((x) => x.id !== li.id));
    } catch (err) {
      setItemsError(getErrorMessage(err));
    }
  };

  return (
    <div className="card">
      <div className="flex items-start justify-between flex-wrap gap-2">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2 flex-wrap">
            <span className="font-mono text-sm text-ink-muted">
              {variation.variationNumber}
            </span>
            <span
              className={cn(
                "inline-flex px-2 py-0.5 rounded text-xs font-medium",
                status.cls
              )}
            >
              {status.label}
            </span>
            <span className="text-xs text-ink-muted flex items-center gap-1">
              <Calendar size={11} />
              {formatDate(variation.variationDate)}
            </span>
          </div>
          <p className="text-sm mt-2">{variation.description}</p>
          {variation.notes && (
            <p className="text-xs text-ink-muted mt-1 whitespace-pre-wrap">
              {variation.notes}
            </p>
          )}
        </div>
        <div className="flex gap-1 shrink-0">
          {isDraft && (
            <>
              <button
                type="button"
                onClick={onEdit}
                className="text-primary-700 hover:bg-primary-50 p-1 rounded"
                title="تعديل"
                aria-label="تعديل أمر التغيير"
              >
                <Pencil size={14} />
              </button>
              <button
                type="button"
                onClick={onApprove}
                disabled={busy}
                className="text-green-600 hover:bg-green-50 p-1 rounded disabled:opacity-50"
                title="اعتماد"
                aria-label="اعتماد أمر التغيير"
              >
                {busy ? <Loader2 className="animate-spin" size={14} /> : <CheckCircle2 size={14} />}
              </button>
              <button
                type="button"
                onClick={onReject}
                disabled={busy}
                className="text-amber-600 hover:bg-amber-50 p-1 rounded disabled:opacity-50"
                title="رفض"
                aria-label="رفض أمر التغيير"
              >
                <XCircle size={14} />
              </button>
              <button
                type="button"
                onClick={onDelete}
                disabled={busy}
                className="text-red-600 hover:bg-red-50 p-1 rounded disabled:opacity-50"
                title="حذف"
                aria-label="حذف أمر التغيير"
              >
                {busy ? <Loader2 className="animate-spin" size={14} /> : <Trash2 size={14} />}
              </button>
            </>
          )}
          <button
            type="button"
            onClick={toggle}
            className="text-ink-subtle hover:text-ink-muted p-1 rounded"
            title={expanded ? "إخفاء البنود" : "عرض البنود"}
            aria-label={expanded ? "إخفاء البنود" : "عرض البنود"}
          >
            {expanded ? <ChevronUp size={16} /> : <ChevronDown size={16} />}
          </button>
        </div>
      </div>

      {/* Totals row */}
      <div className="mt-3 grid grid-cols-3 gap-2 text-sm">
        <div className="px-2 py-1 rounded bg-raised">
          <div className="text-[10px] text-ink-muted">إضافات</div>
          <div className="font-mono text-green-700" dir="ltr">
            {formatNumber(variation.additionsTotal)}
          </div>
        </div>
        <div className="px-2 py-1 rounded bg-raised">
          <div className="text-[10px] text-ink-muted">خصومات</div>
          <div className="font-mono text-red-700" dir="ltr">
            -{formatNumber(variation.deductionsTotal)}
          </div>
        </div>
        <div className="px-2 py-1 rounded bg-raised">
          <div className="text-[10px] text-ink-muted">الصافي</div>
          <div
            className={cn(
              "font-mono font-semibold",
              variation.netAmount >= 0 ? "text-green-700" : "text-red-700"
            )}
            dir="ltr"
          >
            {variation.netAmount >= 0 ? "+" : ""}
            {formatNumber(variation.netAmount)}
          </div>
        </div>
      </div>

      {expanded && (
        <div className="mt-3 border-t border-edge pt-3">
          {itemsError && (
            <div className="p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 mb-2 flex items-start gap-1">
              <AlertCircle size={12} className="mt-0.5 shrink-0" />
              <span>{itemsError}</span>
            </div>
          )}

          {itemsLoading ? (
            <div className="flex items-center justify-center py-4 text-ink-muted gap-2 text-sm">
              <Loader2 className="animate-spin" size={14} />
              جاري تحميل البنود...
            </div>
          ) : items.length === 0 ? (
            <div className="text-sm text-ink-muted text-center py-3">
              لا توجد بنود في هذا الأمر
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-xs">
                <thead>
                  <tr className="bg-raised border-b border-edge">
                    <th className="text-right py-1 px-2 font-semibold text-ink-muted">
                      #
                    </th>
                    <th className="text-right py-1 px-2 font-semibold text-ink-muted">
                      النوع
                    </th>
                    <th className="text-right py-1 px-2 font-semibold text-ink-muted">
                      الوصف
                    </th>
                    <th className="text-left py-1 px-2 font-semibold text-ink-muted">
                      الكمية
                    </th>
                    <th className="text-left py-1 px-2 font-semibold text-ink-muted">
                      سعر الوحدة
                    </th>
                    <th className="text-left py-1 px-2 font-semibold text-ink-muted">
                      الإجمالي
                    </th>
                    {isDraft && (
                      <th className="text-right py-1 px-2 font-semibold text-ink-muted w-20">
                        إجراءات
                      </th>
                    )}
                  </tr>
                </thead>
                <tbody>
                  {items.map((it) => (
                    <tr key={it.id} className="border-b border-edge">
                      <td className="py-1 px-2 font-mono">#{it.lineNumber}</td>
                      <td className="py-1 px-2">
                        <span
                          className={cn(
                            "inline-flex px-1.5 py-0.5 rounded text-[10px] font-medium",
                            it.notes?.includes("خصم") ||
                              (it as any).isAddition === false
                              ? "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300"
                              : "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300"
                          )}
                        >
                          {(it as any).isAddition === false ? "خصم" : "إضافة"}
                        </span>
                      </td>
                      <td className="py-1 px-2 max-w-xs truncate" title={it.description}>
                        {it.description}
                      </td>
                      <td className="py-1 px-2 text-left font-mono" dir="ltr">
                        {formatNumber(it.quantity, 3)}
                      </td>
                      <td className="py-1 px-2 text-left font-mono" dir="ltr">
                        {formatNumber(it.unitPrice, 3)}
                      </td>
                      <td className="py-1 px-2 text-left font-mono font-semibold" dir="ltr">
                        {formatNumber(it.totalPrice)}
                      </td>
                      {isDraft && (
                        <td className="py-1 px-2">
                          <div className="flex items-center gap-0.5">
                            <button
                              type="button"
                              onClick={() => setEditingItem(it)}
                              className="text-primary-700 hover:bg-primary-50 p-0.5 rounded"
                              title="تعديل"
                              aria-label="تعديل البند"
                            >
                              <Pencil size={11} />
                            </button>
                            <button
                              type="button"
                              onClick={() => handleItemDelete(it)}
                              className="text-red-600 hover:bg-red-50 p-0.5 rounded"
                              title="حذف"
                              aria-label="حذف البند"
                            >
                              <Trash2 size={11} />
                            </button>
                          </div>
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {isDraft && (
            <div className="mt-2">
              <button
                type="button"
                onClick={() => setAddingItem(true)}
                className="btn-secondary text-xs"
              >
                <PlusIcon size={12} />
                إضافة بند
              </button>
            </div>
          )}
        </div>
      )}

      <LineItemModal
        open={addingItem}
        onClose={() => setAddingItem(false)}
        onSaved={(li) => {
          handleItemSaved(li);
          setAddingItem(false);
        }}
        contractId={contractId}
        lineItem={null}
        showAdditionToggle
      />
      <LineItemModal
        open={!!editingItem}
        onClose={() => setEditingItem(null)}
        onSaved={(li) => {
          handleItemSaved(li);
          setEditingItem(null);
        }}
        contractId={contractId}
        lineItem={editingItem}
        showAdditionToggle
      />
    </div>
  );
}

"use client";

/**
 * Sprint 38 — Add/edit contract line item modal.
 *
 * Used for both:
 *   - creating a single BOQ line item from the BOQ tab
 *   - creating a variation line item from the Variations tab
 *   (with `isAddition` flag determining whether it adds to or
 *   subtracts from the contract — only for variation items)
 *
 * The backend shape we POST:
 *   POST /api/contracts/{contractId}/line-items
 *   body: { lineNumber?, description, unit, customUnit?, quantity,
 *           unitPrice, notes? }
 *
 * `lineNumber` is auto-assigned server-side when omitted (we leave
 * the field read-only as a display anyway).
 *
 * `unit` is one of a fixed set of measurement units. The dropdown
 * includes "أخرى" (other) which reveals a free-text `customUnit`
 * field — same pattern as the i18n "Custom" option.
 *
 * Numeric fields use local string state (so empty stays empty, not
 * "0" or NaN) and we parse on submit. Same convention as
 * ContractModal and BillingModal.
 */
import { useEffect, useState } from "react";
import { X, Loader2, AlertCircle, CheckCircle2, Ruler } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";

export const UNIT_OPTIONS = [
  { value: "m3", label: "متر مكعب (m³)" },
  { value: "m2", label: "متر مربع (m²)" },
  { value: "m", label: "متر طولي (m)" },
  { value: "ton", label: "طن" },
  { value: "kg", label: "كيلوجرام" },
  { value: "piece", label: "قطعة" },
  { value: "lump", label: "مقطوعية (lump)" },
  { value: "hour", label: "ساعة" },
  { value: "day", label: "يوم" },
  { value: "other", label: "أخرى" },
] as const;

export type UnitValue = (typeof UNIT_OPTIONS)[number]["value"];

export interface LineItemDto {
  id: string;
  contractId: string;
  variationId?: string | null;
  lineNumber: number;
  description: string;
  unit: string;
  customUnit?: string | null;
  quantity: number;
  unitPrice: number;
  totalPrice: number;
  billedQuantity: number;
  remainingQuantity: number;
  amountBilled: number;
  notes?: string | null;
  createdAt?: string;
  updatedAt?: string;
}

interface Props {
  open: boolean;
  onClose: () => void;
  onSaved: (li: LineItemDto) => void;
  contractId: string;
  /** Existing line item for edit mode. null/undefined for create. */
  lineItem?: LineItemDto | null;
  /** Optional next line_number to display (for create only). */
  nextLineNumber?: number;
  /** Show "isAddition" checkbox (variation items). Default false. */
  showAdditionToggle?: boolean;
}

export default function LineItemModal({
  open,
  onClose,
  onSaved,
  contractId,
  lineItem,
  nextLineNumber,
  showAdditionToggle = false,
}: Props) {
  const isEdit = !!lineItem;

  const [lineNumberDisplay, setLineNumberDisplay] = useState<string>("");
  const [description, setDescription] = useState("");
  const [unit, setUnit] = useState<UnitValue>("m3");
  const [customUnit, setCustomUnit] = useState("");
  const [quantity, setQuantity] = useState<string>("");
  const [unitPrice, setUnitPrice] = useState<string>("");
  const [notes, setNotes] = useState("");
  const [isAddition, setIsAddition] = useState(true);

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // Reset form when the modal opens.
  useEffect(() => {
    if (!open) return;
    if (lineItem) {
      setLineNumberDisplay(String(lineItem.lineNumber));
      setDescription(lineItem.description);
      const u = (lineItem.unit || "m3") as UnitValue;
      // If the unit isn't in the list, treat it as "other" and
      // surface the original value in customUnit.
      const known = UNIT_OPTIONS.find((o) => o.value === u);
      if (known) {
        setUnit(u);
        setCustomUnit("");
      } else {
        setUnit("other");
        setCustomUnit(lineItem.unit);
      }
      setQuantity(String(lineItem.quantity));
      setUnitPrice(String(lineItem.unitPrice));
      setNotes(lineItem.notes || "");
      setIsAddition(true);
    } else {
      setLineNumberDisplay(
        nextLineNumber != null ? String(nextLineNumber) : "—"
      );
      setDescription("");
      setUnit("m3");
      setCustomUnit("");
      setQuantity("");
      setUnitPrice("");
      setNotes("");
      setIsAddition(true);
    }
    setError(null);
    setSuccess(null);
  }, [open, lineItem, nextLineNumber]);

  if (!open) return null;

  const qty = Number(quantity) || 0;
  const price = Number(unitPrice) || 0;
  const total = qty * price;
  const showCustomUnit = unit === "other";

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSuccess(null);

    if (!description.trim()) {
      setError("الوصف مطلوب");
      return;
    }
    if (showCustomUnit && !customUnit.trim()) {
      setError("وحدة القياس المخصصة مطلوبة");
      return;
    }
    if (!quantity || isNaN(qty) || qty <= 0) {
      setError("الكمية مطلوبة ويجب أن تكون أكبر من صفر");
      return;
    }
    if (unitPrice === "" || isNaN(price) || price < 0) {
      setError("سعر الوحدة مطلوب ويجب ألا يكون سالباً");
      return;
    }

    // Resolve the unit value: for "other", send the customUnit text
    // (the backend should fall back to using it).
    const unitValue = showCustomUnit ? customUnit.trim() : unit;

    const body: Record<string, unknown> = {
      description: description.trim(),
      unit: unitValue,
      customUnit: showCustomUnit ? customUnit.trim() : null,
      quantity: qty,
      unitPrice: price,
      notes: notes.trim() || null,
      isAddition: showAdditionToggle ? isAddition : undefined,
    };

    setSaving(true);
    try {
      const res = isEdit
        ? await api.put(`/contracts/${contractId}/line-items/${lineItem!.id}`, body)
        : await api.post(`/contracts/${contractId}/line-items`, body);
      setSuccess(isEdit ? "تم تحديث البند" : "تم إضافة البند");
      onSaved(res.data);
      setTimeout(() => onClose(), 600);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-2 sm:p-4">
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold flex items-center gap-2">
            <Ruler size={18} className="text-primary-600" />
            {isEdit ? "تعديل بند" : "بند جديد"}
          </h2>
          <button
            onClick={onClose}
            className="text-ink-subtle hover:text-ink-muted"
            type="button"
            aria-label="إغلاق"
          >
            <X size={20} />
          </button>
        </div>

        <form onSubmit={submit} className="space-y-3">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">رقم البند</label>
              <input
                className="input bg-raised cursor-not-allowed"
                value={lineNumberDisplay}
                readOnly
                dir="ltr"
                title="يُحسب تلقائياً"
              />
              {!isEdit && (
                <p className="text-xs text-ink-muted mt-1">يُحسب تلقائياً</p>
              )}
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">الوحدة *</label>
              <select
                className="input"
                value={unit}
                onChange={(e) => setUnit(e.target.value as UnitValue)}
              >
                {UNIT_OPTIONS.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {showCustomUnit && (
            <div>
              <label className="block text-sm font-medium mb-1">
                وحدة مخصصة *
              </label>
              <input
                className="input"
                value={customUnit}
                onChange={(e) => setCustomUnit(e.target.value)}
                placeholder="اكتب الوحدة..."
                dir="ltr"
              />
            </div>
          )}

          <div>
            <label className="block text-sm font-medium mb-1">الوصف *</label>
            <input
              className="input"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="مثال: حفر أساسات"
              required
            />
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">الكمية *</label>
              <input
                type="number"
                step="0.001"
                min="0"
                className="input"
                value={quantity}
                onChange={(e) => setQuantity(e.target.value)}
                required
                dir="ltr"
                placeholder="1000"
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">سعر الوحدة *</label>
              <input
                type="number"
                step="0.001"
                min="0"
                className="input"
                value={unitPrice}
                onChange={(e) => setUnitPrice(e.target.value)}
                required
                dir="ltr"
                placeholder="5.000"
              />
            </div>
          </div>

          <div className="rounded-md border border-edge bg-raised p-3 text-sm">
            <div className="flex items-center justify-between">
              <span className="text-ink-muted">الإجمالي (محسوب)</span>
              <span className="font-mono font-semibold" dir="ltr">
                {total > 0 ? total.toLocaleString("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 }) : "0.00"} د.ل
              </span>
            </div>
          </div>

          {showAdditionToggle && (
            <label className="flex items-center gap-2 text-sm cursor-pointer">
              <input
                type="checkbox"
                checked={isAddition}
                onChange={(e) => setIsAddition(e.target.checked)}
                className="w-4 h-4 rounded border-edge accent-primary-700"
              />
              <span>
                بند إضافي (يضاف إلى قيمة العقد) —{" "}
                <span className="text-ink-muted">
                  {isAddition ? "إضافة" : "خصم"}
                </span>
              </span>
            </label>
          )}

          <div>
            <label className="block text-sm font-medium mb-1">ملاحظات</label>
            <textarea
              className="input"
              rows={2}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              placeholder="أي ملاحظات..."
            />
          </div>

          {error && (
            <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm flex items-start gap-2">
              <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
              <span>{error}</span>
            </div>
          )}
          {success && (
            <div className="p-3 bg-green-50 text-green-700 rounded-md text-sm flex items-center gap-2">
              <CheckCircle2 size={16} />
              <span>{success}</span>
            </div>
          )}

          <div className="flex gap-2 pt-2">
            <button type="submit" disabled={saving} className="btn-primary flex-1">
              {saving ? (
                <>
                  <Loader2 className="animate-spin" size={16} /> جاري الحفظ...
                </>
              ) : isEdit ? (
                "حفظ التعديلات"
              ) : (
                "إضافة البند"
              )}
            </button>
            <button type="button" onClick={onClose} className="btn-secondary">
              إلغاء
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

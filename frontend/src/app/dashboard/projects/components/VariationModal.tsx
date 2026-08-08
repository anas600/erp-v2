"use client";

/**
 * Sprint 38 — Create/edit variation order modal.
 *
 * A variation order is a *change* to a contract (additions or
 * removals of scope). The modal captures only the header here
 * (description, date, notes). Items are added separately from
 * the VariationTab's per-card "items" panel — that flow is too
 * dynamic for a single modal.
 *
 * Two reasons we don't put items inside this modal:
 *   1. Items have their own modal (LineItemModal) so the same
 *      "add line item" UI is reused between BOQ and variations.
 *   2. The user typically iterates — add variation, add a few
 *      items, come back, add more. A modal that locks the page
 *      would be friction. The per-card items panel is better.
 */
import { useEffect, useState } from "react";
import { X, Loader2, AlertCircle, CheckCircle2, FilePlus } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";

export interface VariationDto {
  id: string;
  contractId: string;
  variationNumber: string;
  variationDate: string;
  description: string;
  notes?: string | null;
  status: "DRAFT" | "APPROVED" | "REJECTED";
  /** Sum of items where is_addition = true (in LYD). */
  additionsTotal: number;
  /** Sum of items where is_addition = false (in LYD, positive). */
  deductionsTotal: number;
  /** Net = additions - deductions. */
  netAmount: number;
  createdAt?: string;
  updatedAt?: string;
}

interface Props {
  open: boolean;
  onClose: () => void;
  onSaved: (v: VariationDto) => void;
  contractId: string;
  /** Existing variation to edit. null/undefined for create. */
  variation?: VariationDto | null;
  /** Optional suggested variation_number (e.g. "V-2"). */
  suggestedNumber?: string;
}

export default function VariationModal({
  open,
  onClose,
  onSaved,
  contractId,
  variation,
  suggestedNumber,
}: Props) {
  const isEdit = !!variation;
  const [variationNumber, setVariationNumber] = useState("");
  const [variationDate, setVariationDate] = useState(
    new Date().toISOString().slice(0, 10)
  );
  const [description, setDescription] = useState("");
  const [notes, setNotes] = useState("");

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    if (variation) {
      setVariationNumber(variation.variationNumber);
      setVariationDate((variation.variationDate || "").slice(0, 10));
      setDescription(variation.description);
      setNotes(variation.notes || "");
    } else {
      setVariationNumber(suggestedNumber || "");
      setVariationDate(new Date().toISOString().slice(0, 10));
      setDescription("");
      setNotes("");
    }
    setError(null);
    setSuccess(null);
  }, [open, variation, suggestedNumber]);

  if (!open) return null;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSuccess(null);

    if (!variationNumber.trim()) {
      setError("رقم أمر التغيير مطلوب");
      return;
    }
    if (!description.trim()) {
      setError("الوصف مطلوب");
      return;
    }
    if (!variationDate) {
      setError("التاريخ مطلوب");
      return;
    }

    const body = {
      variationNumber: variationNumber.trim(),
      variationDate,
      description: description.trim(),
      notes: notes.trim() || null,
    };

    setSaving(true);
    try {
      const res = isEdit
        ? await api.put(`/contracts/${contractId}/variations/${variation!.id}`, body)
        : await api.post(`/contracts/${contractId}/variations`, body);
      setSuccess(isEdit ? "تم تحديث أمر التغيير" : "تم إنشاء أمر التغيير");
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
            <FilePlus size={18} className="text-primary-600" />
            {isEdit ? "تعديل أمر تغيير" : "أمر تغيير جديد"}
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
              <label className="block text-sm font-medium mb-1">
                رقم أمر التغيير *
              </label>
              <input
                className="input font-mono"
                value={variationNumber}
                onChange={(e) => setVariationNumber(e.target.value)}
                placeholder="V-1"
                dir="ltr"
                required
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">التاريخ *</label>
              <input
                type="date"
                className="input"
                value={variationDate}
                onChange={(e) => setVariationDate(e.target.value)}
                required
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">الوصف *</label>
            <textarea
              className="input"
              rows={3}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="وصف أمر التغيير (نطاق العمل الجديد أو الملغي)..."
              required
            />
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">ملاحظات</label>
            <textarea
              className="input"
              rows={2}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              placeholder="ملاحظات إضافية (اختياري)..."
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
                "إنشاء أمر التغيير"
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

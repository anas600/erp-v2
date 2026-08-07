"use client";

/**
 * Sprint 36 — Create/Edit contract modal.
 *
 * Single modal used both for create (when no contract exists) and
 * edit (when one does). The contract_number field is optional for
 * public-sector projects (which often don't have a number until
 * the budget is approved).
 *
 * Numeric fields (contract_value, advance_percent, retention_percent,
 * retention_start_billing) use local string state and we parse to
 * Number on submit. This keeps the empty/zero UX clean (empty
 * input -> 0 in payload, not NaN).
 *
 * Validation:
 *   - contractValue > 0 (required)
 *   - advance_percent + retention_percent should not exceed 100
 *     (warn but don't block; e.g. you might intentionally have
 *     30% advance + 10% retention and that means 140% is taken
 *     from gross, leaving -40% which is also valid in some
 *     contracts. Backend can decide.)
 */
import { useEffect, useState } from "react";
import { X, Loader2, AlertCircle, CheckCircle2 } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";

export interface ContractDto {
  id: string;
  companyId: string;
  projectId: string;
  contractNumber?: string | null;
  contractValue: number;
  advancePercent: number;
  retentionPercent: number;
  retentionStartBilling: number;
  startDate?: string | null;
  endDate?: string | null;
  notes?: string | null;
  createdAt?: string;
  updatedAt?: string;
}

interface Props {
  open: boolean;
  onClose: () => void;
  onSaved: (c: ContractDto) => void;
  projectId: string;
  /** Existing contract to edit. Null/undefined for create mode. */
  contract: ContractDto | null;
}

export default function ContractModal({ open, onClose, onSaved, projectId, contract }: Props) {
  const isEdit = !!contract;
  const [contractNumber, setContractNumber] = useState("");
  const [contractValue, setContractValue] = useState<string>("");
  const [advancePercent, setAdvancePercent] = useState<string>("0");
  const [retentionPercent, setRetentionPercent] = useState<string>("0");
  const [retentionStartBilling, setRetentionStartBilling] = useState<string>("1");
  const [startDate, setStartDate] = useState("");
  const [endDate, setEndDate] = useState("");
  const [notes, setNotes] = useState("");

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // Reset form whenever the modal opens (so the same modal can be
  // used for create and edit without leaking state between opens).
  useEffect(() => {
    if (!open) return;
    setContractNumber(contract?.contractNumber || "");
    setContractValue(contract ? String(contract.contractValue) : "");
    setAdvancePercent(contract ? String(contract.advancePercent) : "0");
    setRetentionPercent(contract ? String(contract.retentionPercent) : "0");
    setRetentionStartBilling(contract ? String(contract.retentionStartBilling) : "1");
    setStartDate((contract?.startDate || "").slice(0, 10));
    setEndDate((contract?.endDate || "").slice(0, 10));
    setNotes(contract?.notes || "");
    setError(null);
    setSuccess(null);
  }, [open, contract]);

  if (!open) return null;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSuccess(null);

    const value = Number(contractValue);
    if (!contractValue || isNaN(value) || value <= 0) {
      setError("قيمة العقد مطلوبة ويجب أن تكون أكبر من صفر");
      return;
    }

    const body = {
      contractNumber: contractNumber.trim() || null,
      contractValue: value,
      advancePercent: Number(advancePercent) || 0,
      retentionPercent: Number(retentionPercent) || 0,
      retentionStartBilling: Math.max(1, Math.floor(Number(retentionStartBilling) || 1)),
      startDate: startDate || null,
      endDate: endDate || null,
      notes: notes.trim() || null,
    };

    setSaving(true);
    try {
      const res = isEdit
        ? await api.put(`/contracts/${contract!.id}`, body)
        : await api.post(`/projects/${projectId}/contract`, body);
      setSuccess(isEdit ? "تم تحديث العقد" : "تم إنشاء العقد");
      onSaved(res.data);
      setTimeout(() => onClose(), 700);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-2 sm:p-4">
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-2xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold">
            {isEdit ? "تعديل العقد" : "إنشاء عقد"}
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
                رقم العقد
                <span className="text-xs text-ink-muted mr-1">(اختياري - للقطاع الحكومي)</span>
              </label>
              <input
                className="input"
                value={contractNumber}
                onChange={(e) => setContractNumber(e.target.value)}
                placeholder="مثال: 2026/123"
                dir="ltr"
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">قيمة العقد *</label>
              <input
                type="number"
                step="0.001"
                min="0"
                className="input"
                value={contractValue}
                onChange={(e) => setContractValue(e.target.value)}
                required
                dir="ltr"
                placeholder="500000"
              />
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">نسبة المقدمة %</label>
              <input
                type="number"
                step="0.01"
                min="0"
                max="100"
                className="input"
                value={advancePercent}
                onChange={(e) => setAdvancePercent(e.target.value)}
                dir="ltr"
              />
              <p className="text-xs text-ink-muted mt-1">تُخصم من أول مستخلصات</p>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">نسبة الاحتجاز %</label>
              <input
                type="number"
                step="0.01"
                min="0"
                max="100"
                className="input"
                value={retentionPercent}
                onChange={(e) => setRetentionPercent(e.target.value)}
                dir="ltr"
              />
              <p className="text-xs text-ink-muted mt-1">تُحتجز من المستخلص</p>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">الاحتجاز من مستخلص رقم</label>
              <input
                type="number"
                step="1"
                min="1"
                className="input"
                value={retentionStartBilling}
                onChange={(e) => setRetentionStartBilling(e.target.value)}
                dir="ltr"
              />
              <p className="text-xs text-ink-muted mt-1">يبدأ الاحتجاز من رقم</p>
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">تاريخ بداية العقد</label>
              <input
                type="date"
                className="input"
                value={startDate}
                onChange={(e) => setStartDate(e.target.value)}
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">تاريخ نهاية العقد</label>
              <input
                type="date"
                className="input"
                value={endDate}
                onChange={(e) => setEndDate(e.target.value)}
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">ملاحظات</label>
            <textarea
              className="input"
              rows={3}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              placeholder="أي شروط أو ملاحظات خاصة بالعقد..."
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
                "إنشاء العقد"
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

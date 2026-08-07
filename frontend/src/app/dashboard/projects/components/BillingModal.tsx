"use client";

/**
 * Sprint 36 — Create progress billing modal.
 *
 * Live preview: as the user types work_completed_percent, we
 * recompute gross / advance_deducted / retention_deducted /
 * net_amount on the client. This is a *preview only* — the
 * authoritative numbers come from BillingService.CreateAsync on
 * the backend (it has the full picture: contract value, previous
 * billings, retention start, etc.).
 *
 * Why client-side math?
 *   The backend endpoint to create a billing validates the input
 *   and returns the computed amounts. For the user to see numbers
 *   *before* they submit, we have to do the math somewhere.
 *   Round-tripping on every keystroke would be noisy. The
 *   backend calculation matches the formula in
 *   backend/Features/Projects/BillingService.cs — see the
 *   "Killer test" in the plan for the worked example.
 */
import { useEffect, useMemo, useState } from "react";
import { X, Loader2, AlertCircle, CheckCircle2, Info } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatNumber } from "@/lib/utils";
import type { ContractDto } from "./ContractModal";
import type { ProgressBillingDto } from "./BillingsTab";

interface Props {
  open: boolean;
  onClose: () => void;
  onCreated: (b: ProgressBillingDto) => void;
  projectId: string;
  contract: ContractDto;
  /** All existing billings (used to compute cumulative advance). */
  existingBillings: ProgressBillingDto[];
}

export default function BillingModal({
  open,
  onClose,
  onCreated,
  projectId,
  contract,
  existingBillings,
}: Props) {
  const [billingNumber, setBillingNumber] = useState("");
  const [billingDate, setBillingDate] = useState(
    new Date().toISOString().slice(0, 10)
  );
  const [periodFrom, setPeriodFrom] = useState("");
  const [periodTo, setPeriodTo] = useState("");
  const [workCompletedPercent, setWorkCompletedPercent] = useState<string>("");
  const [notes, setNotes] = useState("");

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // Reset on open
  useEffect(() => {
    if (!open) return;
    setBillingNumber("");
    setBillingDate(new Date().toISOString().slice(0, 10));
    setPeriodFrom("");
    setPeriodTo("");
    setWorkCompletedPercent("");
    setNotes("");
    setError(null);
    setSuccess(null);
  }, [open]);

  // Determine the next billing number (highest previous + 1) for
  // the user's convenience. They can still override it.
  const suggestedNextNumber = useMemo(() => {
    if (existingBillings.length === 0) return "1";
    const nums = existingBillings
      .map((b) => parseInt(b.billingNumber, 10))
      .filter((n) => !isNaN(n));
    if (nums.length === 0) return "1";
    return String(Math.max(...nums) + 1);
  }, [existingBillings]);

  // The previous billings — non-cancelled. We use the formula
  // described in the backend's BillingService.CreateAsync.
  const previousBillings = useMemo(
    () => existingBillings.filter((b) => b.status !== "CANCELLED"),
    [existingBillings]
  );
  const previousMaxPercent = useMemo(() => {
    if (previousBillings.length === 0) return 0;
    return Math.max(...previousBillings.map((b) => Number(b.workCompletedPercent) || 0));
  }, [previousBillings]);

  // Compute the live preview numbers. Same formula as the
  // backend's BillingService.CreateAsync.
  const preview = useMemo(() => {
    const pct = Number(workCompletedPercent) || 0;
    const cv = Number(contract.contractValue) || 0;
    const advancePct = Number(contract.advancePercent) || 0;
    const retentionPct = Number(contract.retentionPercent) || 0;
    const retentionStart = Math.max(1, Number(contract.retentionStartBilling) || 1);

    // The new billing's ordinal (within non-cancelled billings) is
    // (previousBillings.length + 1). The retention check uses
    // this ordinal, not the billing_number itself.
    const newOrdinal = previousBillings.length + 1;

    const gross = cv * (pct / 100);
    const advanceTotal = cv * (advancePct / 100);
    const previousAdvanceSum = previousBillings.reduce(
      (s, b) => s + (Number(b.advanceDeducted) || 0),
      0
    );
    const remainingAdvance = Math.max(0, advanceTotal - previousAdvanceSum);
    const advanceDeducted = Math.min(gross, remainingAdvance);
    const retentionDeducted =
      newOrdinal >= retentionStart ? gross * (retentionPct / 100) : 0;
    const net = gross - advanceDeducted - retentionDeducted;
    return { gross, advanceDeducted, retentionDeducted, net, newOrdinal, advanceTotal };
  }, [workCompletedPercent, contract, previousBillings]);

  if (!open) return null;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSuccess(null);

    if (!billingNumber.trim()) {
      setError("رقم المستخلص مطلوب");
      return;
    }
    if (!billingDate) {
      setError("تاريخ المستخلص مطلوب");
      return;
    }
    const pct = Number(workCompletedPercent);
    if (!workCompletedPercent || isNaN(pct) || pct <= 0 || pct > 100) {
      setError("نسبة الإنجاز يجب أن تكون بين 0 و 100");
      return;
    }
    if (pct < previousMaxPercent) {
      setError(
        `نسبة الإنجاز (${pct}%) أقل من المستخلص السابق (${previousMaxPercent}%)`
      );
      return;
    }

    setSaving(true);
    try {
      const res = await api.post(`/projects/${projectId}/billings`, {
        contractId: contract.id,
        billingNumber: billingNumber.trim(),
        billingDate,
        periodFrom: periodFrom || null,
        periodTo: periodTo || null,
        workCompletedPercent: pct,
        notes: notes.trim() || null,
      });
      setSuccess("تم إنشاء المستخلص");
      onCreated(res.data);
      setTimeout(() => onClose(), 700);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-2 sm:p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-2xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold">مستخلص جديد</h2>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600"
            type="button"
            aria-label="إغلاق"
          >
            <X size={20} />
          </button>
        </div>

        <form onSubmit={submit} className="space-y-3">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">رقم المستخلص *</label>
              <input
                className="input font-mono"
                value={billingNumber}
                onChange={(e) => setBillingNumber(e.target.value)}
                required
                placeholder={suggestedNextNumber}
                dir="ltr"
              />
              <p className="text-xs text-gray-500 mt-1">
                المقترح: <span className="font-mono">{suggestedNextNumber}</span>
              </p>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">تاريخ المستخلص *</label>
              <input
                type="date"
                className="input"
                value={billingDate}
                onChange={(e) => setBillingDate(e.target.value)}
                required
              />
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">الفترة من</label>
              <input
                type="date"
                className="input"
                value={periodFrom}
                onChange={(e) => setPeriodFrom(e.target.value)}
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">الفترة إلى</label>
              <input
                type="date"
                className="input"
                value={periodTo}
                onChange={(e) => setPeriodTo(e.target.value)}
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">
              نسبة الإنجاز التراكمية % *
            </label>
            <input
              type="number"
              step="0.01"
              min="0"
              max="100"
              className="input"
              value={workCompletedPercent}
              onChange={(e) => setWorkCompletedPercent(e.target.value)}
              required
              dir="ltr"
              placeholder="30"
            />
            {previousMaxPercent > 0 && (
              <p className="text-xs text-gray-500 mt-1">
                أعلى نسبة سابقة: <span className="font-mono">{previousMaxPercent}%</span>
                {" "}(يجب أن تكون أكبر أو تساويها)
              </p>
            )}
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">ملاحظات</label>
            <textarea
              className="input"
              rows={2}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
            />
          </div>

          {/* Live preview of computed amounts */}
          <div className="border border-blue-200 bg-blue-50 rounded-md p-3">
            <div className="flex items-center gap-1 text-sm font-semibold text-blue-800 mb-2">
              <Info size={14} />
              معاينة المبالغ (تحسب تلقائياً)
            </div>
            <div className="grid grid-cols-2 gap-2 text-sm">
              <PreviewRow
                label="إجمالي المستخلص (Gross)"
                value={preview.gross}
                mono
              />
              <PreviewRow
                label="إجمالي المقدمة"
                value={preview.advanceTotal}
                muted
                mono
              />
              <PreviewRow
                label="متبقي المقدمة"
                value={Math.max(0, preview.advanceTotal - previousBillings.reduce((s, b) => s + (Number(b.advanceDeducted) || 0), 0))}
                muted
                mono
              />
              <PreviewRow
                label="خصم المقدمة"
                value={preview.advanceDeducted}
                highlight
                mono
              />
              <PreviewRow
                label={`احتجاز (يبدأ من #${Math.max(1, Number(contract.retentionStartBilling) || 1)} — هذا رقم #${preview.newOrdinal})`}
                value={preview.retentionDeducted}
                highlight
                mono
              />
              <PreviewRow
                label="الصافي (Net)"
                value={preview.net}
                highlight
                strong
                mono
              />
            </div>
            <p className="text-xs text-blue-700 mt-2">
              الأرقام النهائية تُحسب في الخادم. هذه معاينة فقط.
            </p>
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
              ) : (
                "إنشاء المستخلص"
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

function PreviewRow({
  label,
  value,
  mono,
  highlight,
  strong,
  muted,
}: {
  label: string;
  value: number;
  mono?: boolean;
  highlight?: boolean;
  strong?: boolean;
  muted?: boolean;
}) {
  return (
    <div
      className={`flex items-center justify-between gap-2 px-2 py-1 rounded ${
        highlight ? "bg-white border border-blue-100" : ""
      }`}
    >
      <span className={`text-xs ${muted ? "text-gray-500" : "text-gray-700"}`}>
        {label}
      </span>
      <span
        dir="ltr"
        className={`${mono ? "font-mono" : ""} ${
          strong ? "font-bold text-blue-900" : ""
        } ${muted ? "text-gray-500" : ""}`}
      >
        {formatNumber(value)} د.ل
      </span>
    </div>
  );
}

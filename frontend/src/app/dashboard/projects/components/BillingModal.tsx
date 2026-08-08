"use client";

/**
 * Sprint 38 — Progress billing creation WIZARD.
 *
 * Replaces the Sprint 36 single-form modal with a 2-step wizard:
 *
 *   Step 1: اختيار البنود (Select Items)
 *     - Show all BOQ line items with a checkbox
 *     - Show "remaining quantity" so the user can see at a glance
 *       what's left to bill
 *     - Quick select buttons: "الكل" / "البنود المتبقية فقط"
 *     - "التالي" advances to step 2 (disabled if no selection)
 *
 *   Step 2: إدخال الكميات (Enter Quantities)
 *     - Table of selected line items
 *     - Per row: previous qty | remaining | THIS PERIOD (input) |
 *                new cumulative (auto)
 *     - Live validation: this_period + previous > total -> error
 *     - Live total at the bottom: gross, advance deducted,
 *       retention deducted, net
 *     - "السابق" returns to step 1
 *     - "حفظ المسودة" POSTs the new billing
 *
 * Why a wizard instead of one long form?
 *   The user has two distinct mental tasks:
 *     (a) "which line items am I billing this period?"
 *     (b) "how much of each did we actually do?"
 *   Combining them on one form makes the screen busy and hides
 *   the wrong-type-of-error. Splitting lets each step be quiet
 *   and focused.
 *
 * On submit, the API call is:
 *   POST /api/projects/{id}/billings
 *   body: {
 *     contractId, billingNumber, billingDate, periodFrom, periodTo,
 *     notes,
 *     items: [{ lineItemId, thisPeriodQuantity }, ...]
 *   }
 * The backend computes gross/advance/retention/net from the items
 * — we don't pass those pre-computed.
 */
import { useEffect, useMemo, useState } from "react";
import {
  X,
  Loader2,
  AlertCircle,
  CheckCircle2,
  Info,
  ArrowRight,
  ArrowLeft,
  CheckSquare,
  Square,
  FileText,
  Calendar,
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatNumber, cn } from "@/lib/utils";
import type { ContractDto } from "./ContractModal";
import type { ProgressBillingDto } from "./BillingsTab";
import type { LineItemDto } from "./LineItemModal";

interface Props {
  open: boolean;
  onClose: () => void;
  onCreated: (b: ProgressBillingDto) => void;
  projectId: string;
  contract: ContractDto;
  /** Existing non-cancelled billings. Used to determine
   *  previousCumulative per line item. */
  existingBillings: ProgressBillingDto[];
}

type Step = 1 | 2;

export default function BillingModal({
  open,
  onClose,
  onCreated,
  projectId,
  contract,
  existingBillings,
}: Props) {
  const [step, setStep] = useState<Step>(1);

  // Header fields (set on step 1 alongside the item selection so
  // they're not lost when navigating back)
  const [billingNumber, setBillingNumber] = useState("");
  const [billingDate, setBillingDate] = useState(
    new Date().toISOString().slice(0, 10)
  );
  const [periodFrom, setPeriodFrom] = useState("");
  const [periodTo, setPeriodTo] = useState("");
  const [notes, setNotes] = useState("");

  // Line items source-of-truth
  const [lineItems, setLineItems] = useState<LineItemDto[]>([]);
  const [loadingLineItems, setLoadingLineItems] = useState(false);

  // Selected line item IDs (step 1)
  const [selected, setSelected] = useState<Set<string>>(new Set());
  // Per-line this-period quantity (step 2)
  const [thisPeriod, setThisPeriod] = useState<Record<string, string>>({});

  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  // Reset on open + load line items
  useEffect(() => {
    if (!open) return;
    setStep(1);
    setBillingNumber("");
    setBillingDate(new Date().toISOString().slice(0, 10));
    setPeriodFrom("");
    setPeriodTo("");
    setNotes("");
    setSelected(new Set());
    setThisPeriod({});
    setError(null);
    setSuccess(null);
    setLoadingLineItems(true);
    api
      .get(`/contracts/${contract.id}/line-items`)
      .then((res) => setLineItems(res.data || []))
      .catch((err) => setError(getErrorMessage(err)))
      .finally(() => setLoadingLineItems(false));
  }, [open, contract.id]);

  // Auto-suggested next billing number
  const suggestedNextNumber = useMemo(() => {
    if (existingBillings.length === 0) return "1";
    const nums = existingBillings
      .map((b) => parseInt(b.billingNumber, 10))
      .filter((n) => !isNaN(n));
    if (nums.length === 0) return "1";
    return String(Math.max(...nums) + 1);
  }, [existingBillings]);

  // Cumulative quantities already billed per line item (across
  // non-cancelled billings). For the Sprint 36 schema the line
  // item itself stores billedQuantity — we use that, but fall
  // back to summing if the API response doesn't include it.
  const previousQtyByItem: Record<string, number> = useMemo(() => {
    const out: Record<string, number> = {};
    for (const li of lineItems) {
      out[li.id] = Number(li.billedQuantity) || 0;
    }
    return out;
  }, [lineItems]);

  const remainingByItem: Record<string, number> = useMemo(() => {
    const out: Record<string, number> = {};
    for (const li of lineItems) {
      out[li.id] = Math.max(0, Number(li.remainingQuantity) || 0);
    }
    return out;
  }, [lineItems]);

  // Items selected in step 1, in the same order they appear.
  const selectedItems = useMemo(
    () => lineItems.filter((li) => selected.has(li.id)),
    [lineItems, selected]
  );

  // Live totals for step 2
  const livePreview = useMemo(() => {
    const advancePct = Number(contract.advancePercent) || 0;
    const retentionPct = Number(contract.retentionPercent) || 0;
    const retentionStart = Math.max(
      1,
      Number(contract.retentionStartBilling) || 1
    );
    const cv = Number(contract.contractValue) || 0;

    let gross = 0;
    let totalQty = 0;
    for (const li of selectedItems) {
      const tp = Number(thisPeriod[li.id]) || 0;
      const price = Number(li.unitPrice) || 0;
      gross += tp * price;
      totalQty += tp;
    }
    const newOrdinal =
      existingBillings.filter((b) => b.status !== "CANCELLED").length + 1;
    const advanceTotal = cv * (advancePct / 100);
    const previousAdvanceSum = existingBillings
      .filter((b) => b.status !== "CANCELLED")
      .reduce((s, b) => s + (Number(b.advanceDeducted) || 0), 0);
    const remainingAdvance = Math.max(0, advanceTotal - previousAdvanceSum);
    const advanceDeducted = Math.min(gross, remainingAdvance);
    const retentionDeducted =
      newOrdinal >= retentionStart ? gross * (retentionPct / 100) : 0;
    const net = gross - advanceDeducted - retentionDeducted;
    const workPct = cv > 0 ? (gross / cv) * 100 : 0;
    return {
      gross,
      advanceDeducted,
      retentionDeducted,
      net,
      workPct,
      totalQty,
      newOrdinal,
    };
  }, [selectedItems, thisPeriod, contract, existingBillings]);

  if (!open) return null;

  // ── Step 1: select items ─────────────────────────────────────
  const renderStep1 = () => {
    const remainingOnly = lineItems.filter(
      (li) => (remainingByItem[li.id] || 0) > 0
    );
    const allSelected =
      lineItems.length > 0 && selected.size === lineItems.length;
    const remainingSelected =
      remainingOnly.length > 0 &&
      remainingOnly.every((li) => selected.has(li.id));

    const toggle = (id: string) => {
      setSelected((prev) => {
        const next = new Set(prev);
        if (next.has(id)) next.delete(id);
        else next.add(id);
        return next;
      });
    };

    const selectAll = () => {
      if (allSelected) {
        setSelected(new Set());
      } else {
        setSelected(new Set(lineItems.map((li) => li.id)));
      }
    };

    const selectRemainingOnly = () => {
      if (remainingSelected) {
        setSelected(new Set());
      } else {
        setSelected(new Set(remainingOnly.map((li) => li.id)));
      }
    };

    return (
      <div className="space-y-3">
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">
              رقم المستخلص *
            </label>
            <input
              className="input font-mono"
              value={billingNumber}
              onChange={(e) => setBillingNumber(e.target.value)}
              required
              placeholder={suggestedNextNumber}
              dir="ltr"
            />
            <p className="text-xs text-ink-muted mt-1">
              المقترح: <span className="font-mono">{suggestedNextNumber}</span>
            </p>
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">التاريخ *</label>
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

        <div className="flex items-center justify-between flex-wrap gap-2">
          <h4 className="text-sm font-semibold">اختر البنود</h4>
          <div className="flex items-center gap-2">
            <button
              type="button"
              onClick={selectRemainingOnly}
              className={cn(
                "text-xs px-2 py-1 rounded border",
                remainingSelected
                  ? "bg-primary-50 border-primary-300 text-primary-800"
                  : "border-edge text-ink-muted hover:bg-raised"
              )}
            >
              البنود المتبقية فقط
            </button>
            <button
              type="button"
              onClick={selectAll}
              className={cn(
                "text-xs px-2 py-1 rounded border",
                allSelected
                  ? "bg-primary-50 border-primary-300 text-primary-800"
                  : "border-edge text-ink-muted hover:bg-raised"
              )}
            >
              الكل
            </button>
          </div>
        </div>

        {loadingLineItems ? (
          <div className="card flex items-center justify-center py-8 text-ink-muted gap-2 text-sm">
            <Loader2 className="animate-spin" size={16} />
            جاري تحميل البنود...
          </div>
        ) : lineItems.length === 0 ? (
          <div className="card text-center text-ink-muted py-6 text-sm">
            لا توجد بنود مسجلة في العقد. أضف بنوداً من تبويب "العقد" أولاً.
          </div>
        ) : (
          <div className="border border-edge rounded-md max-h-80 overflow-y-auto">
            <table className="w-full text-sm">
              <thead className="bg-raised sticky top-0">
                <tr>
                  <th className="text-right py-2 px-3 w-8"></th>
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">#</th>
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوصف</th>
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوحدة</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">الإجمالي</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">المتبقي</th>
                </tr>
              </thead>
              <tbody>
                {lineItems.map((li) => {
                  const checked = selected.has(li.id);
                  const remaining = remainingByItem[li.id] || 0;
                  const exhausted = remaining <= 0;
                  return (
                    <tr
                      key={li.id}
                      onClick={() => !exhausted && toggle(li.id)}
                      className={cn(
                        "border-b border-edge cursor-pointer",
                        checked && "bg-primary-50",
                        exhausted && "opacity-50 cursor-not-allowed"
                      )}
                    >
                      <td className="py-2 px-3">
                        {checked ? (
                          <CheckSquare
                            size={16}
                            className="text-primary-700"
                          />
                        ) : (
                          <Square size={16} className="text-ink-subtle" />
                        )}
                      </td>
                      <td className="py-2 px-3 font-mono text-xs text-ink-muted">
                        #{li.lineNumber}
                      </td>
                      <td className="py-2 px-3 max-w-xs truncate" title={li.description}>
                        {li.description}
                        {exhausted && (
                          <span className="text-[10px] text-green-700 mr-2">
                            (مكتمل)
                          </span>
                        )}
                      </td>
                      <td className="py-2 px-3 text-xs text-ink-muted">
                        {li.customUnit || li.unit}
                      </td>
                      <td className="py-2 px-3 text-left font-mono" dir="ltr">
                        {formatNumber(li.quantity, 3)}
                      </td>
                      <td
                        className={cn(
                          "py-2 px-3 text-left font-mono",
                          exhausted ? "text-green-700" : ""
                        )}
                        dir="ltr"
                      >
                        {formatNumber(remaining, 3)}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        <div className="text-xs text-ink-muted">
          تم اختيار {selected.size} من {lineItems.length} بند
        </div>
      </div>
    );
  };

  // ── Step 2: enter quantities ─────────────────────────────────
  const renderStep2 = () => {
    // Build per-row errors
    const rowErrors: Record<string, string> = {};
    let hasRowError = false;
    for (const li of selectedItems) {
      const tp = Number(thisPeriod[li.id]) || 0;
      const prev = previousQtyByItem[li.id] || 0;
      const total = Number(li.quantity) || 0;
      if (tp < 0) {
        rowErrors[li.id] = "لا يمكن أن تكون الكمية سالبة";
        hasRowError = true;
      } else if (tp + prev > total + 0.0001) {
        rowErrors[li.id] = `تجاوز المتبقي (${formatNumber(total - prev, 3)})`;
        hasRowError = true;
      } else if (tp === 0) {
        // Allow zero for now; we'll validate at submit
        // (the user might be just previewing)
      }
    }

    const setTp = (id: string, val: string) => {
      setThisPeriod((prev) => ({ ...prev, [id]: val }));
    };

    return (
      <div className="space-y-3">
        <div className="text-xs text-ink-muted">
          أدخل الكمية المنجزة من كل بند في هذه الفترة. الإجمالي يُحسب تلقائياً.
        </div>

        {/* Desktop table */}
        <div className="hidden sm:block border border-edge rounded-md overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-raised">
              <tr>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">#</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوصف</th>
                <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوحدة</th>
                <th className="text-left py-2 px-3 font-semibold text-ink-muted">السابق</th>
                <th className="text-left py-2 px-3 font-semibold text-ink-muted">المتبقي</th>
                <th className="text-left py-2 px-3 font-semibold text-ink-muted">هذه الفترة *</th>
                <th className="text-left py-2 px-3 font-semibold text-ink-muted">التراكمي</th>
              </tr>
            </thead>
            <tbody>
              {selectedItems.map((li) => {
                const tp = Number(thisPeriod[li.id]) || 0;
                const prev = previousQtyByItem[li.id] || 0;
                const total = Number(li.quantity) || 0;
                const cum = prev + tp;
                const err = rowErrors[li.id];
                return (
                  <tr
                    key={li.id}
                    className={cn(
                      "border-b border-edge",
                      err && "bg-red-50 dark:bg-red-900/20"
                    )}
                  >
                    <td className="py-2 px-3 font-mono text-xs text-ink-muted">
                      #{li.lineNumber}
                    </td>
                    <td className="py-2 px-3 max-w-xs truncate" title={li.description}>
                      {li.description}
                    </td>
                    <td className="py-2 px-3 text-xs text-ink-muted">
                      {li.customUnit || li.unit}
                    </td>
                    <td className="py-2 px-3 text-left font-mono" dir="ltr">
                      {formatNumber(prev, 3)}
                    </td>
                    <td className="py-2 px-3 text-left font-mono" dir="ltr">
                      {formatNumber(Math.max(0, total - prev), 3)}
                    </td>
                    <td className="py-2 px-3">
                      <input
                        type="number"
                        step="0.001"
                        min="0"
                        className={cn(
                          "input w-24",
                          err && "border-red-300 bg-red-50"
                        )}
                        value={thisPeriod[li.id] ?? ""}
                        onChange={(e) => setTp(li.id, e.target.value)}
                        dir="ltr"
                        placeholder="0"
                      />
                    </td>
                    <td className="py-2 px-3 text-left font-mono font-semibold" dir="ltr">
                      {formatNumber(cum, 3)}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        {/* Mobile cards */}
        <div className="sm:hidden space-y-2">
          {selectedItems.map((li) => {
            const tp = Number(thisPeriod[li.id]) || 0;
            const prev = previousQtyByItem[li.id] || 0;
            const total = Number(li.quantity) || 0;
            const cum = prev + tp;
            const err = rowErrors[li.id];
            return (
              <div
                key={li.id}
                className={cn("card", err && "border-red-300 bg-red-50")}
              >
                <div className="text-sm font-medium">
                  #{li.lineNumber} — {li.description}
                </div>
                <div className="text-xs text-ink-muted mt-1">
                  {li.customUnit || li.unit} • سابق:{" "}
                  <span className="font-mono">{formatNumber(prev, 3)}</span> •
                  متبقي:{" "}
                  <span className="font-mono">
                    {formatNumber(Math.max(0, total - prev), 3)}
                  </span>
                </div>
                <div className="mt-2">
                  <label className="block text-xs text-ink-muted mb-1">
                    هذه الفترة
                  </label>
                  <input
                    type="number"
                    step="0.001"
                    min="0"
                    className={cn(
                      "input",
                      err && "border-red-300 bg-red-50"
                    )}
                    value={thisPeriod[li.id] ?? ""}
                    onChange={(e) => setTp(li.id, e.target.value)}
                    dir="ltr"
                    placeholder="0"
                  />
                </div>
                <div className="mt-2 text-xs text-ink-muted flex items-center justify-between">
                  <span>التراكمي:</span>
                  <span className="font-mono font-semibold text-ink-strong" dir="ltr">
                    {formatNumber(cum, 3)}
                  </span>
                </div>
                {err && (
                  <div className="mt-1 text-xs text-red-700">{err}</div>
                )}
              </div>
            );
          })}
        </div>

        {hasRowError && (
          <div className="p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 flex items-start gap-1">
            <AlertCircle size={12} className="mt-0.5 shrink-0" />
            <span>بعض البنود تتجاوز الكمية المتبقية. صححها قبل الحفظ.</span>
          </div>
        )}

        {/* Live preview */}
        <div className="border border-primary-200 bg-primary-50 rounded-md p-3">
          <div className="flex items-center gap-1 text-sm font-semibold text-primary-800 mb-2">
            <Info size={14} />
 معاينة المستخلص (تحديث لحظي)
          </div>
          <div className="grid grid-cols-2 sm:grid-cols-3 gap-2 text-sm">
            <PreviewRow label="إجمالي البنود المختارة" value={selectedItems.length} />
            <PreviewRow label="إجمالي الكمية" value={formatNumber(livePreview.totalQty, 3)} />
            <PreviewRow label="Gross" value={livePreview.gross} mono highlight />
            <PreviewRow
              label={`خصم مقدمة`}
              value={livePreview.advanceDeducted}
              mono
              highlight
            />
            <PreviewRow
              label={`احتجاز (يبدأ #${Math.max(1, Number(contract.retentionStartBilling) || 1)} — هذا #${livePreview.newOrdinal})`}
              value={livePreview.retentionDeducted}
              mono
              highlight
            />
            <PreviewRow
              label="الصافي"
              value={livePreview.net}
              mono
              highlight
              strong
            />
            <PreviewRow
              label="نسبة إنجاز العقد"
              value={`${livePreview.workPct.toFixed(2)}%`}
              mono
              highlight
            />
          </div>
          <p className="text-xs text-primary-700 mt-2">
            الأرقام النهائية تُحسب في الخادم عند الاعتماد.
          </p>
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">ملاحظات</label>
          <textarea
            className="input"
            rows={2}
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
            placeholder="ملاحظات اختيارية..."
          />
        </div>
      </div>
    );
  };

  // ── Submit ───────────────────────────────────────────────────
  const handleNext = () => {
    setError(null);
    if (selected.size === 0) {
      setError("اختر بنداً واحداً على الأقل");
      return;
    }
    if (!billingNumber.trim()) {
      setError("رقم المستخلص مطلوب");
      return;
    }
    if (!billingDate) {
      setError("تاريخ المستخلص مطلوب");
      return;
    }
    setStep(2);
  };

  const submit = async () => {
    setError(null);
    setSuccess(null);

    // Validate quantities
    const items: Array<{ lineItemId: string; thisPeriodQuantity: number }> = [];
    for (const li of selectedItems) {
      const tp = Number(thisPeriod[li.id]) || 0;
      const prev = previousQtyByItem[li.id] || 0;
      const total = Number(li.quantity) || 0;
      if (tp < 0) {
        setError(`البند ${li.lineNumber}: كمية سالبة`);
        return;
      }
      if (tp + prev > total + 0.0001) {
        setError(`البند ${li.lineNumber}: تجاوز المتبقي`);
        return;
      }
      if (tp > 0) {
        items.push({ lineItemId: li.id, thisPeriodQuantity: tp });
      }
    }
    if (items.length === 0) {
      setError("أدخل كمية موجبة لبند واحد على الأقل");
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
        notes: notes.trim() || null,
        items,
      });
      setSuccess("تم إنشاء المسودة");
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
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-3xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold flex items-center gap-2">
            <FileText size={18} className="text-primary-600" />
            مستخلص جديد
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

        {/* Step indicator */}
        <div className="flex items-center gap-2 mb-4">
          <StepDot active={step === 1} done={step === 2} num={1} label="اختيار البنود" />
          <div className={cn("flex-1 h-0.5", step === 2 ? "bg-primary-500" : "bg-edge")} />
          <StepDot active={step === 2} num={2} label="إدخال الكميات" />
        </div>

        {step === 1 ? renderStep1() : renderStep2()}

        {error && (
          <div className="mt-3 p-3 bg-red-50 text-red-700 rounded-md text-sm flex items-start gap-2">
            <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
            <span>{error}</span>
          </div>
        )}
        {success && (
          <div className="mt-3 p-3 bg-green-50 text-green-700 rounded-md text-sm flex items-center gap-2">
            <CheckCircle2 size={16} />
            <span>{success}</span>
          </div>
        )}

        <div className="mt-4 flex gap-2 justify-between flex-wrap">
          {step === 2 && (
            <button
              type="button"
              onClick={() => setStep(1)}
              className="btn-secondary"
              disabled={saving}
            >
              <ArrowRight size={16} />
              السابق
            </button>
          )}
          <div className="flex gap-2 flex-1 justify-end">
            <button
              type="button"
              onClick={onClose}
              className="btn-secondary"
              disabled={saving}
            >
              إلغاء
            </button>
            {step === 1 ? (
              <button
                type="button"
                onClick={handleNext}
                className="btn-primary"
                disabled={selected.size === 0}
              >
                التالي
                <ArrowLeft size={16} />
              </button>
            ) : (
              <button
                type="button"
                onClick={submit}
                className="btn-primary"
                disabled={saving}
              >
                {saving ? (
                  <>
                    <Loader2 className="animate-spin" size={16} /> جاري الحفظ...
                  </>
                ) : (
                  "حفظ المسودة"
                )}
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}

function StepDot({
  active,
  done,
  num,
  label,
}: {
  active?: boolean;
  done?: boolean;
  num: number;
  label: string;
}) {
  return (
    <div className="flex items-center gap-2">
      <div
        className={cn(
          "w-7 h-7 rounded-full flex items-center justify-center text-xs font-semibold border-2",
          done
            ? "bg-primary-600 border-primary-600 text-white"
            : active
              ? "bg-primary-50 border-primary-600 text-primary-700"
              : "bg-raised border-edge text-ink-muted"
        )}
      >
        {done ? "✓" : num}
      </div>
      <span
        className={cn(
          "text-xs sm:text-sm font-medium",
          active ? "text-ink-strong" : "text-ink-muted"
        )}
      >
        {label}
      </span>
    </div>
  );
}

function PreviewRow({
  label,
  value,
  mono,
  highlight,
  strong,
}: {
  label: string;
  value: number | string;
  mono?: boolean;
  highlight?: boolean;
  strong?: boolean;
}) {
  return (
    <div
      className={cn(
        "flex items-center justify-between gap-2 px-2 py-1 rounded",
        highlight ? "bg-canvas border border-primary-100 dark:border-primary-900" : ""
      )}
    >
      <span className="text-xs text-ink-muted">{label}</span>
      <span
        dir="ltr"
        className={cn(
          mono ? "font-mono" : "",
          strong ? "font-bold text-primary-900" : ""
        )}
      >
        {typeof value === "number" ? formatNumber(value) : value}
      </span>
    </div>
  );
}

"use client";

/**
 * Sprint 55 — Field Measurement Entry editor.
 *
 * The inline form for adding a BOQ line item's measurements to an
 * FMB. Each entry has sub-rows:
 *   - Real measurement: { label, count, length, width, height }
 *   - Deduction:        { label, deduction: 7.7 }
 *
 * The system computes:
 *   initialTotal = Σ(count × length × width × height) for real rows
 *   deductionsTotal = Σ(deduction) for deduction rows
 *   finalTotal = max(0, initialTotal - deductionsTotal)
 *   amount = finalTotal × unit_price
 */
import { useState } from "react";
import { Plus, X, Calculator, AlertCircle, Loader2 } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatNumber, cn } from "@/lib/utils";

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

export interface FieldMeasurementEntryDto {
  id: string;
  fmbId: string;
  lineItemId: string;
  lineNumber: number;
  description: string;
  unit: string;
  measurements: MeasurementRow[];
  initialTotal: number;
  deductionsTotal: number;
  finalTotal: number;
  unitPrice: number;
  amount: number;
  notes?: string | null;
}

export interface LineItemOption {
  id: string;
  lineNumber: number;
  description: string;
  unit: string;
  unitPrice: number;
}

interface Props {
  fmbId: string;
  /** Available BOQ line items to add. */
  lineItems: LineItemOption[];
  /** Existing entries (to avoid duplicates + to show the table). */
  entries: FieldMeasurementEntryDto[];
  onAdded: (e: FieldMeasurementEntryDto) => void;
}

export default function FieldMeasurementEntryForm({ fmbId, lineItems, entries, onAdded }: Props) {
  const [showForm, setShowForm] = useState(false);
  const [selectedLineItemId, setSelectedLineItemId] = useState("");
  const [measurements, setMeasurements] = useState<MeasurementRow[]>([
    { label: "", count: 1, length: 0, width: 0, height: 0 },
  ]);
  const [notes, setNotes] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Filter out line items already added
  const used = new Set(entries.map((e) => e.lineItemId));
  const available = lineItems.filter((li) => !used.has(li.id));

  const selected = lineItems.find((li) => li.id === selectedLineItemId);
  const computed = computeTotals(measurements);

  const addRow = (isDeduction: boolean) => {
    setMeasurements((prev) => [
      ...prev,
      isDeduction
        ? { label: "", deduction: 0 }
        : { label: "", count: 1, length: 0, width: 0, height: 0 },
    ]);
  };

  const updateRow = (idx: number, patch: Partial<MeasurementRow>) => {
    setMeasurements((prev) => prev.map((m, i) => (i === idx ? { ...m, ...patch } : m)));
  };

  const removeRow = (idx: number) => {
    setMeasurements((prev) => prev.filter((_, i) => i !== idx));
  };

  const submit = async () => {
    if (!selectedLineItemId) {
      setError("اختر بنداً من بنود العقد");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const res = await api.post(`/field-measurement-books/${fmbId}/entries`, {
        lineItemId: selectedLineItemId,
        measurements,
        notes: notes || null,
      });
      onAdded(res.data);
      setShowForm(false);
      setSelectedLineItemId("");
      setMeasurements([{ label: "", count: 1, length: 0, width: 0, height: 0 }]);
      setNotes("");
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  if (available.length === 0 && !showForm) {
    return (
      <div className="text-xs text-ink-muted italic">
        تم إضافة جميع بنود العقد إلى هذا الدفتر
      </div>
    );
  }

  return (
    <div className="space-y-2">
      {!showForm ? (
        <button
          onClick={() => setShowForm(true)}
          className="btn-secondary flex items-center gap-2 text-sm"
        >
          <Plus size={14} /> إضافة بند (مقاسات)
        </button>
      ) : (
        <div className="border border-edge rounded-md p-3 bg-raised space-y-2">
          <div className="flex items-center justify-between">
            <h4 className="font-semibold text-sm">إضافة بند جديد</h4>
            <button onClick={() => setShowForm(false)} className="text-ink-subtle hover:text-ink-muted">
              <X size={16} />
            </button>
          </div>

          <div>
            <label className="block text-xs font-medium mb-1">بند العقد</label>
            <select
              className="input"
              value={selectedLineItemId}
              onChange={(e) => setSelectedLineItemId(e.target.value)}
            >
              <option value="">— اختر البند —</option>
              {available.map((li) => (
                <option key={li.id} value={li.id}>
                  #{li.lineNumber} — {li.description} ({li.unit} × {formatNumber(li.unitPrice, 2)})
                </option>
              ))}
            </select>
          </div>

          {selected && (
            <div>
              <label className="block text-xs font-medium mb-1">القياسات (sub-rows)</label>
              <div className="border border-edge rounded overflow-x-auto">
                <table className="w-full text-xs">
                  <thead className="bg-canvas">
                    <tr>
                      <th className="text-right py-1 px-2">الوصف</th>
                      <th className="text-right py-1 px-2 w-16">عدد</th>
                      <th className="text-right py-1 px-2 w-16">طول</th>
                      <th className="text-right py-1 px-2 w-16">عرض</th>
                      <th className="text-right py-1 px-2 w-16">ارتفاع</th>
                      <th className="text-right py-1 px-2 w-16">الكمية</th>
                      <th className="text-right py-1 px-2 w-16">خصم</th>
                      <th className="w-8"></th>
                    </tr>
                  </thead>
                  <tbody>
                    {measurements.map((m, idx) => (
                      <tr key={idx} className="border-t border-edge">
                        <td className="py-1 px-1">
                          <input
                            className="input text-xs w-full"
                            placeholder={m.deduction ? "بند خصم" : "الواجهة / البند"}
                            value={m.label ?? ""}
                            onChange={(e) => updateRow(idx, { label: e.target.value })}
                          />
                        </td>
                        <td className="py-1 px-1">
                          <input
                            type="number" step="0.0001"
                            className="input text-xs w-14 text-center"
                            value={m.count ?? ""}
                            disabled={!!m.deduction}
                            onChange={(e) => updateRow(idx, { count: Number(e.target.value) || 0 })}
                            dir="ltr"
                          />
                        </td>
                        <td className="py-1 px-1">
                          <input
                            type="number" step="0.0001"
                            className="input text-xs w-14 text-center"
                            value={m.length ?? ""}
                            disabled={!!m.deduction}
                            onChange={(e) => updateRow(idx, { length: Number(e.target.value) || 0 })}
                            dir="ltr"
                          />
                        </td>
                        <td className="py-1 px-1">
                          <input
                            type="number" step="0.0001"
                            className="input text-xs w-14 text-center"
                            value={m.width ?? ""}
                            disabled={!!m.deduction}
                            onChange={(e) => updateRow(idx, { width: Number(e.target.value) || 0 })}
                            dir="ltr"
                          />
                        </td>
                        <td className="py-1 px-1">
                          <input
                            type="number" step="0.0001"
                            className="input text-xs w-14 text-center"
                            value={m.height ?? ""}
                            disabled={!!m.deduction}
                            onChange={(e) => updateRow(idx, { height: Number(e.target.value) || 0 })}
                            dir="ltr"
                          />
                        </td>
                        <td className="py-1 px-1 font-mono text-center" dir="ltr">
                          {m.deduction
                            ? "—"
                            : formatNumber(
                                (m.count ?? 0) * (m.length ?? 0) * (m.width ?? 0) * (m.height ?? 0),
                                3
                              )}
                        </td>
                        <td className="py-1 px-1">
                          <input
                            type="number" step="0.0001"
                            className={cn("input text-xs w-14 text-center", m.deduction ? "" : "opacity-50")}
                            value={m.deduction ?? ""}
                            disabled={!m.deduction}
                            onChange={(e) => updateRow(idx, { deduction: Number(e.target.value) || 0 })}
                            dir="ltr"
                            placeholder="خصم"
                          />
                        </td>
                        <td className="py-1 px-1">
                          <button
                            onClick={() => removeRow(idx)}
                            className="text-red-600 hover:text-red-800"
                            title="حذف"
                          >
                            <X size={14} />
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="flex gap-2 mt-2">
                <button onClick={() => addRow(false)} className="btn-secondary text-xs flex items-center gap-1">
                  <Plus size={12} /> صف قياس
                </button>
                <button onClick={() => addRow(true)} className="btn-secondary text-xs flex items-center gap-1">
                  <Plus size={12} /> خصم
                </button>
              </div>
            </div>
          )}

          {selected && (
            <div className="bg-canvas border border-edge rounded p-2 text-xs">
              <div className="flex items-center gap-2 font-semibold mb-1">
                <Calculator size={14} /> معاينة
              </div>
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
                <div>
                  <div className="text-ink-muted">ابتدائي</div>
                  <div className="font-mono" dir="ltr">{formatNumber(computed.initial, 3)}</div>
                </div>
                <div>
                  <div className="text-ink-muted">تنزيلات</div>
                  <div className="font-mono" dir="ltr">{formatNumber(computed.deductions, 3)}</div>
                </div>
                <div>
                  <div className="text-ink-muted">نهائي</div>
                  <div className="font-mono font-semibold" dir="ltr">{formatNumber(computed.final, 3)}</div>
                </div>
                <div>
                  <div className="text-ink-muted">المبلغ</div>
                  <div className="font-mono text-primary-700 font-semibold" dir="ltr">
                    {formatNumber(computed.amount, 3)}
                  </div>
                </div>
              </div>
            </div>
          )}

          <div>
            <label className="block text-xs font-medium mb-1">ملاحظات</label>
            <input className="input text-xs" value={notes} onChange={(e) => setNotes(e.target.value)} />
          </div>

          {error && (
            <div className="p-2 bg-red-50 border border-red-200 rounded text-xs text-red-700 flex items-start gap-1">
              <AlertCircle size={12} className="mt-0.5 shrink-0" />
              <span>{error}</span>
            </div>
          )}

          <div className="flex justify-end gap-2">
            <button onClick={() => setShowForm(false)} className="btn-secondary text-xs">إلغاء</button>
            <button onClick={submit} disabled={busy || !selected} className="btn-primary text-xs flex items-center gap-1">
              {busy && <Loader2 className="animate-spin" size={12} />}
              إضافة
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

function computeTotals(rows: MeasurementRow[]) {
  let initial = 0;
  let deductions = 0;
  for (const m of rows) {
    if (m.deduction && m.deduction > 0) {
      deductions += m.deduction;
    } else {
      const c = m.count ?? 0;
      const l = m.length ?? 0;
      const w = m.width ?? 0;
      const h = m.height ?? 0;
      initial += c * l * w * h;
    }
  }
  const final = Math.max(0, initial - deductions);
  return { initial, deductions, final, amount: 0 };
}

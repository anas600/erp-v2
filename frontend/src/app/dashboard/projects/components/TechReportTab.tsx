"use client";

/**
 * Sprint 56 — Project Technical Report tab (التقرير الفني).
 *
 * Shows the overall project progress + per-line item progress.
 * 4 status flags + 2 auto-computed progress %s.
 *
 * Data flow:
 *   - GET /api/projects/{id}/progress      → header (4 statuses + 2 %s + counts)
 *   - GET /api/projects/{id}/line-progress → list of line items with their %
 *   - PATCH /api/projects/{id}/progress     → update header statuses
 *   - PATCH /api/projects/{id}/line-progress/{lineId} → override a single line %
 *
 * Header:
 *   - نسبة التقدم الفعلية (Physical %) — auto from line items
 *   - نسبة الإنجاز المالي (Financial %) — from billings
 *   - حالة البرنامج (Schedule status): on_track | delayed | ahead | no_schedule | stopped
 *   - حالة التنفيذ (Execution status): in_progress | completed | stopped
 *   - تاريخ التقرير (Report date)
 *
 * Per-line:
 *   - من البند، الوصف، الوحدة
 *   - الكمية العقدية، الكمية المنجزة، النسبة المئوية
 *   - "Override" toggle (manual override)
 *   - "حفظ" button to persist
 */
import { useEffect, useState } from "react";
import {
  Loader2, Save, CheckCircle, AlertCircle, Pencil, X, ChevronDown, ChevronUp
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatDate, formatNumber, cn } from "@/lib/utils";

export interface LineItemProgressDto {
  id: string;
  lineItemId: string;
  lineNumber: number;
  description: string;
  unit: string;
  contractQuantity: number;
  unitPrice: number;
  quantityDone: number;
  progressPercent: number;
  lastUpdated: string;
  isManualOverride: boolean;
  notes?: string | null;
}

export interface ProjectProgressDto {
  id: string;
  code: string;
  name: string;
  physicalProgressPercent: number;
  financialProgressPercent: number;
  scheduleStatus: string;
  executionStatus: string;
  techReportDate?: string | null;
  totalLineItems: number;
  completedLineItems: number;
  totalContractValue: number;
  totalCompletedValue: number;
  lineItems: LineItemProgressDto[];
}

interface Props {
  projectId: string;
  onSave?: () => void;
}

const SCHEDULE_STATUSES: { value: string; label: string; color: string }[] = [
  { value: "on_track",    label: "في الموعد",    color: "bg-emerald-100 text-emerald-700" },
  { value: "delayed",     label: "متأخر",        color: "bg-red-100 text-red-700" },
  { value: "ahead",       label: "متقدم",         color: "bg-blue-100 text-blue-700" },
  { value: "no_schedule", label: "بدون برنامج",   color: "bg-slate-100 text-slate-700" },
  { value: "stopped",     label: "متوقف",         color: "bg-amber-100 text-amber-700" },
];

const EXECUTION_STATUSES: { value: string; label: string; color: string }[] = [
  { value: "in_progress", label: "قيد التنفيذ",   color: "bg-blue-100 text-blue-700" },
  { value: "completed",   label: "منتهي",         color: "bg-emerald-100 text-emerald-700" },
  { value: "stopped",     label: "متوقف",         color: "bg-amber-100 text-amber-700" },
];

export default function TechReportTab({ projectId, onSave }: Props) {
  const [data, setData] = useState<ProjectProgressDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Editable header state
  const [scheduleStatus, setScheduleStatus] = useState("on_track");
  const [executionStatus, setExecutionStatus] = useState("in_progress");
  const [techReportDate, setTechReportDate] = useState<string>("");

  // Per-line editing
  const [editingLineId, setEditingLineId] = useState<string | null>(null);
  const [lineEdit, setLineEdit] = useState<{ progressPercent: number; quantityDone: number; isManualOverride: boolean; notes: string } | null>(null);

  const loadData = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get<ProjectProgressDto>(`/projects/${projectId}/progress`);
      setData(res.data);
      setScheduleStatus(res.data.scheduleStatus);
      setExecutionStatus(res.data.executionStatus);
      setTechReportDate(res.data.techReportDate ? res.data.techReportDate.split("T")[0] : "");
    } catch (e) {
      setError(getErrorMessage(e));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    loadData();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId]);

  const handleSaveHeader = async () => {
    setSaving(true);
    setError(null);
    try {
      await api.patch(`/projects/${projectId}/progress`, {
        scheduleStatus,
        executionStatus,
        techReportDate: techReportDate || null,
      });
      await loadData();
      onSave?.();
    } catch (e) {
      setError(getErrorMessage(e));
    } finally {
      setSaving(false);
    }
  };

  const startEditLine = (li: LineItemProgressDto) => {
    setEditingLineId(li.lineItemId);
    setLineEdit({
      progressPercent: li.progressPercent,
      quantityDone: li.quantityDone,
      isManualOverride: li.isManualOverride,
      notes: li.notes || "",
    });
  };

  const cancelEditLine = () => {
    setEditingLineId(null);
    setLineEdit(null);
  };

  const saveEditLine = async (lineItemId: string) => {
    if (!lineEdit) return;
    setSaving(true);
    setError(null);
    try {
      await api.patch(`/projects/${projectId}/line-items/${lineItemId}/progress`, {
        progressPercent: lineEdit.progressPercent,
        quantityDone: lineEdit.quantityDone,
        isManualOverride: lineEdit.isManualOverride,
        notes: lineEdit.notes || null,
      });
      cancelEditLine();
      await loadData();
      onSave?.();
    } catch (e) {
      setError(getErrorMessage(e));
    } finally {
      setSaving(false);
    }
  };

  if (loading) {
    return (
      <div className="card flex items-center justify-center py-16">
        <Loader2 className="animate-spin text-primary-600" size={28} />
        <span className="mr-2 text-ink-muted">جاري تحميل التقرير الفني...</span>
      </div>
    );
  }

  if (!data) {
    return (
      <div className="card text-center py-12 text-ink-muted">
        لا توجد بيانات للتقرير الفني
      </div>
    );
  }

  const scheduleColor = SCHEDULE_STATUSES.find(s => s.value === scheduleStatus)?.color || "bg-slate-100 text-slate-700";
  const scheduleLabel = SCHEDULE_STATUSES.find(s => s.value === scheduleStatus)?.label || scheduleStatus;
  const executionColor = EXECUTION_STATUSES.find(e => e.value === executionStatus)?.color || "bg-slate-100 text-slate-700";
  const executionLabel = EXECUTION_STATUSES.find(e => e.value === executionStatus)?.label || executionStatus;

  return (
    <div className="space-y-4">
      {error && (
        <div className="card flex items-center gap-2 border-red-200 bg-red-50 text-red-700 text-sm">
          <AlertCircle size={16} />
          <span>{error}</span>
        </div>
      )}

      {/* ============== Header summary cards ============== */}
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-3">
        <div className="card">
          <div className="text-xs text-ink-muted mb-1">نسبة التقدم الفعلية</div>
          <div className="text-2xl font-bold text-primary-700">
            {formatNumber(data.physicalProgressPercent, 2)}%
          </div>
          <div className="text-xs text-ink-muted mt-1">
            محسوبة من الدفتر الفني (مرجّحة بقيمة البند)
          </div>
        </div>
        <div className="card">
          <div className="text-xs text-ink-muted mb-1">نسبة الإنجاز المالي</div>
          <div className="text-2xl font-bold text-emerald-700">
            {formatNumber(data.financialProgressPercent, 2)}%
          </div>
          <div className="text-xs text-ink-muted mt-1">
            من المستخلصات المعتمدة
          </div>
        </div>
        <div className="card">
          <div className="text-xs text-ink-muted mb-1">إجمالي قيمة العقد</div>
          <div className="text-2xl font-bold text-ink">
            {formatNumber(data.totalContractValue, 0)}
          </div>
          <div className="text-xs text-ink-muted mt-1">دينار ليبي</div>
        </div>
        <div className="card">
          <div className="text-xs text-ink-muted mb-1">القيمة المنجزة</div>
          <div className="text-2xl font-bold text-ink">
            {formatNumber(data.totalCompletedValue, 0)}
          </div>
          <div className="text-xs text-ink-muted mt-1">
            {data.completedLineItems} من {data.totalLineItems} بند مكتمل
          </div>
        </div>
      </div>

      {/* ============== Header edit form ============== */}
      <div className="card">
        <h3 className="text-sm font-bold mb-3 flex items-center gap-2">
          <CheckCircle size={16} className="text-primary-600" />
          حالة المشروع
        </h3>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
          <div>
            <label className="block text-xs text-ink-muted mb-1">حالة البرنامج الزمني</label>
            <select
              value={scheduleStatus}
              onChange={(e) => setScheduleStatus(e.target.value)}
              className="w-full text-sm border border-edge rounded px-2 py-1.5 bg-surface"
            >
              {SCHEDULE_STATUSES.map((s) => (
                <option key={s.value} value={s.value}>{s.label}</option>
              ))}
            </select>
            <div className="mt-1">
              <span className={cn("inline-block text-[10px] px-2 py-0.5 rounded", scheduleColor)}>
                {scheduleLabel}
              </span>
            </div>
          </div>
          <div>
            <label className="block text-xs text-ink-muted mb-1">حالة التنفيذ</label>
            <select
              value={executionStatus}
              onChange={(e) => setExecutionStatus(e.target.value)}
              className="w-full text-sm border border-edge rounded px-2 py-1.5 bg-surface"
            >
              {EXECUTION_STATUSES.map((s) => (
                <option key={s.value} value={s.value}>{s.label}</option>
              ))}
            </select>
            <div className="mt-1">
              <span className={cn("inline-block text-[10px] px-2 py-0.5 rounded", executionColor)}>
                {executionLabel}
              </span>
            </div>
          </div>
          <div>
            <label className="block text-xs text-ink-muted mb-1">تاريخ التقرير</label>
            <input
              type="date"
              value={techReportDate}
              onChange={(e) => setTechReportDate(e.target.value)}
              className="w-full text-sm border border-edge rounded px-2 py-1.5 bg-surface"
            />
          </div>
        </div>
        <div className="mt-4 flex justify-end">
          <button
            type="button"
            onClick={handleSaveHeader}
            disabled={saving}
            className="btn-primary flex items-center gap-1 text-sm"
          >
            {saving ? <Loader2 className="animate-spin" size={14} /> : <Save size={14} />}
            حفظ حالة المشروع
          </button>
        </div>
      </div>

      {/* ============== Per-line progress table ============== */}
      <div className="card">
        <h3 className="text-sm font-bold mb-3 flex items-center gap-2">
          <Pencil size={16} className="text-primary-600" />
          نسبة الإنجاز حسب البند ({data.lineItems.length} بند)
        </h3>
        {data.lineItems.length === 0 ? (
          <div className="text-center text-ink-muted text-sm py-8">
            لا توجد بنود. أضف بنود من تبويب العقد أولاً.
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="text-right text-ink-muted border-b border-edge">
                  <th className="py-2 px-2 font-medium">#</th>
                  <th className="py-2 px-2 font-medium">الوصف</th>
                  <th className="py-2 px-2 font-medium">الوحدة</th>
                  <th className="py-2 px-2 font-medium">الكمية العقدية</th>
                  <th className="py-2 px-2 font-medium">الكمية المنجزة</th>
                  <th className="py-2 px-2 font-medium">سعر الوحدة</th>
                  <th className="py-2 px-2 font-medium">القيمة المنجزة</th>
                  <th className="py-2 px-2 font-medium">النسبة %</th>
                  <th className="py-2 px-2 font-medium">تجاوز؟</th>
                  <th className="py-2 px-2 font-medium">آخر تحديث</th>
                  <th className="py-2 px-2 font-medium"></th>
                </tr>
              </thead>
              <tbody>
                {data.lineItems.map((li) => {
                  const completedValue = li.quantityDone * li.unitPrice;
                  const isEditing = editingLineId === li.lineItemId;

                  return (
                    <tr key={li.id} className="border-b border-edge hover:bg-surface-muted">
                      <td className="py-2 px-2 font-mono text-xs">{li.lineNumber}</td>
                      <td className="py-2 px-2 max-w-[200px] truncate" title={li.description}>
                        {li.description}
                      </td>
                      <td className="py-2 px-2 text-xs">{li.unit}</td>
                      <td className="py-2 px-2">{formatNumber(li.contractQuantity, 2)}</td>
                      <td className="py-2 px-2">
                        {isEditing && lineEdit ? (
                          <input
                            type="number"
                            value={lineEdit.quantityDone}
                            onChange={(e) => setLineEdit({ ...lineEdit, quantityDone: parseFloat(e.target.value) || 0 })}
                            className="w-24 text-sm border border-edge rounded px-1 py-0.5"
                            step="0.01"
                          />
                        ) : (
                          formatNumber(li.quantityDone, 2)
                        )}
                      </td>
                      <td className="py-2 px-2">{formatNumber(li.unitPrice, 2)}</td>
                      <td className="py-2 px-2 font-medium">
                        {formatNumber(completedValue, 2)}
                      </td>
                      <td className="py-2 px-2">
                        {isEditing && lineEdit ? (
                          <input
                            type="number"
                            value={lineEdit.progressPercent}
                            onChange={(e) => setLineEdit({ ...lineEdit, progressPercent: parseFloat(e.target.value) || 0 })}
                            className="w-20 text-sm border border-edge rounded px-1 py-0.5"
                            step="0.01"
                            min="0"
                            max="100"
                          />
                        ) : (
                          <span
                            className={cn(
                              "inline-block px-2 py-0.5 rounded text-xs font-medium",
                              li.progressPercent >= 100
                                ? "bg-emerald-100 text-emerald-700"
                                : li.progressPercent > 0
                                ? "bg-blue-100 text-blue-700"
                                : "bg-slate-100 text-slate-700"
                            )}
                          >
                            {formatNumber(li.progressPercent, 2)}%
                          </span>
                        )}
                      </td>
                      <td className="py-2 px-2">
                        {isEditing && lineEdit ? (
                          <input
                            type="checkbox"
                            checked={lineEdit.isManualOverride}
                            onChange={(e) => setLineEdit({ ...lineEdit, isManualOverride: e.target.checked })}
                          />
                        ) : li.isManualOverride ? (
                          <span className="inline-block w-2 h-2 rounded-full bg-amber-500" title="تجاوز يدوي" />
                        ) : (
                          <span className="inline-block w-2 h-2 rounded-full bg-slate-300" title="تلقائي" />
                        )}
                      </td>
                      <td className="py-2 px-2 text-xs text-ink-muted">
                        {li.lastUpdated ? formatDate(li.lastUpdated) : "-"}
                      </td>
                      <td className="py-2 px-2 text-xs">
                        {isEditing ? (
                          <div className="flex gap-1">
                            <button
                              type="button"
                              onClick={() => saveEditLine(li.lineItemId)}
                              disabled={saving}
                              className="text-emerald-600 hover:text-emerald-800"
                            >
                              حفظ
                            </button>
                            <button
                              type="button"
                              onClick={cancelEditLine}
                              className="text-ink-muted hover:text-ink"
                            >
                              إلغاء
                            </button>
                          </div>
                        ) : (
                          <button
                            type="button"
                            onClick={() => startEditLine(li)}
                            className="text-primary-600 hover:text-primary-800"
                          >
                            تعديل
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

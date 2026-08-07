"use client";

/**
 * Sprint 36 — Billings tab.
 *
 * Table of all progress billings for this project with:
 *   - "مستخلص جديد" button (disabled if no contract)
 *   - View: opens a detail modal (read-only) showing full fields
 *   - Approve: DRAFT -> INVOICED (creates invoice + journal entry)
 *   - Cancel: DRAFT -> CANCELLED (only allowed from DRAFT)
 *
 * Why is "View" a modal and not a side panel?
 *   The billing detail needs ~10 fields and edit options that the
 *   user mostly won't use (status transitions are buttons on the
 *   row). A modal keeps the table as the primary view and gives
 *   detail-on-demand without disrupting the workflow.
 *
 * Note: We import ProgressBillingDto from this file. BillingModal
 * uses the same type — keeping the type co-located with the tab
 * that owns the list.
 */
import { useEffect, useState } from "react";
import {
  Loader2,
  Plus,
  Eye,
  CheckCircle,
  Ban,
  FileText,
  AlertCircle,
  Calendar,
  Percent,
  X,
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate, cn } from "@/lib/utils";
import StatusBadgeBilling from "./StatusBadgeBilling";
import BillingModal from "./BillingModal";
import type { ContractDto } from "./ContractModal";

export interface ProgressBillingDto {
  id: string;
  companyId: string;
  projectId: string;
  contractId: string;
  billingNumber: string;
  billingDate: string;
  periodFrom?: string | null;
  periodTo?: string | null;
  workCompletedPercent: number;
  grossAmount: number;
  advanceDeducted: number;
  retentionDeducted: number;
  netAmount: number;
  status: string;
  invoiceId?: string | null;
  journalEntryId?: string | null;
  notes?: string | null;
  createdAt?: string;
  updatedAt?: string;
}

interface Props {
  projectId: string;
  contract: ContractDto | null;
  /** Optional pre-loaded billings (e.g. for the integrated tab). */
  initialBillings?: ProgressBillingDto[];
  onBillingsChange?: (b: ProgressBillingDto[]) => void;
}

export default function BillingsTab({
  projectId,
  contract,
  initialBillings,
  onBillingsChange,
}: Props) {
  const { activeCompany } = useAuth();
  const [billings, setBillings] = useState<ProgressBillingDto[]>(
    initialBillings || []
  );
  const [loading, setLoading] = useState(!initialBillings);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [viewing, setViewing] = useState<ProgressBillingDto | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);

  const load = async () => {
    if (!activeCompany) return;
    setLoading(true);
    setError(null);
    try {
      const res = await api.get(`/projects/${projectId}/billings`);
      const list = res.data || [];
      setBillings(list);
      onBillingsChange?.(list);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (!initialBillings) load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, activeCompany?.id]);

  const refresh = async () => {
    await load();
  };

  const handleCreated = (b: ProgressBillingDto) => {
    setBillings((prev) => [...prev, b]);
    onBillingsChange?.([...billings, b]);
  };

  const handleApprove = async (b: ProgressBillingDto) => {
    if (
      !confirm(
        "سيتم إنشاء فاتورة للعميل وقيد محاسبي. متأكد من اعتماد المستخلص؟"
      )
    )
      return;
    setBusyId(b.id);
    setError(null);
    try {
      const res = await api.post(`/billings/${b.id}/approve`, {
        billingDate: b.billingDate,
        notes: b.notes,
      });
      setBillings((prev) =>
        prev.map((x) => (x.id === b.id ? res.data : x))
      );
      onBillingsChange?.(billings.map((x) => (x.id === b.id ? res.data : x)));
      setSuccess("تم اعتماد المستخلص وإنشاء الفاتورة والقيد");
      setTimeout(() => setSuccess(null), 4000);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  };

  const handleCancel = async (b: ProgressBillingDto) => {
    if (!confirm("سيتم إلغاء المستخلص (لا يمكن التراجع). متأكد؟")) return;
    setBusyId(b.id);
    setError(null);
    try {
      const res = await api.post(`/billings/${b.id}/cancel`);
      setBillings((prev) =>
        prev.map((x) => (x.id === b.id ? res.data : x))
      );
      onBillingsChange?.(billings.map((x) => (x.id === b.id ? res.data : x)));
      setSuccess("تم إلغاء المستخلص");
      setTimeout(() => setSuccess(null), 4000);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  };

  const totals = billings
    .filter((b) => b.status !== "CANCELLED")
    .reduce(
      (acc, b) => {
        acc.gross += Number(b.grossAmount) || 0;
        acc.advance += Number(b.advanceDeducted) || 0;
        acc.retention += Number(b.retentionDeducted) || 0;
        acc.net += Number(b.netAmount) || 0;
        return acc;
      },
      { gross: 0, advance: 0, retention: 0, net: 0 }
    );

  return (
    <div className="space-y-3">
      {error && (
        <div className="p-3 bg-red-50 border border-red-200 rounded-md text-sm text-red-700 flex items-start gap-2">
          <AlertCircle size={16} className="mt-0.5 shrink-0" />
          <span>{error}</span>
        </div>
      )}
      {success && (
        <div className="p-3 bg-green-50 border border-green-200 rounded-md text-sm text-green-700 flex items-center gap-2">
          <CheckCircle size={16} />
          <span>{success}</span>
        </div>
      )}

      {/* Action bar */}
      <div className="card">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div>
            <h3 className="font-semibold flex items-center gap-2">
              <FileText size={16} className="text-primary-600" />
              المستخلصات
              {billings.length > 0 && (
                <span className="text-xs text-gray-500 font-normal">
                  ({billings.length})
                </span>
              )}
            </h3>
            {!contract && (
              <p className="text-xs text-gray-500 mt-0.5">
                يتطلب إنشاء مستخلص وجود عقد للمشروع
              </p>
            )}
          </div>
          <button
            type="button"
            onClick={() => setCreating(true)}
            disabled={!contract}
            className="btn-primary"
            title={!contract ? "يتطلب وجود عقد" : "مستخلص جديد"}
          >
            <Plus size={16} />
            <span className="hidden sm:inline">مستخلص جديد</span>
          </button>
        </div>

        {!contract && (
          <div className="mt-3 p-3 bg-amber-50 border border-amber-200 rounded-md text-sm text-amber-800">
            أضف عقداً للمشروع من تبويب "العقد" لتفعيل إنشاء المستخلصات.
          </div>
        )}
      </div>

      {/* Loading */}
      {loading ? (
        <div className="card flex items-center justify-center py-12 text-gray-500 gap-2">
          <Loader2 className="animate-spin" size={20} />
          جاري التحميل...
        </div>
      ) : billings.length === 0 ? (
        <div className="card text-center text-gray-500 py-12 text-sm">
          لا توجد مستخلصات بعد.
          {contract && (
            <div className="mt-2">
              <button
                type="button"
                onClick={() => setCreating(true)}
                className="text-primary-600 hover:underline"
              >
                أنشئ أول مستخلص
              </button>
            </div>
          )}
        </div>
      ) : (
        <>
          {/* Summary row */}
          <div className="card">
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-sm">
              <Summary label="إجمالي Gross" value={totals.gross} />
              <Summary label="إجمالي خصم المقدمة" value={totals.advance} muted />
              <Summary
                label="إجمالي الاحتجاز"
                value={totals.retention}
                muted
              />
              <Summary
                label="إجمالي الصافي"
                value={totals.net}
                highlight
                strong
              />
            </div>
          </div>

          {/* Desktop table */}
          <div className="hidden md:block card p-0 overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-gray-50 border-b border-gray-200">
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">رقم</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">التاريخ</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">% إنجاز</th>
                  <th className="text-left py-2 px-3 font-semibold text-gray-600">Gross</th>
                  <th className="text-left py-2 px-3 font-semibold text-gray-600">خصم مقدمة</th>
                  <th className="text-left py-2 px-3 font-semibold text-gray-600">احتجاز</th>
                  <th className="text-left py-2 px-3 font-semibold text-gray-600">الصافي</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">الحالة</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600 w-32">إجراءات</th>
                </tr>
              </thead>
              <tbody>
                {billings.map((b) => (
                  <tr
                    key={b.id}
                    className={cn(
                      "border-b border-gray-100",
                      b.status === "CANCELLED" && "opacity-60"
                    )}
                  >
                    <td className="py-2 px-3 font-mono text-xs">{b.billingNumber}</td>
                    <td className="py-2 px-3 whitespace-nowrap">{formatDate(b.billingDate)}</td>
                    <td className="py-2 px-3 font-mono" dir="ltr">
                      {formatNumber(b.workCompletedPercent)}%
                    </td>
                    <td className="py-2 px-3 text-left font-mono" dir="ltr">
                      {formatNumber(b.grossAmount)}
                    </td>
                    <td className="py-2 px-3 text-left font-mono text-gray-600" dir="ltr">
                      {formatNumber(b.advanceDeducted)}
                    </td>
                    <td className="py-2 px-3 text-left font-mono text-gray-600" dir="ltr">
                      {formatNumber(b.retentionDeducted)}
                    </td>
                    <td className="py-2 px-3 text-left font-mono font-semibold" dir="ltr">
                      {formatNumber(b.netAmount)}
                    </td>
                    <td className="py-2 px-3">
                      <StatusBadgeBilling status={b.status} />
                    </td>
                    <td className="py-2 px-3">
                      <RowActions
                        b={b}
                        onView={() => setViewing(b)}
                        onApprove={() => handleApprove(b)}
                        onCancel={() => handleCancel(b)}
                        busy={busyId === b.id}
                      />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Mobile cards */}
          <div className="md:hidden space-y-2">
            {billings.map((b) => (
              <div
                key={b.id}
                className={cn(
                  "card",
                  b.status === "CANCELLED" && "opacity-60"
                )}
              >
                <div className="flex items-start justify-between gap-2 mb-2">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 mb-1">
                      <span className="font-mono text-xs text-gray-500">
                        #{b.billingNumber}
                      </span>
                      <StatusBadgeBilling status={b.status} />
                    </div>
                    <div className="text-xs text-gray-500 flex items-center gap-1">
                      <Calendar size={12} />
                      {formatDate(b.billingDate)}
                    </div>
                  </div>
                  <div className="text-left shrink-0">
                    <div className="text-xs text-gray-500">الصافي</div>
                    <div className="font-mono font-bold" dir="ltr">
                      {formatNumber(b.netAmount)}
                    </div>
                  </div>
                </div>
                <div className="grid grid-cols-2 gap-2 text-xs text-gray-600">
                  <div>
                    <Percent size={11} className="inline ml-0.5" />
                    إنجاز:{" "}
                    <span className="font-mono font-semibold">
                      {formatNumber(b.workCompletedPercent)}%
                    </span>
                  </div>
                  <div className="text-left">
                    <span className="text-gray-500">Gross:</span>{" "}
                    <span className="font-mono">{formatNumber(b.grossAmount)}</span>
                  </div>
                  <div>
                    <span className="text-gray-500">مقدمة:</span>{" "}
                    <span className="font-mono">{formatNumber(b.advanceDeducted)}</span>
                  </div>
                  <div>
                    <span className="text-gray-500">احتجاز:</span>{" "}
                    <span className="font-mono">{formatNumber(b.retentionDeducted)}</span>
                  </div>
                </div>
                <div className="mt-3 pt-2 border-t border-gray-100">
                  <RowActions
                    b={b}
                    onView={() => setViewing(b)}
                    onApprove={() => handleApprove(b)}
                    onCancel={() => handleCancel(b)}
                    busy={busyId === b.id}
                    mobile
                  />
                </div>
              </div>
            ))}
          </div>
        </>
      )}

      {/* Create modal */}
      {contract && (
        <BillingModal
          open={creating}
          onClose={() => setCreating(false)}
          onCreated={handleCreated}
          projectId={projectId}
          contract={contract}
          existingBillings={billings}
        />
      )}

      {/* View modal */}
      {viewing && (
        <ViewBillingModal billing={viewing} onClose={() => setViewing(null)} />
      )}
    </div>
  );
}

function RowActions({
  b,
  onView,
  onApprove,
  onCancel,
  busy,
  mobile = false,
}: {
  b: ProgressBillingDto;
  onView: () => void;
  onApprove: () => void;
  onCancel: () => void;
  busy: boolean;
  mobile?: boolean;
}) {
  return (
    <div className={cn("flex items-center gap-1", mobile && "justify-end")}>
      <button
        type="button"
        onClick={onView}
        className="text-blue-600 hover:bg-blue-50 p-1 rounded"
        title="عرض"
        aria-label="عرض المستخلص"
      >
        <Eye size={14} />
      </button>
      {b.status === "DRAFT" && (
        <>
          <button
            type="button"
            onClick={onApprove}
            disabled={busy}
            className="text-green-600 hover:bg-green-50 p-1 rounded disabled:opacity-50"
            title="اعتماد"
            aria-label="اعتماد المستخلص"
          >
            {busy ? <Loader2 className="animate-spin" size={14} /> : <CheckCircle size={14} />}
          </button>
          <button
            type="button"
            onClick={onCancel}
            disabled={busy}
            className="text-red-600 hover:bg-red-50 p-1 rounded disabled:opacity-50"
            title="إلغاء"
            aria-label="إلغاء المستخلص"
          >
            <Ban size={14} />
          </button>
        </>
      )}
    </div>
  );
}

function Summary({
  label,
  value,
  muted,
  highlight,
  strong,
}: {
  label: string;
  value: number;
  muted?: boolean;
  highlight?: boolean;
  strong?: boolean;
}) {
  return (
    <div
      className={cn(
        "px-3 py-2 rounded-md",
        highlight ? "bg-blue-50 border border-blue-200" : "bg-gray-50"
      )}
    >
      <div className="text-xs text-gray-500">{label}</div>
      <div
        dir="ltr"
        className={cn(
          "font-mono",
          strong ? "text-lg font-bold text-blue-900" : "text-sm font-semibold",
          muted && "text-gray-500"
        )}
      >
        {formatNumber(value)} د.ل
      </div>
    </div>
  );
}

function ViewBillingModal({
  billing,
  onClose,
}: {
  billing: ProgressBillingDto;
  onClose: () => void;
}) {
  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-2 sm:p-4">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-2xl p-4 sm:p-6 max-h-[95vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold flex items-center gap-2">
            <FileText size={18} className="text-primary-600" />
            تفاصيل المستخلص #{billing.billingNumber}
            <StatusBadgeBilling status={billing.status} />
          </h2>
          <button
            onClick={onClose}
            className="text-gray-400 hover:text-gray-600"
            type="button"
            aria-label="إغلاق"
          >
            <X size={20} />
          </button>
        </div>

        <div className="space-y-3 text-sm">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <Row label="رقم المستخلص" value={billing.billingNumber} mono />
            <Row label="التاريخ" value={formatDate(billing.billingDate)} />
            <Row
              label="الفترة من"
              value={billing.periodFrom ? formatDate(billing.periodFrom) : "—"}
            />
            <Row
              label="الفترة إلى"
              value={billing.periodTo ? formatDate(billing.periodTo) : "—"}
            />
            <Row
              label="نسبة الإنجاز التراكمية"
              value={`${formatNumber(billing.workCompletedPercent)}%`}
              mono
            />
            <Row label="الحالة" value={<StatusBadgeBilling status={billing.status} />} />
          </div>

          <div className="border border-gray-200 rounded-md p-3 bg-gray-50">
            <h4 className="font-semibold text-sm mb-2">تفاصيل المبالغ</h4>
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 text-sm">
              <div>
                <div className="text-xs text-gray-500">Gross</div>
                <div className="font-mono" dir="ltr">
                  {formatNumber(billing.grossAmount)}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-500">خصم مقدمة</div>
                <div className="font-mono" dir="ltr">
                  {formatNumber(billing.advanceDeducted)}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-500">احتجاز</div>
                <div className="font-mono" dir="ltr">
                  {formatNumber(billing.retentionDeducted)}
                </div>
              </div>
              <div>
                <div className="text-xs text-gray-500">الصافي</div>
                <div className="font-mono font-bold text-blue-900" dir="ltr">
                  {formatNumber(billing.netAmount)}
                </div>
              </div>
            </div>
          </div>

          {billing.invoiceId && (
            <div className="text-xs text-gray-600">
              الفاتورة المرتبطة:{" "}
              <span className="font-mono">{billing.invoiceId.slice(0, 8)}</span>
            </div>
          )}
          {billing.journalEntryId && (
            <div className="text-xs text-gray-600">
              القيد المحاسبي:{" "}
              <span className="font-mono">{billing.journalEntryId.slice(0, 8)}</span>
            </div>
          )}

          {billing.notes && (
            <div>
              <p className="text-xs text-gray-500 mb-1">ملاحظات</p>
              <p className="text-sm text-gray-700 whitespace-pre-wrap">
                {billing.notes}
              </p>
            </div>
          )}

          <div className="text-xs text-gray-500 pt-2 border-t border-gray-100">
            تاريخ الإنشاء: {formatDate(billing.createdAt)}
            {billing.updatedAt && ` • آخر تحديث: ${formatDate(billing.updatedAt)}`}
          </div>
        </div>

        <div className="mt-4 flex justify-end">
          <button type="button" onClick={onClose} className="btn-secondary">
            إغلاق
          </button>
        </div>
      </div>
    </div>
  );
}

function Row({
  label,
  value,
  mono,
}: {
  label: string;
  value: React.ReactNode;
  mono?: boolean;
}) {
  return (
    <div>
      <p className="text-xs text-gray-500 mb-0.5">{label}</p>
      <p className={mono ? "font-mono" : ""} dir="ltr">
        {value}
      </p>
    </div>
  );
}

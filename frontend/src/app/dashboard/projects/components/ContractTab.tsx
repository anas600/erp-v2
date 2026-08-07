"use client";

/**
 * Sprint 36 — Contract tab.
 *
 * Two states:
 *   1. No contract: empty state with a single CTA "إنشاء عقد"
 *   2. Contract exists: read-only grid + edit + delete actions
 *
 * The ContractModal handles both create and edit. Delete uses
 * confirm() — same pattern as AllocationPanel.
 *
 * Why don't we show the contract value as editable inline?
 *   The user usually wants to look at the contract, not edit it
 *   every time they open the tab. The grid view is a quick
 *   reference; explicit "تعديل" / "حذف" buttons keep destructive
 *   actions deliberate.
 */
import { useEffect, useState } from "react";
import {
  Loader2,
  Pencil,
  Trash2,
  FileSignature,
  Calendar,
  DollarSign,
  Percent,
  Hash,
  AlertCircle,
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate, cn } from "@/lib/utils";
import ContractModal, { type ContractDto } from "./ContractModal";

interface Props {
  projectId: string;
  /** When parent knows there's a contract, it can pass it in
   *  (avoids an extra fetch on first render). Otherwise we
   *  fetch on mount. */
  initialContract: ContractDto | null;
  onContractChange?: (c: ContractDto | null) => void;
}

export default function ContractTab({ projectId, initialContract, onContractChange }: Props) {
  const { activeCompany } = useAuth();
  const [contract, setContract] = useState<ContractDto | null>(initialContract);
  const [loading, setLoading] = useState(initialContract == null);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [deleting, setDeleting] = useState(false);

  const load = async () => {
    if (!activeCompany) return;
    setLoading(true);
    setError(null);
    try {
      // The backend returns 404 when no contract exists. The axios
      // call rejects with err.response.status === 404 — that's our
      // "no contract" signal, not an error to display.
      const res = await api.get(`/projects/${projectId}/contract`);
      setContract(res.data);
      onContractChange?.(res.data);
    } catch (err: any) {
      if (err?.response?.status === 404) {
        setContract(null);
        onContractChange?.(null);
      } else {
        setError(getErrorMessage(err));
      }
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    // Only fetch if the parent didn't already give us a contract.
    if (initialContract == null) {
      load();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, activeCompany?.id]);

  const handleSaved = (c: ContractDto) => {
    setContract(c);
    onContractChange?.(c);
  };

  const handleDelete = async () => {
    if (!contract) return;
    if (!confirm("سيتم حذف العقد وكل المستخلصات المرتبطة به. متأكد؟")) return;
    setDeleting(true);
    setError(null);
    try {
      await api.delete(`/contracts/${contract.id}`);
      setContract(null);
      onContractChange?.(null);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setDeleting(false);
    }
  };

  if (loading) {
    return (
      <div className="card flex items-center justify-center py-12 text-gray-500 gap-2">
        <Loader2 className="animate-spin" size={20} />
        جاري التحميل...
      </div>
    );
  }

  if (error) {
    return (
      <div className="card border-red-200 bg-red-50 text-red-700 text-sm flex items-start gap-2">
        <AlertCircle size={16} className="mt-0.5 shrink-0" />
        <span>{error}</span>
      </div>
    );
  }

  // ── No contract: empty state ─────────────────────────────────────
  if (!contract) {
    return (
      <>
        <div className="card text-center py-12">
          <FileSignature size={40} className="mx-auto text-gray-300 mb-3" />
          <p className="text-gray-600 mb-1">لا يوجد عقد مسجّل لهذا المشروع</p>
          <p className="text-xs text-gray-500 mb-4">
            أضف عقداً لتفعيل المستخلصات وكشف حساب العميل
          </p>
          <button type="button" onClick={() => setEditing(true)} className="btn-primary">
            <FileSignature size={16} />
            إنشاء عقد
          </button>
        </div>
        <ContractModal
          open={editing}
          onClose={() => setEditing(false)}
          onSaved={handleSaved}
          projectId={projectId}
          contract={null}
        />
      </>
    );
  }

  // ── Contract exists: read view + actions ─────────────────────────
  return (
    <>
      <div className="space-y-3">
        {/* Header card */}
        <div className="card">
          <div className="flex items-start justify-between flex-wrap gap-2">
            <div>
              <h3 className="font-semibold flex items-center gap-2">
                <FileSignature size={16} className="text-primary-600" />
                العقد
              </h3>
              {contract.contractNumber && (
                <p className="text-xs text-gray-500 mt-0.5" dir="ltr">
                  رقم العقد: {contract.contractNumber}
                </p>
              )}
            </div>
            <div className="flex gap-2">
              <button
                type="button"
                onClick={() => setEditing(true)}
                className="btn-secondary"
                title="تعديل"
              >
                <Pencil size={14} />
                <span className="hidden sm:inline">تعديل</span>
              </button>
              <button
                type="button"
                onClick={handleDelete}
                disabled={deleting}
                className="btn-danger"
                title="حذف"
              >
                {deleting ? (
                  <Loader2 className="animate-spin" size={14} />
                ) : (
                  <Trash2 size={14} />
                )}
                <span className="hidden sm:inline">حذف</span>
              </button>
            </div>
          </div>

          <div className="mt-4 grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-y-3 gap-x-6 text-sm">
            <Field
              icon={DollarSign}
              label="قيمة العقد"
              value={`${formatNumber(contract.contractValue)} د.ل`}
            />
            <Field
              icon={Percent}
              label="نسبة المقدمة"
              value={`${formatNumber(contract.advancePercent)}%`}
            />
            <Field
              icon={Percent}
              label="نسبة الاحتجاز"
              value={`${formatNumber(contract.retentionPercent)}%`}
            />
            <Field
              icon={Hash}
              label="الاحتجاز من مستخلص رقم"
              value={String(contract.retentionStartBilling)}
            />
            <Field
              icon={Calendar}
              label="تاريخ بداية العقد"
              value={contract.startDate ? formatDate(contract.startDate) : "—"}
            />
            <Field
              icon={Calendar}
              label="تاريخ نهاية العقد"
              value={contract.endDate ? formatDate(contract.endDate) : "—"}
            />
          </div>

          {contract.notes && (
            <div className="mt-4 pt-3 border-t border-gray-100">
              <p className="text-xs text-gray-500 mb-1">ملاحظات</p>
              <p className="text-sm text-gray-700 whitespace-pre-wrap">
                {contract.notes}
              </p>
            </div>
          )}

          <div className="mt-4 pt-3 border-t border-gray-100 text-xs text-gray-500 flex items-center gap-3 flex-wrap">
            <span>تاريخ الإنشاء: {formatDate(contract.createdAt)}</span>
            {contract.updatedAt && (
              <span>آخر تحديث: {formatDate(contract.updatedAt)}</span>
            )}
          </div>
        </div>
      </div>
      <ContractModal
        open={editing}
        onClose={() => setEditing(false)}
        onSaved={handleSaved}
        projectId={projectId}
        contract={contract}
      />
    </>
  );
}

function Field({
  icon: Icon,
  label,
  value,
}: {
  icon: any;
  label: string;
  value: string;
}) {
  return (
    <div className="flex items-start gap-2">
      <Icon size={14} className="text-gray-400 mt-0.5 shrink-0" />
      <div className="min-w-0">
        <p className="text-xs text-gray-500">{label}</p>
        <p className="font-medium truncate" dir="ltr">
          {value}
        </p>
      </div>
    </div>
  );
}

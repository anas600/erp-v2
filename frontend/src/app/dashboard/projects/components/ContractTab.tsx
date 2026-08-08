"use client";

/**
 * Sprint 38 — Contract tab (now with 2 sub-tabs: contract + BOQ).
 *
 * Sub-tabs:
 *   1. "بيانات العقد" — the original Sprint 36 contract view
 *      (value, advance %, retention %, dates)
 *   2. "بنود العقد (BOQ)" — list of line items, with add/edit/
 *      delete, import from Excel, paste from clipboard, reorder
 *
 * Why sub-tabs and not separate top-level tabs?
 *   BOQ only makes sense once a contract exists. By keeping it
 *   under the contract tab, the user opens one tab and finds
 *   everything related to the contract: terms + line items.
 *   One URL state, one place to maintain.
 *
 * The "effective contract value" panel sits at the top of the
 * sub-tabs and shows the value AFTER approved variations:
 *   contract_value + sum(approved variation net amounts)
 *   = effective_value
 */
import { useEffect, useState, useCallback } from "react";
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
  Ruler,
  Plus,
  FileSpreadsheet,
  Clipboard,
  TrendingUp,
  TrendingDown,
  FileText,
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate, cn } from "@/lib/utils";
import ContractModal, { type ContractDto } from "./ContractModal";
import LineItemModal, { type LineItemDto } from "./LineItemModal";
import LineItemRow from "./LineItemRow";
import ExcelImportModal from "./ExcelImportModal";

type SubTabId = "info" | "boq";

interface Props {
  projectId: string;
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
  const [subTab, setSubTab] = useState<SubTabId>("info");

  const load = async () => {
    if (!activeCompany) return;
    setLoading(true);
    setError(null);
    try {
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
      <div className="card flex items-center justify-center py-12 text-ink-muted gap-2">
        <Loader2 className="animate-spin" size={20} />
        جاري التحميل...
      </div>
    );
  }

  if (error && !contract) {
    return (
      <div className="card border-red-200 bg-red-50 text-red-700 text-sm flex items-start gap-2">
        <AlertCircle size={16} className="mt-0.5 shrink-0" />
        <span>{error}</span>
      </div>
    );
  }

  // ── No contract: empty state ────────────────────────────────
  if (!contract) {
    return (
      <>
        <div className="card text-center py-12">
          <FileSignature size={40} className="mx-auto text-ink-subtle mb-3" />
          <p className="text-ink-muted mb-1">لا يوجد عقد مسجّل لهذا المشروع</p>
          <p className="text-xs text-ink-muted mb-4">
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

  // ── Contract exists: sub-tabs ──────────────────────────────
  return (
    <>
      <div className="space-y-3">
        {/* Effective value panel — always shown above the sub-tabs */}
        <EffectiveValuePanel contract={contract} />

        {/* Sub-tab bar */}
        <div className="border-b border-edge -mx-1 px-1">
          <div className="flex gap-1">
            <SubTab
              id="info"
              active={subTab === "info"}
              onClick={() => setSubTab("info")}
              icon={<FileSignature size={14} />}
              label="بيانات العقد"
            />
            <SubTab
              id="boq"
              active={subTab === "boq"}
              onClick={() => setSubTab("boq")}
              icon={<Ruler size={14} />}
              label="بنود العقد (BOQ)"
            />
          </div>
        </div>

        {error && (
          <div className="card border-red-200 bg-red-50 text-red-700 text-sm flex items-start gap-2">
            <AlertCircle size={16} className="mt-0.5 shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {subTab === "info" && (
          <ContractInfoCard
            contract={contract}
            onEdit={() => setEditing(true)}
            onDelete={handleDelete}
            deleting={deleting}
          />
        )}

        {subTab === "boq" && <BoqPanel contract={contract} />}
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

// ============================================================
// Effective value panel — shows original + variations + effective
// ============================================================
interface EffectiveValueResponse {
  contractId: string;
  contractValue: number;
  approvedAdditions: number;
  approvedDeductions: number;
  netVariations: number;
  effectiveValue: number;
  approvedVariationsCount: number;
}

function EffectiveValuePanel({ contract }: { contract: ContractDto }) {
  const [data, setData] = useState<EffectiveValueResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get(`/contracts/${contract.id}/effective-value`);
      setData(res.data);
    } catch (err: any) {
      // 404 is OK — backend may not have variations yet
      if (err?.response?.status === 404) {
        setData({
          contractId: contract.id,
          contractValue: contract.contractValue,
          approvedAdditions: 0,
          approvedDeductions: 0,
          netVariations: 0,
          effectiveValue: contract.contractValue,
          approvedVariationsCount: 0,
        });
      } else {
        setError(getErrorMessage(err));
      }
    } finally {
      setLoading(false);
    }
  }, [contract.id, contract.contractValue]);

  useEffect(() => {
    load();
  }, [load]);

  // Listen for cross-component refresh hints via window event
  // (VariationTab fires this when a variation is approved/rejected)
  useEffect(() => {
    const onRefresh = (e: Event) => {
      const detail = (e as CustomEvent).detail;
      if (!detail || detail.contractId === contract.id) load();
    };
    window.addEventListener("contract-effective-value:refresh", onRefresh);
    return () =>
      window.removeEventListener("contract-effective-value:refresh", onRefresh);
  }, [load, contract.id]);

  if (loading) {
    return (
      <div className="card flex items-center justify-center py-6 text-ink-muted gap-2 text-sm">
        <Loader2 className="animate-spin" size={16} />
        جاري حساب القيمة الفعّالة...
      </div>
    );
  }
  if (error && !data) {
    return (
      <div className="card border-red-200 bg-red-50 text-red-700 text-sm">
        {error}
      </div>
    );
  }
  if (!data) return null;

  const net = data.netVariations;
  return (
    <div className="card border-primary-200 bg-primary-50 dark:bg-primary-900/20">
      <div className="flex items-center gap-2 text-sm font-semibold text-primary-800 mb-2">
        <DollarSign size={16} />
        القيمة الفعّالة للعقد
      </div>
      <div className="space-y-1 text-sm">
        <div className="flex items-center justify-between">
          <span className="text-ink-muted">قيمة العقد الأصلية</span>
          <span className="font-mono" dir="ltr">
            {formatNumber(data.contractValue)} د.ل
          </span>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-ink-muted flex items-center gap-1">
            <TrendingUp size={12} className="text-green-600" />
            أوامر التغيير المعتمدة (إضافات)
          </span>
          <span className="font-mono text-green-700" dir="ltr">
            +{formatNumber(data.approvedAdditions)}
          </span>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-ink-muted flex items-center gap-1">
            <TrendingDown size={12} className="text-red-600" />
            أوامر التغيير المعتمدة (خصومات)
          </span>
          <span className="font-mono text-red-700" dir="ltr">
            -{formatNumber(data.approvedDeductions)}
          </span>
        </div>
        <div className="border-t border-primary-200 my-1" />
        <div className="flex items-center justify-between">
          <span className="font-semibold text-primary-900">القيمة الفعّالة</span>
          <span
            className="font-mono text-lg font-bold text-primary-900"
            dir="ltr"
          >
            {formatNumber(data.effectiveValue)} د.ل
          </span>
        </div>
        {data.approvedVariationsCount > 0 && (
          <div className="text-[10px] text-ink-muted text-left" dir="ltr">
            ({data.approvedVariationsCount} أمر تغيير معتمد)
          </div>
        )}
        {net !== 0 && (
          <div className="text-xs text-ink-muted text-center pt-1">
            صافي أثر أوامر التغيير:{" "}
            <span
              dir="ltr"
              className={cn(
                "font-mono font-semibold",
                net > 0 ? "text-green-700" : "text-red-700"
              )}
            >
              {net > 0 ? "+" : ""}
              {formatNumber(net)} د.ل
            </span>
          </div>
        )}
      </div>
    </div>
  );
}

// ============================================================
// Sub-tab button
// ============================================================
function SubTab({
  active,
  onClick,
  icon,
  label,
}: {
  id: string;
  active: boolean;
  onClick: () => void;
  icon: React.ReactNode;
  label: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        "flex items-center gap-1 px-3 py-2 text-sm font-medium border-b-2 -mb-px transition-colors",
        active
          ? "border-primary-600 text-primary-700"
          : "border-transparent text-ink-muted hover:text-ink-muted"
      )}
    >
      {icon}
      {label}
    </button>
  );
}

// ============================================================
// Contract info card (the original Sprint 36 view)
// ============================================================
function ContractInfoCard({
  contract,
  onEdit,
  onDelete,
  deleting,
}: {
  contract: ContractDto;
  onEdit: () => void;
  onDelete: () => void;
  deleting: boolean;
}) {
  return (
    <div className="card">
      <div className="flex items-start justify-between flex-wrap gap-2">
        <div>
          <h3 className="font-semibold flex items-center gap-2">
            <FileSignature size={16} className="text-primary-600" />
            العقد
          </h3>
          {contract.contractNumber && (
            <p className="text-xs text-ink-muted mt-0.5" dir="ltr">
              رقم العقد: {contract.contractNumber}
            </p>
          )}
        </div>
        <div className="flex gap-2">
          <button type="button" onClick={onEdit} className="btn-secondary" title="تعديل">
            <Pencil size={14} />
            <span className="hidden sm:inline">تعديل</span>
          </button>
          <button
            type="button"
            onClick={onDelete}
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
        <Field icon={DollarSign} label="قيمة العقد" value={`${formatNumber(contract.contractValue)} د.ل`} />
        <Field icon={Percent} label="نسبة المقدمة" value={`${formatNumber(contract.advancePercent)}%`} />
        <Field icon={Percent} label="نسبة الاحتجاز" value={`${formatNumber(contract.retentionPercent)}%`} />
        <Field icon={Hash} label="الاحتجاز من مستخلص رقم" value={String(contract.retentionStartBilling)} />
        <Field icon={Calendar} label="تاريخ بداية العقد" value={contract.startDate ? formatDate(contract.startDate) : "—"} />
        <Field icon={Calendar} label="تاريخ نهاية العقد" value={contract.endDate ? formatDate(contract.endDate) : "—"} />
      </div>

      {contract.notes && (
        <div className="mt-4 pt-3 border-t border-edge">
          <p className="text-xs text-ink-muted mb-1">ملاحظات</p>
          <p className="text-sm text-ink-muted whitespace-pre-wrap">{contract.notes}</p>
        </div>
      )}

      <div className="mt-4 pt-3 border-t border-edge text-xs text-ink-muted flex items-center gap-3 flex-wrap">
        <span>تاريخ الإنشاء: {formatDate(contract.createdAt)}</span>
        {contract.updatedAt && (
          <span>آخر تحديث: {formatDate(contract.updatedAt)}</span>
        )}
      </div>
    </div>
  );
}

// ============================================================
// BOQ panel — line items list
// ============================================================
function BoqPanel({ contract }: { contract: ContractDto }) {
  const [items, setItems] = useState<LineItemDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<LineItemDto | null>(null);
  const [creating, setCreating] = useState(false);
  const [importing, setImporting] = useState(false);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get(`/contracts/${contract.id}/line-items`);
      setItems(res.data || []);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [contract.id]);

  useEffect(() => {
    load();
  }, [load]);

  // Cross-tab refresh (when a billing changes, refresh BOQ)
  useEffect(() => {
    const onRefresh = () => load();
    window.addEventListener("contract-line-items:refresh", onRefresh);
    return () => window.removeEventListener("contract-line-items:refresh", onRefresh);
  }, [load]);

  const handleSaved = (li: LineItemDto) => {
    setItems((prev) => {
      const exists = prev.find((x) => x.id === li.id);
      return exists
        ? prev.map((x) => (x.id === li.id ? li : x))
        : [...prev, li];
    });
  };

  const handleImported = (imported: LineItemDto[]) => {
    setItems((prev) => [...prev, ...imported]);
    // Also nudge sibling tabs to refresh
    window.dispatchEvent(
      new CustomEvent("contract-line-items:refresh", { detail: { contractId: contract.id } })
    );
  };

  const handleDelete = async (li: LineItemDto) => {
    if (li.billedQuantity > 0) {
      alert("لا يمكن حذف بند تم استخدامه في مستخلصات");
      return;
    }
    if (!confirm("سيتم حذف هذا البند نهائياً. متأكد؟")) return;
    setBusyId(li.id);
    setError(null);
    try {
      await api.delete(`/contracts/${contract.id}/line-items/${li.id}`);
      setItems((prev) => prev.filter((x) => x.id !== li.id));
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusyId(null);
    }
  };

  const handleMove = async (idx: number, dir: -1 | 1) => {
    const target = idx + dir;
    if (target < 0 || target >= items.length) return;
    const a = items[idx];
    const b = items[target];
    // Optimistic local reorder
    const next = items.slice();
    next[idx] = b;
    next[target] = a;
    setItems(next);
    try {
      // Backend reorders by swapping line numbers
      await api.post(`/contracts/${contract.id}/line-items/reorder`, {
        orderedIds: next.map((x) => x.id),
      });
    } catch (err) {
      // Revert on failure
      setError(getErrorMessage(err));
      load();
    }
  };

  const totals = items.reduce(
    (acc, i) => {
      acc.quantity += Number(i.quantity) || 0;
      acc.total += Number(i.totalPrice) || 0;
      acc.billed += Number(i.amountBilled) || 0;
      acc.remaining += Number(i.remainingQuantity) || 0;
      return acc;
    },
    { quantity: 0, total: 0, billed: 0, remaining: 0 }
  );

  const nextLineNumber = useCallback(() => {
    if (items.length === 0) return 1;
    return Math.max(...items.map((i) => i.lineNumber)) + 1;
  }, [items]);

  return (
    <div className="space-y-3">
      {/* Action bar */}
      <div className="card">
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div>
            <h3 className="font-semibold flex items-center gap-2">
              <Ruler size={16} className="text-primary-600" />
              بنود العقد (BOQ)
              {items.length > 0 && (
                <span className="text-xs text-ink-muted font-normal">
                  ({items.length})
                </span>
              )}
            </h3>
            <p className="text-xs text-ink-muted mt-0.5">
              البنود التفصيلية للعقد — تُستخدم كأساس للمستخلصات
            </p>
          </div>
          <div className="flex gap-2 flex-wrap">
            <button
              type="button"
              onClick={() => setImporting(true)}
              className="btn-secondary"
              title="استيراد من Excel"
            >
              <FileSpreadsheet size={14} />
              <span className="hidden sm:inline">استيراد من Excel</span>
            </button>
            <button
              type="button"
              onClick={() => {
                setImporting(true);
                // The ExcelImportModal will let the user pick "paste"
                // via its inner method tabs.
              }}
              className="btn-secondary"
              title="لصق من الحافظة"
            >
              <Clipboard size={14} />
              <span className="hidden sm:inline">لصق</span>
            </button>
            <button
              type="button"
              onClick={() => setCreating(true)}
              className="btn-primary"
            >
              <Plus size={16} />
              <span className="hidden sm:inline">بند جديد</span>
            </button>
          </div>
        </div>
      </div>

      {error && (
        <div className="p-3 bg-red-50 border border-red-200 rounded-md text-sm text-red-700 flex items-start gap-2">
          <AlertCircle size={16} className="mt-0.5 shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {loading ? (
        <div className="card flex items-center justify-center py-12 text-ink-muted gap-2">
          <Loader2 className="animate-spin" size={20} />
          جاري التحميل...
        </div>
      ) : items.length === 0 ? (
        <div className="card text-center text-ink-muted py-12 text-sm">
          لا توجد بنود في هذا العقد بعد.
          <div className="mt-3 flex gap-2 justify-center flex-wrap">
            <button
              type="button"
              onClick={() => setCreating(true)}
              className="btn-primary"
            >
              <Plus size={14} />
              إضافة أول بند
            </button>
            <button
              type="button"
              onClick={() => setImporting(true)}
              className="btn-secondary"
            >
              <FileSpreadsheet size={14} />
              استيراد من Excel
            </button>
          </div>
        </div>
      ) : (
        <>
          {/* Desktop table */}
          <div className="hidden md:block card p-0 overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-raised border-b border-edge">
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted w-32">#</th>
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوصف</th>
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوحدة</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">الكمية</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">سعر الوحدة</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">الإجمالي</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">المُنجَز</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">المتبقي</th>
                </tr>
              </thead>
              <tbody>
                {items.map((it, idx) => (
                  <LineItemRow
                    key={it.id}
                    item={it}
                    canDelete={it.billedQuantity <= 0 && busyId !== it.id}
                    canMoveUp={idx > 0}
                    canMoveDown={idx < items.length - 1}
                    onEdit={() => setEditing(it)}
                    onDelete={() => handleDelete(it)}
                    onMoveUp={() => handleMove(idx, -1)}
                    onMoveDown={() => handleMove(idx, 1)}
                  />
                ))}
                {/* Totals row */}
                <tr className="bg-raised font-semibold border-t-2 border-edge">
                  <td className="py-2 px-3 text-right" colSpan={3}>
                    الإجماليات
                  </td>
                  <td className="py-2 px-3 text-left font-mono" dir="ltr">
                    {formatNumber(totals.quantity, 3)}
                  </td>
                  <td className="py-2 px-3" />
                  <td className="py-2 px-3 text-left font-mono" dir="ltr">
                    {formatNumber(totals.total)}
                  </td>
                  <td className="py-2 px-3 text-left font-mono" dir="ltr">
                    {formatNumber(totals.billed)}
                  </td>
                  <td className="py-2 px-3 text-left font-mono" dir="ltr">
                    {formatNumber(totals.remaining, 3)}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>

          {/* Mobile cards */}
          <div className="md:hidden space-y-2">
            {items.map((it, idx) => (
              <LineItemRow
                key={it.id}
                item={it}
                canDelete={it.billedQuantity <= 0 && busyId !== it.id}
                canMoveUp={idx > 0}
                canMoveDown={idx < items.length - 1}
                onEdit={() => setEditing(it)}
                onDelete={() => handleDelete(it)}
                onMoveUp={() => handleMove(idx, -1)}
                onMoveDown={() => handleMove(idx, 1)}
                variant="mobile"
              />
            ))}
            {/* Totals card */}
            <div className="card bg-raised space-y-1 text-sm">
              <div className="flex items-center justify-between">
                <span className="text-ink-muted">إجمالي الكميات</span>
                <span className="font-mono font-semibold" dir="ltr">
                  {formatNumber(totals.quantity, 3)}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-ink-muted">إجمالي قيمة البنود</span>
                <span className="font-mono font-semibold" dir="ltr">
                  {formatNumber(totals.total)}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-ink-muted">إجمالي المفوتر</span>
                <span className="font-mono font-semibold" dir="ltr">
                  {formatNumber(totals.billed)}
                </span>
              </div>
              <div className="flex items-center justify-between">
                <span className="text-ink-muted">إجمالي المتبقي</span>
                <span className="font-mono font-semibold" dir="ltr">
                  {formatNumber(totals.remaining, 3)}
                </span>
              </div>
            </div>
          </div>
        </>
      )}

      <LineItemModal
        open={creating}
        onClose={() => setCreating(false)}
        onSaved={(li) => {
          handleSaved(li);
          setCreating(false);
        }}
        contractId={contract.id}
        lineItem={null}
        nextLineNumber={nextLineNumber()}
      />
      <LineItemModal
        open={!!editing}
        onClose={() => setEditing(null)}
        onSaved={(li) => {
          handleSaved(li);
          setEditing(null);
        }}
        contractId={contract.id}
        lineItem={editing}
      />
      <ExcelImportModal
        open={importing}
        onClose={() => setImporting(false)}
        onImported={handleImported}
        contractId={contract.id}
      />
    </div>
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
      <Icon size={14} className="text-ink-subtle mt-0.5 shrink-0" />
      <div className="min-w-0">
        <p className="text-xs text-ink-muted">{label}</p>
        <p className="font-medium truncate" dir="ltr">
          {value}
        </p>
      </div>
    </div>
  );
}

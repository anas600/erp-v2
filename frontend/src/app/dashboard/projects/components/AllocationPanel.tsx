"use client";

/**
 * Sprint 35 — Allocation panel.
 *
 * Two sections:
 *   1. Unallocated purchase invoices (the bucket of costs the
 *      user can tag to THIS project)
 *   2. Currently allocated to this project (with a "Remove" button
 *      so the user can fix misallocations)
 *
 * Why bulk + per-row?
 *   The supervisor typically sits down at end-of-week and says
 *   "allocate everything from supplier X this week to project Y".
 *   Per-row would mean 40 clicks. Bulk with a checkbox column is
 *   one selection pass + one click.
 *
 * Why not auto-allocate based on supplier or amount?
 *   Because that's a wrong-default. The rule has to be human-set
 *   per cost-center. The bulk-allocate UI is the right balance:
 *   fast for humans, deliberate enough that the numbers are right.
 */
import { useEffect, useState } from "react";
import { Loader2, AlertCircle, Trash2, CheckSquare, Square, FolderKanban } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate } from "@/lib/utils";

interface UnallocatedInvoice {
  id: string;
  invoiceNumber: string;
  invoiceDate: string;
  partyName: string;
  partyNameAr?: string;
  total: number;
}

interface AllocatedInvoice {
  id: string;
  invoiceNumber: string;
  invoiceDate: string;
  partyName: string;
  partyNameAr?: string;
  total: number;
}

interface Props {
  projectId: string;
  onChange?: () => void;
}

export default function AllocationPanel({ projectId, onChange }: Props) {
  const { activeCompany } = useAuth();
  const [unallocated, setUnallocated] = useState<UnallocatedInvoice[]>([]);
  const [allocated, setAllocated] = useState<AllocatedInvoice[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [success, setSuccess] = useState<string | null>(null);

  const load = async () => {
    if (!activeCompany) return;
    setLoading(true);
    setError(null);
    try {
      // We piggyback on the project P&L costs endpoint shape:
      //   GET /api/projects/{id}/costs  -> list of all allocated
      // For unallocated, we need a separate endpoint that
      // returns purchase invoices WHERE project_id IS NULL.
      // The backend team added GET /api/projects/{id}/costs and
      // an "unallocated" filter for the project picker.
      const [unallocRes, allocRes] = await Promise.all([
        api
          .get(`/invoices?companyId=${activeCompany.id}&invoiceType=purchase&unallocated=true&limit=200`)
          .catch(() => ({ data: [] })),
        api.get(`/projects/${projectId}/costs`).catch(() => ({ data: [] })),
      ]);
      setUnallocated(unallocRes.data || []);
      // The costs endpoint returns a mixed list (invoices + journal lines);
      // for the "currently allocated" table we only want invoices.
      const invoiceRows = (allocRes.data || []).filter(
        (r: any) => r.source === "invoice" || r.invoiceId || r.invoiceNumber
      );
      setAllocated(invoiceRows);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, activeCompany]);

  const toggle = (id: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  };

  const toggleAll = () => {
    if (selected.size === unallocated.length) {
      setSelected(new Set());
    } else {
      setSelected(new Set(unallocated.map((u) => u.id)));
    }
  };

  const allocate = async () => {
    if (selected.size === 0) return;
    setBusy(true);
    setError(null);
    try {
      const res = await api.post(`/projects/${projectId}/allocate-invoices`, {
        invoiceIds: Array.from(selected),
      });
      setSuccess(`تم تخصيص ${res.data?.allocatedCount ?? selected.size} فاتورة للمشروع`);
      setSelected(new Set());
      await load();
      onChange?.();
      setTimeout(() => setSuccess(null), 3000);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  const deallocate = async (invoiceId: string) => {
    if (!confirm("إلغاء تخصيص هذه الفاتورة من المشروع؟")) return;
    setBusy(true);
    setError(null);
    try {
      await api.post(`/projects/${projectId}/deallocate-invoices`, {
        invoiceIds: [invoiceId],
      });
      setSuccess("تم إلغاء التخصيص");
      await load();
      onChange?.();
      setTimeout(() => setSuccess(null), 3000);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="space-y-6">
      {error && (
        <div className="p-3 bg-red-50 border border-red-200 rounded-md text-sm text-red-700 flex items-start gap-2">
          <AlertCircle size={16} className="shrink-0 mt-0.5" />
          <span>{error}</span>
        </div>
      )}
      {success && (
        <div className="p-3 bg-green-50 border border-green-200 rounded-md text-sm text-green-700">
          {success}
        </div>
      )}

      {/* Section 1 — Unallocated purchase invoices */}
      <div className="card">
        <div className="flex items-center justify-between mb-3">
          <div>
            <h3 className="font-semibold flex items-center gap-2">
              <FolderKanban size={16} className="text-primary-600" />
              فواتير الشراء غير المخصصة
            </h3>
            <p className="text-xs text-gray-500 mt-0.5">
              حدد الفواتير التي تريد تحميلها على هذا المشروع
            </p>
          </div>
          <button
            type="button"
            onClick={allocate}
            disabled={selected.size === 0 || busy}
            className="btn-primary"
          >
            {busy ? <Loader2 className="animate-spin" size={14} /> : null}
            تخصيص المحدد ({selected.size})
          </button>
        </div>

        {loading ? (
          <div className="flex items-center justify-center py-8 text-gray-500 text-sm gap-2">
            <Loader2 className="animate-spin" size={16} />
            جاري التحميل...
          </div>
        ) : unallocated.length === 0 ? (
          <p className="text-sm text-gray-500 py-6 text-center">
            لا توجد فواتير شراء بدون تخصيص. كل الفواتير إما مُحمَّلة على مشروع أو لم تُرحَّل بعد.
          </p>
        ) : (
          <>
            {/* Desktop table */}
            <div className="hidden md:block overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-gray-200">
                    <th className="py-2 text-right w-8">
                      <button
                        type="button"
                        onClick={toggleAll}
                        className="text-gray-500 hover:text-primary-600"
                        aria-label="تحديد الكل"
                      >
                        {selected.size === unallocated.length && unallocated.length > 0 ? (
                          <CheckSquare size={16} />
                        ) : (
                          <Square size={16} />
                        )}
                      </button>
                    </th>
                    <th className="text-right py-2 font-semibold text-gray-600">رقم الفاتورة</th>
                    <th className="text-right py-2 font-semibold text-gray-600">التاريخ</th>
                    <th className="text-right py-2 font-semibold text-gray-600">المورّد</th>
                    <th className="text-left py-2 font-semibold text-gray-600">المبلغ</th>
                  </tr>
                </thead>
                <tbody>
                  {unallocated.map((inv) => (
                    <tr
                      key={inv.id}
                      className={`border-b border-gray-100 cursor-pointer hover:bg-gray-50 ${
                        selected.has(inv.id) ? "bg-primary-50" : ""
                      }`}
                      onClick={() => toggle(inv.id)}
                    >
                      <td className="py-2">
                        {selected.has(inv.id) ? (
                          <CheckSquare size={16} className="text-primary-600" />
                        ) : (
                          <Square size={16} className="text-gray-400" />
                        )}
                      </td>
                      <td className="py-2 font-mono text-xs">{inv.invoiceNumber}</td>
                      <td className="py-2">{formatDate(inv.invoiceDate)}</td>
                      <td className="py-2">{inv.partyNameAr || inv.partyName}</td>
                      <td className="py-2 text-left font-mono" dir="ltr">{formatNumber(inv.total)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {/* Mobile cards */}
            <div className="md:hidden space-y-2">
              <button
                type="button"
                onClick={toggleAll}
                className="text-xs text-primary-600 hover:underline"
              >
                {selected.size === unallocated.length && unallocated.length > 0
                  ? "إلغاء تحديد الكل"
                  : "تحديد الكل"}
              </button>
              {unallocated.map((inv) => {
                const isSel = selected.has(inv.id);
                return (
                  <label
                    key={inv.id}
                    className={`block border rounded-md p-3 cursor-pointer ${
                      isSel ? "border-primary-500 bg-primary-50" : "border-gray-200"
                    }`}
                  >
                    <div className="flex items-start gap-2">
                      <input
                        type="checkbox"
                        checked={isSel}
                        onChange={() => toggle(inv.id)}
                        className="mt-1"
                      />
                      <div className="flex-1 min-w-0">
                        <div className="flex items-center justify-between">
                          <span className="font-mono text-xs text-gray-600">{inv.invoiceNumber}</span>
                          <span className="font-mono text-sm font-semibold" dir="ltr">
                            {formatNumber(inv.total)}
                          </span>
                        </div>
                        <div className="text-sm mt-1 truncate">{inv.partyNameAr || inv.partyName}</div>
                        <div className="text-xs text-gray-500 mt-0.5">{formatDate(inv.invoiceDate)}</div>
                      </div>
                    </div>
                  </label>
                );
              })}
            </div>
          </>
        )}
      </div>

      {/* Section 2 — Currently allocated */}
      <div className="card">
        <div className="mb-3">
          <h3 className="font-semibold flex items-center gap-2">
            <FolderKanban size={16} className="text-primary-600" />
            الفواتير المخصصة لهذا المشروع
          </h3>
          <p className="text-xs text-gray-500 mt-0.5">
            {allocated.length} فاتورة محمَّلة — اضغط "إزالة" لإلغاء التخصيص
          </p>
        </div>

        {loading ? (
          <div className="flex items-center justify-center py-8 text-gray-500 text-sm gap-2">
            <Loader2 className="animate-spin" size={16} />
            جاري التحميل...
          </div>
        ) : allocated.length === 0 ? (
          <p className="text-sm text-gray-500 py-6 text-center">
            لا توجد فواتير مخصصة لهذا المشروع بعد
          </p>
        ) : (
          <>
            {/* Desktop */}
            <div className="hidden md:block overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-gray-200">
                    <th className="text-right py-2 font-semibold text-gray-600">رقم الفاتورة</th>
                    <th className="text-right py-2 font-semibold text-gray-600">التاريخ</th>
                    <th className="text-right py-2 font-semibold text-gray-600">المورّد</th>
                    <th className="text-left py-2 font-semibold text-gray-600">المبلغ</th>
                    <th className="text-left py-2 font-semibold text-gray-600 w-16">إجراء</th>
                  </tr>
                </thead>
                <tbody>
                  {allocated.map((inv) => (
                    <tr key={inv.id} className="border-b border-gray-100">
                      <td className="py-2 font-mono text-xs">{inv.invoiceNumber}</td>
                      <td className="py-2">{formatDate(inv.invoiceDate)}</td>
                      <td className="py-2">{inv.partyNameAr || inv.partyName}</td>
                      <td className="py-2 text-left font-mono" dir="ltr">{formatNumber(inv.total)}</td>
                      <td className="py-2 text-left">
                        <button
                          type="button"
                          onClick={() => deallocate(inv.id)}
                          disabled={busy}
                          className="text-red-600 hover:bg-red-50 p-1 rounded"
                          title="إزالة"
                          aria-label="إزالة التخصيص"
                        >
                          <Trash2 size={14} />
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {/* Mobile cards */}
            <div className="md:hidden space-y-2">
              {allocated.map((inv) => (
                <div key={inv.id} className="border border-gray-200 rounded-md p-3">
                  <div className="flex items-start justify-between gap-2">
                    <div className="min-w-0 flex-1">
                      <div className="flex items-center justify-between">
                        <span className="font-mono text-xs text-gray-600">{inv.invoiceNumber}</span>
                        <span className="font-mono text-sm font-semibold" dir="ltr">
                          {formatNumber(inv.total)}
                        </span>
                      </div>
                      <div className="text-sm mt-1 truncate">{inv.partyNameAr || inv.partyName}</div>
                      <div className="text-xs text-gray-500 mt-0.5">{formatDate(inv.invoiceDate)}</div>
                    </div>
                    <button
                      type="button"
                      onClick={() => deallocate(inv.id)}
                      disabled={busy}
                      className="text-red-600 hover:bg-red-50 p-2 rounded shrink-0"
                      title="إزالة"
                      aria-label="إزالة التخصيص"
                    >
                      <Trash2 size={16} />
                    </button>
                  </div>
                </div>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );
}

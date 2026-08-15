"use client";

/**
 * Sprint 57 — 4-party Approvals Panel (لوحة الاعتمادات).
 *
 * Shown inside BillingsTab at the top. Shows the 4 approval rows
 * for the selected billing:
 *   1. المقاول    (contractor) — submitted
 *   2. الاستشاري  (consultant) — certified
 *   3. إدارة المشروعات (pmo) — verified
 *   4. المالك     (owner)     — approved
 *
 * Each row is one of 3 states:
 *   - pending  : grey dot
 *   - approved : green check + user + date
 *   - rejected : red x + reason
 *
 * Approve / Reject / Reset are admin-only (super_admin).
 *
 * When all 4 are approved, the panel shows a "Print Final" button
 * that opens the print view in a new tab.
 */
import { useEffect, useState } from "react";
import {
  Loader2, CheckCircle2, XCircle, Clock, RotateCcw, Printer, AlertCircle, X
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatDate, formatDateTime, cn } from "@/lib/utils";

export interface BillingApproval {
  id: string;
  companyId: string;
  billingId: string;
  role: string;            // contractor | consultant | pmo | owner
  roleLabel: string;       // Arabic
  approverUserId?: string | null;
  approverName?: string | null;
  status: string;          // pending | approved | rejected
  approvedAt?: string | null;
  rejectionReason?: string | null;
  notes?: string | null;
  createdAt: string;
  updatedAt?: string | null;
}

interface Props {
  projectId: string;
  billingId: string;
  onChange?: () => void;
}

const STATUS_CONFIG: Record<string, { icon: any; color: string; label: string }> = {
  pending:  { icon: Clock,       color: "bg-slate-100 text-slate-700 border-slate-200",   label: "بانتظار الاعتماد" },
  approved: { icon: CheckCircle2, color: "bg-emerald-100 text-emerald-700 border-emerald-200", label: "معتمد" },
  rejected: { icon: XCircle,     color: "bg-red-100 text-red-700 border-red-200",       label: "مرفوض" },
};

const ROLE_ORDER: string[] = ["contractor", "consultant", "pmo", "owner"];

export default function ApprovalsPanel({ projectId, billingId, onChange }: Props) {
  const [approvals, setApprovals] = useState<BillingApproval[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null); // role currently being acted on

  // Reject modal state
  const [rejectRole, setRejectRole] = useState<string | null>(null);
  const [rejectReason, setRejectReason] = useState("");
  const [rejectNotes, setRejectNotes] = useState("");

  const load = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.get<BillingApproval[]>(
        `/projects/${projectId}/billings/${billingId}/approvals`
      );
      // Sort by ROLE_ORDER
      const sorted = [...res.data].sort(
        (a, b) => ROLE_ORDER.indexOf(a.role) - ROLE_ORDER.indexOf(b.role)
      );
      setApprovals(sorted);
    } catch (e) {
      setError(getErrorMessage(e));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [billingId]);

  const handleApprove = async (role: string) => {
    setBusy(role);
    setError(null);
    try {
      await api.post(`/projects/${projectId}/billings/${billingId}/approvals/${role}/approve`, {
        notes: null,
      });
      await load();
      onChange?.();
    } catch (e) {
      setError(getErrorMessage(e));
    } finally {
      setBusy(null);
    }
  };

  const handleReject = async () => {
    if (!rejectRole || !rejectReason.trim()) return;
    setBusy(rejectRole);
    setError(null);
    try {
      await api.post(`/projects/${projectId}/billings/${billingId}/approvals/${rejectRole}/reject`, {
        reason: rejectReason,
        notes: rejectNotes || null,
      });
      setRejectRole(null);
      setRejectReason("");
      setRejectNotes("");
      await load();
      onChange?.();
    } catch (e) {
      setError(getErrorMessage(e));
    } finally {
      setBusy(null);
    }
  };

  const handleReset = async (role: string) => {
    setBusy(role);
    setError(null);
    try {
      await api.delete(`/projects/${projectId}/billings/${billingId}/approvals/${role}`);
      await load();
      onChange?.();
    } catch (e) {
      setError(getErrorMessage(e));
    } finally {
      setBusy(null);
    }
  };

  const handlePrint = () => {
    window.open(`/print/billing/${billingId}`, "_blank");
  };

  if (loading) {
    return (
      <div className="card flex items-center justify-center py-8">
        <Loader2 className="animate-spin text-primary-600" size={20} />
        <span className="mr-2 text-ink-muted text-sm">جاري تحميل الاعتمادات...</span>
      </div>
    );
  }

  const allApproved = approvals.length === 4 && approvals.every(a => a.status === "approved");
  const anyRejected = approvals.some(a => a.status === "rejected");

  return (
    <div className="card">
      <div className="flex items-center justify-between mb-3">
        <h3 className="text-sm font-bold flex items-center gap-2">
          <CheckCircle2 size={16} className="text-primary-600" />
          الاعتمادات (4 أطراف)
        </h3>
        {allApproved && (
          <button
            type="button"
            onClick={handlePrint}
            className="btn-primary text-xs flex items-center gap-1"
          >
            <Printer size={14} />
            طباعة المسودة النهائية
          </button>
        )}
      </div>

      {error && (
        <div className="mb-3 flex items-center gap-2 border border-red-200 bg-red-50 text-red-700 text-sm rounded px-3 py-2">
          <AlertCircle size={14} />
          <span>{error}</span>
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
        {approvals.map((a) => {
          const cfg = STATUS_CONFIG[a.status] || STATUS_CONFIG.pending;
          const Icon = cfg.icon;
          const isBusy = busy === a.role;
          const canAct = !allApproved; // disable actions once all approved

          return (
            <div key={a.id} className={cn("border rounded p-3", cfg.color)}>
              <div className="flex items-start justify-between gap-2">
                <div className="flex-1">
                  <div className="flex items-center gap-2 mb-1">
                    <Icon size={16} />
                    <span className="font-bold text-sm">{a.roleLabel}</span>
                    <span className="text-xs">({a.status === "pending" ? "بانتظار" : a.status === "approved" ? "معتمد" : "مرفوض"})</span>
                  </div>

                  {a.status === "approved" && (
                    <div className="text-xs mt-1 space-y-0.5">
                      <div>
                        <span className="text-ink-muted">المعتمد: </span>
                        <span className="font-medium">{a.approverName || "(غير معروف)"}</span>
                      </div>
                      {a.approvedAt && (
                        <div>
                          <span className="text-ink-muted">التاريخ: </span>
                          {formatDateTime(a.approvedAt)}
                        </div>
                      )}
                      {a.notes && (
                        <div className="text-ink-muted mt-1 italic">{a.notes}</div>
                      )}
                    </div>
                  )}

                  {a.status === "rejected" && (
                    <div className="text-xs mt-1 space-y-0.5">
                      <div>
                        <span className="text-ink-muted">السبب: </span>
                        <span className="font-medium">{a.rejectionReason}</span>
                      </div>
                      {a.approverName && (
                        <div>
                          <span className="text-ink-muted">الرافض: </span>
                          {a.approverName}
                        </div>
                      )}
                    </div>
                  )}

                  {a.status === "pending" && (
                    <div className="text-xs text-ink-muted mt-1">
                      في انتظار اعتماد هذا الطرف
                    </div>
                  )}
                </div>
              </div>

              {canAct && (
                <div className="flex gap-1 mt-2 justify-end">
                  {a.status !== "approved" && (
                    <button
                      type="button"
                      onClick={() => handleApprove(a.role)}
                      disabled={isBusy}
                      className="text-xs px-2 py-1 rounded bg-emerald-600 text-white hover:bg-emerald-700 disabled:opacity-50 flex items-center gap-1"
                    >
                      {isBusy ? <Loader2 className="animate-spin" size={12} /> : <CheckCircle2 size={12} />}
                      اعتماد
                    </button>
                  )}
                  {a.status !== "rejected" && (
                    <button
                      type="button"
                      onClick={() => setRejectRole(a.role)}
                      disabled={isBusy}
                      className="text-xs px-2 py-1 rounded bg-red-100 text-red-700 hover:bg-red-200 disabled:opacity-50 flex items-center gap-1"
                    >
                      <XCircle size={12} />
                      رفض
                    </button>
                  )}
                  {a.status !== "pending" && (
                    <button
                      type="button"
                      onClick={() => handleReset(a.role)}
                      disabled={isBusy}
                      className="text-xs px-2 py-1 rounded bg-slate-100 text-slate-700 hover:bg-slate-200 disabled:opacity-50 flex items-center gap-1"
                    >
                      <RotateCcw size={12} />
                      إعادة
                    </button>
                  )}
                </div>
              )}
            </div>
          );
        })}
      </div>

      {anyRejected && !allApproved && (
        <div className="mt-3 text-xs text-red-700 flex items-center gap-1">
          <AlertCircle size={12} />
          يوجد رفض - يجب إعادة جميع الاعتمادات المرفوضة قبل المتابعة
        </div>
      )}

      {/* Reject modal */}
      {rejectRole && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/30 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-md p-6">
            <div className="flex items-center justify-between mb-4">
              <h3 className="text-lg font-bold">رفض اعتماد: {BillingApprovalRoles.find(r => r.value === rejectRole)?.label}</h3>
              <button
                type="button"
                onClick={() => { setRejectRole(null); setRejectReason(""); setRejectNotes(""); }}
                className="text-ink-muted hover:text-ink"
              >
                <X size={20} />
              </button>
            </div>
            <div className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">سبب الرفض <span className="text-red-600">*</span></label>
                <input
                  type="text"
                  value={rejectReason}
                  onChange={(e) => setRejectReason(e.target.value)}
                  className="w-full text-sm border border-edge rounded px-3 py-2"
                  placeholder="مثل: المقاسات غير مطابقة للمواصفات"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">ملاحظات (اختياري)</label>
                <textarea
                  value={rejectNotes}
                  onChange={(e) => setRejectNotes(e.target.value)}
                  className="w-full text-sm border border-edge rounded px-3 py-2"
                  rows={3}
                />
              </div>
            </div>
            <div className="mt-4 flex justify-end gap-2">
              <button
                type="button"
                onClick={() => { setRejectRole(null); setRejectReason(""); setRejectNotes(""); }}
                className="px-4 py-2 text-sm text-ink-muted hover:text-ink"
              >
                إلغاء
              </button>
              <button
                type="button"
                onClick={handleReject}
                disabled={!rejectReason.trim() || busy === rejectRole}
                className="px-4 py-2 text-sm bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 flex items-center gap-1"
              >
                {busy === rejectRole && <Loader2 className="animate-spin" size={12} />}
                رفض الاعتماد
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

const BillingApprovalRoles = [
  { value: "contractor", label: "المقاول" },
  { value: "consultant", label: "الاستشاري" },
  { value: "pmo", label: "إدارة المشروعات" },
  { value: "owner", label: "المالك" },
];

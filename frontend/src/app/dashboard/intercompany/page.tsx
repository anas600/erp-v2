"use client";

/**
 * Intercompany Pairs page.
 *
 * A "pair" is created when a sales or purchase invoice is posted with
 * `intercompanyCompanyId` set. The system automatically creates a mirror
 * invoice in the sister company and links the two via an intercompany_pairs
 * row. Both halves share the same `intercompany_pair_id` on their journal
 * entries so consolidation can find and eliminate them.
 *
 * Status flow:
 *   pending  → primary posted, mirror pending (rare, only if mirror failed)
 *   posted   → both halves posted
 *   reversed → both sides reversed (Reverse button does this for both)
 *
 * Soft-reverse (not delete) preserves audit trail. GAAP/IFRS rule.
 */

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { ArrowRightLeft, Loader2, X, Eye, Undo2 } from "lucide-react";

interface IntercompanyPair {
  id: string;
  primaryInvoiceId: string;
  mirrorInvoiceId: string | null;
  primaryCompanyId: string;
  mirrorCompanyId: string;
  amount: number;
  currency: string;
  status: string;
  createdAt: string;
}

interface InvoiceSummary {
  id: string;
  invoiceNumber: string;
  companyId: string;
  partyName: string;
  total: number;
  status: string;
  postedAt: string | null;
}

interface PairDetails extends IntercompanyPair {
  primaryInvoice: InvoiceSummary;
  mirrorInvoice: InvoiceSummary | null;
}

export default function IntercompanyPage() {
  const { activeCompany } = useAuth();
  const [pairs, setPairs] = useState<IntercompanyPair[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<PairDetails | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(
        `/intercompany/pairs?companyId=${activeCompany.id}`
      );
      setPairs(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeCompany]);

  const openDetails = async (pair: IntercompanyPair) => {
    try {
      setSubmitting(true);
      const res = await api.get(`/intercompany/pairs/${pair.id}`);
      setSelected(res.data);
    } catch (err) {
      alert(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const reverse = async (pair: IntercompanyPair) => {
    if (
      !confirm(
        `هل تريد عكس المعاملة بين الشركات؟ سيتم إنشاء قيود عكسية في كلتا الشركتين.`
      )
    )
      return;
    try {
      setSubmitting(true);
      await api.post(`/intercompany/pairs/${pair.id}/reverse`);
      setSelected(null);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const fmt = (n: number) => n.toLocaleString("ar-LY", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const dateFmt = (s: string) => new Date(s).toLocaleDateString("ar-LY");

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <ArrowRightLeft size={24} className="text-primary-600" />
            المعاملات بين الشركات
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            فواتير بين شركات المجموعة — ينشئ النظام قيداً في كلتا الشركتين
          </p>
        </div>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>
      )}

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>رقم الفاتورة</th>
                <th>المبلغ</th>
                <th>العملة</th>
                <th>الحالة</th>
                <th>التاريخ</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {pairs.map((p) => (
                <tr key={p.id}>
                  <td className="font-mono font-semibold">{p.primaryInvoiceId.substring(0, 8)}…</td>
                  <td className="font-mono" dir="ltr">{fmt(p.amount)}</td>
                  <td>{p.currency}</td>
                  <td>
                    {p.status === "posted" && (
                      <span className="badge badge-success">مرحّل</span>
                    )}
                    {p.status === "pending" && (
                      <span className="badge badge-warning">معلّق</span>
                    )}
                    {p.status === "reversed" && (
                      <span className="badge badge-secondary">معكوس</span>
                    )}
                  </td>
                  <td>{dateFmt(p.createdAt)}</td>
                  <td>
                    <div className="flex items-center gap-1">
                      <button
                        onClick={() => openDetails(p)}
                        disabled={submitting}
                        className="text-primary-600 hover:bg-primary-50 p-1 rounded"
                        title="عرض التفاصيل"
                      >
                        <Eye size={14} />
                      </button>
                      {p.status === "posted" && (
                        <button
                          onClick={() => reverse(p)}
                          disabled={submitting}
                          className="text-red-600 hover:bg-red-50 p-1 rounded"
                          title="عكس"
                        >
                          <Undo2 size={14} />
                        </button>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
              {pairs.length === 0 && (
                <tr>
                  <td colSpan={6} className="text-center text-gray-500 py-6">
                    لا توجد معاملات بين الشركات
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      {selected && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-2xl p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">تفاصيل المعاملة بين الشركات</h2>
              <button
                onClick={() => setSelected(null)}
                className="text-gray-400 hover:text-gray-600"
              >
                <X size={20} />
              </button>
            </div>

            <div className="space-y-4">
              <div className="grid grid-cols-2 gap-4 text-sm">
                <div>
                  <div className="text-gray-500">المبلغ</div>
                  <div className="font-mono font-semibold text-lg" dir="ltr">
                    {fmt(selected.amount)} {selected.currency}
                  </div>
                </div>
                <div>
                  <div className="text-gray-500">الحالة</div>
                  <div>
                    {selected.status === "posted" && (
                      <span className="badge badge-success">مرحّل</span>
                    )}
                    {selected.status === "reversed" && (
                      <span className="badge badge-secondary">معكوس</span>
                    )}
                  </div>
                </div>
              </div>

              {selected.primaryInvoice && (
                <div className="p-3 bg-blue-50 rounded-md">
                  <div className="text-sm font-semibold mb-2">الفاتورة الأساسية</div>
                  <div className="grid grid-cols-2 gap-2 text-sm">
                    <div>
                      <span className="text-gray-600">الرقم: </span>
                      <span className="font-mono">{selected.primaryInvoice.invoiceNumber}</span>
                    </div>
                    <div>
                      <span className="text-gray-600">الطرف: </span>
                      {selected.primaryInvoice.partyName}
                    </div>
                    <div>
                      <span className="text-gray-600">الإجمالي: </span>
                      <span className="font-mono" dir="ltr">
                        {fmt(selected.primaryInvoice.total)}
                      </span>
                    </div>
                    <div>
                      <span className="text-gray-600">الحالة: </span>
                      {selected.primaryInvoice.status}
                    </div>
                  </div>
                </div>
              )}

              {selected.mirrorInvoice && (
                <div className="p-3 bg-green-50 rounded-md">
                  <div className="text-sm font-semibold mb-2">الفاتورة المرآة (الشركة الشقيقة)</div>
                  <div className="grid grid-cols-2 gap-2 text-sm">
                    <div>
                      <span className="text-gray-600">الرقم: </span>
                      <span className="font-mono">{selected.mirrorInvoice.invoiceNumber}</span>
                    </div>
                    <div>
                      <span className="text-gray-600">الطرف: </span>
                      {selected.mirrorInvoice.partyName}
                    </div>
                    <div>
                      <span className="text-gray-600">الإجمالي: </span>
                      <span className="font-mono" dir="ltr">
                        {fmt(selected.mirrorInvoice.total)}
                      </span>
                    </div>
                    <div>
                      <span className="text-gray-600">الحالة: </span>
                      {selected.mirrorInvoice.status}
                    </div>
                  </div>
                </div>
              )}

              {selected.status === "posted" && (
                <button
                  onClick={() => reverse(selected)}
                  disabled={submitting}
                  className="btn-primary w-full bg-red-600 hover:bg-red-700"
                >
                  {submitting ? "جاري العكس..." : "عكس المعاملة في الشركتين"}
                </button>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

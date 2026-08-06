"use client";

import { useEffect, useState, useCallback } from "react";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import { Plus, FileText, Loader2, Trash2, Send, Inbox, CheckCircle, X } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface ReceiptVoucher {
  id: string;
  voucherNumber: string;
  voucherDate: string;
  contactId: string;
  contactName: string;
  contactCode: string;
  amount: number;
  paymentMethod: string;
  status: string;
  reference?: string;
  narration?: string;
  postedAt?: string;
  // Sprint 25 — denormalised link to the invoice this receipt settled.
  invoiceId?: string | null;
  invoiceNumber?: string | null;
  invoiceStatus?: string | null;
}

interface Contact {
  id: string;
  code: string;
  name: string;
  type: string;
}

interface UnpaidInvoice {
  id: string;
  invoiceNumber: string;
  invoiceType: string;
  invoiceDate: string;
  partyName: string;
  total: number;
  amountPaid: number;
  outstanding: number;
  status: string;
}

const PAYMENT_METHODS: Record<string, string> = {
  cash: "نقدي",
  bank: "بنكي",
  check: "شيك",
};

export default function ReceiptsPage() {
  const { activeCompany } = useAuth();
  const [vouchers, setVouchers] = useState<ReceiptVoucher[]>([]);
  const [customers, setCustomers] = useState<Contact[]>([]);
  const [unpaidInvoices, setUnpaidInvoices] = useState<UnpaidInvoice[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [success, setSuccess] = useState<string | null>(null);

  const [form, setForm] = useState({
    voucherDate: new Date().toISOString().slice(0, 10),
    contactId: "",
    amount: 0,
    paymentMethod: "cash",
    invoiceId: "",          // Sprint 25 — pre-link to a sales invoice
    reference: "",
    narration: "",
  });

  const load = useCallback(async () => {
    if (!activeCompany) return;
    setLoading(true);
    try {
      const [vRes, cRes] = await Promise.all([
        api.get(`/receipts?companyId=${activeCompany.id}`),
        api.get(`/contacts?companyId=${activeCompany.id}&type=customer`),
      ]);
      setVouchers(vRes.data);
      setCustomers(cRes.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [activeCompany]);

  // Sprint 25 — When the user picks a customer, fetch their unpaid
  // sales invoices to populate the invoice dropdown. If exactly one
  // invoice has total === form.amount, pre-select it.
  useEffect(() => {
    if (!activeCompany || !form.contactId) {
      setUnpaidInvoices([]);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const res = await api.get(
          `/invoices/unpaid?companyId=${activeCompany.id}&contactId=${form.contactId}&invoiceType=sales`
        );
        if (!cancelled) {
          setUnpaidInvoices(res.data || []);
          // Pre-select if exactly one invoice matches the amount
          // exactly (the same heuristic the backend auto-link uses).
          const exactMatch = (res.data || []).find(
            (inv: UnpaidInvoice) => Number(inv.total) === Number(form.amount)
          );
          if (exactMatch) {
            setForm((f) => (f.invoiceId ? f : { ...f, invoiceId: exactMatch.id }));
          }
        }
      } catch (err) {
        if (!cancelled) setError(getErrorMessage(err));
      }
    })();
    return () => { cancelled = true; };
  }, [activeCompany, form.contactId]);

  // Re-evaluate the auto-select when amount changes. If a different
  // invoice now matches the amount exactly, switch the selection.
  const amount = form.amount;
  useEffect(() => {
    if (!unpaidInvoices.length || !amount) return;
    const exact = unpaidInvoices.find((inv) => Number(inv.total) === Number(amount));
    if (exact) setForm((f) => ({ ...f, invoiceId: exact.id }));
    // Don't auto-clear — if the user manually picked one, leave it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [amount]);

  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    const i = setInterval(load, 30_000);
    return () => clearInterval(i);
  }, [load]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    if (!form.contactId) { setError("اختر العميل"); return; }
    if (form.amount <= 0) { setError("المبلغ يجب أن يكون أكبر من صفر"); return; }
    setSubmitting(true);
    setError(null);
    try {
      const res = await api.post("/receipts", {
        companyId: activeCompany.id,
        voucherDate: form.voucherDate,
        contactId: form.contactId,
        amount: form.amount,
        paymentMethod: form.paymentMethod,
        invoiceId: form.invoiceId || null,  // Sprint 25
        reference: form.reference || null,
        narration: form.narration || null,
      });
      setSuccess(`تم حفظ السند ${res.data.voucherNumber} كمسودة`);
      setForm({
        voucherDate: new Date().toISOString().slice(0, 10),
        contactId: "", amount: 0, paymentMethod: "cash", invoiceId: "",
        reference: "", narration: "",
      });
      setUnpaidInvoices([]);
      await load();
      setShowForm(false);
      setTimeout(() => setSuccess(null), 3000);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const post = async (id: string) => {
    if (!confirm("ترحيل هذا السند؟ سيُنشأ قيد يومية وينتظر اعتماد المحاسب.")) return;
    try {
      await api.post(`/receipts/${id}/post`);
      setSuccess("تم ترحيل السند وإنشاء القيد");
      await load();
      setTimeout(() => setSuccess(null), 3000);
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const del = async (id: string) => {
    if (!confirm("حذف هذه المسودة؟")) return;
    try {
      await api.delete(`/receipts/${id}`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Inbox size={24} className="text-green-600" />
            سندات القبض
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            تسجيل تحصيلات العملاء — تحويلها إلى قيود محاسبية
          </p>
        </div>
        <button onClick={() => setShowForm(true)} className="btn-primary">
          <Plus size={18} />
          سند قبض جديد
        </button>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}
      {success && (
        <div className="mb-4 p-3 bg-green-50 text-green-700 rounded-md text-sm flex items-center gap-2">
          <CheckCircle size={16} /> {success}
        </div>
      )}

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : vouchers.length === 0 ? (
          <div className="text-center py-12 text-gray-500">
            <Inbox size={48} className="mx-auto mb-3 text-gray-300" />
            <p>لا توجد سندات قبض</p>
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>رقم السند</th>
                <th>الفاتورة</th>     {/* Sprint 25 — linked invoice */}
                <th>التاريخ</th>
                <th>العميل</th>
                <th>طريقة الدفع</th>
                <th>المرجع</th>
                <th>المبلغ</th>
                <th>الحالة</th>
                <th>الإجراءات</th>
              </tr>
            </thead>
            <tbody>
              {vouchers.map((v) => (
                <tr key={v.id}>
                  <td className="font-mono font-semibold">{v.voucherNumber}</td>
                  <td className="text-sm">
                    {v.invoiceNumber ? (
                      <span className="font-mono text-blue-700">{v.invoiceNumber}</span>
                    ) : (
                      <span className="text-gray-400">—</span>
                    )}
                    {v.invoiceStatus === "paid" && (
                      <span className="badge badge-success mr-1">مسددة</span>
                    )}
                  </td>
                  <td>{formatDate(v.voucherDate)}</td>
                  <td>{v.contactName} <span className="text-xs text-gray-500">({v.contactCode})</span></td>
                  <td className="text-sm">{PAYMENT_METHODS[v.paymentMethod] || v.paymentMethod}</td>
                  <td className="text-sm text-gray-600">{v.reference || "—"}</td>
                  <td className="font-mono" dir="ltr">{formatNumber(v.amount)}</td>
                  <td>
                    {v.status === "posted" && <span className="badge badge-success">مرحّل</span>}
                    {v.status === "draft" && <span className="badge badge-warning">مسودة</span>}
                    {v.status === "void" && <span className="badge badge-danger">مُلغى</span>}
                  </td>
                  <td>
                    <div className="flex items-center gap-1">
                      {v.status === "draft" && (
                        <>
                          <button onClick={() => post(v.id)}
                            className="text-green-600 hover:bg-green-50 p-1 rounded" title="ترحيل">
                            <Send size={14} />
                          </button>
                          <button onClick={() => del(v.id)}
                            className="text-red-600 hover:bg-red-50 p-1 rounded" title="حذف">
                            <Trash2 size={14} />
                          </button>
                        </>
                      )}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-2xl p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold flex items-center gap-2">
                <FileText size={20} className="text-green-600" /> سند قبض جديد
              </h2>
              <button onClick={() => setShowForm(false)} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submit} className="space-y-3">
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">التاريخ *</label>
                  <input type="date" className="input"
                    value={form.voucherDate}
                    onChange={(e) => setForm({ ...form, voucherDate: e.target.value })} required />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">العميل *</label>
                  <select className="input" value={form.contactId}
                    onChange={(e) => setForm({ ...form, contactId: e.target.value, invoiceId: "" })} required>
                    <option value="">— اختر عميل —</option>
                    {customers.map((c) => (
                      <option key={c.id} value={c.id}>{c.code} - {c.name}</option>
                    ))}
                  </select>
                </div>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">المبلغ (د.ل) *</label>
                  <input type="number" step="0.01" min="0.01" className="input"
                    value={form.amount || ""}
                    onChange={(e) => setForm({ ...form, amount: Number(e.target.value) })}
                    dir="ltr" required />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">طريقة الدفع *</label>
                  <select className="input" value={form.paymentMethod}
                    onChange={(e) => setForm({ ...form, paymentMethod: e.target.value })}>
                    <option value="cash">نقدي (صندوق)</option>
                    <option value="bank">بنكي (تحويل)</option>
                    <option value="check">شيك</option>
                  </select>
                </div>
              </div>
              {/* Sprint 25 — invoice dropdown. Populated once a customer
                  is selected. Pre-selects an invoice whose total equals
                  the receipt amount (the exact-match heuristic the
                  backend auto-link also runs). */}
              <div>
                <label className="block text-sm font-medium mb-1">
                  الفاتورة
                  <span className="text-xs text-gray-500 mr-2">
                    (اختياري — للربط المباشر)
                  </span>
                </label>
                <select className="input" value={form.invoiceId}
                  onChange={(e) => setForm({ ...form, invoiceId: e.target.value })}
                  disabled={!form.contactId}>
                  <option value="">— بدون ربط (دفعة عامة) —</option>
                  {unpaidInvoices.map((inv) => (
                    <option key={inv.id} value={inv.id}>
                      {inv.invoiceNumber} — متبقي {formatNumber(inv.outstanding)} د.ل
                      {Number(inv.total) === Number(form.amount) ? " ✓ مطابق" : ""}
                    </option>
                  ))}
                </select>
                {unpaidInvoices.length === 0 && form.contactId && (
                  <p className="text-xs text-gray-500 mt-1">
                    لا توجد فواتير غير مسددة لهذا العميل
                  </p>
                )}
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">المرجع / البيان</label>
                <input className="input" value={form.reference}
                  onChange={(e) => setForm({ ...form, reference: e.target.value })}
                  placeholder="مثال: سداد جزئي لفاتورة INV-S-2026-0008" />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">ملاحظات</label>
                <input className="input" value={form.narration}
                  onChange={(e) => setForm({ ...form, narration: e.target.value })}
                  placeholder="ملاحظات اختيارية" />
              </div>

              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : "حفظ كمسودة"}
                </button>
                <button type="button" onClick={() => setShowForm(false)} className="btn-secondary">
                  إلغاء
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}

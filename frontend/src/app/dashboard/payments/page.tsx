"use client";

import { Suspense, useEffect, useState, useCallback } from "react";
import { useSearchParams } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import {
  Plus, FileText, Loader2, Trash2, Send, Inbox, CheckCircle, X,
  Wallet, FileCheck
} from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface PaymentVoucher {
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
  /** Sprint 25 — link to a specific supplier invoice. */
  invoiceId?: string;
  invoiceNumber?: string;
  /** Bank account used (e.g. Cash 1000). */
  bankAccountId?: string;
  bankAccountCode?: string;
}

interface Contact {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  type: string;
}

interface OutstandingInvoice {
  invoiceId: string;
  invoiceNumber: string;
  invoiceDate: string;
  total: number;
  amountPaid: number;
  outstanding: number;
  status: string;
}

interface BankAccount {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  accountType: string;
}

const PAYMENT_METHODS: Record<string, string> = {
  cash: "نقدي",
  bank: "بنكي",
  check: "شيك",
};

const CASH_ACCOUNT_CODE = "1000";

/**
 * Payment vouchers (سندات الصرف) — the supplier-side mirror of
 * receipts. Sprint 25 brought the same settlement features:
 *   1. Optional "Invoice" dropdown (link the payment to a specific
 *      outstanding supplier bill).
 *   2. Required "Bank Account" dropdown (defaults to Cash 1000).
 *   3. URL deep-link support from the contact-detail page.
 *   4. Bank account + invoice number surfaced in the list.
 */
export default function PaymentsPage() {
  return (
    <Suspense fallback={
      <div className="flex justify-center py-12">
        <Loader2 className="animate-spin text-primary-500" size={32} />
      </div>
    }>
      <PaymentsPageInner />
    </Suspense>
  );
}

function PaymentsPageInner() {
  const { activeCompany } = useAuth();
  const searchParams = useSearchParams();
  const [vouchers, setVouchers] = useState<PaymentVoucher[]>([]);
  const [suppliers, setSuppliers] = useState<Contact[]>([]);
  const [bankAccounts, setBankAccounts] = useState<BankAccount[]>([]);
  const [outstandingInvoices, setOutstandingInvoices] = useState<OutstandingInvoice[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [success, setSuccess] = useState<string | null>(null);

  const [form, setForm] = useState({
    voucherDate: new Date().toISOString().slice(0, 10),
    contactId: "",
    invoiceId: "",
    amount: 0,
    paymentMethod: "cash",
    bankAccountId: "",
    reference: "",
    narration: "",
  });

  const load = useCallback(async () => {
    if (!activeCompany) return;
    setLoading(true);
    try {
      const [vRes, cRes, aRes] = await Promise.all([
        api.get(`/payments?companyId=${activeCompany.id}`),
        api.get(`/contacts?companyId=${activeCompany.id}&type=supplier`),
        api.get(`/accounts?companyId=${activeCompany.id}`),
      ]);
      setVouchers(vRes.data);
      setSuppliers(cRes.data);
      // Sprint 33 hotfix — the previous code took aRes.data directly,
      // which for our tree API means only the 6 L1 roots. The payment
      // form's "bank account" dropdown then showed only "1 - الأصول"
      // because that's the only L1 with accountType=Asset. The user
      // had no way to pick a specific cash/bank sub-ledger.
      //
      // Fix: flatten the tree, then filter to Asset accounts that are
      // POSTABLE (L4 sub-ledgers under 1101 Cash / 1102 Bank, plus
      // any other postable Asset accounts the admin may have added).
      const raw = Array.isArray(aRes.data) ? aRes.data : (aRes.data?.data || []);
      const flat: any[] = [];
      const walk = (n: any) => {
        const { children, ...rest } = n;
        flat.push(rest);
        if (Array.isArray(children)) children.forEach(walk);
      };
      raw.forEach(walk);

      const assets: BankAccount[] = flat
        .filter((a) =>
          a.accountType === "Asset" &&
          a.isPostable === true &&
          a.isActive !== false
        )
        .map((a) => ({
          id: a.id,
          code: a.code,
          name: a.name,
          nameAr: a.nameAr,
          accountType: a.accountType
        }));
      setBankAccounts(assets);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [activeCompany]);

  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    const i = setInterval(load, 30_000);
    return () => clearInterval(i);
  }, [load]);

  const loadOutstanding = useCallback(async (contactId: string) => {
    if (!contactId) {
      setOutstandingInvoices([]);
      return;
    }
    try {
      const r = await api.get(`/invoices?companyId=${activeCompany?.id}&contactId=${contactId}&status=outstanding`);
      const list: OutstandingInvoice[] = Array.isArray(r.data) ? r.data : (r.data?.data || []);
      setOutstandingInvoices(list);
    } catch {
      setOutstandingInvoices([]);
    }
  }, [activeCompany]);

  useEffect(() => {
    loadOutstanding(form.contactId);
  }, [form.contactId, loadOutstanding]);

  useEffect(() => {
    const contactId = searchParams.get("contactId");
    const invoiceId = searchParams.get("invoiceId");
    const amount = searchParams.get("amount");
    if (contactId || invoiceId || amount) {
      setForm((f) => ({
        ...f,
        contactId: contactId || f.contactId,
        invoiceId: invoiceId || f.invoiceId,
        amount: amount ? Number(amount) : f.amount
      }));
      setShowForm(true);
    }
  }, [searchParams]);

  useEffect(() => {
    if (bankAccounts.length === 0) return;
    if (form.bankAccountId) return;
    const cash = bankAccounts.find((a) => a.code === CASH_ACCOUNT_CODE);
    setForm((f) => ({ ...f, bankAccountId: cash?.id || bankAccounts[0].id }));
  }, [bankAccounts, form.bankAccountId]);

  useEffect(() => {
    if (!form.invoiceId) return;
    const inv = outstandingInvoices.find((i) => i.invoiceId === form.invoiceId);
    if (inv) {
      setForm((f) => ({ ...f, amount: inv.outstanding }));
    }
  }, [form.invoiceId, outstandingInvoices]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    if (!form.contactId) { setError("اختر المورّد"); return; }
    if (!form.bankAccountId) { setError("اختر الحساب (الصندوق/البنك)"); return; }
    if (form.amount <= 0) { setError("المبلغ يجب أن يكون أكبر من صفر"); return; }
    setSubmitting(true);
    setError(null);
    try {
      const res = await api.post("/payments", {
        companyId: activeCompany.id,
        voucherDate: form.voucherDate,
        contactId: form.contactId,
        amount: form.amount,
        paymentMethod: form.paymentMethod,
        bankAccountId: form.bankAccountId,
        reference: form.reference || null,
        narration: form.narration || null,
        invoiceId: form.invoiceId || null,
      });
      setSuccess(`تم حفظ السند ${res.data.voucherNumber} كمسودة`);
      setForm({
        voucherDate: new Date().toISOString().slice(0, 10),
        contactId: "", invoiceId: "", amount: 0, paymentMethod: "cash",
        bankAccountId: form.bankAccountId,
        reference: "", narration: "",
      });
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
    if (!confirm("ترحيل هذا السند؟ سيُنشأ قيد يومية ينتظر اعتماد المحاسب.")) return;
    try {
      await api.post(`/payments/${id}/post`);
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
      await api.delete(`/payments/${id}`);
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
            <Inbox size={24} className="text-red-600" />
            سندات الصرف
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            تسجيل دفعات الموردين — تحويلها إلى قيود محاسبية
          </p>
        </div>
        <button onClick={() => setShowForm(true)} className="btn-primary">
          <Plus size={18} />
          سند صرف جديد
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
            <p>لا توجد سندات صرف</p>
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>رقم السند</th>
                <th>التاريخ</th>
                <th>المورّد</th>
                <th>الفاتورة</th>
                <th>طريقة الدفع</th>
                <th>الحساب</th>
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
                  <td>{formatDate(v.voucherDate)}</td>
                  <td>{v.contactName} <span className="text-xs text-gray-500">({v.contactCode})</span></td>
                  <td className="font-mono text-sm text-primary-700">
                    {v.invoiceNumber || <span className="text-gray-400">— على الحساب —</span>}
                  </td>
                  <td className="text-sm">{PAYMENT_METHODS[v.paymentMethod] || v.paymentMethod}</td>
                  <td className="font-mono text-sm" dir="ltr">{v.bankAccountCode || "—"}</td>
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
          <div className="bg-white rounded-lg shadow-xl w-full max-w-2xl p-6 my-8 max-h-[90vh] overflow-y-auto">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold flex items-center gap-2">
                <FileText size={20} className="text-red-600" /> سند صرف جديد
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
                  <label className="block text-sm font-medium mb-1">المورّد *</label>
                  <select className="input" value={form.contactId}
                    onChange={(e) => setForm({ ...form, contactId: e.target.value, invoiceId: "" })} required>
                    <option value="">— اختر مورّد —</option>
                    {suppliers.map((c) => (
                      <option key={c.id} value={c.id}>{c.code} - {c.nameAr || c.name}</option>
                    ))}
                  </select>
                </div>
              </div>

              <div>
                <label className="block text-sm font-medium mb-1 flex items-center gap-1">
                  <FileCheck size={12} /> الفاتورة
                  <span className="text-xs text-gray-500 mr-1">(اختياري — للدفعات على الحساب اتركها فارغة)</span>
                </label>
                <select
                  className="input"
                  value={form.invoiceId}
                  onChange={(e) => setForm({ ...form, invoiceId: e.target.value })}
                  disabled={!form.contactId}
                >
                  <option value="">— على الحساب (بدون فاتورة) —</option>
                  {outstandingInvoices.map((inv) => (
                    <option key={inv.invoiceId} value={inv.invoiceId}>
                      {inv.invoiceNumber} — {formatDate(inv.invoiceDate)} — متبقي: {formatNumber(inv.outstanding)} د.ل
                    </option>
                  ))}
                </select>
                {form.contactId && outstandingInvoices.length === 0 && (
                  <p className="text-xs text-gray-500 mt-1">لا توجد فواتير مستحقة لهذا المورّد</p>
                )}
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

              <div>
                <label className="block text-sm font-medium mb-1 flex items-center gap-1">
                  <Wallet size={12} /> الحساب (الصندوق/البنك) *
                </label>
                <select className="input" value={form.bankAccountId}
                  onChange={(e) => setForm({ ...form, bankAccountId: e.target.value })} required>
                  <option value="">— اختر حساب —</option>
                  {bankAccounts.map((a) => (
                    <option key={a.id} value={a.id}>
                      {a.code} — {a.nameAr || a.name}
                    </option>
                  ))}
                </select>
                {bankAccounts.length === 0 && (
                  <p className="text-xs text-amber-700 mt-1">
                    ⚠ لا توجد حسابات أصول قابلة للترحيل. أضف حساب L4 فرعي للصندوق (1101-CASH-XXX) أو البنك (1102-BANK-XXX) من شجرة الحسابات.
                  </p>
                )}
              </div>

              <div>
                <label className="block text-sm font-medium mb-1">المرجع / البيان</label>
                <input className="input" value={form.reference}
                  onChange={(e) => setForm({ ...form, reference: e.target.value })}
                  placeholder="مثال: دفع لفاتورة INV-P-2026-0008" />
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

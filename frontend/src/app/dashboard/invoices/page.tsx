"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { FileText, Plus, Loader2, X, Send, XCircle, Eye } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface Account {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  accountType: string;
}

interface InvoiceLine {
  id: string;
  accountId: string;
  accountCode?: string;
  accountName?: string;
  description: string;
  quantity: number;
  unitPrice: number;
  taxRate: number;
  amount: number;
  lineNumber: number;
}

interface Invoice {
  id: string;
  invoiceNumber: string;
  invoiceType: "purchase" | "sales";
  invoiceDate: string;
  partyName: string;
  partyNameAr?: string;
  subtotal: number;
  taxAmount: number;
  total: number;
  status: "draft" | "posted" | "paid" | "cancelled";
  createdAt: string;
  postedAt?: string;
  lines: InvoiceLine[];
}

export default function InvoicesPage() {
  const { activeCompany, user } = useAuth();
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [filter, setFilter] = useState<"all" | "purchase" | "sales">("all");

  const [form, setForm] = useState({
    invoiceType: "purchase" as "purchase" | "sales",
    invoiceDate: new Date().toISOString().slice(0, 10),
    partyName: "",
    partyNameAr: "",
    taxRate: 0,
    lines: [
      { accountId: "", description: "", quantity: 1, unitPrice: 0, taxRate: 0 }
    ]
  });

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const [invoicesRes, accountsRes] = await Promise.all([
        api.get(`/invoices?companyId=${activeCompany.id}&limit=100`),
        api.get(`/accounts?companyId=${activeCompany.id}`)
      ]);
      setInvoices(invoicesRes.data);
      setAccounts(accountsRes.data.filter((a: Account) =>
        a.accountType === "Expense" || a.accountType === "Revenue" || a.accountType === "Asset"
      ));
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  const addLine = () => {
    setForm({
      ...form,
      lines: [...form.lines, { accountId: "", description: "", quantity: 1, unitPrice: 0, taxRate: 0 }]
    });
  };

  const removeLine = (idx: number) => {
    setForm({ ...form, lines: form.lines.filter((_, i) => i !== idx) });
  };

  const updateLine = (idx: number, field: string, value: any) => {
    const newLines = [...form.lines];
    newLines[idx] = { ...newLines[idx], [field]: value };
    setForm({ ...form, lines: newLines });
  };

  const subtotal = form.lines.reduce((s, l) => s + (l.quantity * l.unitPrice), 0);
  const taxAmount = form.lines.reduce((s, l) => s + (l.quantity * l.unitPrice * (l.taxRate || form.taxRate)), 0);
  const total = subtotal + taxAmount;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    setSubmitting(true);
    setError(null);
    try {
      await api.post("/invoices", {
        companyId: activeCompany.id,
        invoiceType: form.invoiceType,
        invoiceDate: form.invoiceDate,
        partyName: form.partyName,
        partyNameAr: form.partyNameAr || null,
        taxRate: form.taxRate,
        lines: form.lines
          .filter((l) => l.accountId)
          .map((l) => ({
            accountId: l.accountId,
            description: l.description,
            quantity: l.quantity,
            unitPrice: l.unitPrice,
            taxRate: l.taxRate
          }))
      });
      setForm({
        invoiceType: "purchase",
        invoiceDate: new Date().toISOString().slice(0, 10),
        partyName: "",
        partyNameAr: "",
        taxRate: 0,
        lines: [{ accountId: "", description: "", quantity: 1, unitPrice: 0, taxRate: 0 }]
      });
      setShowForm(false);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const postInvoice = async (id: string) => {
    try {
      await api.post(`/invoices/${id}/post`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const cancelInvoice = async (id: string) => {
    if (!confirm("هل أنت متأكد من إلغاء هذه الفاتورة؟")) return;
    try {
      await api.post(`/invoices/${id}/cancel`);
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const filteredInvoices = invoices.filter((inv) =>
    filter === "all" ? true : inv.invoiceType === filter
  );

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <FileText size={24} className="text-primary-600" />
            الفواتير
          </h1>
          <p className="text-sm text-gray-600 mt-1">فواتير المشتريات والمبيعات</p>
        </div>
        <button onClick={() => setShowForm(true)} className="btn-primary">
          <Plus size={18} />
          فاتورة جديدة
        </button>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {/* Filter tabs */}
      <div className="flex gap-2 mb-4">
        {[
          { v: "all", l: "الكل" },
          { v: "purchase", l: "مشتريات" },
          { v: "sales", l: "مبيعات" }
        ].map((t) => (
          <button
            key={t.v}
            onClick={() => setFilter(t.v as any)}
            className={`px-4 py-2 rounded-md text-sm font-medium ${
              filter === t.v
                ? "bg-primary-600 text-white"
                : "bg-white text-gray-700 border border-gray-300 hover:bg-gray-50"
            }`}
          >
            {t.l}
          </button>
        ))}
      </div>

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : (
          <table className="table">
            <thead>
              <tr>
                <th>الرقم</th>
                <th>النوع</th>
                <th>التاريخ</th>
                <th>الطرف</th>
                <th>الإجمالي</th>
                <th>الحالة</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {filteredInvoices.map((inv) => (
                <InvoiceRow
                  key={inv.id}
                  inv={inv}
                  expanded={expanded === inv.id}
                  onToggle={() => setExpanded(expanded === inv.id ? null : inv.id)}
                  onPost={() => postInvoice(inv.id)}
                  onCancel={() => cancelInvoice(inv.id)}
                />
              ))}
              {filteredInvoices.length === 0 && (
                <tr>
                  <td colSpan={7} className="text-center text-gray-500 py-6">لا توجد فواتير</td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      {showForm && (
        <InvoiceForm
          form={form}
          setForm={setForm}
          accounts={accounts}
          subtotal={subtotal}
          taxAmount={taxAmount}
          total={total}
          submitting={submitting}
          error={error}
          onSubmit={submit}
          onAddLine={addLine}
          onRemoveLine={removeLine}
          onUpdateLine={updateLine}
          onCancel={() => setShowForm(false)}
        />
      )}
    </div>
  );
}

function InvoiceRow({ inv, expanded, onToggle, onPost, onCancel }: any) {
  const statusBadge = {
    draft: <span className="badge badge-warning">مسودة</span>,
    posted: <span className="badge badge-success">مرحّلة</span>,
    paid: <span className="badge badge-info">مدفوعة</span>,
    cancelled: <span className="badge badge-danger">ملغاة</span>
  }[inv.status as string];

  return (
    <>
      <tr className="cursor-pointer hover:bg-gray-50" onClick={onToggle}>
        <td className="font-mono font-semibold">{inv.invoiceNumber}</td>
        <td>
          {inv.invoiceType === "purchase" ? (
            <span className="badge badge-warning">مشتريات</span>
          ) : (
            <span className="badge badge-info">مبيعات</span>
          )}
        </td>
        <td>{formatDate(inv.invoiceDate)}</td>
        <td>{inv.partyNameAr || inv.partyName}</td>
        <td className="font-mono" dir="ltr">{formatNumber(inv.total)}</td>
        <td>{statusBadge}</td>
        <td>
          {inv.status === "draft" && (
            <div className="flex items-center gap-1">
              <button
                onClick={(e) => { e.stopPropagation(); onPost(); }}
                className="text-primary-600 hover:bg-primary-50 p-1 rounded text-sm flex items-center gap-1"
                title="ترحيل"
              >
                <Send size={14} />
              </button>
              <button
                onClick={(e) => { e.stopPropagation(); onCancel(); }}
                className="text-red-600 hover:bg-red-50 p-1 rounded"
                title="إلغاء"
              >
                <XCircle size={14} />
              </button>
            </div>
          )}
        </td>
      </tr>
      {expanded && (
        <tr>
          <td colSpan={7} className="bg-gray-50 p-4">
            <table className="w-full text-sm">
              <thead>
                <tr className="text-xs text-gray-600">
                  <th className="text-right py-1">الحساب</th>
                  <th className="text-right py-1">البيان</th>
                  <th className="text-right py-1">الكمية</th>
                  <th className="text-left py-1">السعر</th>
                  <th className="text-left py-1">الضريبة</th>
                  <th className="text-left py-1">المبلغ</th>
                </tr>
              </thead>
              <tbody>
                {inv.lines.map((l: InvoiceLine) => (
                  <tr key={l.id}>
                    <td className="py-1">
                      <span className="font-mono text-xs text-gray-500">{l.accountCode}</span>{" "}
                      {l.accountName}
                    </td>
                    <td className="py-1">{l.description}</td>
                    <td className="py-1 font-mono" dir="ltr">{l.quantity}</td>
                    <td className="py-1 font-mono" dir="ltr">{formatNumber(l.unitPrice)}</td>
                    <td className="py-1 font-mono" dir="ltr">{(l.taxRate * 100).toFixed(1)}%</td>
                    <td className="py-1 font-mono" dir="ltr">{formatNumber(l.amount)}</td>
                  </tr>
                ))}
                <tr className="border-t font-semibold">
                  <td colSpan={5} className="py-1">الإجمالي الفرعي</td>
                  <td className="py-1 font-mono" dir="ltr">{formatNumber(inv.subtotal)}</td>
                </tr>
                <tr>
                  <td colSpan={5} className="py-1">الضريبة</td>
                  <td className="py-1 font-mono" dir="ltr">{formatNumber(inv.taxAmount)}</td>
                </tr>
                <tr className="font-bold">
                  <td colSpan={5} className="py-1">الإجمالي</td>
                  <td className="py-1 font-mono" dir="ltr">{formatNumber(inv.total)}</td>
                </tr>
              </tbody>
            </table>
          </td>
        </tr>
      )}
    </>
  );
}

function InvoiceForm({ form, setForm, accounts, subtotal, taxAmount, total, submitting, error, onSubmit, onAddLine, onRemoveLine, onUpdateLine, onCancel }: any) {
  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4 overflow-y-auto">
      <div className="bg-white rounded-lg shadow-xl w-full max-w-4xl p-6 my-8">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold">فاتورة جديدة</h2>
          <button onClick={onCancel} className="text-gray-400 hover:text-gray-600">
            <X size={20} />
          </button>
        </div>

        <form onSubmit={onSubmit} className="space-y-4">
          <div className="grid grid-cols-3 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">نوع الفاتورة *</label>
              <select
                className="input"
                value={form.invoiceType}
                onChange={(e) => setForm({ ...form, invoiceType: e.target.value })}
              >
                <option value="purchase">مشتريات</option>
                <option value="sales">مبيعات</option>
              </select>
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">التاريخ *</label>
              <input
                type="date"
                className="input"
                value={form.invoiceDate}
                onChange={(e) => setForm({ ...form, invoiceDate: e.target.value })}
                required
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">نسبة الضريبة %</label>
              <input
                type="number"
                step="0.01"
                className="input"
                value={form.taxRate}
                onChange={(e) => setForm({ ...form, taxRate: Number(e.target.value) / 100 })}
                dir="ltr"
                placeholder="e.g., 15"
              />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">اسم المورد/العميل (English) *</label>
              <input
                className="input"
                value={form.partyName}
                onChange={(e) => setForm({ ...form, partyName: e.target.value })}
                required
                placeholder="e.g., ABC Trading Co."
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">اسم المورد/العميل (عربي)</label>
              <input
                className="input"
                value={form.partyNameAr}
                onChange={(e) => setForm({ ...form, partyNameAr: e.target.value })}
                placeholder="مثال: شركة ABC التجارية"
              />
            </div>
          </div>

          <div>
            <div className="flex items-center justify-between mb-2">
              <h3 className="text-sm font-semibold">بنود الفاتورة</h3>
              <button type="button" onClick={onAddLine} className="text-sm text-primary-600 hover:underline">
                + بند جديد
              </button>
            </div>
            <div className="space-y-2">
              {form.lines.map((line: any, idx: number) => (
                <div key={idx} className="grid grid-cols-12 gap-2 items-center">
                  <select
                    className="input col-span-4"
                    value={line.accountId}
                    onChange={(e) => onUpdateLine(idx, "accountId", e.target.value)}
                    required
                  >
                    <option value="">- اختر حساب -</option>
                    {accounts.map((a: Account) => (
                      <option key={a.id} value={a.id}>
                        {a.code} - {a.nameAr || a.name}
                      </option>
                    ))}
                  </select>
                  <input
                    className="input col-span-3"
                    placeholder="البيان"
                    value={line.description}
                    onChange={(e) => onUpdateLine(idx, "description", e.target.value)}
                  />
                  <input
                    type="number"
                    step="0.01"
                    className="input col-span-1"
                    placeholder="كمية"
                    value={line.quantity}
                    onChange={(e) => onUpdateLine(idx, "quantity", Number(e.target.value))}
                    dir="ltr"
                  />
                  <input
                    type="number"
                    step="0.01"
                    className="input col-span-2"
                    placeholder="سعر"
                    value={line.unitPrice}
                    onChange={(e) => onUpdateLine(idx, "unitPrice", Number(e.target.value))}
                    dir="ltr"
                  />
                  <input
                    type="number"
                    step="0.01"
                    className="input col-span-1"
                    placeholder="%"
                    value={(line.taxRate * 100).toFixed(2)}
                    onChange={(e) => onUpdateLine(idx, "taxRate", Number(e.target.value) / 100)}
                    dir="ltr"
                  />
                  <button type="button" onClick={() => onRemoveLine(idx)} className="text-red-500 hover:text-red-700 col-span-1">
                    <X size={16} />
                  </button>
                </div>
              ))}
            </div>
          </div>

          <div className="grid grid-cols-3 gap-3 p-3 bg-gray-50 rounded-md">
            <div>
              <p className="text-xs text-gray-600">الإجمالي الفرعي</p>
              <p className="text-lg font-bold" dir="ltr">{formatNumber(subtotal)}</p>
            </div>
            <div>
              <p className="text-xs text-gray-600">الضريبة</p>
              <p className="text-lg font-bold" dir="ltr">{formatNumber(taxAmount)}</p>
            </div>
            <div>
              <p className="text-xs text-gray-600">الإجمالي</p>
              <p className="text-lg font-bold text-primary-600" dir="ltr">{formatNumber(total)}</p>
            </div>
          </div>

          {error && <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

          <div className="flex gap-2 pt-2">
            <button type="submit" disabled={submitting} className="btn-primary flex-1">
              {submitting ? "جاري الحفظ..." : "حفظ كمسودة"}
            </button>
            <button type="button" onClick={onCancel} className="btn-secondary">
              إلغاء
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

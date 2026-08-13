"use client";

import { useEffect, useMemo, useState, useCallback } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { FileText, Plus, Loader2, X, Send, XCircle, Eye, Pencil, FolderKanban } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";
import ProjectPicker from "../projects/components/ProjectPicker";

/**
 * Invoices are now product-based. Each line picks a product from
 * the catalogue (code + name + unit price + tax rate), and the user
 * only has to enter a quantity. The backend `InvoiceService`
 * auto-fills description / unit_price / tax_rate from the product
 * if the user leaves them blank, then computes line_total and
 * line_total_with_tax server-side to avoid floating-point drift.
 *
 * Posting an invoice delegates to the Business Rule
 * (`SalesInvoiceApproved` / `PurchaseInvoiceApproved`) which builds
 * the journal entry from the rule's account mapping.
 */

interface Product {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  unitPrice: number;
  defaultTaxRate: number;
}

interface Contact {
  id: string;
  type: "customer" | "supplier";
  code: string;
  name: string;
  nameAr?: string;
  taxId?: string;
}

interface InvoiceLine {
  id: string;
  accountId?: string;
  accountCode?: string;
  accountName?: string;
  productId?: string;
  productCode?: string;
  productName?: string;
  productNameAr?: string;
  description: string;
  quantity: number;
  unitPrice: number;
  taxRate: number;
  amount: number;
  lineTotalWithTax: number;
  lineNumber: number;
}

interface Invoice {
  id: string;
  invoiceNumber: string;
  invoiceType: "purchase" | "sales";
  invoiceDate: string;
  partyName: string;
  partyNameAr?: string;
  partyContactId?: string;
  partyTaxId?: string;
  intercompanyCompanyId?: string;
  notes?: string;
  subtotal: number;
  taxAmount: number;
  total: number;
  /**
   * Sprint 25 — backend now emits settlement status. The status
   * string is one of: "draft" | "posted" | "partiallypaid" | "paid" | "cancelled".
   * The status badge below renders based on the string value.
   *
   * `amountPaid` and `fullyPaidAt` may be returned by the backend
   * (the new migration 014 added the columns). The frontend
   * doesn't *require* them — older backends that haven't shipped
   * the settlement work will simply omit them, and the badge
   * falls back to the original 4 states.
   */
  status: "draft" | "posted" | "partiallypaid" | "paid" | "cancelled";
  amountPaid?: number;
  fullyPaidAt?: string;
  createdAt: string;
  postedAt?: string;
  lines: InvoiceLine[];
}

interface FormLine {
  productId: string;
  description: string;
  quantity: number;
  unitPrice: number;
  taxRate: number;
}

const emptyFormLine: FormLine = {
  productId: "",
  description: "",
  quantity: 1,
  unitPrice: 0,
  taxRate: 0
};

export default function InvoicesPage() {
  const { activeCompany, user, companies } = useAuth();
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [products, setProducts] = useState<Product[]>([]);
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  // Sprint 45 — added status filter alongside the type filter. The user
  // had created an invoice (INV-P-2026-0118, SUPP-009, 13-08-2026) that
  // was in 'draft' state but the default view didn't show drafts — so the
  // user thought the invoice had disappeared. Status filter + a column
  // make it visible without having to open the detail.
  const [filter, setFilter] = useState<"all" | "purchase" | "sales">("all");
  const [statusFilter, setStatusFilter] = useState<
    "all" | "draft" | "posted" | "partiallypaid" | "paid" | "cancelled"
  >("all");
  // Sprint 29 — null = create mode, object = edit mode
  const [editing, setEditing] = useState<Invoice | null>(null);

  const [form, setForm] = useState({
    invoiceType: "purchase" as "purchase" | "sales",
    invoiceDate: new Date().toISOString().slice(0, 10),
    partyContactId: "" as string,  // when picked from catalogue
    partyName: "",
    partyNameAr: "",
    taxRate: 0,
    intercompanyCompanyId: "" as string,
    // Sprint 35 — project tag (optional). The backend persists
    // this on the invoice header (invoices.project_id) so the
    // cost can be grouped under the project P&L later.
    projectId: "" as string,
    lines: [emptyFormLine] as FormLine[]
  });

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const [invoicesRes, productsRes, contactsRes] = await Promise.all([
        api.get(`/invoices?companyId=${activeCompany.id}&limit=100`),
        api.get(`/products?companyId=${activeCompany.id}`),
        api.get(`/contacts?companyId=${activeCompany.id}`)
      ]);
      setInvoices(invoicesRes.data);
      setProducts(productsRes.data);
      setContacts(contactsRes.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  const productMap = useMemo(() => {
    const m = new Map<string, Product>();
    products.forEach((p) => m.set(p.id, p));
    return m;
  }, [products]);

  const addLine = () => {
    setForm({ ...form, lines: [...form.lines, { ...emptyFormLine }] });
  };

  const removeLine = (idx: number) => {
    setForm({ ...form, lines: form.lines.filter((_, i) => i !== idx) });
  };

  const updateLine = (idx: number, field: keyof FormLine, value: any) => {
    const newLines = [...form.lines];
    const line: FormLine = { ...newLines[idx], [field]: value };
    // When the user picks a product, auto-fill description,
    // unit_price, and tax_rate from the catalogue — the user
    // can still override.
    if (field === "productId" && value) {
      const p = productMap.get(value);
      if (p) {
        line.description = line.description || p.nameAr || p.name;
        line.unitPrice = line.unitPrice || p.unitPrice;
        // Only override tax if the user hasn't set one yet.
        if (!line.taxRate) line.taxRate = p.defaultTaxRate;
      }
    }
    newLines[idx] = line;
    setForm({ ...form, lines: newLines });
  };

  const subtotal = form.lines.reduce(
    (s, l) => s + (l.quantity * l.unitPrice),
    0
  );
  const lineTotalsWithTax = form.lines.map(
    (l) => l.quantity * l.unitPrice * (1 + (l.taxRate || form.taxRate))
  );
  const total = lineTotalsWithTax.reduce((s, x) => s + x, 0);
  const taxAmount = total - subtotal;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    setSubmitting(true);
    setError(null);
    try {
      const payload = {
        companyId: activeCompany.id,
        invoiceType: form.invoiceType,
        invoiceDate: form.invoiceDate,
        partyName: form.partyName,
        partyNameAr: form.partyNameAr || null,
        taxRate: form.taxRate,
        intercompanyCompanyId: form.intercompanyCompanyId || null,
        // Sprint 35 — optional project tag for cost-center reports.
        projectId: form.projectId || null,
        lines: form.lines
          .filter((l) => l.productId)
          .map((l) => ({
            productId: l.productId,
            description: l.description,
            quantity: l.quantity,
            unitPrice: l.unitPrice,
            taxRate: l.taxRate
          }))
      };
      if (editing) {
        // Sprint 29 — PUT replaces the draft in place
        await api.put(`/invoices/${editing.id}`, payload);
      } else {
        await api.post("/invoices", payload);
      }
      setForm({
        invoiceType: "purchase",
        invoiceDate: new Date().toISOString().slice(0, 10),
        partyContactId: "",
        partyName: "",
        partyNameAr: "",
        taxRate: 0,
        intercompanyCompanyId: "",
        projectId: "",
        lines: [emptyFormLine] as FormLine[]
      });
      setEditing(null);
      setShowForm(false);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  // Sprint 29 — open the form pre-filled with the draft invoice's data
  const startEdit = (inv: Invoice) => {
    setEditing(inv);
    setForm({
      invoiceType: inv.invoiceType,
      invoiceDate: (inv.invoiceDate || "").slice(0, 10),
      partyContactId: inv.partyContactId || "",
      partyName: inv.partyName || "",
      partyNameAr: inv.partyNameAr || "",
      taxRate: inv.lines?.[0]?.taxRate ?? 0,
      intercompanyCompanyId: inv.intercompanyCompanyId || "",
      // Sprint 35 — if the backend returns the project tag, prefill it.
      // The DTO may not have a `projectId` field if the older backend
      // hasn't shipped the migration; the `|| ""` keeps us safe.
      projectId: (inv as any).projectId || "",
      lines: (inv.lines || []).map((l) => ({
        productId: l.productId || "",
        description: l.description || "",
        quantity: l.quantity,
        unitPrice: l.unitPrice,
        taxRate: l.taxRate
      }))
    });
    setShowForm(true);
  };

  const cancelEdit = () => {
    setEditing(null);
    setShowForm(false);
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

  // Sprint 45 — combined filter (type + status). Both must match.
  const filteredInvoices = invoices.filter((inv) => {
    if (filter !== "all" && inv.invoiceType !== filter) return false;
    if (statusFilter !== "all" && inv.status !== statusFilter) return false;
    return true;
  });

  return (
    <div>
      {/* Sprint 45 — Mode banner. Shows the user what mode the
          system is in right now (HUMAN-ONLY vs TRUSTED-ACCOUNTANT)
          and offers a one-click toggle. Mirrors the admin page
          banner so the user always knows what flow they're in. */}
      <ModeBanner />

      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
            <FileText size={24} className="text-primary-600" />
            الفواتير
          </h1>
          <p className="text-sm text-ink-muted mt-1">
            فواتير المشتريات والمبيعات — مبنية على المنتجات
          </p>
        </div>
        <button onClick={() => setShowForm(true)} className="btn-primary">
          <Plus size={18} />
          فاتورة جديدة
        </button>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {/* Filter tabs — by type (purchase/sales) */}
      <div className="flex gap-2 mb-3">
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
                : "bg-canvas dark:bg-neutral-900 text-ink-muted border border-edge hover:bg-raised"
            }`}
          >
            {t.l}
          </button>
        ))}
      </div>

      {/* Sprint 45 — filter by status (draft/posted/paid/cancelled).
          Drafts are the most-need-to-see state because the user
          often creates an invoice, walks away, then wonders where
          it went. Surfacing them in the filter makes them findable. */}
      <div className="flex flex-wrap gap-2 mb-4">
        {[
          { v: "all", l: "كل الحالات", cls: "bg-canvas dark:bg-neutral-900 text-ink-muted border border-edge" },
          { v: "draft", l: "مسودة", cls: "bg-amber-100 text-amber-700 border border-amber-200" },
          { v: "posted", l: "مرحّلة", cls: "bg-blue-100 text-blue-700 border border-blue-200" },
          { v: "partiallypaid", l: "مدفوعة جزئياً", cls: "bg-violet-100 text-violet-700 border border-violet-200" },
          { v: "paid", l: "مدفوعة", cls: "bg-emerald-100 text-emerald-700 border border-emerald-200" },
          { v: "cancelled", l: "ملغية", cls: "bg-rose-100 text-rose-700 border border-rose-200" }
        ].map((s) => (
          <button
            key={s.v}
            onClick={() => setStatusFilter(s.v as any)}
            className={`px-3 py-1.5 rounded-md text-xs font-medium ${
              statusFilter === s.v
                ? "ring-2 ring-primary-500 " + s.cls
                : s.cls + " opacity-60 hover:opacity-100"
            }`}
          >
            {s.l}
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
                  onEdit={() => startEdit(inv)}
                />
              ))}
              {filteredInvoices.length === 0 && (
                <tr>
                  <td colSpan={7} className="text-center text-ink-muted py-6">لا توجد فواتير</td>
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
          products={products}
          contacts={contacts}
          lineTotalsWithTax={lineTotalsWithTax}
          subtotal={subtotal}
          taxAmount={taxAmount}
          total={total}
          submitting={submitting}
          error={error}
          companies={companies}
          activeCompany={activeCompany}
          editing={editing}
          onSubmit={submit}
          onAddLine={addLine}
          onRemoveLine={removeLine}
          onUpdateLine={updateLine}
          onCancel={cancelEdit}
        />
      )}
    </div>
  );
}

function InvoiceRow({ inv, expanded, onToggle, onPost, onCancel, onEdit }: any) {
  // Sprint 25 — the status badge now shows settlement progress
  // when amountPaid is known. The original 4-state badge is still
  // used for draft / cancelled / paid (no numbers needed).
  const outstanding = Math.max(0, (inv.total || 0) - (inv.amountPaid || 0));
  const statusBadge = (() => {
    switch (inv.status) {
      case "draft":
        return <span className="badge badge-warning">مسودة</span>;
      case "posted":
        // Outstanding amount in red.
        return (
          <span className="badge bg-primary-100 text-primary-800 inline-flex items-center gap-1">
            مرحّلة
            {inv.amountPaid !== undefined && outstanding > 0.01 && (
              <span className="text-red-600 font-mono text-xs">
                ({formatNumber(outstanding)} د.ل مستحق)
              </span>
            )}
          </span>
        );
      case "partiallypaid":
        // Show "X / Y LYD مدفوع" — paid / total
        return (
          <span className="badge bg-amber-100 text-amber-800 inline-flex items-center gap-1">
            مدفوع جزئياً
            {inv.amountPaid !== undefined && (
              <span className="font-mono text-xs">
                ({formatNumber(inv.amountPaid)} / {formatNumber(inv.total)})
              </span>
            )}
          </span>
        );
      case "paid":
        return <span className="badge badge-success">مدفوع بالكامل</span>;
      case "cancelled":
        return <span className="badge badge-danger">ملغاة</span>;
      default:
        return <span className="badge bg-raised text-ink-strong">{inv.status}</span>;
    }
  })();

  return (
    <>
      <tr className="cursor-pointer hover:bg-raised" onClick={onToggle}>
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
                onClick={(e) => { e.stopPropagation(); onEdit(); }}
                className="text-amber-600 hover:bg-amber-50 p-1 rounded text-sm flex items-center gap-1"
                title="تعديل"
              >
                <Pencil size={14} />
              </button>
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
          <td colSpan={7} className="bg-raised p-4">
            <table className="w-full text-sm">
              <thead>
                <tr className="text-xs text-ink-muted">
                  <th className="text-right py-1">المنتج</th>
                  <th className="text-right py-1">البيان</th>
                  <th className="text-right py-1">الكمية</th>
                  <th className="text-left py-1">السعر</th>
                  <th className="text-left py-1">الضريبة</th>
                  <th className="text-left py-1">المبلغ</th>
                  <th className="text-left py-1">شامل الضريبة</th>
                </tr>
              </thead>
              <tbody>
                {inv.lines.map((l: InvoiceLine) => (
                  <tr key={l.id}>
                    <td className="py-1">
                      {l.productCode ? (
                        <>
                          <span className="font-mono text-xs text-ink-muted">{l.productCode}</span>{" "}
                          {l.productNameAr || l.productName}
                        </>
                      ) : (
                        <span className="text-ink-subtle text-xs">بدون منتج</span>
                      )}
                    </td>
                    <td className="py-1">{l.description}</td>
                    <td className="py-1 font-mono" dir="ltr">{l.quantity}</td>
                    <td className="py-1 font-mono" dir="ltr">{formatNumber(l.unitPrice)}</td>
                    <td className="py-1 font-mono" dir="ltr">{(l.taxRate * 100).toFixed(1)}%</td>
                    <td className="py-1 font-mono" dir="ltr">{formatNumber(l.amount)}</td>
                    <td className="py-1 font-mono font-semibold" dir="ltr">
                      {formatNumber(l.lineTotalWithTax)}
                    </td>
                  </tr>
                ))}
                <tr className="border-t font-semibold">
                  <td colSpan={6} className="py-1">الإجمالي الفرعي</td>
                  <td className="py-1 font-mono" dir="ltr">{formatNumber(inv.subtotal)}</td>
                </tr>
                <tr>
                  <td colSpan={6} className="py-1">الضريبة</td>
                  <td className="py-1 font-mono" dir="ltr">{formatNumber(inv.taxAmount)}</td>
                </tr>
                <tr className="font-bold bg-primary-50">
                  <td colSpan={6} className="py-2">الإجمالي الكلي</td>
                  <td className="py-2 font-mono text-primary-700" dir="ltr">{formatNumber(inv.total)}</td>
                </tr>
              </tbody>
            </table>
          </td>
        </tr>
      )}
    </>
  );
}

function InvoiceForm({
  form, setForm, products, contacts, lineTotalsWithTax,
  subtotal, taxAmount, total,
  submitting, error,
  companies, activeCompany, editing,
  onSubmit, onAddLine, onRemoveLine, onUpdateLine, onCancel
}: any) {
  const isEdit = !!editing;
  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4 overflow-y-auto">
      <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-5xl p-6 my-8">
        <div className="flex items-center justify-between mb-4">
          <h2 className="text-lg font-semibold flex items-center gap-2">
            {isEdit && <Pencil size={18} className="text-amber-600" />}
            {isEdit ? `تعديل فاتورة مسودة — ${editing.invoiceNumber}` : "فاتورة جديدة"}
          </h2>
          <button onClick={onCancel} className="text-ink-subtle hover:text-ink-muted">
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
              <label className="block text-sm font-medium mb-1">الضريبة الافتراضية %</label>
              <input
                type="number"
                step="0.01"
                className="input"
                value={(form.taxRate * 100).toFixed(2)}
                onChange={(e) => setForm({ ...form, taxRate: Number(e.target.value) / 100 })}
                dir="ltr"
                placeholder="e.g., 15"
                title="يُطبَّق على البنود التي لا تحدد نسبة الضريبة الخاصة بها"
              />
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">
                شركة شقيقة
                <span className="text-xs text-ink-muted mr-2">(اختياري - للمعاملات بين الشركات)</span>
              </label>
              <select
                className="input"
                value={form.intercompanyCompanyId}
                onChange={(e) => setForm({ ...form, intercompanyCompanyId: e.target.value })}
              >
                <option value="">— لا توجد (فاتورة عادية) —</option>
                {companies
                  .filter((c: any) => c.id !== activeCompany?.id)
                  .map((c: any) => (
                    <option key={c.id} value={c.id}>
                      {c.code} — {c.nameAr || c.name}
                    </option>
                  ))}
              </select>
              <p className="text-xs text-ink-muted mt-1">
                عند الترحيل، ينشئ النظام قيداً في الشركة الحالية وفي الشركة الشقيقة
              </p>
            </div>
            <div>
              {/* Sprint 35 — project tag (optional). Lets the
                  cost get rolled up under the project P&L. We
                  use a combobox because a 50-project company
                  can't be scrolled on a phone. */}
              <label className="block text-sm font-medium mb-1 flex items-center gap-1">
                <FolderKanban size={12} />
                المشروع
                <span className="text-xs text-ink-muted mr-1">(اختياري — لتحميل التكلفة على مركز التكلفة)</span>
              </label>
              <ProjectPicker
                companyId={activeCompany?.id}
                value={form.projectId || null}
                onChange={(id) => setForm({ ...form, projectId: id || "" })}
                disabled={submitting}
              />
            </div>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="block text-sm font-medium mb-1">
                {form.invoiceType === "sales" ? "اختر العميل من القائمة" : "اختر المورد من القائمة"}
              </label>
              <select
                className="input"
                value={form.partyContactId}
                onChange={(e) => {
                  const id = e.target.value;
                  const c = contacts.find((c: Contact) => c.id === id);
                  if (c) {
                    setForm({
                      ...form,
                      partyContactId: id,
                      partyName: c.name,
                      partyNameAr: c.nameAr || c.name
                    });
                  } else {
                    setForm({ ...form, partyContactId: "" });
                  }
                }}
              >
                <option value="">— أو اكتب اسم جديد (يدوي) —</option>
                {contacts
                  .filter((c: Contact) => c.type === (form.invoiceType === "sales" ? "customer" : "supplier"))
                  .map((c: Contact) => (
                    <option key={c.id} value={c.id}>
                      {c.code} — {c.nameAr || c.name}
                    </option>
                  ))}
              </select>
              {contacts.filter((c: Contact) => c.type === (form.invoiceType === "sales" ? "customer" : "supplier")).length === 0 && (
                <p className="text-xs text-amber-600 mt-1">
                  ⚠ لا يوجد {form.invoiceType === "sales" ? "عملاء" : "موردين"} في الكتالوج. اكتب الاسم يدوياً أو أضف من صفحة "العملاء والموردين".
                </p>
              )}
            </div>
            <div>
              <label className="block text-sm font-medium mb-1">
                اسم الطرف (عربي) — يُملأ تلقائياً
              </label>
              <input
                className="input"
                value={form.partyNameAr}
                onChange={(e) => setForm({ ...form, partyNameAr: e.target.value })}
                placeholder="مثال: شركة ABC التجارية"
                dir="rtl"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium mb-1">اسم الطرف (English) *</label>
            <input
              className="input"
              value={form.partyName}
              onChange={(e) => setForm({ ...form, partyName: e.target.value })}
              required
              placeholder="e.g., ABC Trading Co."
              dir="ltr"
            />
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
                <div key={idx} className="grid grid-cols-12 gap-2 items-center bg-raised p-2 rounded">
                  <select
                    className="input col-span-4"
                    value={line.productId}
                    onChange={(e) => onUpdateLine(idx, "productId", e.target.value)}
                    required
                  >
                    <option value="">- اختر منتج -</option>
                    {products.map((p: Product) => (
                      <option key={p.id} value={p.id}>
                        {p.code} - {p.nameAr || p.name} ({formatNumber(p.unitPrice)})
                      </option>
                    ))}
                  </select>
                  <input
                    className="input col-span-3"
                    placeholder="البيان (اختياري)"
                    value={line.description}
                    onChange={(e) => onUpdateLine(idx, "description", e.target.value)}
                  />
                  <input
                    type="number"
                    step="0.01"
                    min="0.01"
                    className="input col-span-1"
                    placeholder="كمية"
                    value={line.quantity}
                    onChange={(e) => onUpdateLine(idx, "quantity", Number(e.target.value))}
                    dir="ltr"
                    title="الكمية"
                  />
                  <input
                    type="number"
                    step="0.01"
                    min="0"
                    className="input col-span-2"
                    placeholder="سعر الوحدة"
                    value={line.unitPrice}
                    onChange={(e) => onUpdateLine(idx, "unitPrice", Number(e.target.value))}
                    dir="ltr"
                    title="سعر الوحدة (يتم التعبئة من المنتج إذا تُرك فارغاً)"
                  />
                  <input
                    type="number"
                    step="0.01"
                    min="0"
                    className="input col-span-1"
                    placeholder="%"
                    value={(line.taxRate * 100).toFixed(2)}
                    onChange={(e) => onUpdateLine(idx, "taxRate", Number(e.target.value) / 100)}
                    dir="ltr"
                    title="نسبة الضريبة %"
                  />
                  <button type="button" onClick={() => onRemoveLine(idx)} className="text-red-500 hover:text-red-700 col-span-1 flex justify-center">
                    <X size={16} />
                  </button>
                </div>
              ))}
            </div>
            {products.length === 0 && (
              <p className="text-xs text-amber-700 bg-amber-50 p-2 rounded mt-2">
                ⚠ لا توجد منتجات لهذه الشركة. أضف منتجات أولاً من صفحة "المنتجات".
              </p>
            )}
          </div>

          <div className="grid grid-cols-3 gap-3 p-3 bg-raised rounded-md">
            <div>
              <p className="text-xs text-ink-muted">الإجمالي الفرعي</p>
              <p className="text-lg font-bold" dir="ltr">{formatNumber(subtotal)}</p>
            </div>
            <div>
              <p className="text-xs text-ink-muted">الضريبة</p>
              <p className="text-lg font-bold" dir="ltr">{formatNumber(taxAmount)}</p>
            </div>
            <div>
              <p className="text-xs text-ink-muted">الإجمالي (شامل الضريبة)</p>
              <p className="text-xl font-bold text-primary-600" dir="ltr">{formatNumber(total)}</p>
            </div>
          </div>

          {error && <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

          <div className="flex gap-2 pt-2">
            <button type="submit" disabled={submitting} className="btn-primary flex-1">
              {submitting ? "جاري الحفظ..." : (isEdit ? "حفظ التعديلات" : "حفظ كمسودة")}
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

/**
 * Sprint 45 — Mode banner with one-click toggle.
 *
 * Shows the user the current mode (HUMAN-ONLY vs TRUSTED-ACCOUNTANT)
 * and lets them switch without going to the admin page. The
 * "اكتشف" link goes to the journal page so the user can see the
 * effect of the mode on existing draft JEs.
 */
function ModeBanner() {
  const [info, setInfo] = useState<{
    trustedMode: boolean;
    source: string;
    label: string;
  } | null>(null);
  const [busy, setBusy] = useState(false);
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const refresh = useCallback(async () => {
    try {
      const r = await api.get("/admin/seed-status");
      setInfo({
        trustedMode: r.data.trustedMode,
        source: r.data.trustedModeSource,
        label: r.data.trustedModeLabel,
      });
    } catch {
      // Backend unreachable — fail silent, the banner just won't show
    }
  }, []);

  useEffect(() => { refresh(); }, [refresh]);

  const switchTo = async (enabled: boolean | null) => {
    setBusy(true);
    try {
      const url = enabled === null
        ? "/admin/trusted-mode"
        : `/admin/trusted-mode?enabled=${enabled}`;
      await api.post(url);
      await refresh();
    } catch (err) {
      alert(getErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  if (!info) return null;
  const isTrusted = info.trustedMode;

  return (
    <div
      className={`mb-4 p-3 rounded-md flex items-center gap-3 text-sm ${
        isTrusted
          ? "bg-violet-50 text-violet-800 border border-violet-200"
          : "bg-emerald-50 text-emerald-800 border border-emerald-200"
      }`}
    >
      <div className="flex-1">
        <div className="font-semibold flex items-center gap-2">
          {isTrusted ? "⚡ وضع Mavis كمحاسب" : "👤 وضع المحاسب البشري"}
          <span className="text-xs font-normal opacity-75">({info.source})</span>
        </div>
        <div className="text-xs mt-0.5">
          {isTrusted
            ? "القيود تُوافق وتُرحّل تلقائياً (تجريبي / Demo فقط)"
            : "كل قيد لازم توافق عليه وترحّله يدوياً (وضع الإنتاج الفعلي)"}
        </div>
      </div>
      {isTrusted ? (
        <button
          onClick={() => switchTo(false)}
          disabled={busy}
          className="px-3 py-1.5 rounded bg-emerald-600 text-white text-xs font-medium hover:bg-emerald-700 disabled:opacity-50"
        >
          {busy ? "..." : "← تحوّل للإنتاج البشري"}
        </button>
      ) : (
        <button
          onClick={() => switchTo(true)}
          disabled={busy}
          className="px-3 py-1.5 rounded bg-violet-600 text-white text-xs font-medium hover:bg-violet-700 disabled:opacity-50"
        >
          {busy ? "..." : "تحوّل للوضع التلقائي →"}
        </button>
      )}
    </div>
  );
}

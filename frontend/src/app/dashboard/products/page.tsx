"use client";

/**
 * Products Catalogue page.
 *
 * Each row is a reusable invoice line template: code, name,
 * default unit price, default tax rate. Picking a product on
 * an invoice auto-fills the description, unit_price, and
 * tax_rate so the user only enters a quantity.
 *
 * Products are per-company (the same code can mean different
 * products in HOLD vs CO-A). Soft-delete (is_active=false)
 * preserves historical invoice line references.
 */

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Package, Plus, Loader2, X, Pencil, Power } from "lucide-react";
import { formatNumber } from "@/lib/utils";

interface Product {
  id: string;
  companyId: string;
  code: string;
  name: string;
  nameAr?: string;
  unitPrice: number;
  defaultTaxRate: number;
  isActive: boolean;
  createdAt: string;
}

interface FormState {
  code: string;
  name: string;
  nameAr: string;
  unitPrice: number;
  defaultTaxRate: number;
}

const emptyForm: FormState = {
  code: "",
  name: "",
  nameAr: "",
  unitPrice: 0,
  defaultTaxRate: 0
};

export default function ProductsPage() {
  const { activeCompany } = useAuth();
  const [products, setProducts] = useState<Product[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<Product | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      // includeInactive=true so we can show deactivated products with a badge
      // (user can reactivate via the toggle).
      const res = await api.get(`/products?companyId=${activeCompany.id}&includeInactive=true`);
      setProducts(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  const openCreate = () => {
    setEditing(null);
    setForm(emptyForm);
    setError(null);
    setShowForm(true);
  };

  const openEdit = (p: Product) => {
    setEditing(p);
    setForm({
      code: p.code,
      name: p.name,
      nameAr: p.nameAr || "",
      unitPrice: p.unitPrice,
      defaultTaxRate: p.defaultTaxRate
    });
    setError(null);
    setShowForm(true);
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    setSubmitting(true);
    setError(null);
    try {
      if (editing) {
        await api.put(`/products/${editing.id}`, {
          code: form.code,
          name: form.name,
          nameAr: form.nameAr || null,
          unitPrice: form.unitPrice,
          defaultTaxRate: form.defaultTaxRate
        });
      } else {
        await api.post("/products", {
          companyId: activeCompany.id,
          code: form.code,
          name: form.name,
          nameAr: form.nameAr || null,
          unitPrice: form.unitPrice,
          defaultTaxRate: form.defaultTaxRate
        });
      }
      setShowForm(false);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const toggleActive = async (p: Product) => {
    const action = p.isActive ? "إيقاف" : "تفعيل";
    if (!confirm(`هل تريد ${action} المنتج "${p.name}"؟`)) return;
    try {
      await api.put(`/products/${p.id}`, { isActive: !p.isActive });
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
            <Package size={24} className="text-primary-600" />
            المنتجات
          </h1>
          <p className="text-sm text-ink-muted mt-1">
            كتالوج المنتجات والخدمات — تُستخدم لتعبئة بنود الفاتورة تلقائياً
          </p>
        </div>
        <button onClick={openCreate} className="btn-primary">
          <Plus size={18} />
          منتج جديد
        </button>
      </div>

      {error && !showForm && (
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
                <th>الكود</th>
                <th>الاسم</th>
                <th>الاسم بالعربي</th>
                <th>السعر الافتراضي</th>
                <th>الضريبة</th>
                <th>الحالة</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {products.map((p) => (
                <tr key={p.id} className={!p.isActive ? "opacity-60" : ""}>
                  <td className="font-mono font-semibold">{p.code}</td>
                  <td>{p.name}</td>
                  <td>{p.nameAr || <span className="text-ink-subtle">—</span>}</td>
                  <td className="font-mono" dir="ltr">{formatNumber(p.unitPrice)}</td>
                  <td className="font-mono" dir="ltr">{(p.defaultTaxRate * 100).toFixed(1)}%</td>
                  <td>
                    {p.isActive ? (
                      <span className="badge badge-success">نشط</span>
                    ) : (
                      <span className="badge badge-secondary">موقوف</span>
                    )}
                  </td>
                  <td>
                    <div className="flex items-center gap-1">
                      <button
                        onClick={() => openEdit(p)}
                        className="text-primary-600 hover:bg-primary-50 p-1 rounded"
                        title="تعديل"
                      >
                        <Pencil size={14} />
                      </button>
                      <button
                        onClick={() => toggleActive(p)}
                        className="text-ink-muted hover:bg-raised p-1 rounded"
                        title={p.isActive ? "إيقاف" : "تفعيل"}
                      >
                        <Power size={14} />
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
              {products.length === 0 && (
                <tr>
                  <td colSpan={7} className="text-center text-ink-muted py-6">
                    لا توجد منتجات — أضف أول منتج للبدء
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-md p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">
                {editing ? "تعديل منتج" : "منتج جديد"}
              </h2>
              <button
                onClick={() => setShowForm(false)}
                className="text-ink-subtle hover:text-ink-muted"
              >
                <X size={20} />
              </button>
            </div>

            <form onSubmit={submit} className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">الكود *</label>
                <input
                  className="input"
                  value={form.code}
                  onChange={(e) => setForm({ ...form, code: e.target.value })}
                  required
                  placeholder="e.g., SRV-001"
                  dir="ltr"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم (English) *</label>
                <input
                  className="input"
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  required
                  placeholder="e.g., Consulting hours"
                  dir="ltr"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم (عربي)</label>
                <input
                  className="input"
                  value={form.nameAr}
                  onChange={(e) => setForm({ ...form, nameAr: e.target.value })}
                  placeholder="ساعات استشارة"
                />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">السعر الافتراضي *</label>
                  <input
                    type="number"
                    step="0.01"
                    min="0"
                    className="input"
                    value={form.unitPrice}
                    onChange={(e) => setForm({ ...form, unitPrice: Number(e.target.value) })}
                    required
                    dir="ltr"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الضريبة %</label>
                  <input
                    type="number"
                    step="0.01"
                    min="0"
                    className="input"
                    value={(form.defaultTaxRate * 100).toFixed(2)}
                    onChange={(e) => setForm({ ...form, defaultTaxRate: Number(e.target.value) / 100 })}
                    dir="ltr"
                    placeholder="e.g., 15"
                  />
                </div>
              </div>

              {error && (
                <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>
              )}

              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : editing ? "حفظ التعديلات" : "إضافة المنتج"}
                </button>
                <button
                  type="button"
                  onClick={() => setShowForm(false)}
                  className="btn-secondary"
                >
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

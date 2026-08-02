"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Building2, Plus, Loader2, X } from "lucide-react";

interface Company {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  parentId?: string;
  isHolding: boolean;
  baseCurrency: string;
  isActive: boolean;
  createdAt: string;
}

export default function CompaniesPage() {
  const { user } = useAuth();
  const [companies, setCompanies] = useState<Company[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [form, setForm] = useState({
    code: "",
    name: "",
    nameAr: "",
    parentId: "",
    isHolding: false
  });

  const load = async () => {
    try {
      setLoading(true);
      const res = await api.get("/companies");
      setCompanies(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await api.post("/companies", {
        code: form.code,
        name: form.name,
        nameAr: form.nameAr || null,
        parentId: form.parentId || null,
        isHolding: form.isHolding,
        baseCurrency: "LYD"
      });
      setForm({ code: "", name: "", nameAr: "", parentId: "", isHolding: false });
      setShowForm(false);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">الشركات</h1>
          <p className="text-sm text-gray-600 mt-1">إدارة الشركة القابضة والشركات التابعة</p>
        </div>
        {user?.isSuperAdmin && (
          <button onClick={() => setShowForm(true)} className="btn-primary">
            <Plus size={18} />
            شركة جديدة
          </button>
        )}
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

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
                <th>النوع</th>
                <th>العملة</th>
                <th>الحالة</th>
              </tr>
            </thead>
            <tbody>
              {companies.map((c) => (
                <tr key={c.id}>
                  <td className="font-mono">{c.code}</td>
                  <td className="font-semibold">{c.name}</td>
                  <td>{c.nameAr || "-"}</td>
                  <td>
                    {c.isHolding ? (
                      <span className="badge badge-info">قابضة</span>
                    ) : (
                      <span className="badge badge-success">تابعة</span>
                    )}
                  </td>
                  <td className="font-mono">{c.baseCurrency}</td>
                  <td>
                    {c.isActive ? (
                      <span className="badge badge-success">نشطة</span>
                    ) : (
                      <span className="badge badge-danger">معطلة</span>
                    )}
                  </td>
                </tr>
              ))}
              {companies.length === 0 && (
                <tr>
                  <td colSpan={6} className="text-center text-gray-500 py-6">لا توجد شركات</td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-md p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">شركة جديدة</h2>
              <button onClick={() => setShowForm(false)} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submit} className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">الكود *</label>
                <input className="input" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} required />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم بالإنجليزية *</label>
                <input className="input" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم بالعربية</label>
                <input className="input" value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الشركة الأم (اختياري)</label>
                <select className="input" value={form.parentId} onChange={(e) => setForm({ ...form, parentId: e.target.value })}>
                  <option value="">- لا يوجد (شركة قابضة) -</option>
                  {companies.filter((c) => c.isHolding).map((c) => (
                    <option key={c.id} value={c.id}>{c.nameAr || c.name}</option>
                  ))}
                </select>
              </div>
              <div className="flex items-center gap-2">
                <input
                  type="checkbox"
                  id="isHolding"
                  checked={form.isHolding}
                  onChange={(e) => setForm({ ...form, isHolding: e.target.checked })}
                />
                <label htmlFor="isHolding" className="text-sm">شركة قابضة</label>
              </div>
              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : "حفظ"}
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

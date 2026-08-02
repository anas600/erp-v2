"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { BookOpen, Plus, Loader2, X } from "lucide-react";
import { formatNumber } from "@/lib/utils";

interface Account {
  id: string;
  companyId: string;
  code: string;
  name: string;
  nameAr?: string;
  parentId?: string;
  accountType: string;
  nature: string;
  isActive: boolean;
  balance: number;
}

const TYPE_LABELS: Record<string, string> = {
  Asset: "أصول",
  Liability: "خصوم",
  Equity: "حقوق ملكية",
  Revenue: "إيرادات",
  Expense: "مصروفات"
};

const TYPE_COLORS: Record<string, string> = {
  Asset: "bg-green-100 text-green-700",
  Liability: "bg-red-100 text-red-700",
  Equity: "bg-blue-100 text-blue-700",
  Revenue: "bg-purple-100 text-purple-700",
  Expense: "bg-orange-100 text-orange-700"
};

export default function AccountsPage() {
  const { activeCompany } = useAuth();
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [form, setForm] = useState({
    code: "",
    name: "",
    nameAr: "",
    parentId: "",
    accountType: "Asset",
    nature: "Debit"
  });

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(`/accounts?companyId=${activeCompany.id}`);
      setAccounts(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    setSubmitting(true);
    setError(null);
    try {
      await api.post("/accounts", {
        companyId: activeCompany.id,
        code: form.code,
        name: form.name,
        nameAr: form.nameAr || null,
        parentId: form.parentId || null,
        accountType: form.accountType,
        nature: form.nature
      });
      setForm({ code: "", name: "", nameAr: "", parentId: "", accountType: "Asset", nature: "Debit" });
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
          <h1 className="text-2xl font-bold text-gray-900">شجرة الحسابات</h1>
          <p className="text-sm text-gray-600 mt-1">
            الحسابات المحاسبية للشركة الحالية — {activeCompany?.nameAr || activeCompany?.name}
          </p>
        </div>
        <button onClick={() => setShowForm(true)} className="btn-primary">
          <Plus size={18} />
          حساب جديد
        </button>
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
                <th>الطبيعة</th>
                <th>الرصيد</th>
              </tr>
            </thead>
            <tbody>
              {accounts.map((a) => (
                <tr key={a.id}>
                  <td className="font-mono font-semibold">{a.code}</td>
                  <td>{a.name}</td>
                  <td>{a.nameAr || "-"}</td>
                  <td>
                    <span className={`badge ${TYPE_COLORS[a.accountType]}`}>
                      {TYPE_LABELS[a.accountType]}
                    </span>
                  </td>
                  <td>
                    {a.nature === "Debit" ? (
                      <span className="badge badge-info">مدين</span>
                    ) : (
                      <span className="badge badge-warning">دائن</span>
                    )}
                  </td>
                  <td className="font-mono" dir="ltr">{formatNumber(a.balance)}</td>
                </tr>
              ))}
              {accounts.length === 0 && (
                <tr>
                  <td colSpan={6} className="text-center text-gray-500 py-6">لا توجد حسابات</td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-lg p-6 max-h-[90vh] overflow-y-auto">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">حساب جديد</h2>
              <button onClick={() => setShowForm(false)} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submit} className="space-y-3">
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">الكود *</label>
                  <input className="input" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} required placeholder="e.g., 1100-01" />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الحساب الأب (اختياري)</label>
                  <select className="input" value={form.parentId} onChange={(e) => setForm({ ...form, parentId: e.target.value })}>
                    <option value="">- حساب رئيسي -</option>
                    {accounts.map((a) => (
                      <option key={a.id} value={a.id}>{a.code} - {a.nameAr || a.name}</option>
                    ))}
                  </select>
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم بالإنجليزية *</label>
                <input className="input" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم بالعربية</label>
                <input className="input" value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">النوع *</label>
                  <select className="input" value={form.accountType} onChange={(e) => setForm({ ...form, accountType: e.target.value })}>
                    <option value="Asset">أصول</option>
                    <option value="Liability">خصوم</option>
                    <option value="Equity">حقوق ملكية</option>
                    <option value="Revenue">إيرادات</option>
                    <option value="Expense">مصروفات</option>
                  </select>
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الطبيعة *</label>
                  <select className="input" value={form.nature} onChange={(e) => setForm({ ...form, nature: e.target.value })}>
                    <option value="Debit">مدين</option>
                    <option value="Credit">دائن</option>
                  </select>
                </div>
              </div>
              <div className="text-xs text-gray-500 bg-gray-50 p-2 rounded">
                💡 <strong>قاعدة الطبيعة:</strong> الحساب من نوع Asset/Expense طبيعته Debit.
                الحساب من نوع Liability/Equity/Revenue طبيعته Credit.
                الحسابات المعكوسة (مثل مجمع الإهلاك) تكون طبيعتها معكوسة.
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

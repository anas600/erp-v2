"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Users, Plus, Loader2, X, Key, Trash2, Edit2 } from "lucide-react";
import { formatDate } from "@/lib/utils";

interface UserCompanyMembership {
  companyId: string;
  companyCode: string;
  companyName: string;
  companyNameAr?: string;
  roleId: string;
  roleName: string;
  roleNameAr?: string;
  isPrimary: boolean;
}

interface User {
  id: string;
  email: string;
  fullName?: string;
  fullNameAr?: string;
  isSuperAdmin: boolean;
  isActive: boolean;
  createdAt: string;
  companies: UserCompanyMembership[];
}

interface Company { id: string; code: string; name: string; nameAr?: string; }
interface Role { id: string; name: string; displayName: string; displayNameAr?: string; }

export default function UsersPage() {
  const { user: currentUser } = useAuth();
  const [users, setUsers] = useState<User[]>([]);
  const [companies, setCompanies] = useState<Company[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [showPasswordForm, setShowPasswordForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [form, setForm] = useState({
    email: "",
    password: "",
    fullName: "",
    fullNameAr: "",
    companies: [] as { companyId: string; roleId: string; isPrimary: boolean }[]
  });

  const [passwordForm, setPasswordForm] = useState({
    currentPassword: "",
    newPassword: ""
  });

  const load = async () => {
    try {
      setLoading(true);
      const [usersRes, companiesRes] = await Promise.all([
        api.get("/users"),
        api.get("/companies")
      ]);
      setUsers(usersRes.data);
      setCompanies(companiesRes.data);

      // Hard-coded role list (we don't have a /roles endpoint yet, so use known ones)
      setRoles([
        { id: "00000000-0000-0000-0000-000000000001", name: "super_admin", displayName: "مدير عام", displayNameAr: "مدير عام" },
        { id: "00000000-0000-0000-0000-000000000002", name: "holding_admin", displayName: "مدير قابضة", displayNameAr: "مدير قابضة" },
        { id: "00000000-0000-0000-0000-000000000003", name: "company_admin", displayName: "مدير شركة", displayNameAr: "مدير شركة" },
        { id: "00000000-0000-0000-0000-000000000004", name: "accountant", displayName: "محاسب", displayNameAr: "محاسب" },
        { id: "00000000-0000-0000-0000-000000000005", name: "project_engineer", displayName: "مهندس مشاريع", displayNameAr: "مهندس مشاريع" },
        { id: "00000000-0000-0000-0000-000000000006", name: "viewer", displayName: "مشاهد", displayNameAr: "مشاهد" }
      ]);
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
      await api.post("/users", form);
      setForm({ email: "", password: "", fullName: "", fullNameAr: "", companies: [] });
      setShowForm(false);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const toggleUserStatus = async (u: User) => {
    try {
      await api.put(`/users/${u.id}`, { isActive: !u.isActive });
      await load();
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const changePassword = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      await api.post("/users/me/change-password", passwordForm);
      alert("تم تغيير كلمة المرور بنجاح");
      setPasswordForm({ currentPassword: "", newPassword: "" });
      setShowPasswordForm(false);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const addCompanyMembership = () => {
    setForm({
      ...form,
      companies: [...form.companies, { companyId: "", roleId: "", isPrimary: form.companies.length === 0 }]
    });
  };

  const removeCompanyMembership = (idx: number) => {
    setForm({ ...form, companies: form.companies.filter((_, i) => i !== idx) });
  };

  const updateCompanyMembership = (idx: number, field: string, value: any) => {
    const newComps = [...form.companies];
    newComps[idx] = { ...newComps[idx], [field]: value };
    setForm({ ...form, companies: newComps });
  };

  if (currentUser && !currentUser.isSuperAdmin) {
    return (
      <div>
        <h1 className="text-2xl font-bold text-gray-900 mb-4">المستخدمون</h1>
        <div className="card text-center text-gray-500">
          ليس لديك صلاحية لعرض هذه الصفحة (تحتاج إلى صلاحية مدير عام)
        </div>
      </div>
    );
  }

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Users size={24} className="text-primary-600" />
            المستخدمون والصلاحيات
          </h1>
          <p className="text-sm text-gray-600 mt-1">إدارة المستخدمين والأدوار والشركات</p>
        </div>
        <div className="flex gap-2">
          <button onClick={() => setShowPasswordForm(true)} className="btn-secondary">
            <Key size={18} />
            تغيير كلمة المرور
          </button>
          <button onClick={() => setShowForm(true)} className="btn-primary">
            <Plus size={18} />
            مستخدم جديد
          </button>
        </div>
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
                <th>البريد</th>
                <th>الاسم</th>
                <th>الحالة</th>
                <th>الشركات</th>
                <th>تاريخ الإنشاء</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td dir="ltr" className="font-mono text-sm">{u.email}</td>
                  <td>{u.fullNameAr || u.fullName || "-"}</td>
                  <td>
                    {u.isSuperAdmin ? (
                      <span className="badge badge-info">مدير عام</span>
                    ) : u.isActive ? (
                      <span className="badge badge-success">نشط</span>
                    ) : (
                      <span className="badge badge-danger">معطل</span>
                    )}
                  </td>
                  <td>
                    <div className="text-xs space-y-1">
                      {u.companies.map((c, i) => (
                        <div key={i}>
                          <span className="font-semibold">{c.companyNameAr || c.companyName}</span>
                          {" · "}
                          <span className="text-gray-600">{c.roleNameAr || c.roleName}</span>
                          {c.isPrimary && <span className="badge badge-info mr-1">رئيسية</span>}
                        </div>
                      ))}
                    </div>
                  </td>
                  <td className="text-xs">{formatDate(u.createdAt)}</td>
                  <td>
                    {!u.isSuperAdmin && (
                      <button
                        onClick={() => toggleUserStatus(u)}
                        className={`text-sm px-2 py-1 rounded ${
                          u.isActive
                            ? "text-red-600 hover:bg-red-50"
                            : "text-green-600 hover:bg-green-50"
                        }`}
                      >
                        {u.isActive ? "تعطيل" : "تفعيل"}
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4 overflow-y-auto">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-2xl p-6 my-8">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">مستخدم جديد</h2>
              <button onClick={() => setShowForm(false)} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submit} className="space-y-3">
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">البريد الإلكتروني *</label>
                  <input type="email" className="input" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })} required dir="ltr" />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">كلمة المرور *</label>
                  <input type="password" className="input" value={form.password} onChange={(e) => setForm({ ...form, password: e.target.value })} required minLength={6} dir="ltr" />
                </div>
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">الاسم (English)</label>
                  <input className="input" value={form.fullName} onChange={(e) => setForm({ ...form, fullName: e.target.value })} />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الاسم (عربي)</label>
                  <input className="input" value={form.fullNameAr} onChange={(e) => setForm({ ...form, fullNameAr: e.target.value })} />
                </div>
              </div>

              <div>
                <div className="flex items-center justify-between mb-2">
                  <label className="text-sm font-semibold">العضويات (شركة + دور) *</label>
                  <button type="button" onClick={addCompanyMembership} className="text-sm text-primary-600 hover:underline">
                    + إضافة شركة
                  </button>
                </div>
                <div className="space-y-2">
                  {form.companies.map((comp, idx) => (
                    <div key={idx} className="grid grid-cols-12 gap-2 items-center">
                      <select
                        className="input col-span-5"
                        value={comp.companyId}
                        onChange={(e) => updateCompanyMembership(idx, "companyId", e.target.value)}
                        required
                      >
                        <option value="">- اختر شركة -</option>
                        {companies.map((c) => (
                          <option key={c.id} value={c.id}>{c.code} - {c.nameAr || c.name}</option>
                        ))}
                      </select>
                      <select
                        className="input col-span-5"
                        value={comp.roleId}
                        onChange={(e) => updateCompanyMembership(idx, "roleId", e.target.value)}
                        required
                      >
                        <option value="">- اختر دور -</option>
                        {roles.map((r) => (
                          <option key={r.id} value={r.id}>{r.displayNameAr || r.displayName}</option>
                        ))}
                      </select>
                      <label className="col-span-1 flex items-center gap-1 text-xs">
                        <input
                          type="checkbox"
                          checked={comp.isPrimary}
                          onChange={(e) => updateCompanyMembership(idx, "isPrimary", e.target.checked)}
                        />
                        رئيسية
                      </label>
                      <button type="button" onClick={() => removeCompanyMembership(idx)} className="text-red-500 hover:text-red-700 col-span-1">
                        <X size={16} />
                      </button>
                    </div>
                  ))}
                  {form.companies.length === 0 && (
                    <p className="text-xs text-gray-500 text-center py-2">لم تتم إضافة شركات بعد</p>
                  )}
                </div>
              </div>

              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الإنشاء..." : "إنشاء المستخدم"}
                </button>
                <button type="button" onClick={() => setShowForm(false)} className="btn-secondary">إلغاء</button>
              </div>
            </form>
          </div>
        </div>
      )}

      {showPasswordForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-md p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">تغيير كلمة المرور</h2>
              <button onClick={() => setShowPasswordForm(false)} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={changePassword} className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">كلمة المرور الحالية *</label>
                <input type="password" className="input" value={passwordForm.currentPassword} onChange={(e) => setPasswordForm({ ...passwordForm, currentPassword: e.target.value })} required dir="ltr" />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">كلمة المرور الجديدة * (6 أحرف على الأقل)</label>
                <input type="password" className="input" value={passwordForm.newPassword} onChange={(e) => setPasswordForm({ ...passwordForm, newPassword: e.target.value })} required minLength={6} dir="ltr" />
              </div>
              {error && <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}
              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري التغيير..." : "تغيير"}
                </button>
                <button type="button" onClick={() => setShowPasswordForm(false)} className="btn-secondary">إلغاء</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}

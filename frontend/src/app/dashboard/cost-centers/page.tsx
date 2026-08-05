"use client";

/**
 * Cost Centers page.
 *
 * Cost Centers are used to tag journal entry lines with the project,
 * department, or activity they relate to. Combined with the 4-level
 * Chart of Accounts (Type → Category → Sub-category → Detail/sub-ledger),
 * they let you answer questions like "what did Project Alpha cost us
 * last quarter" without posting to a separate set of accounts.
 *
 * Three types of cost centers:
 *   - project    (links to an existing project record)
 *   - department (e.g. "الإدارة", "المبيعات")
 *   - activity   (e.g. "تأجير معدات", "خدمات استشارية")
 *
 * Soft delete (is_active = false) preserves historical journal line
 * references: a 2024 line tagged "Project Alpha" still resolves to the
 * same cost center, even if you renamed it in 2026.
 */

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Building2, Plus, Loader2, X, Pencil, Power } from "lucide-react";

interface CostCenter {
  id: string;
  companyId: string;
  code: string;
  name: string;
  nameAr?: string;
  type: "project" | "department" | "activity";
  projectId?: string | null;
  parentId?: string | null;
  isActive: boolean;
  createdAt: string;
}

interface FormState {
  code: string;
  name: string;
  nameAr: string;
  type: "project" | "department" | "activity";
  projectId: string;
  parentId: string;
}

const emptyForm: FormState = {
  code: "",
  name: "",
  nameAr: "",
  type: "project",
  projectId: "",
  parentId: ""
};

const TYPE_LABEL: Record<FormState["type"], string> = {
  project: "مشروع",
  department: "إدارة",
  activity: "نشاط"
};

export default function CostCentersPage() {
  const { activeCompany } = useAuth();
  const [items, setItems] = useState<CostCenter[]>([]);
  const [projects, setProjects] = useState<{ id: string; name: string }[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<CostCenter | null>(null);
  const [form, setForm] = useState<FormState>(emptyForm);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(
        `/cost-centers?companyId=${activeCompany.id}&includeInactive=true`
      );
      setItems(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  const loadProjects = async () => {
    if (!activeCompany) return;
    try {
      const res = await api.get(`/projects?companyId=${activeCompany.id}`);
      setProjects(res.data);
    } catch {
      // projects are optional; silent fail
    }
  };

  useEffect(() => {
    load();
    loadProjects();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeCompany]);

  const openCreate = () => {
    setEditing(null);
    setForm(emptyForm);
    setError(null);
    setShowForm(true);
  };

  const openEdit = (c: CostCenter) => {
    setEditing(c);
    setForm({
      code: c.code,
      name: c.name,
      nameAr: c.nameAr || "",
      type: c.type,
      projectId: c.projectId || "",
      parentId: c.parentId || ""
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
        await api.put(`/cost-centers/${editing.id}`, {
          name: form.name,
          nameAr: form.nameAr || null,
          isActive: editing.isActive
        });
      } else {
        await api.post("/cost-centers", {
          companyId: activeCompany.id,
          code: form.code,
          name: form.name,
          nameAr: form.nameAr || null,
          type: form.type,
          projectId: form.projectId || null,
          parentId: form.parentId || null
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

  const toggleActive = async (c: CostCenter) => {
    const action = c.isActive ? "إيقاف" : "تفعيل";
    if (!confirm(`هل تريد ${action} مركز التكلفة "${c.nameAr || c.name}"؟`)) return;
    try {
      await api.put(`/cost-centers/${c.id}`, { isActive: !c.isActive });
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
            <Building2 size={24} className="text-primary-600" />
            مراكز التكلفة
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            مشاريع وإدارات وأنشطة — تُستخدم لربط بنود القيد بالمركز المعني
          </p>
        </div>
        <button onClick={openCreate} className="btn-primary">
          <Plus size={18} />
          مركز تكلفة جديد
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
                <th>النوع</th>
                <th>المشروع</th>
                <th>الحالة</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {items.map((c) => {
                const proj = projects.find((p) => p.id === c.projectId);
                return (
                  <tr key={c.id} className={!c.isActive ? "opacity-60" : ""}>
                    <td className="font-mono font-semibold">{c.code}</td>
                    <td>{c.name}</td>
                    <td>{c.nameAr || <span className="text-gray-400">—</span>}</td>
                    <td>
                      <span className="badge badge-info">{TYPE_LABEL[c.type]}</span>
                    </td>
                    <td>
                      {proj ? (
                        proj.name
                      ) : (
                        <span className="text-gray-400">—</span>
                      )}
                    </td>
                    <td>
                      {c.isActive ? (
                        <span className="badge badge-success">نشط</span>
                      ) : (
                        <span className="badge badge-secondary">موقوف</span>
                      )}
                    </td>
                    <td>
                      <div className="flex items-center gap-1">
                        <button
                          onClick={() => openEdit(c)}
                          className="text-primary-600 hover:bg-primary-50 p-1 rounded"
                          title="تعديل"
                        >
                          <Pencil size={14} />
                        </button>
                        <button
                          onClick={() => toggleActive(c)}
                          className="text-gray-600 hover:bg-gray-50 p-1 rounded"
                          title={c.isActive ? "إيقاف" : "تفعيل"}
                        >
                          <Power size={14} />
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
              {items.length === 0 && (
                <tr>
                  <td colSpan={7} className="text-center text-gray-500 py-6">
                    لا توجد مراكز تكلفة بعد — أضف أول مركز للبدء
                  </td>
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
              <h2 className="text-lg font-semibold">
                {editing ? "تعديل مركز تكلفة" : "مركز تكلفة جديد"}
              </h2>
              <button
                onClick={() => setShowForm(false)}
                className="text-gray-400 hover:text-gray-600"
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
                  placeholder="e.g., CC-001"
                  dir="ltr"
                  disabled={!!editing}
                />
                {editing && (
                  <p className="text-xs text-gray-500 mt-1">لا يمكن تغيير الكود بعد الإنشاء</p>
                )}
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم (English) *</label>
                <input
                  className="input"
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  required
                  placeholder="e.g., Project Alpha"
                  dir="ltr"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم (عربي)</label>
                <input
                  className="input"
                  value={form.nameAr}
                  onChange={(e) => setForm({ ...form, nameAr: e.target.value })}
                  placeholder="مشروع ألفا"
                />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">النوع *</label>
                <select
                  className="input"
                  value={form.type}
                  onChange={(e) =>
                    setForm({ ...form, type: e.target.value as FormState["type"] })
                  }
                  required
                  disabled={!!editing}
                >
                  <option value="project">مشروع</option>
                  <option value="department">إدارة</option>
                  <option value="activity">نشاط</option>
                </select>
              </div>
              {form.type === "project" && projects.length > 0 && (
                <div>
                  <label className="block text-sm font-medium mb-1">المشروع</label>
                  <select
                    className="input"
                    value={form.projectId}
                    onChange={(e) => setForm({ ...form, projectId: e.target.value })}
                    disabled={!!editing}
                  >
                    <option value="">— لا يوجد —</option>
                    {projects.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.name}
                      </option>
                    ))}
                  </select>
                </div>
              )}

              {error && (
                <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>
              )}

              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : editing ? "حفظ التعديلات" : "إضافة مركز التكلفة"}
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

"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Folder, Plus, Loader2, X, CheckCircle, Trash2 } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface Milestone {
  id: string;
  name: string;
  nameAr?: string;
  description?: string;
  amount: number;
  status: "pending" | "completed";
  targetDate?: string;
  completedAt?: string;
  orderIndex: number;
}

interface Project {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  description?: string;
  status: "active" | "completed" | "on_hold" | "cancelled";
  startDate?: string;
  endDate?: string;
  budget: number;
  actualCost: number;
  notes?: string;
  milestones: Milestone[];
}

export default function ProjectsPage() {
  const { activeCompany } = useAuth();
  const [projects, setProjects] = useState<Project[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [form, setForm] = useState({
    code: "",
    name: "",
    nameAr: "",
    description: "",
    startDate: "",
    endDate: "",
    budget: 0
  });

  const [milestoneForm, setMilestoneForm] = useState({
    projectId: "",
    name: "",
    nameAr: "",
    amount: 0,
    targetDate: ""
  });

  const [showMilestoneForm, setShowMilestoneForm] = useState(false);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(`/projects?companyId=${activeCompany.id}`);
      setProjects(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  const submitProject = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    setSubmitting(true);
    setError(null);
    try {
      await api.post("/projects", {
        companyId: activeCompany.id,
        code: form.code,
        name: form.name,
        nameAr: form.nameAr || null,
        description: form.description || null,
        startDate: form.startDate || null,
        endDate: form.endDate || null,
        budget: form.budget,
        notes: null
      });
      setForm({ code: "", name: "", nameAr: "", description: "", startDate: "", endDate: "", budget: 0 });
      setShowForm(false);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const addMilestone = async (e: React.FormEvent) => {
    e.preventDefault();
    setSubmitting(true);
    setError(null);
    try {
      const orderIndex = projects.find(p => p.id === milestoneForm.projectId)?.milestones.length ?? 0;
      await api.post(`/projects/${milestoneForm.projectId}/milestones`, {
        name: milestoneForm.name,
        nameAr: milestoneForm.nameAr || null,
        amount: milestoneForm.amount,
        targetDate: milestoneForm.targetDate || null,
        orderIndex
      });
      setMilestoneForm({ projectId: "", name: "", nameAr: "", amount: 0, targetDate: "" });
      setShowMilestoneForm(false);
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const completeMilestone = async (projectId: string, milestoneId: string) => {
    if (!confirm("هل تريد إكمال هذه المرحلة؟ سيتم إنشاء قيد يومية تلقائياً عبر محرك القواعد.")) return;
    try {
      const res = await api.post(`/projects/${projectId}/milestones/${milestoneId}/complete`);
      alert(`تم إكمال المرحلة! تم إنشاء ${res.data.journalEntriesCreated} قيد يومية.`);
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
            <Folder size={24} className="text-primary-600" />
            المشاريع
          </h1>
          <p className="text-sm text-gray-600 mt-1">إدارة المشاريع والمراحل</p>
        </div>
        <div className="flex gap-2">
          <button onClick={() => setShowMilestoneForm(true)} className="btn-secondary">
            + مرحلة
          </button>
          <button onClick={() => setShowForm(true)} className="btn-primary">
            <Plus size={18} />
            مشروع جديد
          </button>
        </div>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {loading ? (
          <div className="col-span-2 flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : projects.length === 0 ? (
          <div className="col-span-2 text-center text-gray-500 py-8 card">
            لا توجد مشاريع. أنشئ مشروعك الأول للبدء.
          </div>
        ) : (
          projects.map((p) => (
            <div key={p.id} className="card">
              <div className="flex items-start justify-between mb-2">
                <div>
                  <h3 className="font-semibold text-lg">{p.nameAr || p.name}</h3>
                  <p className="text-xs text-gray-500 font-mono">{p.code}</p>
                </div>
                <span className={
                  p.status === "active" ? "badge badge-success" :
                  p.status === "completed" ? "badge badge-info" :
                  p.status === "on_hold" ? "badge badge-warning" :
                  "badge badge-danger"
                }>
                  {p.status === "active" ? "نشط" :
                   p.status === "completed" ? "مكتمل" :
                   p.status === "on_hold" ? "متوقف" : "ملغي"}
                </span>
              </div>

              {p.description && <p className="text-sm text-gray-600 mb-2">{p.description}</p>}

              <div className="grid grid-cols-2 gap-2 text-sm mb-3">
                <div>
                  <span className="text-gray-500">الميزانية:</span>{" "}
                  <span className="font-mono font-semibold" dir="ltr">{formatNumber(p.budget)}</span>
                </div>
                <div>
                  <span className="text-gray-500">التكلفة الفعلية:</span>{" "}
                  <span className="font-mono font-semibold" dir="ltr">{formatNumber(p.actualCost)}</span>
                </div>
              </div>

              {p.milestones.length > 0 && (
                <div className="border-t pt-2 mt-2">
                  <p className="text-xs text-gray-500 mb-1 font-semibold">المراحل ({p.milestones.filter(m => m.status === "completed").length}/{p.milestones.length}):</p>
                  <ul className="space-y-1">
                    {p.milestones.map((m) => (
                      <li key={m.id} className="flex items-center justify-between text-sm">
                        <div className="flex items-center gap-2">
                          {m.status === "completed" ? (
                            <CheckCircle size={14} className="text-green-600" />
                          ) : (
                            <div className="w-3.5 h-3.5 rounded-full border-2 border-gray-300" />
                          )}
                          <span className={m.status === "completed" ? "line-through text-gray-500" : ""}>
                            {m.nameAr || m.name}
                          </span>
                        </div>
                        <div className="flex items-center gap-2">
                          <span className="font-mono text-xs text-gray-600" dir="ltr">
                            {formatNumber(m.amount)}
                          </span>
                          {m.status === "pending" && (
                            <button
                              onClick={() => completeMilestone(p.id, m.id)}
                              className="text-xs text-primary-600 hover:bg-primary-50 px-2 py-0.5 rounded"
                            >
                              إكمال
                            </button>
                          )}
                        </div>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
            </div>
          ))
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-lg p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">مشروع جديد</h2>
              <button onClick={() => setShowForm(false)} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submitProject} className="space-y-3">
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">الكود *</label>
                  <input className="input" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} required />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الميزانية</label>
                  <input type="number" className="input" value={form.budget} onChange={(e) => setForm({ ...form, budget: Number(e.target.value) })} dir="ltr" />
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم (English) *</label>
                <input className="input" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} required />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم (عربي)</label>
                <input className="input" value={form.nameAr} onChange={(e) => setForm({ ...form, nameAr: e.target.value })} />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الوصف</label>
                <textarea className="input" rows={2} value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">تاريخ البداية</label>
                  <input type="date" className="input" value={form.startDate} onChange={(e) => setForm({ ...form, startDate: e.target.value })} />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">تاريخ النهاية</label>
                  <input type="date" className="input" value={form.endDate} onChange={(e) => setForm({ ...form, endDate: e.target.value })} />
                </div>
              </div>
              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : "حفظ"}
                </button>
                <button type="button" onClick={() => setShowForm(false)} className="btn-secondary">إلغاء</button>
              </div>
            </form>
          </div>
        </div>
      )}

      {showMilestoneForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-md p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold">مرحلة جديدة</h2>
              <button onClick={() => setShowMilestoneForm(false)} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={addMilestone} className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">المشروع *</label>
                <select
                  className="input"
                  value={milestoneForm.projectId}
                  onChange={(e) => setMilestoneForm({ ...milestoneForm, projectId: e.target.value })}
                  required
                >
                  <option value="">- اختر مشروع -</option>
                  {projects.map((p) => (
                    <option key={p.id} value={p.id}>{p.nameAr || p.name}</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">اسم المرحلة (English) *</label>
                <input className="input" value={milestoneForm.name} onChange={(e) => setMilestoneForm({ ...milestoneForm, name: e.target.value })} required />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الاسم (عربي)</label>
                <input className="input" value={milestoneForm.nameAr} onChange={(e) => setMilestoneForm({ ...milestoneForm, nameAr: e.target.value })} />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">المبلغ *</label>
                <input type="number" className="input" value={milestoneForm.amount} onChange={(e) => setMilestoneForm({ ...milestoneForm, amount: Number(e.target.value) })} dir="ltr" required />
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">تاريخ مستهدف</label>
                <input type="date" className="input" value={milestoneForm.targetDate} onChange={(e) => setMilestoneForm({ ...milestoneForm, targetDate: e.target.value })} />
              </div>
              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : "حفظ"}
                </button>
                <button type="button" onClick={() => setShowMilestoneForm(false)} className="btn-secondary">إلغاء</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}

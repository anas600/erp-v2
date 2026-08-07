"use client";

/**
 * Sprint 35 — New Project page.
 *
 * Form with all 11 fields from the backend CreateProjectRequest.
 * On submit, POSTs to /api/projects then routes to the detail
 * page for the new project.
 *
 * Notes:
 *   - The 4 type options match backend ProjectModels.ProjectDto.Type
 *   - The 5 status options match the new default of "draft"
 *   - customerId is loaded from /api/contacts?type=customer
 *   - All numbers are formatted in the parent's text direction
 *     (LTR) so decimal alignment is consistent
 */
import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { ArrowRight, Loader2, Save, FolderKanban } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";

interface Customer {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
}

interface FormState {
  code: string;
  name: string;
  nameAr: string;
  type: string;
  status: string;
  customerId: string;
  contractValue: number;
  startDate: string;
  expectedEndDate: string;
  projectManager: string;
  location: string;
  description: string;
  notes: string;
}

const EMPTY: FormState = {
  code: "",
  name: "",
  nameAr: "",
  type: "construction",
  status: "draft",
  customerId: "",
  contractValue: 0,
  startDate: new Date().toISOString().slice(0, 10),
  expectedEndDate: "",
  projectManager: "",
  location: "",
  description: "",
  notes: "",
};

export default function NewProjectPage() {
  const router = useRouter();
  const { activeCompany } = useAuth();
  const [form, setForm] = useState<FormState>(EMPTY);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!activeCompany) return;
    api
      .get(`/contacts?companyId=${activeCompany.id}&type=customer&limit=200`)
      .then((res) => setCustomers(res.data || []))
      .catch(() => setCustomers([]));
  }, [activeCompany]);

  const set = <K extends keyof FormState>(k: K, v: FormState[K]) =>
    setForm((prev) => ({ ...prev, [k]: v }));

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    if (!form.code.trim()) { setError("كود المشروع مطلوب"); return; }
    if (!form.name.trim()) { setError("اسم المشروع مطلوب"); return; }
    setSubmitting(true);
    setError(null);
    try {
      const res = await api.post("/projects", {
        companyId: activeCompany.id,
        code: form.code.trim(),
        name: form.name.trim(),
        nameAr: form.nameAr.trim() || null,
        type: form.type,
        status: form.status,
        customerId: form.customerId || null,
        contractValue: Number(form.contractValue) || 0,
        startDate: form.startDate || null,
        expectedEndDate: form.expectedEndDate || null,
        projectManager: form.projectManager.trim() || null,
        location: form.location.trim() || null,
        description: form.description.trim() || null,
        notes: form.notes.trim() || null,
        // Keep budget in sync with contractValue for backwards
        // compat with the Sprint 11 backend (which still uses
        // budget as the primary money field).
        budget: Number(form.contractValue) || 0,
      });
      router.push(`/dashboard/projects/${res.data.id}`);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div>
      <div className="flex items-center gap-2 mb-4">
        <Link href="/dashboard/projects" className="text-gray-500 hover:text-gray-700">
          <ArrowRight size={20} />
        </Link>
        <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
          <FolderKanban size={24} className="text-primary-600" />
          مشروع جديد
        </h1>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm border border-red-200">
          {error}
        </div>
      )}

      <form onSubmit={submit} className="card space-y-4 max-w-3xl">
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">كود المشروع *</label>
            <input
              className="input"
              value={form.code}
              onChange={(e) => set("code", e.target.value)}
              placeholder="PRJ-2026-001"
              required
            />
            <p className="text-xs text-gray-500 mt-1">يجب أن يكون فريداً داخل الشركة</p>
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">الحالة *</label>
            <select className="input" value={form.status} onChange={(e) => set("status", e.target.value)}>
              <option value="draft">مسودة</option>
              <option value="active">نشط</option>
              <option value="on_hold">متوقف</option>
              <option value="completed">مكتمل</option>
              <option value="closed">مغلق</option>
            </select>
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">الاسم (English) *</label>
            <input
              className="input"
              value={form.name}
              onChange={(e) => set("name", e.target.value)}
              required
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">الاسم (عربي)</label>
            <input
              className="input"
              value={form.nameAr}
              onChange={(e) => set("nameAr", e.target.value)}
            />
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">نوع المشروع *</label>
            <select className="input" value={form.type} onChange={(e) => set("type", e.target.value)}>
              <option value="construction">مقاولات</option>
              <option value="supply">توريد</option>
              <option value="service">خدمات</option>
              <option value="maintenance">صيانة</option>
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">العميل</label>
            <select
              className="input"
              value={form.customerId}
              onChange={(e) => set("customerId", e.target.value)}
              disabled={customers.length === 0}
            >
              <option value="">— بدون عميل —</option>
              {customers.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.code} — {c.nameAr || c.name}
                </option>
              ))}
            </select>
            {customers.length === 0 && (
              <p className="text-xs text-amber-700 mt-1">
                لم يتم تحميل قائمة العملاء. أضف عميلاً أولاً من شاشة العملاء.
              </p>
            )}
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">قيمة العقد (د.ل)</label>
            <input
              type="number"
              step="0.01"
              min="0"
              className="input"
              value={form.contractValue || ""}
              onChange={(e) => set("contractValue", Number(e.target.value))}
              dir="ltr"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">تاريخ البداية</label>
            <input
              type="date"
              className="input"
              value={form.startDate}
              onChange={(e) => set("startDate", e.target.value)}
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">تاريخ النهاية المتوقع</label>
            <input
              type="date"
              className="input"
              value={form.expectedEndDate}
              onChange={(e) => set("expectedEndDate", e.target.value)}
            />
          </div>
        </div>

        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">مدير المشروع</label>
            <input
              className="input"
              value={form.projectManager}
              onChange={(e) => set("projectManager", e.target.value)}
              placeholder="اسم مدير الموقع"
            />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">الموقع</label>
            <input
              className="input"
              value={form.location}
              onChange={(e) => set("location", e.target.value)}
              placeholder="طرابلس - شارع الزاوية"
            />
          </div>
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">الوصف</label>
          <textarea
            className="input"
            rows={3}
            value={form.description}
            onChange={(e) => set("description", e.target.value)}
            placeholder="ملخص عن المشروع..."
          />
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">ملاحظات</label>
          <textarea
            className="input"
            rows={2}
            value={form.notes}
            onChange={(e) => set("notes", e.target.value)}
            placeholder="ملاحظات داخلية (لن تظهر في الفاتورة)..."
          />
        </div>

        <div className="flex gap-2 pt-2">
          <button type="submit" disabled={submitting} className="btn-primary flex-1 sm:flex-none sm:min-w-[180px]">
            {submitting ? (
              <>
                <Loader2 className="animate-spin" size={16} />
                جاري الحفظ...
              </>
            ) : (
              <>
                <Save size={16} />
                حفظ
              </>
            )}
          </button>
          <Link href="/dashboard/projects" className="btn-secondary">
            إلغاء
          </Link>
        </div>
      </form>
    </div>
  );
}

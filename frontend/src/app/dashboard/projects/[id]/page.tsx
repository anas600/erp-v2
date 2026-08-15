"use client";

/**
 * Sprint 35+36 — Project detail page with 8 tabs.
 *
 *   Overview       — project info, milestones, edit/back actions
 *   Costs          — table of allocated transactions (invoices + JE lines)
 *   Revenue        — table of sales invoices billed to this project
 *   P&L            — money shot: revenue - costs grouped by 5401-5407
 *   Allocation     — bulk-allocate purchase invoices to this project
 *   Contract       — Sprint 36: contract CRUD
 *   Billings       — Sprint 36: progress billings + approve/cancel
 *   Client Stmt    — Sprint 36: customer-facing statement of account
 *
 * Tab state is local to this page (useState). We don't persist
 * it in the URL on purpose — most of the time the user lands
 * here, picks an action (P&L or Allocation), and goes back. URL
 * state would just add complexity without value.
 *
 * Cross-tab data flow:
 *   - Contract state is owned by [id]/page.tsx (the page knows
 *     whether a contract exists; ContractTab and BillingsTab
 *     share the same contract via the `contract` prop).
 *   - Billings list is owned by [id]/page.tsx for the same
 *     reason — when BillingModal or Approve updates the list,
 *     the page's view stays in sync.
 *   - This avoids both tabs racing to fetch the same data, and
 *     keeps the modal-open/closed lifecycle on the parent.
 */
import { useEffect, useState, use } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import {
  ArrowRight, Loader2, Pencil, FolderKanban, MapPin, User, Calendar,
  DollarSign, FileText, TrendingUp, Wallet, ClipboardList, CheckCircle2,
  FileSignature, FileBarChart, Receipt, FilePlus, Ruler
} from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate, cn } from "@/lib/utils";
import ProjectTypeBadge from "../components/ProjectTypeBadge";
import StatusBadge from "../components/StatusBadge";
import PnLSummary, { type ProjectPnLResponse } from "../components/PnLSummary";
import AllocationPanel from "../components/AllocationPanel";
import ContractTab from "../components/ContractTab";
import type { ContractDto } from "../components/ContractModal";
import BillingsTab, { type ProgressBillingDto } from "../components/BillingsTab";
import StatementTab from "../components/StatementTab";
import VariationTab from "../components/VariationTab";
import TechReportTab from "../components/TechReportTab";
import FieldMeasurementBookTab from "../components/FieldMeasurementBookTab";

interface Project {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  description?: string;
  type?: string | null;
  status: string;
  customerId?: string | null;
  customerName?: string | null;
  contractValue?: number;
  budget: number;
  startDate?: string;
  endDate?: string;
  expectedEndDate?: string;
  actualEndDate?: string;
  projectManager?: string | null;
  location?: string | null;
  notes?: string;
  createdAt?: string;
  milestones: Milestone[];
}

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

interface CostRow {
  id: string;
  source: "invoice" | "journal";
  invoiceNumber?: string;
  entryNumber?: string;
  date: string;
  accountCode?: string;
  accountName?: string;
  partyName?: string;
  amount: number;
  description?: string;
}

interface RevenueRow {
  id: string;
  invoiceNumber: string;
  invoiceDate: string;
  partyName: string;
  partyNameAr?: string;
  total: number;
  status: string;
}

type TabId = "overview" | "costs" | "revenue" | "pnl" | "allocation" | "contract" | "billings" | "variations" | "statement" | "tech-report" | "fmb";

const TABS: { id: TabId; label: string; icon: any }[] = [
  { id: "overview",   label: "نظرة عامة",       icon: ClipboardList },
  { id: "contract",   label: "العقد",           icon: FileSignature },
  { id: "fmb",        label: "الدفتر الفني",     icon: Ruler },
  { id: "billings",   label: "المستخلصات",      icon: Receipt },
  { id: "variations", label: "أوامر التغيير",   icon: FilePlus },
  { id: "costs",      label: "التكاليف",        icon: Wallet },
  { id: "revenue",    label: "الإيرادات",       icon: TrendingUp },
  { id: "pnl",        label: "الربح والخسارة",   icon: DollarSign },
  { id: "statement",  label: "كشف حساب العميل",  icon: FileBarChart },
  { id: "tech-report", label: "التقرير الفني",   icon: FileBarChart },
  { id: "allocation", label: "التخصيص",         icon: FileText },
];

export default function ProjectDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const router = useRouter();
  const { activeCompany } = useAuth();
  const { id: projectId } = use(params);

  const [project, setProject] = useState<Project | null>(null);
  const [pnl, setPnl] = useState<ProjectPnLResponse | null>(null);
  const [costs, setCosts] = useState<CostRow[]>([]);
  const [revenue, setRevenue] = useState<RevenueRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [pnlLoading, setPnlLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<TabId>("overview");
  const [editing, setEditing] = useState(false);
  // Sprint 36 — cross-tab state. The contract and billings tabs
  // are tightly coupled (you can't create a billing without a
  // contract), so the page owns the contract and shares it.
  const [contract, setContract] = useState<ContractDto | null>(null);
  const [billings, setBillings] = useState<ProgressBillingDto[]>([]);

  const loadProject = async () => {
    try {
      setLoading(true);
      setError(null);
      const res = await api.get(`/projects/${projectId}`);
      setProject(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  const loadTab = async (t: TabId) => {
    try {
      if (t === "pnl" && !pnl) {
        setPnlLoading(true);
        const res = await api.get(`/projects/${projectId}/pnl`);
        setPnl(res.data);
        setPnlLoading(false);
      } else if (t === "costs" && costs.length === 0) {
        const res = await api.get(`/projects/${projectId}/costs`);
        setCosts(res.data || []);
      } else if (t === "revenue" && revenue.length === 0) {
        const res = await api.get(`/projects/${projectId}/revenue`);
        setRevenue(res.data || []);
      }
    } catch (err) {
      setError(getErrorMessage(err));
    }
  };

  useEffect(() => {
    if (!activeCompany) return;
    loadProject();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, activeCompany]);

  useEffect(() => {
    if (project) loadTab(tab);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tab, project]);

  if (loading) {
    return (
      <div className="flex justify-center py-12">
        <Loader2 className="animate-spin text-primary-500" size={32} />
      </div>
    );
  }

  if (error || !project) {
    return (
      <div>
        <Link href="/dashboard/projects" className="flex items-center gap-1 text-ink-muted hover:text-ink-muted mb-4">
          <ArrowRight size={16} />
          العودة إلى المشاريع
        </Link>
        <div className="card border-red-200 bg-red-50 text-red-700 text-sm">
          {error || "المشروع غير موجود"}
        </div>
      </div>
    );
  }

  return (
    <div>
      {/* Header */}
      <div className="flex items-start justify-between mb-4 gap-2 flex-wrap">
        <div>
          <Link href="/dashboard/projects" className="flex items-center gap-1 text-ink-muted hover:text-ink-muted text-sm mb-2">
            <ArrowRight size={14} />
            المشاريع
          </Link>
          <div className="flex items-center gap-2 flex-wrap">
            <h1 className="text-2xl font-bold text-ink-strong">{project.nameAr || project.name}</h1>
            <ProjectTypeBadge type={project.type} />
            <StatusBadge status={project.status} />
          </div>
          <div className="flex items-center gap-3 mt-1 text-sm text-ink-muted flex-wrap">
            <span className="font-mono">{project.code}</span>
            {project.customerName && (
              <span className="flex items-center gap-1">
                <span className="text-ink-subtle">•</span>
                العميل: <span className="text-ink-muted">{project.customerName}</span>
              </span>
            )}
          </div>
        </div>
        <div className="flex gap-2">
          <button
            type="button"
            onClick={() => setEditing(!editing)}
            className="btn-secondary"
            title="تعديل"
          >
            <Pencil size={16} />
            <span className="hidden sm:inline">تعديل</span>
          </button>
        </div>
      </div>

      {/* Tab bar */}
      <div className="border-b border-edge mb-4 -mx-1 px-1 overflow-x-auto">
        <div className="flex gap-1 min-w-max">
          {TABS.map((t) => {
            const Icon = t.icon;
            const active = tab === t.id;
            return (
              <button
                key={t.id}
                type="button"
                onClick={() => setTab(t.id)}
                className={cn(
                  "flex items-center gap-1 px-3 py-2 text-sm font-medium border-b-2 -mb-px transition-colors",
                  active
                    ? "border-primary-600 text-primary-700"
                    : "border-transparent text-ink-muted hover:text-ink-muted"
                )}
                aria-current={active ? "page" : undefined}
              >
                <Icon size={14} />
                <span className="hidden sm:inline">{t.label}</span>
              </button>
            );
          })}
        </div>
      </div>

      {/* Tab content */}
      {tab === "overview" && (
        <OverviewTab project={project} onSave={loadProject} editing={editing} setEditing={setEditing} />
      )}
      {tab === "costs" && <CostsTab rows={costs} />}
      {tab === "revenue" && <RevenueTab rows={revenue} />}
      {tab === "pnl" && (
        <PnLSummary
          pnl={pnl}
          loading={pnlLoading}
          error={error}
          projectId={projectId}
          contractId={contract?.id}
          contractValue={contract?.contractValue ?? project.contractValue ?? 0}
        />
      )}
      {tab === "allocation" && (
        <AllocationPanel projectId={projectId} onChange={() => { setPnl(null); loadTab("pnl"); }} />
      )}
      {tab === "contract" && (
        <ContractTab
          projectId={projectId}
          initialContract={contract}
          onContractChange={setContract}
        />
      )}
      {tab === "billings" && (
        <BillingsTab
          projectId={projectId}
          contract={contract}
          initialBillings={billings}
          onBillingsChange={setBillings}
        />
      )}
      {tab === "variations" && contract && (
        <VariationTab
          projectId={projectId}
          contractId={contract.id}
          onVariationsChange={() => {
            // Nudge the contract tab to refresh its effective value
            window.dispatchEvent(
              new CustomEvent("contract-effective-value:refresh", {
                detail: { contractId: contract.id },
              })
            );
          }}
        />
      )}
      {tab === "variations" && !contract && (
        <div className="card text-center text-ink-muted py-12 text-sm">
          يتطلب إنشاء أوامر تغيير وجود عقد للمشروع. أضف عقداً من تبويب "العقد" أولاً.
        </div>
      )}
      {tab === "statement" && (
        <StatementTab
          projectId={projectId}
          projectName={project.nameAr || project.name}
        />
      )}
      {tab === "tech-report" && (
        <TechReportTab
          projectId={projectId}
          onSave={() => loadProject()}
        />
      )}
      {tab === "fmb" && (
        <FieldMeasurementBookTab
          projectId={projectId}
        />
      )}
    </div>
  );
}

// ============================================================
// Overview tab
// ============================================================
function OverviewTab({
  project,
  onSave,
  editing,
  setEditing,
}: {
  project: Project;
  onSave: () => Promise<void>;
  editing: boolean;
  setEditing: (b: boolean) => void;
}) {
  const [form, setForm] = useState({
    name: project.name,
    nameAr: project.nameAr || "",
    type: project.type || "construction",
    status: project.status,
    contractValue: project.contractValue ?? 0,
    startDate: (project.startDate || "").slice(0, 10),
    expectedEndDate: (project.expectedEndDate || "").slice(0, 10),
    projectManager: project.projectManager || "",
    location: project.location || "",
    description: project.description || "",
    notes: project.notes || "",
  });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setForm({
      name: project.name,
      nameAr: project.nameAr || "",
      type: project.type || "construction",
      status: project.status,
      contractValue: project.contractValue ?? 0,
      startDate: (project.startDate || "").slice(0, 10),
      expectedEndDate: (project.expectedEndDate || "").slice(0, 10),
      projectManager: project.projectManager || "",
      location: project.location || "",
      description: project.description || "",
      notes: project.notes || "",
    });
  }, [project]);

  const set = <K extends keyof typeof form>(k: K, v: (typeof form)[K]) =>
    setForm((p) => ({ ...p, [k]: v }));

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      await api.put(`/projects/${project.id}`, {
        name: form.name,
        nameAr: form.nameAr || null,
        type: form.type,
        status: form.status,
        contractValue: Number(form.contractValue) || 0,
        startDate: form.startDate || null,
        expectedEndDate: form.expectedEndDate || null,
        projectManager: form.projectManager || null,
        location: form.location || null,
        description: form.description || null,
        notes: form.notes || null,
        budget: Number(form.contractValue) || 0,
      });
      await onSave();
      setEditing(false);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSaving(false);
    }
  };

  if (editing) {
    return (
      <form onSubmit={save} className="card space-y-3 max-w-3xl">
        {error && <div className="p-2 bg-red-50 text-red-700 rounded text-sm">{error}</div>}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">الاسم (English) *</label>
            <input className="input" value={form.name} onChange={(e) => set("name", e.target.value)} required />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">الاسم (عربي)</label>
            <input className="input" value={form.nameAr} onChange={(e) => set("nameAr", e.target.value)} />
          </div>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">النوع</label>
            <select className="input" value={form.type} onChange={(e) => set("type", e.target.value)}>
              <option value="construction">مقاولات</option>
              <option value="supply">توريد</option>
              <option value="service">خدمات</option>
              <option value="maintenance">صيانة</option>
            </select>
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">الحالة</label>
            <select className="input" value={form.status} onChange={(e) => set("status", e.target.value)}>
              <option value="draft">مسودة</option>
              <option value="active">نشط</option>
              <option value="on_hold">متوقف</option>
              <option value="completed">مكتمل</option>
              <option value="closed">مغلق</option>
            </select>
          </div>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-3 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">قيمة العقد</label>
            <input type="number" className="input" value={form.contractValue || ""} onChange={(e) => set("contractValue", Number(e.target.value))} dir="ltr" />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">تاريخ البداية</label>
            <input type="date" className="input" value={form.startDate} onChange={(e) => set("startDate", e.target.value)} />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">النهاية المتوقعة</label>
            <input type="date" className="input" value={form.expectedEndDate} onChange={(e) => set("expectedEndDate", e.target.value)} />
          </div>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
          <div>
            <label className="block text-sm font-medium mb-1">مدير المشروع</label>
            <input className="input" value={form.projectManager} onChange={(e) => set("projectManager", e.target.value)} />
          </div>
          <div>
            <label className="block text-sm font-medium mb-1">الموقع</label>
            <input className="input" value={form.location} onChange={(e) => set("location", e.target.value)} />
          </div>
        </div>
        <div>
          <label className="block text-sm font-medium mb-1">الوصف</label>
          <textarea className="input" rows={2} value={form.description} onChange={(e) => set("description", e.target.value)} />
        </div>
        <div>
          <label className="block text-sm font-medium mb-1">ملاحظات</label>
          <textarea className="input" rows={2} value={form.notes} onChange={(e) => set("notes", e.target.value)} />
        </div>
        <div className="flex gap-2 pt-2">
          <button type="submit" disabled={saving} className="btn-primary">
            {saving ? "جاري الحفظ..." : "حفظ"}
          </button>
          <button type="button" onClick={() => setEditing(false)} className="btn-secondary">إلغاء</button>
        </div>
      </form>
    );
  }

  // View mode
  return (
    <div className="space-y-4">
      {/* Project info grid */}
      <div className="card">
        <h3 className="font-semibold mb-3 flex items-center gap-2">
          <FolderKanban size={16} className="text-primary-600" />
          معلومات المشروع
        </h3>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-y-3 gap-x-6 text-sm">
          <InfoRow icon={DollarSign} label="قيمة العقد" value={formatNumber(project.contractValue ?? 0) + " د.ل"} />
          <InfoRow icon={Calendar} label="تاريخ البداية" value={project.startDate ? formatDate(project.startDate) : "—"} />
          <InfoRow icon={Calendar} label="النهاية المتوقعة" value={project.expectedEndDate ? formatDate(project.expectedEndDate) : "—"} />
          {project.projectManager && <InfoRow icon={User} label="مدير المشروع" value={project.projectManager} />}
          {project.location && <InfoRow icon={MapPin} label="الموقع" value={project.location} />}
        </div>
        {project.description && (
          <div className="mt-3 pt-3 border-t border-edge">
            <p className="text-xs text-ink-muted mb-1">الوصف</p>
            <p className="text-sm">{project.description}</p>
          </div>
        )}
        {project.notes && (
          <div className="mt-3 pt-3 border-t border-edge">
            <p className="text-xs text-ink-muted mb-1">ملاحظات</p>
            <p className="text-sm text-ink-muted whitespace-pre-wrap">{project.notes}</p>
          </div>
        )}
      </div>

      {/* Milestones */}
      <div className="card">
        <h3 className="font-semibold mb-3 flex items-center gap-2">
          <CheckCircle2 size={16} className="text-primary-600" />
          المراحل
          {project.milestones.length > 0 && (
            <span className="text-xs text-ink-muted font-normal">
              ({project.milestones.filter((m) => m.status === "completed").length}/{project.milestones.length} مكتملة)
            </span>
          )}
        </h3>
        {project.milestones.length === 0 ? (
          <p className="text-sm text-ink-muted py-4 text-center">لا توجد مراحل</p>
        ) : (
          <ul className="space-y-2">
            {project.milestones.map((m) => (
              <li key={m.id} className="flex items-center justify-between p-2 hover:bg-raised rounded">
                <div className="flex items-center gap-2">
                  {m.status === "completed" ? (
                    <CheckCircle2 size={16} className="text-green-600" />
                  ) : (
                    <div className="w-4 h-4 rounded-full border-2 border-edge" />
                  )}
                  <span className={m.status === "completed" ? "line-through text-ink-muted" : ""}>
                    {m.nameAr || m.name}
                  </span>
                </div>
                <div className="flex items-center gap-2 text-sm">
                  {m.targetDate && <span className="text-xs text-ink-muted">{formatDate(m.targetDate)}</span>}
                  <span className="font-mono" dir="ltr">{formatNumber(m.amount)}</span>
                </div>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function InfoRow({ icon: Icon, label, value }: { icon: any; label: string; value: string }) {
  return (
    <div className="flex items-start gap-2">
      <Icon size={14} className="text-ink-subtle mt-0.5 shrink-0" />
      <div className="min-w-0">
        <p className="text-xs text-ink-muted">{label}</p>
        <p className="font-medium truncate" dir="ltr">{value}</p>
      </div>
    </div>
  );
}

// ============================================================
// Costs tab
// ============================================================
function CostsTab({ rows }: { rows: CostRow[] }) {
  if (rows.length === 0) {
    return <EmptyState message="لا توجد تكاليف مخصصة لهذا المشروع بعد. استخدم تبويب 'التخصيص' لإضافة فواتير." />;
  }
  const total = rows.reduce((s, r) => s + r.amount, 0);
  return (
    <div className="card p-0 overflow-x-auto">
      <div className="px-4 py-2 border-b border-edge flex items-center justify-between bg-raised">
        <span className="text-sm font-semibold">عدد الحركات: {rows.length}</span>
        <span className="text-sm font-mono font-semibold" dir="ltr">الإجمالي: {formatNumber(total)}</span>
      </div>
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-edge">
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">المصدر</th>
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">الرقم</th>
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">التاريخ</th>
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">الحساب</th>
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">الطرف</th>
            <th className="text-left py-2 px-3 font-semibold text-ink-muted">المبلغ</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id} className="border-b border-edge">
              <td className="py-2 px-3">
                <span className={cn(
                  "inline-flex px-2 py-0.5 rounded text-xs",
                  r.source === "invoice" ? "bg-primary-100 text-primary-800" : "bg-purple-100 text-purple-800"
                )}>
                  {r.source === "invoice" ? "فاتورة" : "قيد"}
                </span>
              </td>
              <td className="py-2 px-3 font-mono text-xs">{r.invoiceNumber || r.entryNumber || r.id.slice(0, 8)}</td>
              <td className="py-2 px-3">{formatDate(r.date)}</td>
              <td className="py-2 px-3 font-mono text-xs text-ink-muted">{r.accountCode || "—"}</td>
              <td className="py-2 px-3">{r.partyName || "—"}</td>
              <td className="py-2 px-3 text-left font-mono" dir="ltr">{formatNumber(r.amount)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ============================================================
// Revenue tab
// ============================================================
function RevenueTab({ rows }: { rows: RevenueRow[] }) {
  if (rows.length === 0) {
    return <EmptyState message="لا توجد إيرادات (فواتير بيع) مخصصة لهذا المشروع بعد." />;
  }
  const total = rows.reduce((s, r) => s + r.total, 0);
  return (
    <div className="card p-0 overflow-x-auto">
      <div className="px-4 py-2 border-b border-edge flex items-center justify-between bg-raised">
        <span className="text-sm font-semibold">عدد الفواتير: {rows.length}</span>
        <span className="text-sm font-mono font-semibold" dir="ltr">الإجمالي: {formatNumber(total)}</span>
      </div>
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b border-edge">
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">رقم الفاتورة</th>
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">التاريخ</th>
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">العميل</th>
            <th className="text-right py-2 px-3 font-semibold text-ink-muted">الحالة</th>
            <th className="text-left py-2 px-3 font-semibold text-ink-muted">المبلغ</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.id} className="border-b border-edge">
              <td className="py-2 px-3 font-mono text-xs">{r.invoiceNumber}</td>
              <td className="py-2 px-3">{formatDate(r.invoiceDate)}</td>
              <td className="py-2 px-3">{r.partyNameAr || r.partyName}</td>
              <td className="py-2 px-3 text-xs text-ink-muted">{r.status}</td>
              <td className="py-2 px-3 text-left font-mono" dir="ltr">{formatNumber(r.total)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function EmptyState({ message }: { message: string }) {
  return (
    <div className="card text-center text-ink-muted py-12 text-sm">
      {message}
    </div>
  );
}

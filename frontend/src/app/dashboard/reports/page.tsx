/**
 * Reports Index Page — Cycle 1.
 *
 * Route: /dashboard/reports
 *
 * A "lobby" for the Reports section. Lists all 7 working
 * reports + 3 missing/in-progress ones in a single table,
 * with quick filters and status badges. Each row links to
 * the actual report.
 *
 * Why a separate index page (not just the sidebar):
 *   1. The sidebar's التقارير المالية group is collapsed by
 *      default to keep the nav short. New users don't know
 *      it exists.
 *   2. With 10+ reports, even an open sidebar is a wall of
 *      text. The index page gives a searchable, sortable
 *      catalog.
 *   3. We can show status ("missing"/"working"/"url-mismatch")
 *      so the team can see at a glance what's deployed and
 *      what isn't.
 *
 * This page does NOT fetch report data. It just renders a
 * static catalog (defined below) plus a small "recent
 * reports" strip that reads localStorage. Report data is
 * fetched by the individual report pages.
 */

"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import {
  BarChart3, BookOpen, TrendingUp, FileSpreadsheet, Users, Scale, Wallet,
  Layers, FileText, Wrench, AlertCircle, Search, Filter, ChevronRight, Clock,
  X, Folder, Briefcase, Tag
} from "lucide-react";
import { cn } from "@/lib/utils";

type ReportModule = "accounting" | "projects" | "contacts" | "all";
type ReportPriority = "P0" | "P1" | "P2" | "P3";
type ReportStatus = "working" | "missing" | "url-mismatch" | "in-progress";

interface ReportEntry {
  id: string;
  name: string;
  nameAr: string;
  description: string;
  descriptionAr: string;
  path: string;
  module: ReportModule;
  moduleAr: string;
  priority: ReportPriority;
  status: ReportStatus;
  icon: any;
  /** Estimated render time in ms (for the "fast" badge) */
  speed?: "fast" | "medium" | "slow";
}

// Static catalog. Kept inline (no JSON file) so the page
// doesn't need a fetch. If we ever exceed 20 reports we can
// move this to /api/reports/catalog.
const REPORTS: ReportEntry[] = [
  {
    id: "trial-balance",
    name: "Trial Balance",
    nameAr: "ميزان المراجعة",
    description: "Foundation of every audit. Verifies the double-entry balance.",
    descriptionAr: "أساس كل مراجعة. يتحقق من توازن القيد المزدوج.",
    path: "/dashboard/reports/trial-balance",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P0",
    status: "working",
    icon: Scale,
    speed: "fast",
  },
  {
    id: "general-ledger",
    name: "General Ledger",
    nameAr: "دفتر الأستاذ",
    description: "Per-account transaction list with running balance.",
    descriptionAr: "حركات كل حساب مع رصيد جاري.",
    path: "/dashboard/reports/general-ledger",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P0",
    status: "working",
    icon: BookOpen,
    speed: "medium",
  },
  {
    id: "customer-aging",
    name: "AR Aging (Customer)",
    nameAr: "أعمار المدينين",
    description: "Customer outstanding balances, split by age bucket.",
    descriptionAr: "أرصدة العملاء المستحقة، مقسمة زمنياً.",
    path: "/dashboard/reports/customer-aging",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P1",
    status: "working",
    icon: Users,
    speed: "fast",
  },
  {
    id: "supplier-aging",
    name: "AP Aging (Supplier)",
    nameAr: "أعمار الدائنين",
    description: "Supplier outstanding balances, split by age bucket.",
    descriptionAr: "أرصدة الموردين المستحقة، مقسمة زمنياً.",
    path: "/dashboard/reports/supplier-aging",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P1",
    status: "working",
    icon: Users,
    speed: "fast",
  },
  {
    id: "income-statement",
    name: "Income Statement (P&L)",
    nameAr: "قائمة الدخل",
    description: "Revenue, expenses, and net profit for a period.",
    descriptionAr: "الإيرادات والمصروفات وصافي الربح للفترة.",
    path: "/dashboard/reports/income-statement",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P1",
    status: "working",
    icon: TrendingUp,
    speed: "fast",
  },
  {
    id: "balance-sheet",
    name: "Balance Sheet",
    nameAr: "الميزانية العمومية",
    description: "Assets, liabilities, and equity at a specific date.",
    descriptionAr: "الأصول والخصوم وحقوق الملكية في تاريخ محدد.",
    path: "/dashboard/reports/balance-sheet",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P1",
    status: "working",
    icon: FileSpreadsheet,
    speed: "fast",
  },
  {
    id: "intercompany-elimination",
    name: "Intercompany Eliminations",
    nameAr: "استبعاد المعاملات البينية",
    description: "Eliminate intercompany transactions for consolidated reporting.",
    descriptionAr: "استبعاد المعاملات بين الشركات للدمج المحاسبي.",
    path: "/dashboard/reports/intercompany-elimination",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P3",
    status: "working",
    icon: FileSpreadsheet,
    speed: "slow",
  },
  {
    id: "sub-ledger-schedule",
    name: "Sub-Ledger Schedule",
    nameAr: "جدول الأستاذ المساعد",
    description: "L3 control account broken down into L4 sub-ledgers. For reconciliation.",
    descriptionAr: "تفصيل حساب تحكم L3 إلى حسابات L4 فرعية. للتسوية.",
    path: "/dashboard/reports/sub-ledger-schedule",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P2",
    status: "working",  // Cycle 2: shipped
    icon: Layers,
  },
  {
    id: "projects-pnl",
    name: "Projects P&L",
    nameAr: "ربحية المشاريع",
    description: "Profitability of each project. For project managers.",
    descriptionAr: "ربحية كل مشروع. لمديري المشاريع.",
    path: "/dashboard/reports/projects-pnl",
    module: "projects",
    moduleAr: "المشاريع",
    priority: "P3",
    status: "working",  // Cycle 3: shipped
    icon: Briefcase,
  },
  {
    // Sprint 60 — Phase 2. The P&L by Cost Center is a
    // horizontal slice of the income statement: instead of
    // grouping by account, group by cost center. This lets
    // the accountant answer "where did the money go?" by
    // department / activity / project.
    id: "cost-center-pnl",
    name: "Expenses by Cost Center",
    nameAr: "المصروفات حسب مركز التكلفة",
    description: "Operating expenses grouped by cost center. 3 axes: department, activity, project.",
    descriptionAr: "المصاريف التشغيلية مجمّعة حسب مركز التكلفة. 3 محاور: قسم، نشاط، مشروع.",
    path: "/dashboard/reports/cost-center-pnl",
    module: "accounting",
    moduleAr: "المحاسبة",
    priority: "P2",
    status: "working",  // Sprint 60 Phase 2
    icon: Tag,
    speed: "fast",
  },
  {
    id: "contact-statement",
    name: "Contact Statement (per contact)",
    nameAr: "كشف حساب العميل/المورد",
    description: "Per-contact statement — see the contacts page's statement tab.",
    descriptionAr: "كشف حساب لعميل/مورد محدد — موجود في صفحة جهات الاتصال كـ tab.",
    path: "/dashboard/contacts",
    module: "contacts",
    moduleAr: "جهات الاتصال",
    priority: "P2",
    status: "working",
    icon: Users,
  },
];

const STATUS_META: Record<ReportStatus, { label: string; cls: string }> = {
  "working":     { label: "✅ يعمل",        cls: "bg-green-100 text-green-800" },
  "missing":     { label: "❌ لم يُنشر",    cls: "bg-red-100 text-red-800" },
  "url-mismatch":{ label: "⚠️ رابط مختلف",   cls: "bg-amber-100 text-amber-800" },
  "in-progress": { label: "🔄 قيد التطوير", cls: "bg-blue-100 text-blue-800" },
};

const PRIORITY_META: Record<ReportPriority, { label: string; cls: string }> = {
  P0: { label: "P0 — حرج",   cls: "bg-red-100 text-red-700" },
  P1: { label: "P1 — مهم",   cls: "bg-amber-100 text-amber-700" },
  P2: { label: "P2 — متوسط", cls: "bg-blue-100 text-blue-700" },
  P3: { label: "P3 — منخفض", cls: "bg-gray-100 text-gray-700" },
};

const RECENT_KEY = "erp_v2_recent_reports";
const RECENT_MAX = 4;

interface RecentItem {
  id: string;
  nameAr: string;
  path: string;
  openedAt: number;
}

export default function ReportsIndexPage() {
  // Filters
  const [search, setSearch] = useState("");
  const [moduleFilter, setModuleFilter] = useState<ReportModule | "all">("all");
  const [statusFilter, setStatusFilter] = useState<ReportStatus | "all">("all");
  const [showOnlyWorking, setShowOnlyWorking] = useState(false);

  // Recent reports (localStorage)
  const [recent, setRecent] = useState<RecentItem[]>([]);
  const [recentLoaded, setRecentLoaded] = useState(false);

  // Read recent on mount (client only)
  useEffect(() => {
    try {
      const stored = localStorage.getItem(RECENT_KEY);
      if (stored) setRecent(JSON.parse(stored) as RecentItem[]);
    } catch (err) {
      // localStorage unavailable (private mode, SSR, etc.) — fail silent
      console.warn("Could not read recent reports:", err);
    }
    setRecentLoaded(true);
  }, []);

  // Filter logic
  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase();
    return REPORTS.filter((r) => {
      if (moduleFilter !== "all" && r.module !== moduleFilter) return false;
      if (statusFilter !== "all" && r.status !== statusFilter) return false;
      if (showOnlyWorking && r.status !== "working") return false;
      if (term) {
        const hay = `${r.name} ${r.nameAr} ${r.description} ${r.descriptionAr}`.toLowerCase();
        if (!hay.includes(term)) return false;
      }
      return true;
    });
  }, [search, moduleFilter, statusFilter, showOnlyWorking]);

  // Stats
  const stats = useMemo(() => {
    const total = REPORTS.length;
    const working = REPORTS.filter((r) => r.status === "working").length;
    const missing = REPORTS.filter((r) => r.status === "missing").length;
    const byPriority = {
      P0: REPORTS.filter((r) => r.priority === "P0").length,
      P1: REPORTS.filter((r) => r.priority === "P1").length,
      P2: REPORTS.filter((r) => r.priority === "P2").length,
      P3: REPORTS.filter((r) => r.priority === "P3").length,
    };
    return { total, working, missing, byPriority };
  }, []);

  const clearRecent = () => {
    setRecent([]);
    try { localStorage.removeItem(RECENT_KEY); } catch (err) { /* noop */ }
  };

  return (
    <div>
      {/* Header */}
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
          <BarChart3 size={24} className="text-brand-700" />
          فهرس التقارير
        </h1>
        <p className="text-sm text-ink-muted mt-1">
          مركز كل التقارير المالية. {stats.total} تقرير — {stats.working} يعمل، {stats.missing} قيد التطوير.
        </p>
      </div>

      {/* Quick stats */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-6">
        <StatChip label="إجمالي" value={stats.total} icon={BarChart3} tone="brand" />
        <StatChip label="يعمل" value={stats.working} icon={BarChart3} tone="success" />
        <StatChip label="قيد التطوير" value={stats.missing} icon={Wrench} tone="warn" />
        <StatChip label="أولوية P0" value={stats.byPriority.P0} icon={AlertCircle} tone="danger" />
      </div>

      {/* Recent reports (client only) */}
      {recentLoaded && recent.length > 0 && (
        <div className="card mb-4">
          <div className="flex items-center justify-between mb-3">
            <h2 className="text-sm font-semibold text-ink-strong flex items-center gap-2">
              <Clock size={16} className="text-ink-muted" />
              آخر التقارير المفتوحة
            </h2>
            <button
              onClick={clearRecent}
              className="text-xs text-ink-muted hover:text-red-600 flex items-center gap-1"
              aria-label="مسح السجل"
            >
              <X size={12} /> مسح
            </button>
          </div>
          <div className="flex flex-wrap gap-2">
            {recent.map((r) => (
              <Link
                key={r.id}
                href={r.path}
                className="flex items-center gap-2 px-3 py-2 bg-surface border border-default rounded-md hover:border-brand-300 text-sm"
              >
                <BarChart3 size={14} className="text-brand-700" />
                <span>{r.nameAr}</span>
                <ChevronRight size={12} className="text-ink-muted" />
              </Link>
            ))}
          </div>
        </div>
      )}

      {/* Filters */}
      <div className="card mb-4">
        <div className="flex items-center gap-2 flex-wrap">
          {/* Search */}
          <div className="relative flex-1 min-w-[200px]">
            <Search size={16} className="absolute right-3 top-1/2 -translate-y-1/2 text-ink-muted" />
            <input
              type="text"
              placeholder="ابحث في التقارير..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="input pr-9 w-full"
            />
          </div>

          {/* Module filter */}
          <select
            value={moduleFilter}
            onChange={(e) => setModuleFilter(e.target.value as ReportModule | "all")}
            className="input w-auto"
          >
            <option value="all">كل الموديولات</option>
            <option value="accounting">المحاسبة</option>
            <option value="projects">المشاريع</option>
            <option value="contacts">جهات الاتصال</option>
          </select>

          {/* Status filter */}
          <select
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value as ReportStatus | "all")}
            className="input w-auto"
          >
            <option value="all">كل الحالات</option>
            <option value="working">✅ يعمل</option>
            <option value="missing">❌ لم يُنشر</option>
            <option value="url-mismatch">⚠️ رابط مختلف</option>
            <option value="in-progress">🔄 قيد التطوير</option>
          </select>

          {/* Quick toggle: only working */}
          <label className="flex items-center gap-2 cursor-pointer text-sm">
            <input
              type="checkbox"
              checked={showOnlyWorking}
              onChange={(e) => setShowOnlyWorking(e.target.checked)}
              className="rounded"
            />
            <span>العاملة فقط</span>
          </label>

          {/* Counter */}
          <span className="text-xs text-ink-muted mr-auto">
            {filtered.length} / {REPORTS.length}
          </span>
        </div>
      </div>

      {/* Reports table */}
      <div className="card overflow-hidden p-0">
        {filtered.length === 0 ? (
          <div className="p-8 text-center text-ink-muted">
            <Filter size={32} className="mx-auto mb-2 opacity-50" />
            <p>لا توجد تقارير تطابق المعايير. جرّب توسيع الفلاتر.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th className="w-10"></th>
                  <th>اسم التقرير</th>
                  <th>الوصف</th>
                  <th>الموديول</th>
                  <th>الأولوية</th>
                  <th>الحالة</th>
                  <th className="w-24"></th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((r) => {
                  const Icon = r.icon;
                  const isMissing = r.status === "missing";
                  return (
                    <tr key={r.id} className={cn(isMissing && "opacity-60")}>
                      <td>
                        <Icon size={18} className="text-brand-700" />
                      </td>
                      <td>
                        <div className="font-semibold text-ink-strong">{r.nameAr}</div>
                        <div className="text-xs text-ink-muted">{r.name}</div>
                      </td>
                      <td>
                        <div className="text-sm text-ink-strong">{r.descriptionAr}</div>
                        <div className="text-xs text-ink-muted">{r.description}</div>
                      </td>
                      <td>
                        <span className="badge-info">{r.moduleAr}</span>
                      </td>
                      <td>
                        <span className={cn("px-2 py-0.5 rounded text-xs font-semibold", PRIORITY_META[r.priority].cls)}>
                          {r.priority}
                        </span>
                      </td>
                      <td>
                        <span className={cn("px-2 py-0.5 rounded text-xs font-semibold", STATUS_META[r.status].cls)}>
                          {STATUS_META[r.status].label}
                        </span>
                      </td>
                      <td>
                        {r.status === "working" ? (
                          <Link
                            href={r.path}
                            className="btn-primary text-xs px-3 py-1.5 inline-flex items-center gap-1"
                          >
                            فتح
                            <ChevronRight size={14} />
                          </Link>
                        ) : r.status === "missing" ? (
                          <span className="text-xs text-ink-muted">قريباً</span>
                        ) : (
                          <span className="text-xs text-ink-muted">—</span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Footer note */}
      <p className="text-xs text-ink-muted mt-4 text-center">
        💡 التقارير تُحسب مباشرةً من القيود المُرحّلة. لا توجد كاش — الأرقام دائماً حديثة.
      </p>
    </div>
  );
}

function StatChip({
  label,
  value,
  icon: Icon,
  tone = "brand",
}: {
  label: string;
  value: number;
  icon: any;
  tone?: "brand" | "success" | "warn" | "danger";
}) {
  const toneClasses: Record<string, string> = {
    brand:   "bg-brand-50 text-brand-700",
    success: "bg-green-50 text-green-700",
    warn:    "bg-amber-50 text-amber-700",
    danger:  "bg-red-50 text-red-700",
  };
  return (
    <div className={cn("card flex items-center gap-3", toneClasses[tone])}>
      <Icon size={20} />
      <div>
        <div className="text-2xl font-bold leading-none">{value}</div>
        <div className="text-xs mt-1 opacity-80">{label}</div>
      </div>
    </div>
  );
}

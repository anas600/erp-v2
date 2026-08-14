/**
 * Projects P&L — Cycle 3.
 *
 * Route: /dashboard/reports/projects-pnl
 *
 * Per-project profitability report. Shows each project with
 * its revenue, costs, and gross profit. The cost breakdown
 * by category lets project managers see "where the money
 * went" — a key input for bidding on the next project.
 *
 * Data source: GET /api/reports/projects-pnl?companyId=...
 * Returns: List<ProjectPnLResponse> — one per project.
 *
 * The endpoint reads from Sprint 35+ data: invoices and
 * journal entries tagged with project_id. Revenue comes from
 * sales invoices (the project's billings). Costs come from
 * any journal line whose account is tagged with the project.
 *
 * Why a separate page (not a sub-tab of /dashboard/projects):
 *   - This is a *report* — read-only, accounting-flavored, with
 *     currency formatting and profit calculations. The projects
 *     page is a CRM-style list.
 *   - Project managers often need a printable summary for client
 *     meetings.
 */

"use client";

import { useEffect, useState, useMemo } from "react";
import { Briefcase, Loader2, TrendingUp, TrendingDown, Printer, AlertTriangle, FileText } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber } from "@/lib/utils";

interface CostCategory {
  categoryName: string;
  amount: number;
}

interface ProjectPnL {
  projectId: string;
  projectCode: string;
  projectName: string;
  totalRevenue: number;
  costsByCategory: CostCategory[];
  totalCosts: number;
  grossProfit: number;
  profitMargin: number; // 0..100, already in percent (e.g. 25.5 for 25.5%)
  invoiceCount: number;
  journalEntryCount: number;
}

export default function ProjectsPnLPage() {
  const { activeCompany } = useAuth();
  const [projects, setProjects] = useState<ProjectPnL[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!activeCompany) return;
    (async () => {
      try {
        setLoading(true);
        setError(null);
        const res = await api.get(`/reports/projects-pnl?companyId=${activeCompany.id}`);
        const list: ProjectPnL[] = Array.isArray(res.data) ? res.data : (res.data?.data || []);
        setProjects(list);
      } catch (err) {
        setError(getErrorMessage(err));
      } finally {
        setLoading(false);
      }
    })();
  }, [activeCompany]);

  // Totals
  const totals = useMemo(() => {
    return projects.reduce(
      (acc, p) => {
        acc.revenue += p.totalRevenue;
        acc.costs += p.totalCosts;
        acc.profit += p.grossProfit;
        acc.invoices += p.invoiceCount;
        acc.jes += p.journalEntryCount;
        return acc;
      },
      { revenue: 0, costs: 0, profit: 0, invoices: 0, jes: 0 },
    );
  }, [projects]);

  const avgMargin = totals.revenue > 0 ? (totals.profit / totals.revenue) * 100 : 0;

  return (
    <div>
      {/* Header */}
      <div className="mb-6 flex items-start justify-between flex-wrap gap-3">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
            <Briefcase size={24} className="text-brand-700" />
            ربحية المشاريع
          </h1>
          <p className="text-sm text-ink-muted mt-1">
            تقرير ربحية المشاريع — الإيرادات، التكاليف، وصافي الربح لكل مشروع.
          </p>
        </div>
        <button
          type="button"
          onClick={() => window.print()}
          className="btn-secondary flex items-center gap-1"
        >
          <Printer size={14} /> طباعة
        </button>
      </div>

      {/* Summary cards */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-4">
        <SummaryCard label="عدد المشاريع" value={String(projects.length)} icon={Briefcase} tone="brand" />
        <SummaryCard label="إجمالي الإيرادات" value={formatNumber(totals.revenue)} icon={TrendingUp} tone="success" />
        <SummaryCard label="إجمالي التكاليف" value={formatNumber(totals.costs)} icon={FileText} tone="warn" />
        <SummaryCard
          label="صافي الربح"
          value={formatNumber(totals.profit)}
          icon={totals.profit >= 0 ? TrendingUp : TrendingDown}
          tone={totals.profit >= 0 ? "success" : "danger"}
        />
      </div>

      {/* Loading state */}
      {loading && (
        <div className="card flex items-center justify-center h-48">
          <Loader2 className="animate-spin text-brand-700" size={32} />
        </div>
      )}

      {/* Error state */}
      {error && !loading && (
        <div className="card border-red-200 bg-red-50">
          <div className="flex items-start gap-3">
            <AlertTriangle size={20} className="text-red-600 mt-0.5" />
            <div>
              <p className="font-semibold text-red-700">فشل تحميل التقرير</p>
              <p className="text-sm text-red-600 mt-1">{error}</p>
            </div>
          </div>
        </div>
      )}

      {/* Empty state */}
      {!loading && !error && projects.length === 0 && (
        <div className="card text-center text-ink-muted py-12">
          <Briefcase size={40} className="mx-auto mb-3 opacity-40" />
          <p className="font-medium">لا توجد مشاريع في هذه الشركة</p>
          <p className="text-xs mt-1">أضف مشروعاً من صفحة المشاريع لرؤية تقرير الربحية.</p>
        </div>
      )}

      {/* Table */}
      {!loading && !error && projects.length > 0 && (
        <div className="card p-0 overflow-hidden">
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>المشروع</th>
                  <th>الحالة</th>
                  <th className="text-left">الإيرادات</th>
                  <th className="text-left">التكاليف</th>
                  <th className="text-left">صافي الربح</th>
                  <th className="text-left">هامش الربح</th>
                  <th className="text-center">فواتير</th>
                  <th className="text-center">قيود</th>
                </tr>
              </thead>
              <tbody>
                {projects.map((p) => (
                  <tr key={p.projectId}>
                    <td>
                      <div className="font-semibold text-ink-strong">{p.projectName}</div>
                      <div className="text-xs text-ink-muted font-mono">{p.projectCode}</div>
                    </td>
                    <td>
                      <ProfitStatus profit={p.grossProfit} margin={p.profitMargin} />
                    </td>
                    <td className="font-mono" dir="ltr">
                      {formatNumber(p.totalRevenue)}
                    </td>
                    <td className="font-mono text-ink-muted" dir="ltr">
                      {formatNumber(p.totalCosts)}
                    </td>
                    <td
                      className="font-mono font-bold"
                      dir="ltr"
                      style={{ color: p.grossProfit >= 0 ? undefined : "var(--text-danger, #DC2626)" }}
                    >
                      {formatNumber(p.grossProfit)}
                    </td>
                    <td>
                      <MarginPill margin={p.profitMargin} />
                    </td>
                    <td className="text-center text-sm">{p.invoiceCount}</td>
                    <td className="text-center text-sm">{p.journalEntryCount}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="font-bold bg-surface">
                  <td colSpan={2}>المجموع</td>
                  <td className="font-mono" dir="ltr">{formatNumber(totals.revenue)}</td>
                  <td className="font-mono" dir="ltr">{formatNumber(totals.costs)}</td>
                  <td
                    className="font-mono"
                    dir="ltr"
                    style={{ color: totals.profit >= 0 ? undefined : "var(--text-danger, #DC2626)" }}
                  >
                    {formatNumber(totals.profit)}
                  </td>
                  <td>
                    <MarginPill margin={avgMargin} />
                  </td>
                  <td className="text-center">{totals.invoices}</td>
                  <td className="text-center">{totals.jes}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        </div>
      )}

      {/* Cost breakdown drilldown (expandable) */}
      {!loading && projects.length > 0 && (
        <div className="mt-6">
          <h2 className="text-lg font-semibold text-ink-strong mb-2">تفصيل التكاليف حسب الفئة</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-3">
            {projects.map((p) => (
              <div key={p.projectId} className="card">
                <h3 className="font-semibold text-ink-strong mb-2">
                  {p.projectName}{" "}
                  <span className="text-xs text-ink-muted font-mono">({p.projectCode})</span>
                </h3>
                {p.costsByCategory.length === 0 ? (
                  <p className="text-xs text-ink-muted">لا توجد تكاليف مسجلة.</p>
                ) : (
                  <table className="text-xs w-full">
                    <tbody>
                      {p.costsByCategory.map((c) => (
                        <tr key={c.categoryName} className="border-b border-default last:border-0">
                          <td className="py-1.5 text-ink-muted">{c.categoryName}</td>
                          <td className="py-1.5 font-mono text-left" dir="ltr">
                            {formatNumber(c.amount)}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      <p className="text-xs text-ink-muted mt-4 text-center">
        💡 صافي الربح = الإيرادات − التكاليف. التكاليف تشمل كل القيود المُرحّلة المُسندة لهذا المشروع.
      </p>
    </div>
  );
}

function SummaryCard({
  label,
  value,
  icon: Icon,
  tone = "brand",
}: {
  label: string;
  value: string;
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
    <div className={`card flex items-center gap-3 ${toneClasses[tone]}`}>
      <Icon size={20} />
      <div>
        <div className="text-2xl font-bold leading-none font-mono" dir="ltr">{value}</div>
        <div className="text-xs mt-1 opacity-80">{label}</div>
      </div>
    </div>
  );
}

function ProfitStatus({ profit, margin }: { profit: number; margin: number }) {
  if (profit <= 0) {
    return <span className="badge-danger text-xs">خاسر</span>;
  }
  if (margin >= 25) {
    return <span className="badge-success text-xs">ممتاز</span>;
  }
  if (margin >= 10) {
    return <span className="bg-blue-100 text-blue-800 text-xs px-2 py-0.5 rounded">جيد</span>;
  }
  return <span className="bg-amber-100 text-amber-800 text-xs px-2 py-0.5 rounded">منخفض</span>;
}

function MarginPill({ margin }: { margin: number }) {
  const color =
    margin >= 25 ? "bg-green-100 text-green-800"
    : margin >= 10 ? "bg-blue-100 text-blue-800"
    : margin >= 0 ? "bg-amber-100 text-amber-800"
    : "bg-red-100 text-red-800";
  return (
    <span className={`${color} text-xs px-2 py-0.5 rounded font-mono`} dir="ltr">
      {margin.toFixed(1)}%
    </span>
  );
}

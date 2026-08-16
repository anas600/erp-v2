/**
 * Cost Center P&L — Sprint 60.
 *
 * Route: /dashboard/reports/cost-center-pnl
 *
 * P&L grouped by cost center. Shows where expenses landed —
 * by department (HR, Operations, etc.), by activity (Travel,
 * Marketing, ...), or by project.
 *
 * Data source: GET /api/reports/cost-center-pnl?companyId=...
 * Returns: CostCenterPnLReport — one line per cost center.
 *
 * The endpoint sums expense lines (4xxx accounts) on posted
 * journal entries that have a cost center tag. Reversed
 * entries and their counterparts are excluded (same logic as
 * Project P&L).
 *
 * Sprint 60 — First version, deliberately small: 5 demo cost
 * centers (3 types). The infrastructure scales to any number.
 */

"use client";

import { useEffect, useState, useMemo } from "react";
import { Tag, Loader2, TrendingUp, TrendingDown, Building2, Briefcase, Activity } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber, formatDate } from "@/lib/utils";

interface CostCenterPnLLine {
  costCenterId: string;
  costCenterCode: string;
  costCenterName: string;
  costCenterType: string;       // "department" | "activity" | "project"
  projectId?: string | null;
  totalAmount: number;
  movementCount: number;
}

interface CostCenterPnLReport {
  companyId: string;
  fromDate: string;
  toDate: string;
  lines: CostCenterPnLLine[];
  grandTotal: number;
}

export default function CostCenterPnLPage() {
  const { activeCompany } = useAuth();
  const [report, setReport] = useState<CostCenterPnLReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Date range — defaults to the company's full life.
  const [fromDate, setFromDate] = useState<string>("2020-01-01");
  const [toDate, setToDate] = useState<string>(
    new Date().toISOString().slice(0, 10)
  );

  useEffect(() => {
    if (!activeCompany) return;
    (async () => {
      try {
        setLoading(true);
        setError(null);
        const res = await api.get(
          `/reports/cost-center-pnl?companyId=${activeCompany.id}&from=${fromDate}&to=${toDate}`
        );
        setReport(res.data);
      } catch (err) {
        setError(getErrorMessage(err));
      } finally {
        setLoading(false);
      }
    })();
  }, [activeCompany, fromDate, toDate]);

  // Group lines by type so the table reads top-to-bottom:
  //   1. Departments
  //   2. Activities
  //   3. Projects
  // This is the same order the backend sorts by, but the UI
  // also draws a divider row to make the grouping visual.
  const grouped = useMemo(() => {
    if (!report) return { departments: [] as CostCenterPnLLine[], activities: [] as CostCenterPnLLine[], projects: [] as CostCenterPnLLine[] };
    return {
      departments: report.lines.filter(l => l.costCenterType === "department"),
      activities:  report.lines.filter(l => l.costCenterType === "activity"),
      projects:    report.lines.filter(l => l.costCenterType === "project"),
    };
  }, [report]);

  return (
    <div className="p-4 sm:p-6">
      <div className="flex items-center justify-between mb-4 flex-wrap gap-2">
        <div className="flex items-center gap-2">
          <Tag className="text-primary-600" size={24} />
          <h1 className="text-xl sm:text-2xl font-bold">المصروفات حسب مركز التكلفة</h1>
        </div>
      </div>
      <p className="text-sm text-ink-muted mb-4">
        يعرض هذا التقرير المصاريف التشغيلية (4xxx) مجمّعة حسب مركز التكلفة — قسم، نشاط، أو مشروع.
        مفيد لفهم توزيع المصاريف على إدارات الشركة.
      </p>

      {/* Date range filter */}
      <div className="card p-3 mb-4 flex flex-wrap items-end gap-3">
        <div>
          <label className="block text-xs text-ink-muted mb-1">من تاريخ</label>
          <input
            type="date"
            className="input"
            value={fromDate}
            onChange={(e) => setFromDate(e.target.value)}
          />
        </div>
        <div>
          <label className="block text-xs text-ink-muted mb-1">إلى تاريخ</label>
          <input
            type="date"
            className="input"
            value={toDate}
            onChange={(e) => setToDate(e.target.value)}
          />
        </div>
        <button
          className="btn-secondary"
          onClick={() => {
            setFromDate("2020-01-01");
            setToDate(new Date().toISOString().slice(0, 10));
          }}
        >
          إعادة تعيين
        </button>
      </div>

      {error && (
        <div className="card p-3 mb-4 bg-red-50 border-red-200 text-red-700 text-sm">
          {error}
        </div>
      )}

      {loading && (
        <div className="card p-6 text-center text-ink-muted flex items-center justify-center gap-2">
          <Loader2 className="animate-spin" size={18} />
          <span>جاري تحميل التقرير...</span>
        </div>
      )}

      {!loading && report && (
        <div className="card p-0 overflow-x-auto">
          <div className="px-4 py-3 border-b border-edge bg-raised flex items-center justify-between">
            <span className="text-sm font-semibold">
              الفترة: من {formatDate(report.fromDate)} إلى {formatDate(report.toDate)}
            </span>
            <span className="text-sm font-mono font-semibold" dir="ltr">
              الإجمالي الكلي: {formatNumber(report.grandTotal)} LYD
            </span>
          </div>

          {report.lines.length === 0 ? (
            <div className="p-6 text-center text-ink-muted">
              لا توجد مراكز تكلفة مسجلة لهذه الشركة. أنشئ مراكز التكلفة أولاً.
            </div>
          ) : (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-edge bg-raised">
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">الرمز</th>
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">الاسم</th>
                  <th className="text-right py-2 px-3 font-semibold text-ink-muted">النوع</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">عدد الحركات</th>
                  <th className="text-left py-2 px-3 font-semibold text-ink-muted">المبلغ (LYD)</th>
                </tr>
              </thead>
              <tbody>
                {grouped.departments.length > 0 && (
                  <>
                    <tr className="bg-blue-50">
                      <td colSpan={5} className="py-2 px-3 text-xs font-semibold text-blue-700 flex items-center gap-1">
                        <Building2 size={14} /> الأقسام ({grouped.departments.length})
                      </td>
                    </tr>
                    {grouped.departments.map((l) => (
                      <CostCenterRow key={l.costCenterId} line={l} />
                    ))}
                  </>
                )}

                {grouped.activities.length > 0 && (
                  <>
                    <tr className="bg-purple-50">
                      <td colSpan={5} className="py-2 px-3 text-xs font-semibold text-purple-700 flex items-center gap-1">
                        <Activity size={14} /> الأنشطة ({grouped.activities.length})
                      </td>
                    </tr>
                    {grouped.activities.map((l) => (
                      <CostCenterRow key={l.costCenterId} line={l} />
                    ))}
                  </>
                )}

                {grouped.projects.length > 0 && (
                  <>
                    <tr className="bg-amber-50">
                      <td colSpan={5} className="py-2 px-3 text-xs font-semibold text-amber-700 flex items-center gap-1">
                        <Briefcase size={14} /> المشاريع ({grouped.projects.length})
                      </td>
                    </tr>
                    {grouped.projects.map((l) => (
                      <CostCenterRow key={l.costCenterId} line={l} />
                    ))}
                  </>
                )}

                <tr className="border-t-2 border-edge bg-raised font-semibold">
                  <td colSpan={4} className="py-2 px-3">الإجمالي</td>
                  <td className="py-2 px-3 font-mono text-left" dir="ltr">
                    {formatNumber(report.grandTotal)} LYD
                  </td>
                </tr>
              </tbody>
            </table>
          )}
        </div>
      )}

      {!loading && report && report.lines.length > 0 && (
        <div className="mt-4 p-3 bg-amber-50 border border-amber-200 rounded text-sm text-amber-800">
          <strong>💡 نصيحة:</strong> هذا التقرير يحسب المصاريف على الحسابات 4xxx فقط (مصاريف تشغيلية).
          المصاريف العمومية (إيجار، رواتب إدارية) لم تُعلَّق بعد على مراكز التكلفة — ستظهر تلقائياً
          عند تخصيصها في القيود اليومية.
        </div>
      )}
    </div>
  );
}

function CostCenterRow({ line }: { line: CostCenterPnLLine }) {
  const isEmpty = line.totalAmount === 0;
  return (
    <tr className="border-b border-edge hover:bg-raised">
      <td className="py-2 px-3 font-mono text-xs text-ink-strong">{line.costCenterCode}</td>
      <td className="py-2 px-3 text-ink-strong">{line.costCenterName}</td>
      <td className="py-2 px-3 text-ink-muted text-xs">
        {line.costCenterType === "department" ? "قسم" :
         line.costCenterType === "activity" ? "نشاط" : "مشروع"}
      </td>
      <td className="py-2 px-3 text-ink-muted text-left" dir="ltr">{line.movementCount}</td>
      <td className={`py-2 px-3 font-mono text-left ${isEmpty ? 'text-ink-subtle' : 'text-ink-strong font-semibold'}`} dir="ltr">
        {formatNumber(line.totalAmount)} LYD
      </td>
    </tr>
  );
}

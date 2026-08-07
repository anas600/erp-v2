"use client";

/**
 * Sprint 35+36 — P&L summary card.
 *
 * Renders a project's revenue - costs by category, the gross
 * profit, and the margin %.
 *
 * Sprint 36 additions:
 *   - WIP (Work In Progress) card showing the relationship
 *     between actual costs (from posted JEs) and what we've
 *     billed (from approved progress billings).
 *   - WIP = totalCosts - totalBilled
 *   - Status: BALANCED / COSTS_EXCEED / BILLED_EXCEED
 *   - The card loads separately to avoid forcing the page
 *     to re-fetch P&L on every tab switch.
 *
 * Why a dedicated component?
 *   The P&L tab of the project detail page is the "money shot"
 *   for the site supervisor and the project manager. They want
 *   to see at a glance: how much we billed, how much we spent,
 *   and whether we're profitable. This component owns the
 *   layout and arithmetic; the parent just supplies the DTO.
 *
 * Edge cases handled:
 *   - Zero revenue (margin must not be NaN or Infinity)
 *   - Empty cost categories (render "no costs yet")
 *   - Negative profit (render in red so it's visible)
 *   - WIP data not yet loaded (render skeleton)
 */
import { useEffect, useState } from "react";
import { TrendingUp, TrendingDown, Minus, Loader2, AlertCircle, Briefcase } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { formatNumber } from "@/lib/utils";

export interface CostCategoryPnL {
  category: string;
  accountCode: string;
  amount: number;
}

export interface ProjectPnLResponse {
  projectId: string;
  projectCode: string;
  projectName: string;
  totalRevenue: number;
  costsByCategory: CostCategoryPnL[];
  totalCosts: number;
  grossProfit: number;
  profitMargin: number; // already a percentage (0-100), not a ratio
  invoiceCount: number;
  journalEntryCount: number;
}

interface Props {
  pnl: ProjectPnLResponse | null;
  loading: boolean;
  error: string | null;
  /** Required for the WIP card. */
  projectId: string;
}

export interface WipResponse {
  projectId: string;
  projectCode?: string;
  projectName?: string;
  totalCosts: number;
  totalBilled: number;
  wipAmount: number;
  wipStatus: string; // BALANCED | COSTS_EXCEED_BILLED | BILLED_EXCEED_COSTS | ...
  asOfDate?: string;
}

// Map account code prefix (5401-5407) to a friendly Arabic label.
// Keep these in sync with backend/Features/Projects/ProjectService.cs
// (the same 7 categories are used for the P&L report).
const CATEGORY_LABEL: Record<string, string> = {
  "5401": "مواد مباشرة",
  "5402": "أجور مباشرة",
  "5403": "مقاولين من الباطن",
  "5404": "معدات وآليات",
  "5405": "إيجارات مواقع",
  "5406": "خدمات هندسية",
  "5407": "تكاليف أخرى",
};

function labelForAccount(code?: string | null): string {
  if (!code) return "تكاليف أخرى";
  const prefix = code.substring(0, 4);
  return CATEGORY_LABEL[prefix] || `حساب ${code}`;
}

export default function PnLSummary({ pnl, loading, error, projectId }: Props) {
  if (loading) {
    return (
      <div className="card flex items-center justify-center py-12 text-gray-500">
        جاري حساب الربحية...
      </div>
    );
  }
  if (error) {
    return (
      <div className="card border-red-200 bg-red-50 text-red-700 text-sm">
        فشل تحميل تقرير الربح والخسارة: {error}
      </div>
    );
  }
  if (!pnl) {
    return (
      <div className="card text-center text-gray-500 py-8 text-sm">
        لا توجد بيانات بعد
      </div>
    );
  }

  // Defensive: ensure profitMargin is finite (backend can hand us
  // a non-numeric if a future refactor breaks the divide-by-zero
  // guard; we never want "NaN%" on the screen).
  const margin = Number.isFinite(pnl.profitMargin) ? pnl.profitMargin : 0;
  const profitable = pnl.grossProfit > 0;
  const breakEven = pnl.grossProfit === 0;

  return (
    <div className="space-y-4">
      {/* Top row: revenue / costs / profit / margin */}
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
        <Card
          title="الإيرادات"
          value={pnl.totalRevenue}
          subtitle={`${pnl.invoiceCount} فاتورة`}
          tone="info"
        />
        <Card
          title="إجمالي التكاليف"
          value={pnl.totalCosts}
          subtitle={`${pnl.journalEntryCount} قيد`}
          tone="warn"
        />
        <Card
          title="الربح الإجمالي"
          value={pnl.grossProfit}
          subtitle={breakEven ? "نقطة التعادل" : profitable ? "ربح" : "خسارة"}
          tone={profitable ? "good" : breakEven ? "neutral" : "bad"}
        />
        <Card
          title="هامش الربح"
          value={`${margin.toFixed(1)}%`}
          subtitle={
            breakEven
              ? "تعادل"
              : profitable
                ? "ربحية"
                : "خسارة"
          }
          tone={profitable ? "good" : breakEven ? "neutral" : "bad"}
        />
      </div>

      {/* Cost breakdown */}
      <div className="card">
        <h3 className="text-sm font-semibold text-gray-700 mb-3">تفصيل التكاليف حسب الحساب</h3>
        {pnl.costsByCategory.length === 0 ? (
          <p className="text-sm text-gray-500 py-4 text-center">لا توجد تكاليف مسجلة على هذا المشروع بعد</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-gray-200">
                  <th className="text-right py-2 font-semibold text-gray-600">الحساب</th>
                  <th className="text-right py-2 font-semibold text-gray-600">الكود</th>
                  <th className="text-left py-2 font-semibold text-gray-600">المبلغ</th>
                  <th className="text-left py-2 font-semibold text-gray-600">النسبة</th>
                </tr>
              </thead>
              <tbody>
                {pnl.costsByCategory.map((c) => {
                  const pct = pnl.totalCosts > 0 ? (c.amount / pnl.totalCosts) * 100 : 0;
                  return (
                    <tr key={c.accountCode} className="border-b border-gray-100">
                      <td className="py-2 text-right">{labelForAccount(c.accountCode)}</td>
                      <td className="py-2 text-right font-mono text-xs text-gray-600">{c.accountCode}</td>
                      <td className="py-2 text-left font-mono" dir="ltr">{formatNumber(c.amount)}</td>
                      <td className="py-2 text-left font-mono text-gray-500" dir="ltr">{pct.toFixed(1)}%</td>
                    </tr>
                  );
                })}
                <tr className="border-t-2 border-gray-200 font-semibold">
                  <td className="py-2 text-right" colSpan={2}>الإجمالي</td>
                  <td className="py-2 text-left font-mono" dir="ltr">{formatNumber(pnl.totalCosts)}</td>
                  <td className="py-2 text-left" />
                </tr>
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Sprint 36 — WIP card (separate fetch, separate loading state) */}
      <WipCard projectId={projectId} />
    </div>
  );
}

// ============================================================
// WIP card — Sprint 36 addition
// ============================================================
function WipCard({ projectId }: { projectId: string }) {
  const [wip, setWip] = useState<WipResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setLoading(true);
    setError(null);
    api
      .get(`/projects/${projectId}/wip`)
      .then((res) => setWip(res.data || null))
      .catch((err) => {
        // 404 is fine — project has no billings yet. Don't show as error.
        if ((err as any)?.response?.status === 404) {
          setWip(null);
        } else {
          setError(getErrorMessage(err));
        }
      })
      .finally(() => setLoading(false));
  }, [projectId]);

  if (loading) {
    return (
      <div className="card flex items-center justify-center py-8 text-gray-500 gap-2 text-sm">
        <Loader2 className="animate-spin" size={16} />
        جاري حساب WIP...
      </div>
    );
  }
  if (error) {
    return (
      <div className="card border-red-200 bg-red-50 text-red-700 text-sm flex items-start gap-2">
        <AlertCircle size={16} className="mt-0.5 shrink-0" />
        <span>فشل تحميل WIP: {error}</span>
      </div>
    );
  }
  if (!wip) {
    return null; // 404 — no billings yet
  }

  const status = (wip.wipStatus || "BALANCED").toUpperCase();
  const statusMeta: Record<string, { label: string; tone: string }> = {
    BALANCED: { label: "متوازن", tone: "good" },
    COSTS_EXCEED_BILLED: { label: "التكاليف تتجاوز الفوترة", tone: "bad" },
    BILLED_EXCEED_COSTS: { label: "الفوترة تتجاوز التكاليف", tone: "warn" },
  };
  const meta = statusMeta[status] || { label: status, tone: "neutral" };
  const toneCls: Record<string, string> = {
    good: "border-green-200 bg-green-50",
    bad: "border-red-200 bg-red-50",
    warn: "border-amber-200 bg-amber-50",
    neutral: "border-gray-200 bg-gray-50",
  };
  const toneVal: Record<string, string> = {
    good: "text-green-700",
    bad: "text-red-700",
    warn: "text-amber-700",
    neutral: "text-gray-700",
  };
  const wipValueCls: Record<string, string> = {
    good: "text-green-700",
    bad: "text-red-700",
    warn: "text-amber-700",
    neutral: "text-gray-900",
  };

  return (
    <div className={`card border ${toneCls[meta.tone]}`}>
      <h3 className="font-semibold flex items-center gap-2 mb-3">
        <Briefcase size={16} className={toneVal[meta.tone]} />
        WIP (أعمال تحت التنفيذ)
      </h3>
      <div className="space-y-2 text-sm">
        <div className="flex items-center justify-between">
          <span className="text-gray-700">إجمالي التكاليف الفعلية</span>
          <span className="font-mono font-semibold" dir="ltr">
            {formatNumber(wip.totalCosts)} د.ل
          </span>
        </div>
        <div className="flex items-center justify-between">
          <span className="text-gray-700">إجمالي الإيرادات المفوترة</span>
          <span className="font-mono font-semibold" dir="ltr">
            {formatNumber(wip.totalBilled)} د.ل
          </span>
        </div>
        <div className="border-t border-gray-300 my-1" />
        <div className="flex items-center justify-between">
          <span className="font-semibold">WIP</span>
          <span
            className={`font-mono text-lg font-bold ${wipValueCls[meta.tone]}`}
            dir="ltr"
          >
            {formatNumber(wip.wipAmount)} د.ل
          </span>
        </div>
        <div className="flex items-center justify-between text-xs">
          <span className="text-gray-600">الحالة</span>
          <span className={`font-semibold ${toneVal[meta.tone]}`}>
            {meta.label}
          </span>
        </div>
      </div>
      {wip.asOfDate && (
        <p className="text-xs text-gray-500 mt-3 text-left" dir="ltr">
          As of: {wip.asOfDate}
        </p>
      )}
    </div>
  );
}

function Card({
  title,
  value,
  subtitle,
  tone,
}: {
  title: string;
  value: number | string;
  subtitle?: string;
  tone: "good" | "bad" | "warn" | "info" | "neutral";
}) {
  const toneCls: Record<string, string> = {
    good: "border-green-200 bg-green-50",
    bad: "border-red-200 bg-red-50",
    warn: "border-amber-200 bg-amber-50",
    info: "border-blue-200 bg-blue-50",
    neutral: "border-gray-200 bg-gray-50",
  };
  const valueCls: Record<string, string> = {
    good: "text-green-700",
    bad: "text-red-700",
    warn: "text-amber-700",
    info: "text-blue-700",
    neutral: "text-gray-700",
  };
  const Icon =
    tone === "good" ? TrendingUp : tone === "bad" ? TrendingDown : Minus;
  return (
    <div className={`card border ${toneCls[tone]}`}>
      <div className="flex items-start justify-between">
        <div className="min-w-0">
          <p className="text-xs text-gray-600 font-medium">{title}</p>
          <p className={`text-2xl font-bold mt-1 ${valueCls[tone]}`} dir="ltr">
            {typeof value === "number" ? formatNumber(value) : value}
          </p>
          {subtitle && <p className="text-xs text-gray-500 mt-1">{subtitle}</p>}
        </div>
        <Icon size={20} className={valueCls[tone]} />
      </div>
    </div>
  );
}

"use client";

/**
 * Sprint 36 — Client statement tab.
 *
 * Renders the totals returned by GET /api/projects/{id}/statement.
 * The exact shape is whatever the backend's BillingService
 * decides — we type as `any` to stay loose (we don't want a
 * frontend type change to break this tab if the backend adds a
 * field).
 *
 * Why a separate tab and not a card on the P&L?
 *   The statement is the *customer-facing* view (what does the
 *   customer owe / have paid / is held in retention). The P&L
 *   is the *internal* view (what did we bill and what did we
 *   spend). They're different audiences; putting them on the
 *   same tab would conflate them.
 */
import { useEffect, useState } from "react";
import { Loader2, AlertCircle, FileBarChart, Wallet, Banknote, HandCoins, Clock, Shield } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { formatNumber } from "@/lib/utils";
import { cn } from "@/lib/utils";

interface Props {
  projectId: string;
  projectName: string;
}

interface StatementData {
  projectId: string;
  contractValue?: number;
  totalBilled?: number;
  totalPaid?: number;
  retentionHeld?: number;
  advanceOutstanding?: number;
  netOutstanding?: number;
  // Allow any other fields the backend may add in the future.
  [key: string]: any;
}

export default function StatementTab({ projectId, projectName }: Props) {
  const { activeCompany } = useAuth();
  const [data, setData] = useState<StatementData | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!activeCompany) return;
    setLoading(true);
    setError(null);
    api
      .get(`/projects/${projectId}/statement`)
      .then((res) => setData(res.data || null))
      .catch((err) => setError(getErrorMessage(err)))
      .finally(() => setLoading(false));
  }, [projectId, activeCompany?.id]);

  if (loading) {
    return (
      <div className="card flex items-center justify-center py-12 text-gray-500 gap-2">
        <Loader2 className="animate-spin" size={20} />
        جاري تحميل كشف الحساب...
      </div>
    );
  }
  if (error) {
    return (
      <div className="card border-red-200 bg-red-50 text-red-700 text-sm flex items-start gap-2">
        <AlertCircle size={16} className="mt-0.5 shrink-0" />
        <span>{error}</span>
      </div>
    );
  }
  if (!data) {
    return (
      <div className="card text-center text-gray-500 py-8 text-sm">
        لا توجد بيانات
      </div>
    );
  }

  // The backend may return any subset of these — render every
  // non-null field with the right icon + label.
  const rows: Array<{
    key: string;
    label: string;
    icon: any;
    value: number;
    tone: "default" | "info" | "good" | "bad" | "warn";
    hint?: string;
  }> = [];

  if (data.contractValue != null) {
    rows.push({
      key: "contractValue",
      label: "قيمة العقد",
      icon: FileBarChart,
      value: Number(data.contractValue) || 0,
      tone: "default",
    });
  }
  if (data.totalBilled != null) {
    rows.push({
      key: "totalBilled",
      label: "إجمالي المفوتر (صافي)",
      icon: Wallet,
      value: Number(data.totalBilled) || 0,
      tone: "info",
    });
  }
  if (data.totalPaid != null) {
    rows.push({
      key: "totalPaid",
      label: "إجمالي المدفوع",
      icon: Banknote,
      value: Number(data.totalPaid) || 0,
      tone: "good",
    });
  }
  if (data.retentionHeld != null) {
    rows.push({
      key: "retentionHeld",
      label: "احتجاز معلّق",
      icon: Shield,
      value: Number(data.retentionHeld) || 0,
      tone: "warn",
      hint: "سيُفرج عنه عند اكتمال المشروع",
    });
  }
  if (data.advanceOutstanding != null) {
    rows.push({
      key: "advanceOutstanding",
      label: "دفعات مقدمة غير مخصومة",
      icon: HandCoins,
      value: Number(data.advanceOutstanding) || 0,
      tone: "default",
    });
  }
  if (data.netOutstanding != null) {
    rows.push({
      key: "netOutstanding",
      label: "صافي المستحق على العميل",
      icon: Clock,
      value: Number(data.netOutstanding) || 0,
      tone: Number(data.netOutstanding) > 0 ? "bad" : "good",
      hint:
        Number(data.netOutstanding) > 0
          ? "مستحق على العميل"
          : Number(data.netOutstanding) < 0
          ? "رصيد دائن للعميل"
          : "متوازن",
    });
  }

  // If the backend returns only a few fields, we still need the
  // screen to look complete. If none of the known fields came
  // back, show a "no data" hint.
  if (rows.length === 0) {
    return (
      <div className="card text-center text-gray-500 py-8 text-sm">
        لا توجد بيانات في كشف الحساب
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="card">
        <h3 className="font-semibold flex items-center gap-2 mb-4">
          <FileBarChart size={16} className="text-primary-600" />
          كشف حساب العميل — <span className="text-gray-700">{projectName}</span>
        </h3>

        <div className="space-y-2">
          {rows.map((r, idx) => {
            const Icon = r.icon;
            const isLast = r.key === "netOutstanding";
            return (
              <div key={r.key}>
                <div
                  className={cn(
                    "flex items-center justify-between gap-3 px-3 py-2 rounded-md",
                    isLast ? "bg-primary-50 border border-primary-200" : "hover:bg-gray-50"
                  )}
                >
                  <div className="flex items-center gap-2 min-w-0">
                    <Icon
                      size={16}
                      className={cn(
                        r.tone === "good" && "text-green-600",
                        r.tone === "bad" && "text-red-600",
                        r.tone === "warn" && "text-amber-600",
                        r.tone === "info" && "text-blue-600",
                        r.tone === "default" && "text-gray-500"
                      )}
                    />
                    <div className="min-w-0">
                      <p
                        className={cn(
                          "text-sm",
                          isLast ? "font-semibold" : ""
                        )}
                      >
                        {r.label}
                      </p>
                      {r.hint && (
                        <p className="text-xs text-gray-500">{r.hint}</p>
                      )}
                    </div>
                  </div>
                  <div
                    dir="ltr"
                    className={cn(
                      "font-mono shrink-0",
                      isLast
                        ? "text-lg font-bold text-primary-900"
                        : "text-sm font-semibold",
                      r.tone === "good" && !isLast && "text-green-700",
                      r.tone === "bad" && !isLast && "text-red-700",
                      r.tone === "warn" && !isLast && "text-amber-700",
                      r.tone === "info" && !isLast && "text-blue-700"
                    )}
                  >
                    {formatNumber(r.value)} د.ل
                  </div>
                </div>
                {idx < rows.length - 1 && (
                  <div className="h-px bg-gray-100 mx-3" />
                )}
              </div>
            );
          })}
        </div>
      </div>

      <p className="text-xs text-gray-500 text-center">
        آخر تحديث: {new Date().toLocaleString("en-GB")}
      </p>
    </div>
  );
}

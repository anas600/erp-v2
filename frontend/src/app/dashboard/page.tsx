"use client";

import { useEffect, useState } from "react";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import { formatNumber, formatDateTime } from "@/lib/utils";
import {
  Building2, BookOpen, FileText, TrendingUp, TrendingDown, Wallet, Users, Loader2
} from "lucide-react";

interface TrialBalance {
  totalDebit: number;
  totalCredit: number;
  balanced: boolean;
  lines: Array<{ code: string; name: string; accountType: string; nature: string; debitBalance: number; creditBalance: number }>;
}

interface IncomeStatement {
  totalRevenue: number;
  totalExpense: number;
  netIncome: number;
}

export default function DashboardPage() {
  const { activeCompany, user } = useAuth();
  const [tb, setTb] = useState<TrialBalance | null>(null);
  const [is_, setIs] = useState<IncomeStatement | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    if (!activeCompany) return;
    (async () => {
      try {
        setLoading(true);
        const [tbRes, isRes] = await Promise.all([
          api.get(`/reports/trial-balance?companyId=${activeCompany.id}`),
          api.get(`/reports/income-statement?companyId=${activeCompany.id}`)
        ]);
        setTb(tbRes.data);
        setIs(isRes.data);
      } catch (err) {
        console.error(getErrorMessage(err));
      } finally {
        setLoading(false);
      }
    })();
  }, [activeCompany]);

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <Loader2 className="animate-spin text-primary-700" size={32} />
      </div>
    );
  }

  if (!tb || !is_) {
    return <div className="text-center text-ink-subtle py-12">لا توجد بيانات</div>;
  }

  const totalAssets = tb.lines
    .filter((l) => l.accountType === "Asset")
    .reduce((sum, l) => sum + l.debitBalance - l.creditBalance, 0);
  const totalLiabilities = tb.lines
    .filter((l) => l.accountType === "Liability")
    .reduce((sum, l) => sum + l.creditBalance - l.debitBalance, 0);
  const totalEquity = tb.lines
    .filter((l) => l.accountType === "Equity")
    .reduce((sum, l) => sum + l.creditBalance - l.debitBalance, 0);

  return (
    <div>
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-ink-strong">لوحة التحكم</h1>
        <p className="text-ink-muted text-sm mt-1">
          مرحباً {user?.fullNameAr || user?.fullName} • {activeCompany?.nameAr || activeCompany?.name}
        </p>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
        <StatCard
          icon={Wallet}
          label="إجمالي الأصول"
          value={formatNumber(totalAssets)}
          color="success"
        />
        <StatCard
          icon={TrendingDown}
          label="إجمالي الخصوم"
          value={formatNumber(totalLiabilities)}
          color="danger"
        />
        <StatCard
          icon={Users}
          label="حقوق الملكية"
          value={formatNumber(totalEquity)}
          color="brand"
        />
        <StatCard
          icon={is_.netIncome >= 0 ? TrendingUp : TrendingDown}
          label="صافي الدخل"
          value={formatNumber(is_.netIncome)}
          color={is_.netIncome >= 0 ? "success" : "danger"}
        />
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        <div className="card">
          <h2 className="text-lg font-semibold mb-3 flex items-center gap-2 text-ink-strong">
            <BookOpen size={20} className="text-primary-700" />
            ملخص مالي
          </h2>
          <div className="space-y-2 text-sm">
            <Row label="إجمالي الإيرادات" value={formatNumber(is_.totalRevenue)} valueClass="text-green-600 dark:text-green-400" />
            <Row label="إجمالي المصروفات" value={formatNumber(is_.totalExpense)} valueClass="text-red-600 dark:text-red-400" />
            <div className="border-t border-edge pt-2 mt-2"></div>
            <Row label="صافي الدخل" value={formatNumber(is_.netIncome)} bold valueClass={is_.netIncome >= 0 ? "text-green-700 dark:text-green-400" : "text-red-700 dark:text-red-400"} />
          </div>
        </div>

        <div className="card">
          <h2 className="text-lg font-semibold mb-3 flex items-center gap-2 text-ink-strong">
            <FileText size={20} className="text-primary-700" />
            ميزان المراجعة
          </h2>
          <div className="space-y-2 text-sm">
            <Row label="إجمالي المدين" value={formatNumber(tb.totalDebit)} />
            <Row label="إجمالي الدائن" value={formatNumber(tb.totalCredit)} />
            <div className="border-t border-edge pt-2 mt-2"></div>
            <Row
              label="الحالة"
              value={tb.balanced ? "متوازن ✓" : "غير متوازن ✗"}
              bold
              valueClass={tb.balanced ? "text-green-600 dark:text-green-400" : "text-red-600 dark:text-red-400"}
            />
          </div>
        </div>
      </div>
    </div>
  );
}

function StatCard({ icon: Icon, label, value, color }: any) {
  const colorMap: any = {
    success: "bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-300",
    danger: "bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300",
    brand: "bg-brand-light text-primary-700 dark:bg-brand-900/40 dark:text-primary-300",
    warning: "bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-300"
  };
  return (
    <div className="card">
      <div className="flex items-center justify-between mb-2">
        <p className="text-sm text-ink-muted">{label}</p>
        <div className={`w-9 h-9 rounded-md flex items-center justify-center ${colorMap[color]}`}>
          <Icon size={18} />
        </div>
      </div>
      <p className="text-2xl font-bold text-ink-strong" dir="ltr">{value}</p>
    </div>
  );
}

function Row({ label, value, bold, valueClass }: any) {
  return (
    <div className="flex justify-between items-center">
      <span className={bold ? "font-semibold text-ink-strong" : "text-ink-muted"}>{label}</span>
      <span className={`${bold ? "font-bold" : ""} ${valueClass || "text-ink-strong"}`} dir="ltr">{value}</span>
    </div>
  );
}

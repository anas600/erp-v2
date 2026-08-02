"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { TrendingUp, TrendingDown, Loader2, Calendar } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface IncomeStatement {
  companyId: string;
  companyName: string;
  fromDate: string;
  toDate: string;
  revenues: Array<{ code: string; name: string; amount: number }>;
  expenses: Array<{ code: string; name: string; amount: number }>;
  totalRevenue: number;
  totalExpense: number;
  netIncome: number;
}

export default function IncomeStatementPage() {
  const { activeCompany } = useAuth();
  const [report, setReport] = useState<IncomeStatement | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [from, setFrom] = useState(new Date(new Date().getFullYear(), 0, 1).toISOString().slice(0, 10));
  const [to, setTo] = useState(new Date().toISOString().slice(0, 10));

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(`/reports/income-statement?companyId=${activeCompany.id}&from=${from}&to=${to}`);
      setReport(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany, from, to]);

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64">
        <Loader2 className="animate-spin text-primary-500" size={32} />
      </div>
    );
  }

  if (!report) {
    return <div className="text-center text-gray-500">لا توجد بيانات</div>;
  }

  return (
    <div>
      <div className="mb-6">
        <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
          <TrendingUp size={24} className="text-green-600" />
          قائمة الدخل
        </h1>
        <p className="text-sm text-gray-600 mt-1">
          {report.companyName} • الإيرادات والمصروفات خلال الفترة
        </p>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {/* Date range */}
      <div className="card mb-4">
        <div className="flex items-center gap-4 flex-wrap">
          <div className="flex items-center gap-2">
            <Calendar size={16} className="text-gray-500" />
            <span className="text-sm font-medium">من:</span>
            <input
              type="date"
              value={from}
              onChange={(e) => setFrom(e.target.value)}
              className="input w-auto"
            />
          </div>
          <div className="flex items-center gap-2">
            <span className="text-sm font-medium">إلى:</span>
            <input
              type="date"
              value={to}
              onChange={(e) => setTo(e.target.value)}
              className="input w-auto"
            />
          </div>
        </div>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {/* Revenues */}
        <div className="card">
          <h2 className="text-lg font-semibold mb-3 text-green-700">الإيرادات</h2>
          <table className="table">
            <thead>
              <tr>
                <th>الكود</th>
                <th>الحساب</th>
                <th className="text-left">المبلغ</th>
              </tr>
            </thead>
            <tbody>
              {report.revenues.map((r, idx) => (
                <tr key={idx}>
                  <td className="font-mono">{r.code}</td>
                  <td>{r.name}</td>
                  <td className="font-mono text-green-600" dir="ltr">{formatNumber(r.amount)}</td>
                </tr>
              ))}
              {report.revenues.length === 0 && (
                <tr><td colSpan={3} className="text-center text-gray-500 py-4">لا توجد إيرادات</td></tr>
              )}
            </tbody>
            <tfoot>
              <tr className="font-bold bg-green-50">
                <td colSpan={2}>إجمالي الإيرادات</td>
                <td className="font-mono text-green-700" dir="ltr">{formatNumber(report.totalRevenue)}</td>
              </tr>
            </tfoot>
          </table>
        </div>

        {/* Expenses */}
        <div className="card">
          <h2 className="text-lg font-semibold mb-3 text-red-700">المصروفات</h2>
          <table className="table">
            <thead>
              <tr>
                <th>الكود</th>
                <th>الحساب</th>
                <th className="text-left">المبلغ</th>
              </tr>
            </thead>
            <tbody>
              {report.expenses.map((e, idx) => (
                <tr key={idx}>
                  <td className="font-mono">{e.code}</td>
                  <td>{e.name}</td>
                  <td className="font-mono text-red-600" dir="ltr">{formatNumber(e.amount)}</td>
                </tr>
              ))}
              {report.expenses.length === 0 && (
                <tr><td colSpan={3} className="text-center text-gray-500 py-4">لا توجد مصروفات</td></tr>
              )}
            </tbody>
            <tfoot>
              <tr className="font-bold bg-red-50">
                <td colSpan={2}>إجمالي المصروفات</td>
                <td className="font-mono text-red-700" dir="ltr">{formatNumber(report.totalExpense)}</td>
              </tr>
            </tfoot>
          </table>
        </div>
      </div>

      {/* Net income */}
      <div className="card mt-4">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            {report.netIncome >= 0 ? (
              <TrendingUp size={24} className="text-green-600" />
            ) : (
              <TrendingDown size={24} className="text-red-600" />
            )}
            <span className="text-lg font-semibold">
              {report.netIncome >= 0 ? "صافي الربح" : "صافي الخسارة"}
            </span>
          </div>
          <span
            className={`text-2xl font-bold ${report.netIncome >= 0 ? "text-green-700" : "text-red-700"}`}
            dir="ltr"
          >
            {formatNumber(report.netIncome)}
          </span>
        </div>
        <p className="text-xs text-gray-500 mt-2">
          📐 المعادلة: <strong>الإيرادات − المصروفات = صافي الدخل</strong> (Expenses = Revenues)
        </p>
      </div>
    </div>
  );
}

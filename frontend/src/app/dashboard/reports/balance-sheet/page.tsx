"use client";

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Scale, Loader2, CheckCircle, XCircle } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface BalanceSheet {
  companyId: string;
  companyName: string;
  asOfDate: string;
  assets: Array<{ code: string; name: string; amount: number }>;
  liabilities: Array<{ code: string; name: string; amount: number }>;
  equity: Array<{ code: string; name: string; amount: number }>;
  totalAssets: number;
  totalLiabilities: number;
  totalEquity: number;
  balanced: boolean;
}

export default function BalanceSheetPage() {
  const { activeCompany } = useAuth();
  const [report, setReport] = useState<BalanceSheet | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(`/reports/balance-sheet?companyId=${activeCompany.id}`);
      setReport(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

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
      <div className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Scale size={24} className="text-primary-600" />
            الميزانية العمومية
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            {report.companyName} •截至 {formatDate(report.asOfDate)}
          </p>
        </div>
        <div>
          {report.balanced ? (
            <span className="badge badge-success text-base px-3 py-1">
              <CheckCircle size={14} className="ml-1" /> متوازنة
            </span>
          ) : (
            <span className="badge badge-danger text-base px-3 py-1">
              <XCircle size={14} className="ml-1" /> غير متوازنة
            </span>
          )}
        </div>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      <div className="grid grid-cols-1 lg:grid-cols-2 gap-4">
        {/* Assets */}
        <div className="card">
          <h2 className="text-lg font-semibold mb-3 text-blue-700">الأصول</h2>
          <table className="table">
            <thead>
              <tr>
                <th>الكود</th>
                <th>الحساب</th>
                <th className="text-left">المبلغ</th>
              </tr>
            </thead>
            <tbody>
              {report.assets.map((a, idx) => (
                <tr key={idx}>
                  <td className="font-mono">{a.code}</td>
                  <td>{a.name}</td>
                  <td className="font-mono" dir="ltr">{formatNumber(a.amount)}</td>
                </tr>
              ))}
              {report.assets.length === 0 && (
                <tr><td colSpan={3} className="text-center text-gray-500 py-4">لا توجد أصول</td></tr>
              )}
            </tbody>
            <tfoot>
              <tr className="font-bold bg-blue-50">
                <td colSpan={2}>إجمالي الأصول</td>
                <td className="font-mono text-blue-700" dir="ltr">{formatNumber(report.totalAssets)}</td>
              </tr>
            </tfoot>
          </table>
        </div>

        {/* Liabilities + Equity */}
        <div className="space-y-4">
          <div className="card">
            <h2 className="text-lg font-semibold mb-3 text-red-700">الخصوم</h2>
            <table className="table">
              <thead>
                <tr>
                  <th>الكود</th>
                  <th>الحساب</th>
                  <th className="text-left">المبلغ</th>
                </tr>
              </thead>
              <tbody>
                {report.liabilities.map((l, idx) => (
                  <tr key={idx}>
                    <td className="font-mono">{l.code}</td>
                    <td>{l.name}</td>
                    <td className="font-mono" dir="ltr">{formatNumber(l.amount)}</td>
                  </tr>
                ))}
                {report.liabilities.length === 0 && (
                  <tr><td colSpan={3} className="text-center text-gray-500 py-4">لا توجد خصوم</td></tr>
                )}
              </tbody>
              <tfoot>
                <tr className="font-bold bg-red-50">
                  <td colSpan={2}>إجمالي الخصوم</td>
                  <td className="font-mono text-red-700" dir="ltr">{formatNumber(report.totalLiabilities)}</td>
                </tr>
              </tfoot>
            </table>
          </div>

          <div className="card">
            <h2 className="text-lg font-semibold mb-3 text-purple-700">حقوق الملكية</h2>
            <table className="table">
              <thead>
                <tr>
                  <th>الكود</th>
                  <th>الحساب</th>
                  <th className="text-left">المبلغ</th>
                </tr>
              </thead>
              <tbody>
                {report.equity.map((e, idx) => (
                  <tr key={idx}>
                    <td className="font-mono">{e.code}</td>
                    <td>{e.name}</td>
                    <td className="font-mono" dir="ltr">{formatNumber(e.amount)}</td>
                  </tr>
                ))}
                {report.equity.length === 0 && (
                  <tr><td colSpan={3} className="text-center text-gray-500 py-4">لا توجد حقوق ملكية</td></tr>
                )}
              </tbody>
              <tfoot>
                <tr className="font-bold bg-purple-50">
                  <td colSpan={2}>إجمالي حقوق الملكية</td>
                  <td className="font-mono text-purple-700" dir="ltr">{formatNumber(report.totalEquity)}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        </div>
      </div>

      <div className="card mt-4 bg-blue-50 border-blue-200">
        <div className="grid grid-cols-3 gap-4 text-center">
          <div>
            <p className="text-sm text-gray-600">إجمالي الأصول</p>
            <p className="text-xl font-bold text-blue-700" dir="ltr">{formatNumber(report.totalAssets)}</p>
          </div>
          <div>
            <p className="text-sm text-gray-600">إجمالي الخصوم + حقوق الملكية</p>
            <p className="text-xl font-bold text-purple-700" dir="ltr">{formatNumber(report.totalLiabilities + report.totalEquity)}</p>
          </div>
          <div>
            <p className="text-sm text-gray-600">الفرق</p>
            <p className={`text-xl font-bold ${Math.abs(report.totalAssets - (report.totalLiabilities + report.totalEquity)) < 0.01 ? "text-green-700" : "text-red-700"}`} dir="ltr">
              {formatNumber(report.totalAssets - (report.totalLiabilities + report.totalEquity))}
            </p>
          </div>
        </div>
        <p className="text-xs text-gray-600 mt-3 text-center">
          📐 المعادلة: <strong>الأصول = الخصوم + حقوق الملكية</strong> (A = L + E)
        </p>
      </div>
    </div>
  );
}

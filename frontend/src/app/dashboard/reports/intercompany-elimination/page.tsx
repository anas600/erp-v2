"use client";

/**
 * Intercompany Elimination Report.
 *
 * During consolidation, all intercompany transactions are eliminated to
 * avoid double-counting revenue/expenses across the group. This report
 * shows every pair that would be eliminated, grouped by company.
 *
 * Per-pair entries that the report surfaces:
 *   - In the primary company's books: AR from sister + Revenue
 *   - In the mirror company's books: AP to primary + Expense (or vice versa)
 *
 * The CFO uses this report to generate consolidation journal entries at
 * period close.
 */

import { useEffect, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { FileSpreadsheet, Loader2 } from "lucide-react";

interface EliminationPair {
  pairId: string;
  primaryInvoiceId: string;
  mirrorInvoiceId: string | null;
  primaryCompanyCode: string;
  mirrorCompanyCode: string;
  primaryCompanyName: string;
  mirrorCompanyName: string;
  amount: number;
  currency: string;
  status: string;
  date: string;
  primaryJournalEntryId: string | null;
  mirrorJournalEntryId: string | null;
}

interface ReportResponse {
  pairs: EliminationPair[];
  totalEliminations: number;
  byCompany: Record<string, number>;
  currency: string;
  asOfDate: string;
}

export default function IntercompanyEliminationPage() {
  const { activeCompany } = useAuth();
  const [report, setReport] = useState<ReportResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [asOfDate, setAsOfDate] = useState<string>(
    new Date().toISOString().slice(0, 10)
  );

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(
        `/reports/intercompany-elimination?companyId=${activeCompany.id}&asOfDate=${asOfDate}`
      );
      setReport(res.data);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    load();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeCompany, asOfDate]);

  const fmt = (n: number) =>
    n.toLocaleString("ar-LY", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const dateFmt = (s: string) => new Date(s).toLocaleDateString("ar-LY");

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <FileSpreadsheet size={24} className="text-primary-600" />
            تقرير استبعاد المعاملات البينية
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            المعاملات بين شركات المجموعة التي يجب استبعادها عند إعداد القوائم الموحدة
          </p>
        </div>
        <div className="flex items-center gap-2">
          <label className="text-sm text-gray-600">حتى تاريخ:</label>
          <input
            type="date"
            className="input"
            value={asOfDate}
            onChange={(e) => setAsOfDate(e.target.value)}
            dir="ltr"
          />
        </div>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>
      )}

      {report && (
        <>
          {/* Summary cards */}
          <div className="grid grid-cols-1 md:grid-cols-3 gap-4 mb-6">
            <div className="card">
              <div className="text-sm text-gray-500">إجمالي المعاملات البينية</div>
              <div className="text-2xl font-bold text-gray-900 mt-1" dir="ltr">
                {fmt(report.totalEliminations)}
              </div>
              <div className="text-xs text-gray-500 mt-1">{report.currency}</div>
            </div>
            <div className="card">
              <div className="text-sm text-gray-500">عدد المعاملات</div>
              <div className="text-2xl font-bold text-gray-900 mt-1" dir="ltr">
                {report.pairs.length}
              </div>
              <div className="text-xs text-gray-500 mt-1">معاملة بينية</div>
            </div>
            <div className="card">
              <div className="text-sm text-gray-500">تاريخ التقرير</div>
              <div className="text-lg font-semibold text-gray-900 mt-1">
                {dateFmt(report.asOfDate)}
              </div>
            </div>
          </div>

          {/* By-company breakdown */}
          {Object.keys(report.byCompany).length > 0 && (
            <div className="card mb-6">
              <h3 className="text-sm font-semibold text-gray-700 mb-3">
                التوزيع حسب الشركة
              </h3>
              <div className="space-y-2">
                {Object.entries(report.byCompany).map(([code, amount]) => (
                  <div key={code} className="flex items-center justify-between text-sm">
                    <span className="font-mono font-semibold">{code}</span>
                    <span className="font-mono" dir="ltr">
                      {fmt(amount)} {report.currency}
                    </span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Detailed table */}
          <div className="card">
            <h3 className="text-sm font-semibold text-gray-700 mb-3">
              تفاصيل المعاملات البينية
            </h3>
            {loading ? (
              <div className="flex justify-center py-8">
                <Loader2 className="animate-spin text-primary-500" size={32} />
              </div>
            ) : (
              <table className="table">
                <thead>
                  <tr>
                    <th>التاريخ</th>
                    <th>الشركة الأساسية</th>
                    <th>الشركة الشقيقة</th>
                    <th>المبلغ</th>
                    <th>الحالة</th>
                  </tr>
                </thead>
                <tbody>
                  {report.pairs.map((p) => (
                    <tr key={p.pairId}>
                      <td>{dateFmt(p.date)}</td>
                      <td>
                        <div className="font-mono font-semibold">{p.primaryCompanyCode}</div>
                        <div className="text-xs text-gray-500">{p.primaryCompanyName}</div>
                      </td>
                      <td>
                        <div className="font-mono font-semibold">{p.mirrorCompanyCode}</div>
                        <div className="text-xs text-gray-500">{p.mirrorCompanyName}</div>
                      </td>
                      <td className="font-mono" dir="ltr">
                        {fmt(p.amount)}
                      </td>
                      <td>
                        {p.status === "posted" && (
                          <span className="badge badge-success">مرحّل</span>
                        )}
                        {p.status === "reversed" && (
                          <span className="badge badge-secondary">معكوس</span>
                        )}
                      </td>
                    </tr>
                  ))}
                  {report.pairs.length === 0 && (
                    <tr>
                      <td colSpan={5} className="text-center text-gray-500 py-6">
                        لا توجد معاملات بينية في هذه الفترة
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            )}
          </div>

          <div className="mt-4 flex justify-end">
            <button
              className="btn-secondary"
              onClick={() => alert("سيتم تنفيذ تصدير PDF في Sprint 27")}
            >
              تصدير PDF
            </button>
          </div>
        </>
      )}
    </div>
  );
}

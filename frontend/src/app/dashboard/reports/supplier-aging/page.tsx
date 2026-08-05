"use client";

import { useEffect, useState, useCallback } from "react";
import { useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import { Loader2, Users, AlertCircle, ArrowRight } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";

interface AgingLine {
  contactId: string;
  contactCode: string;
  contactName: string;
  buckets: number[];  // [0-30, 31-60, 61-90, 91+]
  total: number;
  /** Sprint 25 — total amount the supplier has already been paid. */
  paid?: number;
}

interface AgingReport {
  companyId: string;
  asOfDate: string;
  lines: AgingLine[];
  totals: number[];
  grandTotal: number;
  totalPaid?: number;
}

const BUCKET_LABELS = ["0-30 يوم", "31-60 يوم", "61-90 يوم", "+90 يوم"];
const BUCKET_CLASSES = ["text-green-700", "text-yellow-700", "text-orange-700", "text-red-700"];

export default function SupplierAgingPage() {
  const router = useRouter();
  const { activeCompany } = useAuth();
  const [report, setReport] = useState<AgingReport | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!activeCompany) return;
    setLoading(true);
    try {
      const r = await api.get(`/reports/supplier-aging?companyId=${activeCompany.id}`);
      const payload: AgingReport = r.data?.data || r.data;
      setReport(payload);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [activeCompany]);

  useEffect(() => { load(); }, [load]);

  const totalPaid = report?.totalPaid ?? report?.lines.reduce((s, l) => s + (l.paid || 0), 0) ?? 0;

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Users size={24} className="text-amber-600" />
            أعمار الدائنين
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            أرصدة الموردين موزعة حسب مدة التأخر في السداد
          </p>
        </div>
        {report && (
          <div className="text-sm text-gray-600">
            حتى تاريخ: <span className="font-mono font-semibold">{formatDate(report.asOfDate)}</span>
          </div>
        )}
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : !report || report.lines.length === 0 ? (
          <div className="text-center py-12 text-gray-500">
            <Users size={48} className="mx-auto mb-3 text-gray-300" />
            <p>لا توجد أرصدة مدينة مستحقة</p>
            <p className="text-sm mt-1">جميع الفواتير المُرحّلة تم سدادها</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>المورّد</th>
                  {BUCKET_LABELS.map((label, i) => (
                    <th key={i} className="text-left">{label}</th>
                  ))}
                  <th className="text-left text-green-700">مدفوع</th>
                  <th className="text-left">الإجمالي المستحق</th>
                </tr>
              </thead>
              <tbody>
                {report.lines.map((line) => (
                  <tr key={line.contactId} className="hover:bg-gray-50">
                    <td>
                      <button
                        onClick={() => router.push(`/dashboard/contacts/${line.contactId}`)}
                        className="text-right hover:text-primary-700"
                      >
                        <div className="font-semibold flex items-center gap-1">
                          {line.contactName}
                          <ArrowRight size={12} className="text-gray-400" />
                        </div>
                        <div className="text-xs text-gray-500">{line.contactCode}</div>
                      </button>
                    </td>
                    {line.buckets.map((amt, i) => (
                      <td key={i} className={`font-mono text-left ${amt > 0 ? BUCKET_CLASSES[i] : 'text-gray-300'}`} dir="ltr">
                        {amt > 0 ? formatNumber(amt) : '—'}
                      </td>
                    ))}
                    <td className="font-mono text-left text-green-700" dir="ltr">
                      {line.paid != null ? formatNumber(line.paid) : <span className="text-gray-400">—</span>}
                    </td>
                    <td className="font-mono text-left font-bold" dir="ltr">
                      {formatNumber(line.total)}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="border-t-2 font-bold bg-gray-50">
                  <td className="py-2">الإجمالي</td>
                  {report.totals.map((t, i) => (
                    <td key={i} className={`font-mono text-left py-2 ${BUCKET_CLASSES[i]}`} dir="ltr">
                      {formatNumber(t)}
                    </td>
                  ))}
                  <td className="font-mono text-left py-2 text-green-700" dir="ltr">
                    {formatNumber(totalPaid)}
                  </td>
                  <td className="font-mono text-left py-2 text-amber-700" dir="ltr">
                    {formatNumber(report.grandTotal)}
                  </td>
                </tr>
              </tfoot>
            </table>
            <div className="mt-4 p-3 bg-amber-50 text-amber-800 rounded-md text-sm flex items-start gap-2">
              <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
              <div>
                <strong>كيفية القراءة:</strong> المبالغ = ما لم ندفعه بعد (المستحق حالياً).
                <ul className="mt-1 list-disc list-inside">
                  <li>0-30 يوم: لم يحن موعد السداد بعد</li>
                  <li>31-60 يوم: متأخر شهر إلى شهرين</li>
                  <li>61-90 يوم: متأخر ربع سنة (إنذار)</li>
                  <li>+90 يوم: متأخر بشدة</li>
                </ul>
                <p className="mt-2 text-xs">
                  اضغط على اسم المورّد لفتح كشف حسابه الكامل.
                </p>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

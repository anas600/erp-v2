"use client";

/**
 * Sprint 57 — Print Final view for a progress billing (طباعة المسودة النهائية).
 *
 * Server-style HTML page that renders an A4-sized print view of
 * the billing, with:
 *   - Letterhead (company name + project name)
 *   - Project info box (4 parties + contract value)
 *   - Billing summary (number, date, period, % complete)
 *   - Deductions table (6 rows: advance, retention, insurance, admin, original)
 *   - Net amount (highlighted)
 *   - 4 approval signature boxes
 *
 * The page is rendered WITHOUT the dashboard chrome — pure HTML
 * for printing or saving as PDF. The user clicks the browser's
 * Print button (or Ctrl+P) to print.
 *
 * Auth: same super_admin gate as the underlying API.
 */
import { useEffect, useState } from "react";
import { use } from "react";
import { useRouter } from "next/navigation";
import { Loader2, Printer, ArrowRight, AlertCircle } from "lucide-react";
import { api, getErrorMessage } from "@/lib/api";

interface BillingApproval {
  id: string;
  role: string;
  roleLabel: string;
  status: string;          // pending | approved | rejected
  approverName?: string | null;
  approvedAt?: string | null;
  rejectionReason?: string | null;
  notes?: string | null;
}

interface PrintView {
  billingId: string;
  billingNumber: string;
  billingDate: string;
  periodFrom?: string | null;
  periodTo?: string | null;
  workCompletedPercent: number;
  grossAmount: number;
  advanceDeducted: number;
  retentionDeducted: number;
  finalInsuranceDeducted: number;
  adminFeesDeducted: number;
  originalContractDeduction: number;
  netAmount: number;
  billingStatus: string;
  finalApprovedAt?: string | null;
  notes?: string | null;

  projectId: string;
  projectCode: string;
  projectName: string;
  projectNameAr?: string | null;
  projectLocation?: string | null;
  projectStartDate?: string | null;
  projectEndDate?: string | null;
  projectManager?: string | null;

  contractId: string;
  contractNumber?: string | null;
  contractValue: number;
  advancePercent: number;
  retentionPercent: number;
  contractStartDate?: string | null;
  contractEndDate?: string | null;
  siteHandoverDate?: string | null;
  originalContractValue?: number | null;
  finalInsurancePercent: number;
  adminFeePercent: number;
  finalInsuranceReleaseDate?: string | null;

  customerName?: string | null;
  customerNameAr?: string | null;
  contractorName?: string | null;
  contractorNameAr?: string | null;
  consultantName?: string | null;
  consultantNameAr?: string | null;

  companyId: string;
  companyName: string;
  companyNameAr?: string | null;

  approvals: BillingApproval[];
}

const ROLE_ARABIC: Record<string, string> = {
  contractor: "المقاول",
  consultant: "الاستشاري",
  pmo: "إدارة المشروعات",
  owner: "المالك",
};

export default function PrintBillingPage({ params }: { params: Promise<{ id: string }> }) {
  const router = useRouter();
  const { id: billingId } = use(params);
  const [data, setData] = useState<PrintView | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const load = async () => {
      try {
        const res = await api.get<PrintView>(`/print/billings/${billingId}`);
        setData(res.data);
      } catch (e) {
        setError(getErrorMessage(e));
      } finally {
        setLoading(false);
      }
    };
    load();
  }, [billingId]);

  if (loading) {
    return (
      <div dir="rtl" className="min-h-screen flex items-center justify-center bg-slate-100">
        <div className="flex items-center gap-2 text-slate-600">
          <Loader2 className="animate-spin" size={24} />
          <span>جاري تحميل المستخلص للطباعة...</span>
        </div>
      </div>
    );
  }

  if (error || !data) {
    return (
      <div dir="rtl" className="min-h-screen flex items-center justify-center bg-slate-100 p-6">
        <div className="bg-white rounded-lg shadow p-6 max-w-md w-full">
          <div className="flex items-center gap-2 text-red-700 mb-3">
            <AlertCircle size={20} />
            <h1 className="text-lg font-bold">خطأ في التحميل</h1>
          </div>
          <p className="text-slate-600 text-sm mb-4">{error || "لم يتم العثور على المستخلص"}</p>
          <button
            onClick={() => router.back()}
            className="text-sm text-blue-600 hover:text-blue-800 flex items-center gap-1"
          >
            <ArrowRight size={14} />
            رجوع
          </button>
        </div>
      </div>
    );
  }

  const allApproved = data.approvals.every(a => a.status === "approved");
  const approvedCount = data.approvals.filter(a => a.status === "approved").length;

  return (
    <div dir="rtl" className="min-h-screen bg-slate-100 py-6 print:bg-white print:py-0">
      {/* Print-only toolbar (hidden when printing) */}
      <div className="max-w-[210mm] mx-auto mb-4 flex items-center justify-between print:hidden">
        <button
          onClick={() => router.back()}
          className="text-sm text-slate-600 hover:text-slate-800 flex items-center gap-1"
        >
          <ArrowRight size={14} />
          رجوع
        </button>
        <div className="text-sm text-slate-600">
          {allApproved ? (
            <span className="text-emerald-700 font-medium">✓ جميع الاعتمادات مكتملة (4/4)</span>
          ) : (
            <span className="text-amber-700 font-medium">الاعتمادات: {approvedCount} / 4</span>
          )}
        </div>
        <button
          onClick={() => window.print()}
          className="bg-blue-600 text-white px-4 py-2 rounded text-sm flex items-center gap-1 hover:bg-blue-700"
        >
          <Printer size={14} />
          طباعة
        </button>
      </div>

      {/* A4 page */}
      <div className="max-w-[210mm] mx-auto bg-white shadow print:shadow-none p-8 print:p-4" style={{ minHeight: "297mm" }}>
        {/* Letterhead */}
        <div className="border-b-2 border-slate-800 pb-3 mb-4">
          <div className="text-center">
            <h1 className="text-2xl font-bold text-slate-800">{data.companyNameAr || data.companyName}</h1>
            <div className="text-sm text-slate-600 mt-1">المستخلص الدوري — Sprint 57 Print Final</div>
          </div>
        </div>

        {/* Project info */}
        <div className="mb-4">
          <h2 className="text-sm font-bold text-slate-700 mb-2 border-b border-slate-300 pb-1">معلومات المشروع</h2>
          <div className="grid grid-cols-2 gap-2 text-xs">
            <div><span className="text-slate-500">رقم المشروع:</span> <span className="font-mono">{data.projectCode}</span></div>
            <div><span className="text-slate-500">اسم المشروع:</span> {data.projectNameAr || data.projectName}</div>
            <div><span className="text-slate-500">الموقع:</span> {data.projectLocation || "—"}</div>
            <div><span className="text-slate-500">مدير المشروع:</span> {data.projectManager || "—"}</div>
            <div><span className="text-slate-500">تاريخ البدء:</span> {fmtDate(data.projectStartDate)}</div>
            <div><span className="text-slate-500">تاريخ الانتهاء:</span> {fmtDate(data.projectEndDate)}</div>
          </div>
        </div>

        {/* Parties (4) */}
        <div className="mb-4">
          <h2 className="text-sm font-bold text-slate-700 mb-2 border-b border-slate-300 pb-1">الأطراف الأربعة</h2>
          <table className="w-full text-xs border-collapse">
            <thead>
              <tr className="bg-slate-100">
                <th className="border border-slate-300 px-2 py-1 text-right">الدور</th>
                <th className="border border-slate-300 px-2 py-1 text-right">الاسم</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td className="border border-slate-300 px-2 py-1 font-medium">المالك (الجهاز)</td>
                <td className="border border-slate-300 px-2 py-1">{data.customerNameAr || data.customerName || "—"}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1 font-medium">المقاول (المنفذ)</td>
                <td className="border border-slate-300 px-2 py-1">{data.contractorNameAr || data.contractorName || "—"}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1 font-medium">الاستشاري (المشرف)</td>
                <td className="border border-slate-300 px-2 py-1">{data.consultantNameAr || data.consultantName || "—"}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1 font-medium">إدارة المشروعات (لجنة الشركة القابضة)</td>
                <td className="border border-slate-300 px-2 py-1">{data.companyNameAr || data.companyName}</td>
              </tr>
            </tbody>
          </table>
        </div>

        {/* Contract summary */}
        <div className="mb-4">
          <h2 className="text-sm font-bold text-slate-700 mb-2 border-b border-slate-300 pb-1">ملخص العقد</h2>
          <div className="grid grid-cols-2 gap-2 text-xs">
            <div><span className="text-slate-500">رقم العقد:</span> <span className="font-mono">{data.contractNumber || "—"}</span></div>
            <div><span className="text-slate-500">قيمة العقد:</span> <span className="font-mono" dir="ltr">{fmtNum(data.contractValue)} د.ل</span></div>
            <div><span className="text-slate-500">القيمة الأصلية:</span> <span className="font-mono" dir="ltr">{fmtNum(data.originalContractValue || 0)} د.ل</span></div>
            <div><span className="text-slate-500">استلام الموقع:</span> {fmtDate(data.siteHandoverDate)}</div>
          </div>
        </div>

        {/* Billing info */}
        <div className="mb-4">
          <h2 className="text-sm font-bold text-slate-700 mb-2 border-b border-slate-300 pb-1">معلومات المستخلص</h2>
          <div className="grid grid-cols-2 gap-2 text-xs">
            <div><span className="text-slate-500">رقم المستخلص:</span> <span className="font-mono">{data.billingNumber}</span></div>
            <div><span className="text-slate-500">تاريخ المستخلص:</span> {fmtDate(data.billingDate)}</div>
            <div><span className="text-slate-500">الفترة من:</span> {fmtDate(data.periodFrom)}</div>
            <div><span className="text-slate-500">الفترة إلى:</span> {fmtDate(data.periodTo)}</div>
            <div><span className="text-slate-500">نسبة الإنجاز:</span> <span className="font-mono" dir="ltr">{fmtNum(data.workCompletedPercent, 2)}%</span></div>
            <div><span className="text-slate-500">حالة المستخلص:</span> <span className="font-medium">{data.billingStatus}</span></div>
          </div>
        </div>

        {/* Deductions table */}
        <div className="mb-4">
          <h2 className="text-sm font-bold text-slate-700 mb-2 border-b border-slate-300 pb-1">الخصومات والصافي</h2>
          <table className="w-full text-xs border-collapse">
            <thead>
              <tr className="bg-slate-100">
                <th className="border border-slate-300 px-2 py-1 text-right">البيان</th>
                <th className="border border-slate-300 px-2 py-1 text-left" style={{width: "30%"}}>المبلغ (د.ل)</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td className="border border-slate-300 px-2 py-1 font-bold">إجمالي المستخلص (قبل الخصومات)</td>
                <td className="border border-slate-300 px-2 py-1 font-mono text-left" dir="ltr">{fmtNum(data.grossAmount)}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1">خصم الدفعة المقدمة ({data.advancePercent}%)</td>
                <td className="border border-slate-300 px-2 py-1 font-mono text-left" dir="ltr">{fmtNum(data.advanceDeducted)}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1">خصم ضمان الأعمال ({data.retentionPercent}%)</td>
                <td className="border border-slate-300 px-2 py-1 font-mono text-left" dir="ltr">{fmtNum(data.retentionDeducted)}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1">خصم التأمين النهائي ({data.finalInsurancePercent}%)</td>
                <td className="border border-slate-300 px-2 py-1 font-mono text-left" dir="ltr">{fmtNum(data.finalInsuranceDeducted)}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1">رسوم خدمات لصالح الجهاز ({data.adminFeePercent}%)</td>
                <td className="border border-slate-300 px-2 py-1 font-mono text-left" dir="ltr">{fmtNum(data.adminFeesDeducted)}</td>
              </tr>
              <tr>
                <td className="border border-slate-300 px-2 py-1">خصم 15% من قيمة العقد الأصلي</td>
                <td className="border border-slate-300 px-2 py-1 font-mono text-left" dir="ltr">{fmtNum(data.originalContractDeduction)}</td>
              </tr>
              <tr className="bg-emerald-50">
                <td className="border border-slate-400 px-2 py-2 font-bold text-emerald-800">صافي المستخلص (المستحق للدفع)</td>
                <td className="border border-slate-400 px-2 py-2 font-mono text-left font-bold text-emerald-800" dir="ltr">{fmtNum(data.netAmount)} د.ل</td>
              </tr>
            </tbody>
          </table>
        </div>

        {/* Notes */}
        {data.notes && (
          <div className="mb-4">
            <h2 className="text-sm font-bold text-slate-700 mb-2 border-b border-slate-300 pb-1">ملاحظات</h2>
            <p className="text-xs text-slate-700 whitespace-pre-wrap">{data.notes}</p>
          </div>
        )}

        {/* Approvals — 4 signature boxes */}
        <div className="mb-4">
          <h2 className="text-sm font-bold text-slate-700 mb-2 border-b border-slate-300 pb-1">الاعتمادات</h2>
          <div className="grid grid-cols-2 gap-3">
            {data.approvals.map(a => (
              <div key={a.id} className="border border-slate-300 p-2 min-h-[100px]">
                <div className="flex items-center justify-between mb-1">
                  <span className="text-xs font-bold">{a.roleLabel}</span>
                  <span className={`text-[10px] px-2 py-0.5 rounded ${
                    a.status === "approved" ? "bg-emerald-100 text-emerald-700" :
                    a.status === "rejected" ? "bg-red-100 text-red-700" :
                    "bg-slate-100 text-slate-700"
                  }`}>
                    {a.status === "approved" ? "معتمد" : a.status === "rejected" ? "مرفوض" : "بانتظار"}
                  </span>
                </div>
                {a.status === "approved" && (
                  <div className="text-xs space-y-0.5 mt-2">
                    <div><span className="text-slate-500">المعتمد: </span>{a.approverName || "—"}</div>
                    <div><span className="text-slate-500">التاريخ: </span>{fmtDateTime(a.approvedAt)}</div>
                    {a.notes && <div className="italic text-slate-600 mt-1">{a.notes}</div>}
                  </div>
                )}
                {a.status === "rejected" && (
                  <div className="text-xs space-y-0.5 mt-2">
                    <div className="text-red-700"><span className="text-slate-500">السبب: </span>{a.rejectionReason}</div>
                  </div>
                )}
                {a.status === "pending" && (
                  <div className="text-xs text-slate-500 italic mt-2">بانتظار التوقيع</div>
                )}
                {/* Signature line */}
                <div className="border-t border-slate-400 mt-3 pt-1">
                  <div className="text-[10px] text-slate-500">التوقيع</div>
                </div>
              </div>
            ))}
          </div>
        </div>

        {/* Footer */}
        <div className="text-center text-[10px] text-slate-500 mt-6 pt-3 border-t border-slate-300">
          تم إصدار هذه الوثيقة من نظام ERP-V2 — {fmtDateTime(data.finalApprovedAt)} — رقم المستخلص: {data.billingNumber}
        </div>
      </div>
    </div>
  );
}

function fmtNum(n: number, decimals: number = 0): string {
  if (n == null) return "—";
  return n.toLocaleString("en-US", { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
}

function fmtDate(d?: string | null): string {
  if (!d) return "—";
  try {
    return new Date(d).toLocaleDateString("en-GB");
  } catch {
    return d;
  }
}

function fmtDateTime(d?: string | null): string {
  if (!d) return "—";
  try {
    return new Date(d).toLocaleString("en-GB", { dateStyle: "short", timeStyle: "short" });
  } catch {
    return d;
  }
}

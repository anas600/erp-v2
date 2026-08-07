"use client";

import { useEffect, useState, useCallback } from "react";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import {
  CalendarRange, Plus, Loader2, X, Lock, Unlock, AlertCircle, CheckCircle2, Calendar
} from "lucide-react";
import { formatDate } from "@/lib/utils";
import type { FiscalYear, FiscalPeriod } from "@/lib/types";

/**
 * Fiscal years + periods (السنوات والفترات المالية).
 *
 * Sprint 25 introduces fiscal-year bookkeeping:
 *   - A year has 12 (or 13) periods; the year record groups them.
 *   - A closed year cannot accept new postings.
 *   - A locked period also blocks postings (even within an open year).
 *   - Only super-admins may unlock a period once it's locked.
 *
 * This page is the admin surface: list years, create a new year,
 * create its 12 periods in one click, lock/unlock a period, close a
 * year. The accounting surface (period-locked block) lives in the
 * Journal pipeline; the UI just shows the state.
 */
export default function FiscalYearsPage() {
  const { activeCompany, user } = useAuth();
  const [years, setYears] = useState<FiscalYear[]>([]);
  const [periods, setPeriods] = useState<FiscalPeriod[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<string | null>(null);
  const [expandedYear, setExpandedYear] = useState<string | null>(null);

  const [form, setForm] = useState({
    code: "",
    startDate: "",
    endDate: ""
  });

  const load = useCallback(async () => {
    if (!activeCompany) return;
    setLoading(true);
    try {
      const [yRes, pRes] = await Promise.allSettled([
        api.get(`/fiscal-years?companyId=${activeCompany.id}`),
        api.get(`/fiscal-periods?companyId=${activeCompany.id}`)
      ]);
      // Same normalise trick as the other pages: backend may wrap or not.
      const yearsData: FiscalYear[] = yRes.status === "fulfilled"
        ? (Array.isArray(yRes.value.data) ? yRes.value.data : (yRes.value.data?.data || []))
        : [];
      const periodsData: FiscalPeriod[] = pRes.status === "fulfilled"
        ? (Array.isArray(pRes.value.data) ? pRes.value.data : (pRes.value.data?.data || []))
        : [];
      setYears(yearsData);
      setPeriods(periodsData);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [activeCompany]);

  useEffect(() => { load(); }, [load]);

  // Find the current period (the one whose [start, end] contains today).
  // Used to highlight the "now" period at the top of the page.
  const today = new Date().toISOString().slice(0, 10);
  const currentPeriod = periods.find((p) => p.startDate <= today && p.endDate >= today);
  const currentYear = currentPeriod
    ? years.find((y) => y.id === currentPeriod.fiscalYearId)
    : years.find((y) => y.startDate <= today && y.endDate >= today);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    if (!form.code || !form.startDate || !form.endDate) {
      setError("جميع الحقول مطلوبة");
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      await api.post("/fiscal-years", {
        companyId: activeCompany.id,
        code: form.code,
        startDate: form.startDate,
        endDate: form.endDate
      });
      setSuccess(`تم إنشاء السنة المالية ${form.code}`);
      setForm({ code: "", startDate: "", endDate: "" });
      setShowForm(false);
      await load();
      setTimeout(() => setSuccess(null), 3000);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const closeYear = async (id: string, code: string) => {
    if (!confirm(`إغلاق السنة المالية ${code}؟ لن يمكن إنشاء فترات جديدة أو تعديلها بعد الإغلاق.`)) return;
    try {
      await api.post(`/fiscal-years/${id}/close`);
      setSuccess(`تم إغلاق السنة ${code}`);
      await load();
      setTimeout(() => setSuccess(null), 3000);
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const togglePeriodLock = async (period: FiscalPeriod) => {
    const action = period.isClosed ? "unlock" : "lock";
    if (action === "unlock" && !user?.isSuperAdmin) {
      alert("فتح فترة مقفلة يتطلب صلاحية مدير عام");
      return;
    }
    const verb = action === "lock" ? "قفل" : "فتح";
    if (!confirm(`${verb} الفترة ${period.periodNumber}؟`)) return;
    try {
      await api.post(`/fiscal-periods/${period.id}/${action}`);
      setSuccess(`تم ${verb} الفترة`);
      await load();
      setTimeout(() => setSuccess(null), 3000);
    } catch (err) {
      alert(getErrorMessage(err));
    }
  };

  const periodsByYear = (yearId: string) =>
    periods.filter((p) => p.fiscalYearId === yearId).sort((a, b) => a.periodNumber - b.periodNumber);

  // Default start/end for the new-year form: next year Jan 1 → Dec 31.
  const nextYear = new Date().getFullYear() + 1;
  useEffect(() => {
    if (showForm && !form.startDate) {
      setForm((f) => ({
        ...f,
        code: String(nextYear),
        startDate: `${nextYear}-01-01`,
        endDate: `${nextYear}-12-31`
      }));
    }
  }, [showForm, nextYear, form.startDate]);

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-ink-strong flex items-center gap-2">
            <CalendarRange size={24} className="text-primary-600" />
            السنوات والفترات المالية
          </h1>
          <p className="text-sm text-ink-muted mt-1">
            إدارة السنوات المحاسبية — الفترات المقفلة تمنع إنشاء قيود جديدة
          </p>
        </div>
        <button onClick={() => setShowForm(true)} className="btn-primary">
          <Plus size={18} />
          سنة جديدة
        </button>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}
      {success && (
        <div className="mb-4 p-3 bg-green-50 text-green-700 rounded-md text-sm flex items-center gap-2">
          <CheckCircle2 size={16} /> {success}
        </div>
      )}

      {/* Current period callout */}
      {currentYear && (
        <div className="card mb-4 bg-primary-50 border-primary-200">
          <div className="flex items-center gap-3">
            <div className="w-12 h-12 rounded-md bg-primary-600 text-white flex items-center justify-center">
              <Calendar size={24} />
            </div>
            <div>
              <p className="text-xs text-ink-muted">الفترة الحالية</p>
              <p className="text-lg font-bold text-ink-strong">
                السنة المالية {currentYear.code}
                {currentPeriod && ` — الفترة ${currentPeriod.periodNumber}`}
              </p>
              <p className="text-sm text-ink-muted">
                {formatDate(currentYear.startDate)} → {formatDate(currentYear.endDate)}
                {currentPeriod?.isClosed && (
                  <span className="badge badge-danger mr-2">الفترة مقفلة</span>
                )}
              </p>
            </div>
          </div>
        </div>
      )}

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : years.length === 0 ? (
          <div className="text-center py-12 text-ink-muted">
            <CalendarRange size={48} className="mx-auto mb-3 text-ink-subtle" />
            <p>لا توجد سنوات مالية</p>
            <p className="text-sm mt-1">أنشئ سنة جديدة للبدء</p>
          </div>
        ) : (
          <div className="space-y-3">
            {years.map((y) => {
              const expanded = expandedYear === y.id;
              const yearPeriods = periodsByYear(y.id);
              return (
                <div key={y.id} className="border border-edge rounded-md">
                  <div className="flex items-center justify-between p-3 hover:bg-raised">
                    <button
                      onClick={() => setExpandedYear(expanded ? null : y.id)}
                      className="flex items-center gap-3 flex-1 text-right"
                    >
                      <div className={`w-10 h-10 rounded-md flex items-center justify-center text-white ${
                        y.isClosed ? "bg-gray-400" : "bg-green-500"
                      }`}>
                        <CalendarRange size={18} />
                      </div>
                      <div>
                        <div className="font-semibold text-ink-strong">
                          {y.code}
                          {currentYear?.id === y.id && (
                            <span className="badge badge-info mr-2">الحالية</span>
                          )}
                          {y.isClosed && (
                            <span className="badge badge-danger mr-2">مقفلة</span>
                          )}
                        </div>
                        <div className="text-xs text-ink-muted" dir="ltr">
                          {formatDate(y.startDate)} → {formatDate(y.endDate)}
                          <span className="mx-2 text-ink-subtle">|</span>
                          {yearPeriods.length} فترة
                          <span className="mx-2 text-ink-subtle">|</span>
                          {yearPeriods.filter((p) => p.isClosed).length} مقفلة
                        </div>
                      </div>
                    </button>
                    <div className="flex items-center gap-2">
                      {!y.isClosed && (
                        <button
                          onClick={() => closeYear(y.id, y.code)}
                          className="btn-secondary text-xs"
                        >
                          <Lock size={12} /> إغلاق السنة
                        </button>
                      )}
                    </div>
                  </div>
                  {expanded && (
                    <div className="border-t border-edge p-3 bg-raised">
                      {yearPeriods.length === 0 ? (
                        <p className="text-sm text-ink-muted text-center py-4">
                          لا توجد فترات لهذه السنة — أنشئها من الـ Backend (auto-gen عند الإنشاء عادةً)
                        </p>
                      ) : (
                        <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-2">
                          {yearPeriods.map((p) => (
                            <div
                              key={p.id}
                              className={`p-3 rounded-md border text-sm ${
                                p.isClosed
                                  ? "bg-red-50 border-red-200"
                                  : p.id === currentPeriod?.id
                                  ? "bg-primary-50 border-primary-300"
                                  : "bg-canvas dark:bg-neutral-900 border-edge"
                              }`}
                            >
                              <div className="flex items-center justify-between mb-1">
                                <span className="font-semibold">الفترة {p.periodNumber}</span>
                                {p.isClosed && <Lock size={12} className="text-red-600" />}
                                {!p.isClosed && p.id === currentPeriod?.id && (
                                  <span className="badge badge-info text-xs">الآن</span>
                                )}
                              </div>
                              <div className="text-xs text-ink-muted mb-2" dir="ltr">
                                {formatDate(p.startDate)} → {formatDate(p.endDate)}
                              </div>
                              <button
                                onClick={() => togglePeriodLock(p)}
                                disabled={p.isClosed && !user?.isSuperAdmin}
                                className={`text-xs flex items-center gap-1 px-2 py-1 rounded w-full justify-center ${
                                  p.isClosed
                                    ? "bg-green-50 text-green-700 hover:bg-green-100 disabled:opacity-50 disabled:cursor-not-allowed"
                                    : "bg-red-50 text-red-700 hover:bg-red-100"
                                }`}
                                title={p.isClosed && !user?.isSuperAdmin ? "يتطلب صلاحية مدير عام" : ""}
                              >
                                {p.isClosed ? (
                                  <><Unlock size={12} /> فتح</>
                                ) : (
                                  <><Lock size={12} /> قفل</>
                                )}
                              </button>
                            </div>
                          ))}
                        </div>
                      )}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>

      <div className="mt-4 p-3 bg-primary-50 text-primary-800 rounded-md text-sm flex items-start gap-2">
        <AlertCircle size={16} className="mt-0.5 flex-shrink-0" />
        <div>
          <strong>ملاحظة:</strong> قفل فترة أو سنة مالية يمنع إنشاء أي قيود يومية في هذه الفترة.
          المحاسب الذي يحاول الترحيل في فترة مقفلة سيرى رسالة خطأ واضحة.
        </div>
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-canvas dark:bg-neutral-900 rounded-card shadow-xl w-full max-w-md p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold flex items-center gap-2">
                <CalendarRange size={20} className="text-primary-600" /> سنة مالية جديدة
              </h2>
              <button onClick={() => setShowForm(false)} className="text-ink-subtle hover:text-ink-muted">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submit} className="space-y-3">
              <div>
                <label className="block text-sm font-medium mb-1">الكود *</label>
                <input
                  className="input"
                  value={form.code}
                  onChange={(e) => setForm({ ...form, code: e.target.value })}
                  required
                  dir="ltr"
                  placeholder="2026"
                />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">تاريخ البداية *</label>
                  <input
                    type="date"
                    className="input"
                    value={form.startDate}
                    onChange={(e) => setForm({ ...form, startDate: e.target.value })}
                    required
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">تاريخ النهاية *</label>
                  <input
                    type="date"
                    className="input"
                    value={form.endDate}
                    onChange={(e) => setForm({ ...form, endDate: e.target.value })}
                    required
                  />
                </div>
              </div>
              <div className="text-xs text-ink-muted bg-raised p-2 rounded">
                💡 بعد الإنشاء، يمكنك توسيع السنة لإدارة الفترات (12 فترة افتراضية).
              </div>

              {error && <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الإنشاء..." : "إنشاء"}
                </button>
                <button type="button" onClick={() => setShowForm(false)} className="btn-secondary">
                  إلغاء
                </button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  );
}

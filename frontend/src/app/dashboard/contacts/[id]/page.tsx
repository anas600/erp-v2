"use client";

import { useEffect, useMemo, useState, useCallback } from "react";
import { useParams, useRouter } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import {
  Loader2, ArrowRight, Users, Phone, Mail, FileText, Hash, Building2,
  CreditCard, Wallet, Inbox, ScrollText, Calendar, Printer, Search, AlertCircle,
  Link2, Plus, CheckCircle2
} from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";
import type {
  Account, ContactBalance, InvoiceWithOutstanding,
  StatementResponse, VoucherWithInvoice
} from "@/lib/types";

type TabKey = "invoices" | "vouchers" | "statement";

interface Contact {
  id: string;
  type: "customer" | "supplier";
  code: string;
  name: string;
  nameAr?: string;
  taxId?: string;
  phone?: string;
  email?: string;
  isActive: boolean;
}

const TAB_LABELS: Record<TabKey, string> = {
  invoices: "الفواتير",
  vouchers: "السندات",
  statement: "كشف حساب"
};

const STATUS_BADGE: Record<string, { label: string; cls: string }> = {
  draft:       { label: "مسودة",        cls: "badge-warning" },
  posted:      { label: "مُرحّلة",       cls: "bg-blue-100 text-blue-800" },
  partiallypaid: { label: "مدفوع جزئياً", cls: "bg-amber-100 text-amber-800" },
  paid:        { label: "مدفوعة بالكامل", cls: "badge-success" },
  cancelled:   { label: "ملغاة",         cls: "badge-danger" }
};

const PAYMENT_METHODS_AR: Record<string, string> = {
  cash: "نقدي",
  bank: "بنكي",
  check: "شيك"
};

/**
 * Contact detail page (كشف حساب تفصيلي).
 *
 * Route: /dashboard/contacts/{id}
 *
 * Three tabs:
 *   1. الفواتير  (Invoices)   — list of all invoices for the contact
 *                                with settlement status. Each outstanding
 *                                row has a "تسديد" button that pre-fills
 *                                a receipt/payment form on the
 *                                corresponding page.
 *   2. السندات   (Vouchers)    — every receipt + payment that touched
 *                                the contact, with the linked invoice
 *                                number when present.
 *   3. كشف حساب (Statement)   — chronological debit/credit table
 *                                with opening/closing balances, date
 *                                range filter, and print-friendly CSS.
 *
 * Sprint 25 is the first sprint that surfaces contact-level data
 * this way. Until now the receipts/payments pages were the only
 * place to see vouchers, and there was no per-contact invoice view.
 */
export default function ContactDetailPage() {
  const params = useParams<{ id: string }>();
  const router = useRouter();
  const contactId = params?.id;
  const { activeCompany } = useAuth();

  const [contact, setContact] = useState<Contact | null>(null);
  const [balance, setBalance] = useState<ContactBalance | null>(null);
  const [subLedger, setSubLedger] = useState<Account | null>(null);
  const [tab, setTab] = useState<TabKey>("invoices");
  const [error, setError] = useState<string | null>(null);
  const [pageLoading, setPageLoading] = useState(true);
  // Sprint 26: sub-ledger creation state. The badge or button in
  // the header reflects this; the user can create the sub-ledger
  // here if it doesn't exist yet.
  const [creatingSubLedger, setCreatingSubLedger] = useState(false);
  const [subLedgerMsg, setSubLedgerMsg] = useState<string | null>(null);

  // Load the contact + balance once for the header card.
  useEffect(() => {
    if (!contactId) return;
    let cancelled = false;
    (async () => {
      setPageLoading(true);
      try {
        const [c, b, sl] = await Promise.allSettled([
          api.get(`/contacts/${contactId}`),
          api.get(`/contacts/${contactId}/balance`),
          api.get(`/accounts/sub-ledger/${contactId}`)
        ]);
        if (cancelled) return;
        if (c.status === "fulfilled") setContact(c.value.data);
        if (b.status === "fulfilled") setBalance(b.value.data);
        // 404 on the sub-ledger endpoint is expected (the contact
        // has no sub-ledger yet). Anything else is a real error.
        if (sl.status === "fulfilled") {
          setSubLedger(sl.value.data);
        } else if (sl.status === "rejected") {
          const status = (sl.reason as any)?.response?.status;
          if (status !== 404) {
            // Real error — surface to console but don't block the page.
            console.warn("Sub-ledger lookup failed:", sl.reason);
          }
        }
        if (c.status === "rejected" && b.status === "rejected") {
          setError(getErrorMessage(c.reason));
        }
      } finally {
        if (!cancelled) setPageLoading(false);
      }
    })();
    return () => { cancelled = true; };
  }, [contactId]);

  // Create the sub-ledger for this contact. The backend derives the
  // parent (1200 for customer, 2000 for supplier) from the contact
  // type, so we only send the contact id + detail code (which is
  // the contact's own code, per the seeded convention).
  const createSubLedger = async () => {
    if (!activeCompany || !contact) return;
    if (!confirm(
      contact.type === "customer"
        ? "إنشاء حساب تفصيلي (L4) لهذا العميل تحت حساب 1200 — العملاء؟"
        : "إنشاء حساب تفصيلي (L4) لهذا المورّد تحت حساب 2000 — الموردون؟"
    )) return;
    setCreatingSubLedger(true);
    setSubLedgerMsg(null);
    try {
      // The parent account code is chosen by the contact type.
      // The detail code is the contact's own code, so the final
      // sub-ledger code follows the pattern 1200-CUST-001 etc.
      const parentCode = contact.type === "customer" ? "1200" : "2000";
      const res = await api.post("/accounts/sub-ledger", {
        companyId: activeCompany.id,
        contactId: contact.id,
        parentAccountCode: parentCode,
        detailCode: contact.code
      });
      setSubLedger(res.data);
      setSubLedgerMsg("تم إنشاء الحساب التفصيلي بنجاح");
      // Auto-hide the success after a few seconds.
      setTimeout(() => setSubLedgerMsg(null), 3000);
    } catch (err) {
      setSubLedgerMsg(getErrorMessage(err));
    } finally {
      setCreatingSubLedger(false);
    }
  };

  if (pageLoading) {
    return (
      <div className="flex justify-center py-12">
        <Loader2 className="animate-spin text-primary-500" size={32} />
      </div>
    );
  }

  if (error || !contact) {
    return (
      <div className="card">
        <div className="p-6 text-center">
          <AlertCircle size={48} className="mx-auto mb-3 text-red-400" />
          <p className="text-gray-700">{error || "لم يتم العثور على الجهة"}</p>
          <button onClick={() => router.push("/dashboard/contacts")} className="btn-secondary mt-4">
            العودة إلى القائمة
          </button>
        </div>
      </div>
    );
  }

  return (
    <div>
      {/* Header */}
      <div className="flex items-center gap-2 mb-3 text-sm text-gray-600">
        <button
          onClick={() => router.push("/dashboard/contacts")}
          className="flex items-center gap-1 hover:text-primary-600"
        >
          <ArrowRight size={14} /> العملاء والموردون
        </button>
        <span className="text-gray-400">/</span>
        <span className="font-medium text-gray-900">{contact.nameAr || contact.name}</span>
      </div>

      <div className="card mb-4">
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div className="flex items-start gap-3">
            <div className={`w-12 h-12 rounded-md flex items-center justify-center text-white text-lg font-bold ${
              contact.type === "customer" ? "bg-blue-500" : "bg-amber-500"
            }`}>
              {(contact.nameAr || contact.name).charAt(0)}
            </div>
            <div>
              <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
                {contact.nameAr || contact.name}
              </h1>
              <div className="flex flex-wrap items-center gap-3 mt-1 text-sm text-gray-600">
                <span className="flex items-center gap-1">
                  <Hash size={12} /> <span className="font-mono">{contact.code}</span>
                </span>
                <span className={`badge ${contact.type === "customer" ? "badge-info" : "badge-warning"}`}>
                  {contact.type === "customer" ? "عميل" : "مورّد"}
                </span>
                {contact.taxId && (
                  <span className="flex items-center gap-1" dir="ltr">
                    <Building2 size={12} /> {contact.taxId}
                  </span>
                )}
                {contact.phone && (
                  <span className="flex items-center gap-1" dir="ltr">
                    <Phone size={12} /> {contact.phone}
                  </span>
                )}
                {contact.email && (
                  <span className="flex items-center gap-1" dir="ltr">
                    <Mail size={12} /> {contact.email}
                  </span>
                )}
              </div>
            </div>
          </div>
          <div className="text-left">
            <p className="text-xs text-gray-500 mb-1">الرصيد الحالي</p>
            <p className={`text-2xl font-bold font-mono ${
              balance && balance.balance > 0.01
                ? "text-red-600"
                : balance && balance.balance < -0.01
                ? "text-green-600"
                : "text-gray-500"
            }`} dir="ltr">
              {balance ? formatNumber(Math.abs(balance.balance)) : "—"}
            </p>
            <p className="text-xs text-gray-500 mt-0.5">
              {balance && balance.balance > 0.01
                ? (contact.type === "customer" ? "مستحق لنا" : "مستحق علينا")
                : balance && balance.balance < -0.01
                ? (contact.type === "customer" ? "رصيد مدفوع مقدماً" : "رصيد لصالحنا")
                : "مسوّى"}
              <span className="text-gray-400 mr-2">د.ل</span>
            </p>
          </div>
        </div>

        {/* Sub-ledger row — Sprint 26.
            The contact may or may not have a sub-ledger account
            linked. If yes, show a clickable badge that jumps to
            the accounts tree filtered by that account. If no,
            show a "Create sub-ledger" button. */}
        <div className="mt-4 pt-3 border-t border-gray-100 flex flex-wrap items-center gap-3">
          <span className="text-xs text-gray-500 flex items-center gap-1">
            <Link2 size={12} />
            الحساب التفصيلي:
          </span>
          {subLedger ? (
            <button
              onClick={() => router.push(`/dashboard/accounts`)}
              className="badge bg-primary-50 text-primary-700 border border-primary-200 hover:bg-primary-100 cursor-pointer flex items-center gap-1"
              title="اذهب إلى شجرة الحسابات"
              dir="ltr"
            >
              <Wallet size={12} />
              <span className="font-mono font-semibold">{subLedger.code}</span>
            </button>
          ) : (
            <button
              onClick={createSubLedger}
              disabled={creatingSubLedger}
              className="text-xs flex items-center gap-1 px-2 py-1 rounded bg-primary-50 text-primary-700 hover:bg-primary-100 disabled:opacity-50"
            >
              {creatingSubLedger ? (
                <Loader2 className="animate-spin" size={12} />
              ) : (
                <Plus size={12} />
              )}
              إنشاء حساب تفصيلي
            </button>
          )}
          {subLedgerMsg && (
            <span
              className={`text-xs flex items-center gap-1 ${
                subLedger
                  ? "text-green-700"
                  : "text-red-700"
              }`}
            >
              {!subLedger && <AlertCircle size={12} />}
              {subLedger && <CheckCircle2 size={12} />}
              {subLedgerMsg}
            </span>
          )}
          <span className="text-xs text-gray-400">
            (مرتبط بحساب 1200 للعملاء أو 2000 للموردين)
          </span>
        </div>
      </div>

      {/* Tabs */}
      <div className="flex gap-2 mb-4 border-b border-gray-200">
        {(Object.keys(TAB_LABELS) as TabKey[]).map((k) => (
          <button
            key={k}
            onClick={() => setTab(k)}
            className={`px-4 py-2 text-sm font-medium border-b-2 -mb-px ${
              tab === k
                ? "border-primary-600 text-primary-700"
                : "border-transparent text-gray-600 hover:text-gray-900"
            }`}
          >
            {TAB_LABELS[k]}
          </button>
        ))}
      </div>

      {/* Tab body */}
      {tab === "invoices" && (
        <InvoicesTab
          contactId={contact.id}
          contactType={contact.type}
          companyId={activeCompany?.id || ""}
        />
      )}
      {tab === "vouchers" && (
        <VouchersTab
          contactId={contact.id}
          contactType={contact.type}
        />
      )}
      {tab === "statement" && (
        <StatementTab
          contactId={contact.id}
          companyId={activeCompany?.id || ""}
          contactName={contact.nameAr || contact.name}
        />
      )}
    </div>
  );
}

// ─── Invoices tab ─────────────────────────────────────────────────────────

function InvoicesTab({ contactId, contactType, companyId }: {
  contactId: string; contactType: "customer" | "supplier"; companyId: string;
}) {
  const router = useRouter();
  const [invoices, setInvoices] = useState<InvoiceWithOutstanding[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [filter, setFilter] = useState<"all" | "outstanding" | "paid">("all");

  const load = useCallback(async () => {
    if (!contactId) return;
    setLoading(true);
    try {
      const res = await api.get(`/contacts/${contactId}/invoices`);
      // The endpoint may return either a bare array or { data: [...] }
      // depending on backend shape. Normalize here so we never silently
      // crash on a missing wrapper.
      const list: InvoiceWithOutstanding[] = Array.isArray(res.data) ? res.data : (res.data?.data || []);
      setInvoices(list);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [contactId]);

  useEffect(() => { load(); }, [load]);

  const filtered = useMemo(() => {
    if (filter === "all") return invoices;
    if (filter === "outstanding") {
      return invoices.filter((i) => i.status === "posted" || i.status === "partiallypaid");
    }
    if (filter === "paid") {
      return invoices.filter((i) => i.status === "paid");
    }
    return invoices;
  }, [invoices, filter]);

  const totals = useMemo(() => {
    const t = invoices.reduce((acc, i) => {
      acc.total += i.total;
      acc.paid += i.amountPaid;
      acc.outstanding += i.outstanding;
      return acc;
    }, { total: 0, paid: 0, outstanding: 0 });
    return t;
  }, [invoices]);

  return (
    <div>
      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {/* Filter + summary */}
      <div className="card mb-4">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div className="flex gap-2">
            {[
              { v: "all", l: `الكل (${invoices.length})` },
              { v: "outstanding", l: "المستحقة" },
              { v: "paid", l: "المسددة" }
            ].map((t) => (
              <button
                key={t.v}
                onClick={() => setFilter(t.v as any)}
                className={`px-4 py-2 rounded-md text-sm font-medium ${
                  filter === t.v
                    ? "bg-primary-600 text-white"
                    : "bg-white text-gray-700 border border-gray-300 hover:bg-gray-50"
                }`}
              >
                {t.l}
              </button>
            ))}
          </div>
          {invoices.length > 0 && (
            <div className="text-sm text-gray-700 flex gap-4">
              <span>إجمالي: <span className="font-mono font-semibold" dir="ltr">{formatNumber(totals.total)}</span></span>
              <span>مدفوع: <span className="font-mono font-semibold text-green-700" dir="ltr">{formatNumber(totals.paid)}</span></span>
              <span>متبقي: <span className="font-mono font-semibold text-red-700" dir="ltr">{formatNumber(totals.outstanding)}</span></span>
            </div>
          )}
        </div>
      </div>

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : filtered.length === 0 ? (
          <div className="text-center py-12 text-gray-500">
            <FileText size={48} className="mx-auto mb-3 text-gray-300" />
            <p>لا توجد فواتير</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>الرقم</th>
                  <th>النوع</th>
                  <th>التاريخ</th>
                  <th className="text-left">الإجمالي</th>
                  <th className="text-left">المدفوع</th>
                  <th className="text-left">المتبقي</th>
                  <th>الحالة</th>
                  <th>العمر</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((inv) => {
                  const sb = STATUS_BADGE[inv.status] || { label: inv.status, cls: "bg-gray-100 text-gray-800" };
                  const isOutstanding = inv.outstanding > 0.01;
                  return (
                    <tr key={inv.invoiceId} className="hover:bg-gray-50">
                      <td className="font-mono font-semibold">{inv.invoiceNumber}</td>
                      <td>
                        {inv.invoiceType === "sales" ? (
                          <span className="badge badge-info">مبيعات</span>
                        ) : (
                          <span className="badge badge-warning">مشتريات</span>
                        )}
                      </td>
                      <td>{formatDate(inv.invoiceDate)}</td>
                      <td className="font-mono text-left" dir="ltr">{formatNumber(inv.total)}</td>
                      <td className="font-mono text-left text-green-700" dir="ltr">{formatNumber(inv.amountPaid)}</td>
                      <td className={`font-mono text-left font-bold ${isOutstanding ? "text-red-600" : "text-gray-400"}`} dir="ltr">
                        {formatNumber(inv.outstanding)}
                      </td>
                      <td><span className={`badge ${sb.cls}`}>{sb.label}</span></td>
                      <td className="text-xs text-gray-600" dir="ltr">{inv.ageDays} يوم</td>
                      <td>
                        {isOutstanding && (
                          <button
                            onClick={() => {
                              const base = contactType === "customer" ? "receipts" : "payments";
                              const params = new URLSearchParams({
                                contactId,
                                invoiceId: inv.invoiceId,
                                amount: String(inv.outstanding)
                              });
                              router.push(`/dashboard/${base}?${params.toString()}`);
                            }}
                            className="text-xs flex items-center gap-1 px-2 py-1 rounded bg-primary-50 text-primary-700 hover:bg-primary-100"
                            title="تسديد هذه الفاتورة"
                          >
                            <CreditCard size={12} /> تسديد
                          </button>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Vouchers tab ─────────────────────────────────────────────────────────

function VouchersTab({ contactId, contactType }: {
  contactId: string; contactType: "customer" | "supplier";
}) {
  const [vouchers, setVouchers] = useState<VoucherWithInvoice[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // We always fetch BOTH receipts + payments for the contact so the user
  // sees a single chronological view. The endpoint returns an array; we
  // normalise to VoucherWithInvoice so the row layout is identical.
  const load = useCallback(async () => {
    if (!contactId) return;
    setLoading(true);
    try {
      const [r, p] = await Promise.allSettled([
        api.get(`/receipts?contactId=${contactId}`),
        api.get(`/payments?contactId=${contactId}`)
      ]);
      const list: VoucherWithInvoice[] = [];
      if (r.status === "fulfilled") {
        const rows = Array.isArray(r.value.data) ? r.value.data : (r.value.data?.data || []);
        rows.forEach((v: any) => list.push({
          ...v,
          voucherType: "receipt"
        }));
      }
      if (p.status === "fulfilled") {
        const rows = Array.isArray(p.value.data) ? p.value.data : (p.value.data?.data || []);
        rows.forEach((v: any) => list.push({
          ...v,
          voucherType: "payment"
        }));
      }
      list.sort((a, b) => (b.voucherDate || "").localeCompare(a.voucherDate || ""));
      setVouchers(list);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [contactId]);

  useEffect(() => { load(); }, [load]);

  return (
    <div>
      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}
      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : vouchers.length === 0 ? (
          <div className="text-center py-12 text-gray-500">
            <Inbox size={48} className="mx-auto mb-3 text-gray-300" />
            <p>لا توجد سندات لهذه الجهة</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>النوع</th>
                  <th>رقم السند</th>
                  <th>التاريخ</th>
                  <th>الفاتورة المرتبطة</th>
                  <th>طريقة الدفع</th>
                  <th>الحساب</th>
                  <th className="text-left">المبلغ</th>
                  <th>الحالة</th>
                </tr>
              </thead>
              <tbody>
                {vouchers.map((v) => (
                  <tr key={`${v.voucherType}-${v.id}`}>
                    <td>
                      {v.voucherType === "receipt" ? (
                        <span className="badge badge-info">قبض</span>
                      ) : (
                        <span className="badge badge-warning">صرف</span>
                      )}
                    </td>
                    <td className="font-mono font-semibold">{v.voucherNumber}</td>
                    <td>{formatDate(v.voucherDate)}</td>
                    <td className="font-mono text-sm text-primary-700">
                      {v.invoiceNumber || <span className="text-gray-400">— على الحساب —</span>}
                    </td>
                    <td className="text-sm">{PAYMENT_METHODS_AR[v.paymentMethod] || v.paymentMethod}</td>
                    <td className="font-mono text-sm" dir="ltr">{v.bankAccountCode || "—"}</td>
                    <td className="font-mono text-left font-semibold" dir="ltr">
                      {formatNumber(v.amount)}
                    </td>
                    <td>
                      {v.status === "posted" && <span className="badge badge-success">مرحّل</span>}
                      {v.status === "draft" && <span className="badge badge-warning">مسودة</span>}
                      {v.status === "void" && <span className="badge badge-danger">مُلغى</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
}

// ─── Statement tab ────────────────────────────────────────────────────────

function StatementTab({ contactId, companyId, contactName }: {
  contactId: string; companyId: string; contactName: string;
}) {
  const today = new Date().toISOString().slice(0, 10);
  const firstOfYear = `${new Date().getFullYear()}-01-01`;

  const [from, setFrom] = useState(firstOfYear);
  const [to, setTo] = useState(today);
  const [data, setData] = useState<StatementResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!contactId) return;
    setLoading(true);
    setError(null);
    try {
      const res = await api.get(`/contacts/${contactId}/statement?from=${from}&to=${to}`);
      // Normalize shape (bare vs wrapped).
      const payload: StatementResponse = res.data?.data || res.data;
      setData(payload);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, [contactId, from, to]);

  useEffect(() => { load(); }, [load]);

  const totalDebit = data?.lines.reduce((s, l) => s + l.debit, 0) || 0;
  const totalCredit = data?.lines.reduce((s, l) => s + l.credit, 0) || 0;

  return (
    <div>
      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {/* Date range filter */}
      <div className="card mb-4 no-print">
        <div className="flex flex-wrap items-end gap-3">
          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">من تاريخ</label>
            <input
              type="date"
              className="input"
              value={from}
              onChange={(e) => setFrom(e.target.value)}
            />
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">إلى تاريخ</label>
            <input
              type="date"
              className="input"
              value={to}
              onChange={(e) => setTo(e.target.value)}
            />
          </div>
          <button
            onClick={load}
            disabled={loading}
            className="btn-primary"
          >
            {loading ? <Loader2 className="animate-spin" size={16} /> : <Search size={16} />}
            عرض
          </button>
          <button
            onClick={() => window.print()}
            className="btn-secondary"
          >
            <Printer size={16} /> طباعة
          </button>
        </div>
      </div>

      {/* Print-only header */}
      <div className="hidden print:block mb-4">
        <h1 className="text-xl font-bold text-center">كشف حساب — {contactName}</h1>
        <p className="text-center text-sm">من {formatDate(from)} إلى {formatDate(to)}</p>
      </div>

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : !data || data.lines.length === 0 ? (
          <div className="text-center py-12 text-gray-500">
            <ScrollText size={48} className="mx-auto mb-3 text-gray-300" />
            <p>لا توجد حركات في هذه الفترة</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>التاريخ</th>
                  <th>النوع</th>
                  <th>الرقم</th>
                  <th>البيان</th>
                  <th className="text-left">مدين</th>
                  <th className="text-left">دائن</th>
                  <th className="text-left">الرصيد</th>
                </tr>
              </thead>
              <tbody>
                {/* Opening balance row */}
                <tr className="bg-gray-50 font-semibold">
                  <td colSpan={6}>رصيد افتتاحي (حتى {formatDate(from)})</td>
                  <td className="font-mono text-left" dir="ltr">{formatNumber(data.openingBalance)}</td>
                </tr>
                {data.lines.map((l, i) => {
                  const typeLabel = {
                    invoice: "فاتورة",
                    receipt: "سند قبض",
                    payment: "سند صرف",
                    opening: "افتتاحي"
                  }[l.type] || l.type;
                  return (
                    <tr key={i} className="hover:bg-gray-50">
                      <td>{formatDate(l.date)}</td>
                      <td>
                        <span className={`badge text-xs ${
                          l.type === "invoice" ? "badge-info" :
                          l.type === "receipt" ? "badge-success" :
                          l.type === "payment" ? "badge-warning" : "bg-gray-100 text-gray-800"
                        }`}>{typeLabel}</span>
                      </td>
                      <td className="font-mono text-sm">{l.number || "—"}</td>
                      <td className="text-sm text-gray-700">{l.description}</td>
                      <td className="font-mono text-left" dir="ltr">{l.debit > 0 ? formatNumber(l.debit) : "—"}</td>
                      <td className="font-mono text-left" dir="ltr">{l.credit > 0 ? formatNumber(l.credit) : "—"}</td>
                      <td className={`font-mono text-left font-semibold ${l.runningBalance > 0 ? "text-red-600" : l.runningBalance < 0 ? "text-green-600" : ""}`} dir="ltr">
                        {formatNumber(l.runningBalance)}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
              <tfoot>
                <tr className="border-t-2 font-bold bg-gray-50">
                  <td colSpan={4} className="py-2">الإجماليات</td>
                  <td className="font-mono text-left py-2" dir="ltr">{formatNumber(totalDebit)}</td>
                  <td className="font-mono text-left py-2" dir="ltr">{formatNumber(totalCredit)}</td>
                  <td className="font-mono text-left py-2 text-primary-700" dir="ltr">{formatNumber(data.closingBalance)}</td>
                </tr>
                <tr className="font-bold">
                  <td colSpan={6} className="py-2">رصيد ختامي (في {formatDate(to)})</td>
                  <td className={`font-mono text-left py-2 ${data.closingBalance > 0 ? "text-red-700" : "text-green-700"}`} dir="ltr">
                    {formatNumber(data.closingBalance)}
                  </td>
                </tr>
              </tfoot>
            </table>
          </div>
        )}
      </div>

      {/* Print-friendly CSS — hide nav and chrome */}
      <style jsx global>{`
        @media print {
          aside, header, .no-print { display: none !important; }
          main { padding: 0 !important; margin: 0 !important; }
          .card { border: none !important; box-shadow: none !important; }
        }
      `}</style>
    </div>
  );
}

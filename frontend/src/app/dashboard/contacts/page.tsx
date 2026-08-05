"use client";

import { Suspense, useEffect, useMemo, useState } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import { api, getErrorMessage } from "@/lib/api";
import { Users, Plus, Loader2, X, Edit, Eye, Phone, Mail, FileText, Search } from "lucide-react";
import { formatNumber } from "@/lib/utils";
import type { ContactBalance } from "@/lib/types";

interface Contact {
  id: string;
  companyId: string;
  type: "customer" | "supplier";
  code: string;
  name: string;
  nameAr?: string;
  taxId?: string;
  phone?: string;
  email?: string;
  isActive: boolean;
}

const TYPE_LABELS: Record<string, string> = {
  customer: "عميل",
  supplier: "مورّد"
};

const TYPE_BADGE: Record<string, string> = {
  customer: "badge-info",
  supplier: "badge-warning"
};

/**
 * Contacts catalogue.
 *
 * Sprint 25 changes:
 *   - Each row is clickable → opens the contact detail page
 *     (`/dashboard/contacts/{id}`) which has 3 tabs (Invoices,
 *     Vouchers, Statement).
 *   - A new "الرصيد" (balance) column shows the contact's current
 *     outstanding balance. Loaded in bulk from
 *     `GET /api/contacts/{id}/balance` per row to keep this page
 *     honest about what the backend actually has.
 *   - A new "لديهم رصيد" filter narrows the list to contacts whose
 *     outstanding > 0. The URL `?filter=with-balance` is honoured so
 *     the sidebar "كشف حساب" link can deep-link here.
 *   - The form now also asks for a phone number and email (they were
 *     already in the DB but the old UI didn't surface them).
 */
export default function ContactsPage() {
  // useSearchParams forces a Suspense boundary in Next 15, so the
  // page logic lives in `ContactsPageInner` and the default export
  // wraps it. The fallback shows a spinner during the static-render
  // pass before hydration.
  return (
    <Suspense fallback={
      <div className="flex justify-center py-12">
        <Loader2 className="animate-spin text-primary-500" size={32} />
      </div>
    }>
      <ContactsPageInner />
    </Suspense>
  );
}

function ContactsPageInner() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const { activeCompany } = useAuth();
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [balances, setBalances] = useState<Record<string, ContactBalance>>({});
  const [loading, setLoading] = useState(true);
  const [balanceLoading, setBalanceLoading] = useState(false);
  const [showForm, setShowForm] = useState(false);
  const [editing, setEditing] = useState<Contact | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState("");

  // Type filter: all / customer / supplier
  const initialType = (searchParams.get("type") as "customer" | "supplier" | "all") || "all";
  const [typeFilter, setTypeFilter] = useState<"all" | "customer" | "supplier">(initialType);

  // "لديهم رصيد" (have balance) filter — honoured from URL ?filter=with-balance
  const [onlyWithBalance, setOnlyWithBalance] = useState(searchParams.get("filter") === "with-balance");

  const [form, setForm] = useState({
    type: "customer" as "customer" | "supplier",
    code: "",
    name: "",
    nameAr: "",
    taxId: "",
    phone: "",
    email: ""
  });

  const load = async () => {
    if (!activeCompany) return;
    setLoading(true);
    setBalanceLoading(true);
    try {
      // Always include inactive=false (we surface only active contacts here).
      const res = await api.get(`/contacts?companyId=${activeCompany.id}&includeInactive=false`);
      setContacts(res.data);
      // Fetch balances in parallel — the endpoint is per-id, so we fan out.
      // Wrap in allSettled so a single 404 doesn't blank the whole page.
      const balResults = await Promise.allSettled(
        res.data.map((c: Contact) =>
          api.get(`/contacts/${c.id}/balance`).then((r) => [c.id, r.data as ContactBalance] as const)
        )
      );
      const map: Record<string, ContactBalance> = {};
      balResults.forEach((r) => {
        if (r.status === "fulfilled") {
          const [id, bal] = r.value;
          map[id] = bal;
        }
      });
      setBalances(map);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
      setBalanceLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  const resetForm = () => {
    setForm({ type: "customer", code: "", name: "", nameAr: "", taxId: "", phone: "", email: "" });
    setEditing(null);
  };

  const openCreate = () => {
    resetForm();
    setShowForm(true);
  };

  const openEdit = (c: Contact) => {
    setEditing(c);
    setForm({
      type: c.type,
      code: c.code,
      name: c.name,
      nameAr: c.nameAr || "",
      taxId: c.taxId || "",
      phone: c.phone || "",
      email: c.email || ""
    });
    setShowForm(true);
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!activeCompany) return;
    setSubmitting(true);
    setError(null);
    try {
      if (editing) {
        await api.put(`/contacts/${editing.id}`, {
          name: form.name,
          nameAr: form.nameAr || null,
          taxId: form.taxId || null,
          phone: form.phone || null,
          email: form.email || null
        });
      } else {
        await api.post("/contacts", {
          companyId: activeCompany.id,
          type: form.type,
          code: form.code,
          name: form.name,
          nameAr: form.nameAr || null,
          taxId: form.taxId || null,
          phone: form.phone || null,
          email: form.email || null
        });
      }
      setShowForm(false);
      resetForm();
      await load();
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setSubmitting(false);
    }
  };

  const filtered = useMemo(() => {
    let list = contacts;
    if (typeFilter !== "all") list = list.filter((c) => c.type === typeFilter);
    if (search.trim()) {
      const q = search.trim().toLowerCase();
      list = list.filter((c) =>
        c.code.toLowerCase().includes(q) ||
        c.name.toLowerCase().includes(q) ||
        (c.nameAr || "").toLowerCase().includes(q) ||
        (c.taxId || "").toLowerCase().includes(q)
      );
    }
    if (onlyWithBalance) {
      list = list.filter((c) => (balances[c.id]?.balance ?? 0) > 0);
    }
    return list;
  }, [contacts, typeFilter, search, onlyWithBalance, balances]);

  // For a customer: positive balance = they owe us (red). For a supplier:
  // positive balance = we owe them (also red — money going out). The sign
  // is the same in both cases, the *colour* is what the user expects: red
  // = "money I need to track/collect/pay", green = settled or in our favour.
  const balanceColor = (c: Contact, bal?: ContactBalance) => {
    if (!bal || Math.abs(bal.balance) < 0.01) return "text-gray-500";
    return "text-red-600 font-bold";
  };

  return (
    <div>
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Users size={24} className="text-primary-600" />
            العملاء والموردون
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            كتالوج العملاء والموردين — اضغط على أي صف لعرض كشف الحساب
          </p>
        </div>
        <button onClick={openCreate} className="btn-primary">
          <Plus size={18} />
          جهة جديدة
        </button>
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

      {/* Filters row */}
      <div className="card mb-4">
        <div className="flex flex-wrap items-center gap-3">
          <div className="flex gap-2">
            {[
              { v: "all", l: "الكل" },
              { v: "customer", l: "العملاء" },
              { v: "supplier", l: "الموردون" }
            ].map((t) => (
              <button
                key={t.v}
                onClick={() => setTypeFilter(t.v as any)}
                className={`px-4 py-2 rounded-md text-sm font-medium ${
                  typeFilter === t.v
                    ? "bg-primary-600 text-white"
                    : "bg-white text-gray-700 border border-gray-300 hover:bg-gray-50"
                }`}
              >
                {t.l}
              </button>
            ))}
          </div>

          <label className="flex items-center gap-2 text-sm text-gray-700 cursor-pointer select-none">
            <input
              type="checkbox"
              checked={onlyWithBalance}
              onChange={(e) => setOnlyWithBalance(e.target.checked)}
              className="rounded border-gray-300 text-primary-600 focus:ring-primary-500"
            />
            لديهم رصيد
          </label>

          <div className="flex-1 min-w-[200px]">
            <div className="relative">
              <Search size={16} className="absolute right-3 top-1/2 -translate-y-1/2 text-gray-400 pointer-events-none" />
              <input
                className="input pr-9"
                placeholder="بحث بالاسم أو الكود أو الرقم الضريبي..."
                value={search}
                onChange={(e) => setSearch(e.target.value)}
              />
            </div>
          </div>
        </div>
      </div>

      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : filtered.length === 0 ? (
          <div className="text-center py-12 text-gray-500">
            <Users size={48} className="mx-auto mb-3 text-gray-300" />
            <p>{onlyWithBalance ? "لا توجد جهات عليها رصيد حالياً" : "لا توجد جهات مسجلة"}</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="table">
              <thead>
                <tr>
                  <th>الكود</th>
                  <th>الاسم</th>
                  <th>النوع</th>
                  <th>الرقم الضريبي</th>
                  <th>الهاتف</th>
                  <th className="text-left">الرصيد</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((c) => {
                  const bal = balances[c.id];
                  return (
                    <tr
                      key={c.id}
                      className="hover:bg-primary-50/50 cursor-pointer"
                      onClick={() => router.push(`/dashboard/contacts/${c.id}`)}
                    >
                      <td className="font-mono font-semibold">{c.code}</td>
                      <td>
                        <div className="font-medium">{c.nameAr || c.name}</div>
                        {c.nameAr && c.name !== c.nameAr && (
                          <div className="text-xs text-gray-500" dir="ltr">{c.name}</div>
                        )}
                      </td>
                      <td>
                        <span className={`badge ${TYPE_BADGE[c.type]}`}>{TYPE_LABELS[c.type]}</span>
                      </td>
                      <td className="text-sm text-gray-600 font-mono" dir="ltr">{c.taxId || "—"}</td>
                      <td className="text-sm text-gray-600" dir="ltr">{c.phone || "—"}</td>
                      <td className={`font-mono text-left ${balanceColor(c, bal)}`} dir="ltr">
                        {balanceLoading && !bal ? (
                          <span className="inline-block w-12 h-4 bg-gray-100 animate-pulse rounded" />
                        ) : bal ? (
                          formatNumber(bal.balance)
                        ) : (
                          "—"
                        )}
                      </td>
                      <td onClick={(e) => e.stopPropagation()}>
                        <div className="flex items-center gap-1">
                          <button
                            onClick={() => router.push(`/dashboard/contacts/${c.id}`)}
                            className="text-primary-600 hover:bg-primary-50 p-1 rounded"
                            title="كشف حساب"
                          >
                            <Eye size={14} />
                          </button>
                          <button
                            onClick={() => openEdit(c)}
                            className="text-gray-600 hover:bg-gray-100 p-1 rounded"
                            title="تعديل"
                          >
                            <Edit size={14} />
                          </button>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {showForm && (
        <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl w-full max-w-lg p-6">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-lg font-semibold flex items-center gap-2">
                <FileText size={20} className="text-primary-600" />
                {editing ? "تعديل بيانات الجهة" : "جهة جديدة"}
              </h2>
              <button onClick={() => { setShowForm(false); resetForm(); }} className="text-gray-400 hover:text-gray-600">
                <X size={20} />
              </button>
            </div>
            <form onSubmit={submit} className="space-y-3">
              {!editing && (
                <div className="grid grid-cols-2 gap-3">
                  <div>
                    <label className="block text-sm font-medium mb-1">النوع *</label>
                    <select
                      className="input"
                      value={form.type}
                      onChange={(e) => setForm({ ...form, type: e.target.value as any })}
                    >
                      <option value="customer">عميل</option>
                      <option value="supplier">مورّد</option>
                    </select>
                  </div>
                  <div>
                    <label className="block text-sm font-medium mb-1">الكود *</label>
                    <input
                      className="input"
                      value={form.code}
                      onChange={(e) => setForm({ ...form, code: e.target.value })}
                      required
                      dir="ltr"
                      placeholder="C001 / S001"
                    />
                  </div>
                </div>
              )}
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1">الاسم (عربي) *</label>
                  <input
                    className="input"
                    value={form.nameAr}
                    onChange={(e) => setForm({ ...form, nameAr: e.target.value })}
                    placeholder="مثال: شركة الأمل التجارية"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1">الاسم (English) *</label>
                  <input
                    className="input"
                    value={form.name}
                    onChange={(e) => setForm({ ...form, name: e.target.value })}
                    required
                    dir="ltr"
                    placeholder="e.g., Al-Amal Trading Co."
                  />
                </div>
              </div>
              <div>
                <label className="block text-sm font-medium mb-1">الرقم الضريبي</label>
                <input
                  className="input"
                  value={form.taxId}
                  onChange={(e) => setForm({ ...form, taxId: e.target.value })}
                  dir="ltr"
                  placeholder="اختياري"
                />
              </div>
              <div className="grid grid-cols-2 gap-3">
                <div>
                  <label className="block text-sm font-medium mb-1 flex items-center gap-1">
                    <Phone size={12} /> الهاتف
                  </label>
                  <input
                    className="input"
                    value={form.phone}
                    onChange={(e) => setForm({ ...form, phone: e.target.value })}
                    dir="ltr"
                  />
                </div>
                <div>
                  <label className="block text-sm font-medium mb-1 flex items-center gap-1">
                    <Mail size={12} /> البريد
                  </label>
                  <input
                    type="email"
                    className="input"
                    value={form.email}
                    onChange={(e) => setForm({ ...form, email: e.target.value })}
                    dir="ltr"
                  />
                </div>
              </div>

              {error && <div className="p-3 bg-red-50 text-red-700 rounded-md text-sm">{error}</div>}

              <div className="flex gap-2 pt-2">
                <button type="submit" disabled={submitting} className="btn-primary flex-1">
                  {submitting ? "جاري الحفظ..." : (editing ? "حفظ التعديلات" : "إنشاء")}
                </button>
                <button type="button" onClick={() => { setShowForm(false); resetForm(); }} className="btn-secondary">
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

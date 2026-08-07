"use client";

import { useEffect, useMemo, useState } from "react";
import Link from "next/link";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { FolderKanban, Plus, Loader2, X, CheckCircle, Search } from "lucide-react";
import { formatNumber, formatDate } from "@/lib/utils";
import ProjectTypeBadge from "./components/ProjectTypeBadge";
import StatusBadge from "./components/StatusBadge";

/**
 * Sprint 35 — Projects list.
 *
 * Now serves as the central hub for project tracking, with
 * filters (type, status, customer), search (code + name), and
 * click-through to the detail page.
 *
 * Layout:
 *   - Desktop: table with 6 columns + actions
 *   - Mobile: card list (one project per card, stacked)
 *
 * Why both? The supervisor uses a phone; the accountant uses
 * a laptop. We render both with the same data but different
 * markup. The TAILWIND pattern `hidden md:block` / `md:hidden`
 * keeps the desktop / mobile code paths clearly separate.
 */

interface Milestone {
  id: string;
  name: string;
  nameAr?: string;
  description?: string;
  amount: number;
  status: "pending" | "completed";
  targetDate?: string;
  completedAt?: string;
  orderIndex: number;
}

/**
 * Sprint 35 — Project DTO has been extended with new fields.
 * Original Sprint 11 only had status (active/completed/on_hold/cancelled)
 * + budget + actualCost. The migration adds type, customerId, contract,
 * manager, location, expectedEndDate, etc.
 *
 * We mark the new fields optional so the same frontend still works
 * against an older backend (graceful degradation).
 */
interface Project {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  description?: string;
  /** Sprint 11 statuses: active/completed/on_hold/cancelled. */
  /** Sprint 35 statuses: draft/active/on_hold/completed/closed. */
  status: string;
  type?: string | null;
  customerId?: string | null;
  customerName?: string | null;
  contractValue?: number;
  startDate?: string;
  endDate?: string;
  expectedEndDate?: string;
  budget: number;
  actualCost: number;
  projectManager?: string | null;
  location?: string | null;
  notes?: string;
  milestones: Milestone[];
  createdAt?: string;
}

interface Customer {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
}

const TYPE_OPTIONS = [
  { value: "",            label: "كل الأنواع" },
  { value: "construction",label: "مقاولات" },
  { value: "supply",      label: "توريد" },
  { value: "service",     label: "خدمات" },
  { value: "maintenance", label: "صيانة" },
];

const STATUS_OPTIONS = [
  { value: "",          label: "كل الحالات" },
  { value: "draft",     label: "مسودة" },
  { value: "active",    label: "نشط" },
  { value: "on_hold",   label: "متوقف" },
  { value: "completed", label: "مكتمل" },
  { value: "closed",    label: "مغلق" },
];

export default function ProjectsPage() {
  const { activeCompany } = useAuth();
  const [projects, setProjects] = useState<Project[]>([]);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);

  // Filters
  const [typeFilter, setTypeFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  const [customerFilter, setCustomerFilter] = useState("");
  const [search, setSearch] = useState("");

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      setError(null);
      const [projectsRes, customersRes] = await Promise.all([
        api.get(`/projects?companyId=${activeCompany.id}&limit=200`),
        // For the customer filter dropdown. Don't fail the whole
        // page if contacts can't load — just leave the filter empty.
        api.get(`/contacts?companyId=${activeCompany.id}&type=customer&limit=200`).catch(() => ({ data: [] })),
      ]);
      setProjects(projectsRes.data || []);
      setCustomers(customersRes.data || []);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [activeCompany]);

  // Apply filters in-memory. The backend already does a
  // company-scoped filter, so the dataset is small enough to
  // filter client-side. Saves an API roundtrip on every
  // dropdown change.
  const filtered = useMemo(() => {
    let out = projects;
    if (typeFilter) out = out.filter((p) => p.type === typeFilter);
    if (statusFilter) out = out.filter((p) => p.status === statusFilter);
    if (customerFilter) out = out.filter((p) => p.customerId === customerFilter);
    if (search.trim()) {
      const q = search.trim().toLowerCase();
      out = out.filter(
        (p) =>
          p.code.toLowerCase().includes(q) ||
          p.name.toLowerCase().includes(q) ||
          (p.nameAr || "").toLowerCase().includes(q)
      );
    }
    return out;
  }, [projects, typeFilter, statusFilter, customerFilter, search]);

  return (
    <div>
      <div className="flex items-center justify-between mb-4 gap-2">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <FolderKanban size={24} className="text-primary-600" />
            المشاريع
          </h1>
          <p className="text-sm text-gray-600 mt-1">إدارة المشاريع، التكاليف، والربحية</p>
        </div>
        <Link href="/dashboard/projects/new" className="btn-primary">
          <Plus size={18} />
          مشروع جديد
        </Link>
      </div>

      {/* Filters bar */}
      <div className="card mb-4">
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-3">
          {/* Search */}
          <div className="lg:col-span-1">
            <label className="block text-xs font-medium text-gray-600 mb-1">بحث</label>
            <div className="flex items-center gap-1 px-2 py-1.5 border border-gray-300 rounded-md bg-white">
              <Search size={14} className="text-gray-400" />
              <input
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="كود أو اسم..."
                className="flex-1 text-sm outline-none bg-transparent"
              />
              {search && (
                <button type="button" onClick={() => setSearch("")} className="text-gray-400 hover:text-red-600">
                  <X size={14} />
                </button>
              )}
            </div>
          </div>
          {/* Type */}
          <div>
            <label className="block text-xs font-medium text-gray-600 mb-1">النوع</label>
            <select
              value={typeFilter}
              onChange={(e) => setTypeFilter(e.target.value)}
              className="input"
            >
              {TYPE_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          </div>
          {/* Status */}
          <div>
            <label className="block text-xs font-medium text-gray-600 mb-1">الحالة</label>
            <select
              value={statusFilter}
              onChange={(e) => setStatusFilter(e.target.value)}
              className="input"
            >
              {STATUS_OPTIONS.map((o) => (
                <option key={o.value} value={o.value}>{o.label}</option>
              ))}
            </select>
          </div>
          {/* Customer */}
          <div>
            <label className="block text-xs font-medium text-gray-600 mb-1">العميل</label>
            <select
              value={customerFilter}
              onChange={(e) => setCustomerFilter(e.target.value)}
              className="input"
              disabled={customers.length === 0}
            >
              <option value="">كل العملاء</option>
              {customers.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.code} — {c.nameAr || c.name}
                </option>
              ))}
            </select>
          </div>
        </div>
        {(typeFilter || statusFilter || customerFilter || search) && (
          <div className="mt-3 text-xs text-gray-500 flex items-center gap-2">
            <span>النتائج: {filtered.length} من {projects.length}</span>
            <button
              type="button"
              onClick={() => { setTypeFilter(""); setStatusFilter(""); setCustomerFilter(""); setSearch(""); }}
              className="text-primary-600 hover:underline"
            >
              مسح الفلاتر
            </button>
          </div>
        )}
      </div>

      {error && <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm border border-red-200">{error}</div>}

      {loading ? (
        <div className="flex justify-center py-8">
          <Loader2 className="animate-spin text-primary-500" size={32} />
        </div>
      ) : filtered.length === 0 ? (
        <div className="text-center text-gray-500 py-8 card">
          {projects.length === 0
            ? "لا توجد مشاريع. أنشئ مشروعك الأول للبدء."
            : "لا توجد نتائج تطابق الفلاتر"}
        </div>
      ) : (
        <>
          {/* Desktop table */}
          <div className="hidden md:block card overflow-x-auto p-0">
            <table className="w-full text-sm">
              <thead>
                <tr className="bg-gray-50 border-b border-gray-200">
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">الكود</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">الاسم</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">النوع</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">الحالة</th>
                  <th className="text-right py-2 px-3 font-semibold text-gray-600">العميل</th>
                  <th className="text-left py-2 px-3 font-semibold text-gray-600">قيمة العقد</th>
                  <th className="text-left py-2 px-3 font-semibold text-gray-600">التاريخ</th>
                </tr>
              </thead>
              <tbody>
                {filtered.map((p) => (
                  <tr
                    key={p.id}
                    className="border-b border-gray-100 hover:bg-gray-50 cursor-pointer"
                    onClick={() => (window.location.href = `/dashboard/projects/${p.id}`)}
                  >
                    <td className="py-2 px-3 font-mono text-xs">{p.code}</td>
                    <td className="py-2 px-3 font-medium">{p.nameAr || p.name}</td>
                    <td className="py-2 px-3"><ProjectTypeBadge type={p.type} /></td>
                    <td className="py-2 px-3"><StatusBadge status={p.status} /></td>
                    <td className="py-2 px-3 text-gray-600">{p.customerName || "—"}</td>
                    <td className="py-2 px-3 text-left font-mono" dir="ltr">
                      {formatNumber(p.contractValue ?? p.budget)}
                    </td>
                    <td className="py-2 px-3 text-gray-500 text-xs">
                      {formatDate(p.startDate)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {/* Mobile cards */}
          <div className="md:hidden space-y-3">
            {filtered.map((p) => (
              <Link
                key={p.id}
                href={`/dashboard/projects/${p.id}`}
                className="block card hover:border-primary-300 transition-colors"
              >
                <div className="flex items-start justify-between gap-2 mb-2">
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center gap-2 mb-1">
                      <span className="font-mono text-xs text-gray-500">{p.code}</span>
                    </div>
                    <div className="font-semibold truncate">{p.nameAr || p.name}</div>
                  </div>
                  <div className="flex flex-col items-end gap-1 shrink-0">
                    <ProjectTypeBadge type={p.type} />
                    <StatusBadge status={p.status} />
                  </div>
                </div>
                <div className="grid grid-cols-2 gap-2 text-xs text-gray-600 mt-2">
                  <div>
                    <span className="text-gray-500">العميل:</span> {p.customerName || "—"}
                  </div>
                  <div className="text-left" dir="ltr">
                    <span className="text-gray-500">القيمة:</span>{" "}
                    <span className="font-mono font-semibold">{formatNumber(p.contractValue ?? p.budget)}</span>
                  </div>
                </div>
              </Link>
            ))}
          </div>
        </>
      )}
    </div>
  );
}

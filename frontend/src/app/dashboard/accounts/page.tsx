"use client";

import { useEffect, useMemo, useState } from "react";
import { api, getErrorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth-context";
import { Plus, Loader2, Search, Filter, Wallet, X } from "lucide-react";
import type { Account } from "@/lib/types";
import AccountTree from "./components/AccountTree";
import CreateAccountModal from "./components/CreateAccountModal";

/**
 * Chart of Accounts — Sprint 26 rewrite.
 *
 * Before Sprint 26, this page was a flat table. The user
 * asked for a recursive tree view (4 levels) with per-row
 * actions (expand/collapse, add child) and a "smart" create
 * modal that auto-derives level + code from the parent.
 *
 * The new surface lives in two sub-components:
 *   - `<AccountTree>` — pure render of the nested tree.
 *   - `<CreateAccountModal>` — auto-calc level, suggest code,
 *     level-aware isPostable toggle.
 *
 * The page itself just:
 *   1. Fetches the flat list from `GET /api/accounts?companyId=...`.
 *   2. Filters by search query + active-only flag.
 *   3. Hands the filtered list to the tree.
 *   4. Opens the create modal on `+ الحساب` or on `+` from a row.
 */
export default function AccountsPage() {
  const { activeCompany } = useAuth();
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Modal state
  const [showCreate, setShowCreate] = useState(false);
  const [createParent, setCreateParent] = useState<Account | null>(null);

  // Filters
  const [search, setSearch] = useState("");
  const [hideInactive, setHideInactive] = useState(false);

  const load = async () => {
    if (!activeCompany) return;
    try {
      setLoading(true);
      const res = await api.get(`/accounts?companyId=${activeCompany.id}`);
      // Same normalise trick the other pages use: backend may wrap or not.
      const list: Account[] = Array.isArray(res.data)
        ? res.data
        : (res.data?.data || []);
      setAccounts(list);
    } catch (err) {
      setError(getErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, [activeCompany]);

  // Apply search + active filters. The tree itself does the
  // structural grouping; we just shrink the input list.
  const filtered = useMemo(() => {
    let out = accounts;
    if (hideInactive) {
      out = out.filter((a) => a.isActive);
    }
    const q = search.trim().toLowerCase();
    if (q) {
      out = out.filter(
        (a) =>
          a.code.toLowerCase().includes(q) ||
          a.name.toLowerCase().includes(q) ||
          (a.nameAr || "").toLowerCase().includes(q)
      );
    }
    return out;
  }, [accounts, search, hideInactive]);

  // Counts for the header summary
  const counts = useMemo(() => {
    const total = accounts.length;
    const inactive = accounts.filter((a) => !a.isActive).length;
    const postable = accounts.filter((a) => a.isPostable).length;
    return { total, inactive, postable };
  }, [accounts]);

  const openCreateForParent = (parent: Account | null) => {
    setCreateParent(parent);
    setShowCreate(true);
  };

  return (
    <div>
      {/* Header */}
      <div className="flex items-center justify-between mb-6">
        <div>
          <h1 className="text-2xl font-bold text-gray-900 flex items-center gap-2">
            <Wallet size={24} className="text-primary-600" />
            شجرة الحسابات
          </h1>
          <p className="text-sm text-gray-600 mt-1">
            الحسابات المحاسبية للشركة الحالية — {activeCompany?.nameAr || activeCompany?.name}
            {counts.total > 0 && (
              <span className="text-gray-400 mr-2">
                ({counts.total} حساب، {counts.postable} قابل للترحيل)
              </span>
            )}
          </p>
        </div>
        <button
          onClick={() => openCreateForParent(null)}
          className="btn-primary"
        >
          <Plus size={18} />
          حساب جديد
        </button>
      </div>

      {error && (
        <div className="mb-4 p-3 bg-red-50 text-red-700 rounded-md text-sm">
          {error}
        </div>
      )}

      {/* Filter bar */}
      <div className="card mb-4">
        <div className="flex flex-wrap items-center gap-3">
          {/* Search */}
          <div className="relative flex-1 min-w-[240px]">
            <Search
              size={16}
              className="absolute top-1/2 -translate-y-1/2 text-gray-400"
              style={{ insetInlineStart: "0.75rem" }}
            />
            <input
              className="input"
              style={{ paddingInlineStart: "2.25rem" }}
              placeholder="ابحث بالكود أو الاسم..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
            {search && (
              <button
                onClick={() => setSearch("")}
                className="absolute top-1/2 -translate-y-1/2 text-gray-400 hover:text-gray-600"
                style={{ insetInlineEnd: "0.5rem" }}
                title="مسح البحث"
              >
                <X size={14} />
              </button>
            )}
          </div>

          {/* Hide inactive toggle */}
          <label className="flex items-center gap-2 text-sm text-gray-700 cursor-pointer select-none">
            <input
              type="checkbox"
              checked={hideInactive}
              onChange={(e) => setHideInactive(e.target.checked)}
              className="rounded"
            />
            <Filter size={14} className="text-gray-500" />
            إخفاء الحسابات غير المفعلة
          </label>

          {counts.inactive > 0 && !hideInactive && (
            <span className="text-xs text-amber-700 bg-amber-50 px-2 py-1 rounded">
              {counts.inactive} حساب معطل
            </span>
          )}
        </div>
      </div>

      {/* Tree */}
      <div className="card">
        {loading ? (
          <div className="flex justify-center py-8">
            <Loader2 className="animate-spin text-primary-500" size={32} />
          </div>
        ) : (
          <AccountTree
            accounts={filtered}
            onAddChild={(parent) => openCreateForParent(parent)}
          />
        )}
      </div>

      {/* Legend */}
      <div className="mt-4 p-3 bg-gray-50 text-gray-700 rounded-md text-xs flex flex-wrap items-center gap-x-4 gap-y-1">
        <span className="font-semibold text-gray-900">دلالات:</span>
        <span>
          <span className="inline-block w-3 h-3 rounded-full bg-green-500 align-middle" />
          {" "}قابل للترحيل
        </span>
        <span>
          <span className="inline-block w-3 h-3 rounded-full bg-gray-400 align-middle" />
          {" "}تجميعي (لا يقبل ترحيل)
        </span>
        <span className="text-gray-400">|</span>
        <span>L1=نوع، L2=فئة، L3=تشغيلي، L4=تفصيلي</span>
        <span className="text-gray-400">|</span>
        <span>اضغط <kbd className="px-1 bg-white border border-gray-300 rounded">+</kbd> لإضافة حساب فرعي</span>
      </div>

      {/* Create modal */}
      {activeCompany && (
        <CreateAccountModal
          open={showCreate}
          onClose={() => setShowCreate(false)}
          onCreated={async () => {
            await load();
          }}
          parent={createParent}
          accounts={accounts}
          companyId={activeCompany.id}
        />
      )}
    </div>
  );
}

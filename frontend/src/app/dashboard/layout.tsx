"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import {
  LayoutDashboard, Building2, BookOpen, FileText, Zap, BarChart3, LogOut, ChevronDown, User, FolderKanban, Users, Package, Inbox, ChevronLeft, FileSpreadsheet, ScrollText, TrendingUp, Scale, Wallet, ArrowRightLeft
} from "lucide-react";

export default function DashboardLayout({ children }: { children: React.ReactNode }) {
  const { user, companies, activeCompany, loading, logout, switchCompany } = useAuth();
  const router = useRouter();
  const pathname = usePathname();
  const [showCompanyMenu, setShowCompanyMenu] = useState(false);
  const [showUserMenu, setShowUserMenu] = useState(false);

  // The sidebar is now organized as GROUPS, not a flat list.
  // Flat lists worked when we had 5-6 pages. At 14+ pages the
  // sidebar is overwhelming and the 4 financial reports
  // (trial balance, general ledger, income statement, balance
  // sheet) all show in a row — easy to confuse.
  //
  // Groups (Sprint 19):
  //   1. الرئيسية    — لوحة التحكم
  //   2. الأساسيات    — الشركات / الحسابات / المنتجات / المشاريع
  //   3. العمليات     — الفواتير / القيود / المعلقة
  //   4. التقارير     — 4 تقارير في collapsible group
  //   5. الإدارة      — المستخدمون / القواعد
  //
  // The "التقارير" group is collapsed by default but auto-opens
  // when the user is on a report page, so they always see the
  // group context for where they are.
  type NavItem = {
    href: string;
    label: string;
    icon: any;
    perm?: string;
    exact?: boolean;
  };
  type NavGroup = {
    label: string;
    icon: any;
    items: NavItem[];
    collapsible?: boolean;
  };

  const navGroups: NavGroup[] = [
    {
      label: "الرئيسية",
      icon: LayoutDashboard,
      collapsible: false,
      items: [
        { href: "/dashboard", label: "لوحة التحكم", icon: LayoutDashboard, exact: true }
      ]
    },
    {
      label: "الأساسيات",
      icon: Building2,
      collapsible: false,
      items: [
        { href: "/dashboard/companies", label: "الشركات", icon: Building2 },
        { href: "/dashboard/accounts", label: "شجرة الحسابات", icon: Wallet },
        { href: "/dashboard/products", label: "المنتجات", icon: Package },
        { href: "/dashboard/projects", label: "المشاريع", icon: FolderKanban },
        { href: "/dashboard/cost-centers", label: "مراكز التكلفة", icon: Scale }
      ]
    },
    {
      label: "العمليات",
      icon: FileText,
      collapsible: false,
      items: [
        { href: "/dashboard/invoices", label: "الفواتير", icon: FileText },
        { href: "/dashboard/receipts", label: "سندات القبض", icon: Inbox },
        { href: "/dashboard/payments", label: "سندات الصرف", icon: FileText },
        { href: "/dashboard/journal", label: "القيود اليومية", icon: ScrollText },
        { href: "/dashboard/journal/pending", label: "القيود المعلقة", icon: Inbox },
        { href: "/dashboard/intercompany", label: "المعاملات بين الشركات", icon: ArrowRightLeft }
      ]
    },
    {
      label: "التقارير المالية",
      icon: BarChart3,
      collapsible: true,
      items: [
        { href: "/dashboard/reports/trial-balance", label: "ميزان المراجعة", icon: Scale },
        { href: "/dashboard/reports/general-ledger", label: "دفتر الأستاذ", icon: BookOpen },
        { href: "/dashboard/reports/customer-aging", label: "أعمار المدينين", icon: Users },
        { href: "/dashboard/reports/supplier-aging", label: "أعمار الدائنين", icon: Users },
        { href: "/dashboard/reports/income-statement", label: "قائمة الدخل", icon: TrendingUp },
        { href: "/dashboard/reports/balance-sheet", label: "الميزانية العمومية", icon: FileSpreadsheet },
        { href: "/dashboard/reports/intercompany-elimination", label: "استبعاد المعاملات البينية", icon: FileSpreadsheet }
      ]
    },
    {
      label: "الإدارة",
      icon: Users,
      collapsible: false,
      items: [
        { href: "/dashboard/users", label: "المستخدمون", icon: Users },
        { href: "/dashboard/rules", label: "قواعد العمل", icon: Zap }
      ]
    }
  ];

  // Track which collapsible groups are open. The التقارير
  // group opens by default if we're already on a report page.
  const [openGroups, setOpenGroups] = useState<Set<string>>(() => {
    const initial = new Set<string>();
    if (pathname.startsWith("/dashboard/reports")) {
      initial.add("التقارير المالية");
    }
    return initial;
  });

  useEffect(() => {
    if (!loading && !user) router.push("/auth/login");
  }, [user, loading, router]);

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center">
        <div className="text-center">
          <div className="inline-block w-12 h-12 border-4 border-primary-500 border-t-transparent rounded-full animate-spin"></div>
          <p className="mt-3 text-gray-600">جاري التحميل...</p>
        </div>
      </div>
    );
  }

  if (!user) return null;

  const toggleGroup = (label: string) => {
    setOpenGroups((prev) => {
      const next = new Set(prev);
      if (next.has(label)) next.delete(label);
      else next.add(label);
      return next;
    });
  };

  // Is a single nav item the "current" page? Honors the `exact` flag.
  const isItemActive = (item: NavItem) =>
    item.exact ? pathname === item.href : pathname.startsWith(item.href);

  // Is a group "highlighted"? True if any of its items is the
  // current page. Used to keep non-collapsible group headers
  // styled when a child is active.
  const isGroupActive = (group: NavGroup) =>
    group.items.some((it) => isItemActive(it));

  return (
    <div className="min-h-screen flex bg-gray-50">
      {/* Sidebar */}
      <aside className="w-64 bg-white border-l border-gray-200 fixed right-0 top-0 h-full overflow-y-auto">
        <div className="p-4 border-b border-gray-200">
          <Link href="/dashboard" className="flex items-center gap-2">
            <div className="w-9 h-9 bg-primary-600 text-white rounded-md flex items-center justify-center">
              <Building2 size={20} />
            </div>
            <div>
              <h1 className="text-base font-bold text-gray-900">ERP-V2</h1>
              <p className="text-xs text-gray-500">Multi-Company</p>
            </div>
          </Link>
        </div>

        <nav className="p-3 space-y-3">
          {navGroups.map((group) => {
            const GroupIcon = group.icon;
            const groupActive = isGroupActive(group);
            const isOpen = !group.collapsible || openGroups.has(group.label);

            return (
              <div key={group.label}>
                {/* Group header — collapsible or static label */}
                {group.collapsible ? (
                  <button
                    onClick={() => toggleGroup(group.label)}
                    className={`w-full flex items-center justify-between gap-2 px-2 py-1.5 rounded-md text-xs font-semibold uppercase tracking-wider transition-colors ${
                      groupActive
                        ? "text-primary-700"
                        : "text-gray-500 hover:text-gray-700"
                    }`}
                    aria-expanded={isOpen}
                  >
                    <span className="flex items-center gap-2">
                      <GroupIcon size={14} />
                      {group.label}
                    </span>
                    <ChevronLeft
                      size={14}
                      className={`transition-transform ${isOpen ? "-rotate-90" : "rotate-0"}`}
                    />
                  </button>
                ) : (
                  <div
                    className={`px-2 py-1.5 text-xs font-semibold uppercase tracking-wider ${
                      groupActive ? "text-primary-700" : "text-gray-500"
                    }`}
                  >
                    <span className="flex items-center gap-2">
                      <GroupIcon size={14} />
                      {group.label}
                    </span>
                  </div>
                )}

                {/* Group items */}
                {isOpen && (
                  <div className="mt-1 space-y-1">
                    {group.items.map((item) => {
                      const Icon = item.icon;
                      const active = isItemActive(item);
                      return (
                        <Link
                          key={item.href}
                          href={item.href}
                          className={`flex items-center gap-3 px-3 py-2 rounded-md text-sm font-medium transition-colors ${
                            active
                              ? "bg-primary-50 text-primary-700"
                              : "text-gray-700 hover:bg-gray-100"
                          }`}
                        >
                          <Icon size={16} />
                          {item.label}
                        </Link>
                      );
                    })}
                  </div>
                )}
              </div>
            );
          })}
        </nav>
      </aside>

      {/* Main */}
      <div className="flex-1 mr-64">
        {/* Top bar */}
        <header className="bg-white border-b border-gray-200 px-6 py-3 flex items-center justify-between sticky top-0 z-10">
          {/* Company Switcher */}
          <div className="relative">
            <button
              onClick={() => setShowCompanyMenu(!showCompanyMenu)}
              className="flex items-center gap-2 px-3 py-2 rounded-md hover:bg-gray-100"
            >
              <Building2 size={16} className="text-gray-500" />
              <div className="text-right">
                <p className="text-sm font-semibold">{activeCompany?.nameAr || activeCompany?.name || "اختر شركة"}</p>
                <p className="text-xs text-gray-500">{activeCompany?.roleName}</p>
              </div>
              <ChevronDown size={14} className="text-gray-400" />
            </button>
            {showCompanyMenu && (
              <div className="absolute right-0 mt-1 w-72 bg-white border border-gray-200 rounded-md shadow-lg z-20">
                <div className="p-2">
                  <p className="text-xs text-gray-500 px-2 py-1">شركاتي</p>
                  {companies.map((c) => (
                    <button
                      key={c.id}
                      onClick={async () => {
                        setShowCompanyMenu(false);
                        if (c.id !== activeCompany?.id) await switchCompany(c.id);
                      }}
                      className={`w-full text-right px-3 py-2 rounded-md text-sm hover:bg-gray-100 ${
                        c.id === activeCompany?.id ? "bg-primary-50" : ""
                      }`}
                    >
                      <div className="font-medium">{c.nameAr || c.name}</div>
                      <div className="text-xs text-gray-500">{c.code} • {c.roleName}</div>
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>

          {/* User menu */}
          <div className="relative">
            <button
              onClick={() => setShowUserMenu(!showUserMenu)}
              className="flex items-center gap-2 px-3 py-2 rounded-md hover:bg-gray-100"
            >
              <div className="w-8 h-8 bg-primary-100 text-primary-700 rounded-full flex items-center justify-center">
                <User size={16} />
              </div>
              <div className="text-right">
                <p className="text-sm font-semibold">{user.fullNameAr || user.fullName || user.email}</p>
                <p className="text-xs text-gray-500">
                  {user.isSuperAdmin ? "مدير عام" : "مستخدم"}
                </p>
              </div>
              <ChevronDown size={14} className="text-gray-400" />
            </button>
            {showUserMenu && (
              <div className="absolute left-0 mt-1 w-48 bg-white border border-gray-200 rounded-md shadow-lg z-20">
                <div className="p-2">
                  <div className="px-2 py-1 text-xs text-gray-500">{user.email}</div>
                  <button
                    onClick={logout}
                    className="w-full text-right px-3 py-2 rounded-md text-sm text-red-600 hover:bg-red-50 flex items-center gap-2"
                  >
                    <LogOut size={14} />
                    تسجيل الخروج
                  </button>
                </div>
              </div>
            )}
          </div>
        </header>

        <main className="p-6">{children}</main>
      </div>
    </div>
  );
}

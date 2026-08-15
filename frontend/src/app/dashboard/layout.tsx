"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import {
  LayoutDashboard, Building2, BookOpen, FileText, Zap, BarChart3, LogOut, ChevronDown, User, FolderKanban, Users, Package, Inbox, ChevronLeft, FileSpreadsheet, ScrollText, TrendingUp, Scale, Wallet, ArrowRightLeft, CalendarRange, Wrench, Menu, X
} from "lucide-react";
import ThemeToggle from "@/components/ThemeToggle";

// Sprint 34 hotfix v4: removed the pre-warm call that was firing
// an extra GET to /api/health on every dashboard mount. The user
// reported Render free tier usage concerns — every extra request
// counts against the monthly limit. Clean error → manual refresh.
//
// Sprint 37: design system refresh — switched primary blue to
// brand teal, added dark mode classes throughout, and dropped
// the ThemeToggle into the topbar.

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
  // Groups (Sprint 19 + Sprint 25):
  //   1. الرئيسية    — لوحة التحكم
  //   2. الأساسيات    — الشركات / الحسابات / المنتجات / المشاريع
  //   3. العمليات     — الفواتير / سندات القبض والصرف / القيود
  //   4. الحسابات     — العملاء والموردون / كشف حساب / السنوات المالية
  //   5. التقارير     — 4 تقارير في collapsible group
  //   6. الإدارة      — المستخدمون / القواعد
  //
  // The "التقارير" group is collapsed by default but auto-opens
  // when the user is on a report page, so they always see the
  // group context for where they are.
  //
  // Sprint 25 added the "الحسابات" group as a home for the
  // new contact-detail / statement / fiscal-year pages. The
  // previous sidebar only had reports and admin — the customer
  // /supplier pages were nowhere to be found.
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
      // Sprint 25 — new "الحسابات" group. Holds the people-side pages
      // (contacts + their statements) and the period-locking surface.
      // Until now contacts were only inside the invoice dropdown, so
      // they had no first-class home. كشف حساب deep-links into the
      // contacts list pre-filtered to "with balance > 0".
      label: "الحسابات",
      icon: Users,
      collapsible: false,
      items: [
        { href: "/dashboard/contacts", label: "العملاء والموردون", icon: Users },
        { href: "/dashboard/contacts?filter=with-balance", label: "كشف حساب (لديهم رصيد)", icon: FileText },
        { href: "/dashboard/fiscal-years", label: "السنوات والفترات المالية", icon: CalendarRange }
      ]
    },
    {
      label: "التقارير المالية",
      icon: BarChart3,
      collapsible: true,
      items: [
        // Cycle 1: index page — entry point for the reports section.
        // Same group, so users see it right above the per-report links.
        { href: "/dashboard/reports", label: "فهرس التقارير", icon: BarChart3, exact: true },
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
        { href: "/dashboard/rules", label: "قواعد العمل", icon: Zap },
        // Sprint 26: super-admin tool surface (cleanup, seed, reset).
        // Stays in the "الإدارة" group so it doesn't pollute the
        // main nav for regular users.
        { href: "/dashboard/admin", label: "أدوات المدير", icon: Wrench }
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

  // Sprint 59 — mobile sidebar drawer state.
  // The sidebar is `fixed` on the right side of the viewport.
  // On desktop (lg+) it's always visible. On mobile, it would
  // cover the entire content. So we hide it by default on
  // mobile and show it as a drawer when the user taps the
  // hamburger button in the topbar.
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);

  // Auto-close the drawer on every route change. Without this,
  // the user clicks a link, the page changes, but the drawer
  // stays open and the user has to manually close it.
  useEffect(() => {
    setMobileMenuOpen(false);
  }, [pathname]);

  // Lock body scroll while the drawer is open. Without this,
  // background content scrolls when the user swipes inside
  // the drawer.
  useEffect(() => {
    if (mobileMenuOpen) {
      document.body.style.overflow = "hidden";
    } else {
      document.body.style.overflow = "";
    }
    return () => { document.body.style.overflow = ""; };
  }, [mobileMenuOpen]);

  useEffect(() => {
    if (!loading && !user) router.push("/auth/login");
  }, [user, loading, router]);

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-canvas">
        <div className="text-center">
          <div className="inline-block w-12 h-12 border-4 border-primary-700 border-t-transparent rounded-full animate-spin"></div>
          <p className="mt-3 text-ink-muted">جاري التحميل...</p>
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
    <div className="min-h-screen flex bg-raised dark:bg-neutral-950">
      {/* Sprint 59 — mobile drawer backdrop.
          Visible only when the mobile menu is open. Clicking it
          closes the drawer. Sits behind the sidebar (z-30) but
          above the main content (z-40 sidebar). */}
      {mobileMenuOpen && (
        <button
          type="button"
          aria-label="إغلاق القائمة"
          onClick={() => setMobileMenuOpen(false)}
          className="fixed inset-0 bg-black/50 z-30 lg:hidden"
        />
      )}

      {/* Sidebar — drawer on mobile, fixed column on desktop.
          - Mobile (default): hidden, slides in from the right
            when mobileMenuOpen is true (translate-x-0).
          - Desktop (lg+): always visible, sits in the normal
            flow next to the main content. */}
      <aside
        className={`w-64 bg-canvas dark:bg-neutral-950 border-l border-edge fixed right-0 top-0 h-full overflow-y-auto z-40 transition-transform duration-200 ease-in-out
          ${mobileMenuOpen ? "translate-x-0" : "translate-x-full"}
          lg:translate-x-0`}
      >
        <div className="p-4 border-b border-edge flex items-center justify-between">
          <Link href="/dashboard" className="flex items-center gap-2">
            <div className="w-9 h-9 bg-primary-700 text-white rounded-md flex items-center justify-center">
              <Building2 size={20} />
            </div>
            <div>
              <h1 className="text-canvas font-bold text-ink-strong">ERP-V2</h1>
              <p className="text-xs text-ink-subtle">Multi-Company</p>
            </div>
          </Link>
          {/* Close button — only visible on mobile (lg:hidden) */}
          <button
            type="button"
            aria-label="إغلاق"
            onClick={() => setMobileMenuOpen(false)}
            className="lg:hidden p-1 rounded-md text-ink-muted hover:bg-raised hover:text-ink-strong"
          >
            <X size={20} />
          </button>
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
                        ? "text-primary-700 dark:text-primary-300"
                        : "text-ink-subtle hover:text-ink-muted"
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
                      groupActive ? "text-primary-700 dark:text-primary-300" : "text-ink-subtle"
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
                          // Sprint 59 — Render Free Tier rate-limit fix.
                          // Next.js by default pre-fetches every <Link>
                          // when it enters the viewport. With 14 sidebar
                          // items, that's 14 RSC requests on every page
                          // mount. Combined with the actual page's data
                          // fetches, 10 page navigations = 150+ requests
                          // in ~30 seconds, blowing past Render's 400
                          // GET/min limit and triggering 429.
                          //
                          // Disabling prefetch here means the sidebar
                          // links only fetch on click. The user is the
                          // one who decides when to spend API calls.
                          prefetch={false}
                          className={`flex items-center gap-3 px-3 py-2 rounded-md text-sm font-medium transition-colors ${
                            active
                              ? "bg-brand-light text-primary-700 dark:bg-brand-900/30 dark:text-primary-300"
                              : "text-ink-muted hover:bg-raised hover:text-ink-strong"
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

      {/* Main — on mobile (no sidebar taking space) we use no margin;
          on desktop (lg+) the sidebar takes 16rem so we add mr-64. */}
      <div className="flex-1 lg:mr-64">
        {/* Top bar */}
        <header className="bg-canvas dark:bg-neutral-950 border-b border-edge px-4 sm:px-6 py-3 flex items-center justify-between sticky top-0 z-10">
          {/* Left cluster: hamburger (mobile only) + company switcher */}
          <div className="flex items-center gap-2">
            {/* Sprint 59 — mobile hamburger button.
                Hidden on lg+ where the sidebar is always visible. */}
            <button
              type="button"
              aria-label="فتح القائمة"
              onClick={() => setMobileMenuOpen(true)}
              className="lg:hidden p-2 rounded-md text-ink-muted hover:bg-raised hover:text-ink-strong"
            >
              <Menu size={22} />
            </button>

            {/* Company Switcher */}
            <div className="relative">
              <button
                onClick={() => setShowCompanyMenu(!showCompanyMenu)}
                className="flex items-center gap-2 px-3 py-2 rounded-md hover:bg-raised"
              >
                <Building2 size={16} className="text-ink-subtle" />
                <div className="text-right hidden sm:block">
                  <p className="text-sm font-semibold text-ink-strong">{activeCompany?.nameAr || activeCompany?.name || "اختر شركة"}</p>
                  <p className="text-xs text-ink-subtle">{activeCompany?.roleName}</p>
                </div>
                <ChevronDown size={14} className="text-ink-subtle" />
              </button>
              {showCompanyMenu && (
                <div className="absolute right-0 mt-1 w-72 bg-canvas dark:bg-neutral-900 border border-edge rounded-md shadow-lg z-20">
                  <div className="p-2">
                    <p className="text-xs text-ink-subtle px-2 py-1">شركاتي</p>
                    {companies.map((c) => (
                      <button
                        key={c.id}
                        onClick={async () => {
                          setShowCompanyMenu(false);
                          if (c.id !== activeCompany?.id) await switchCompany(c.id);
                        }}
                        className={`w-full text-right px-3 py-2 rounded-md text-sm hover:bg-raised text-ink-strong ${
                          c.id === activeCompany?.id ? "bg-brand-light dark:bg-brand-900/30" : ""
                        }`}
                      >
                        <div className="font-medium">{c.nameAr || c.name}</div>
                        <div className="text-xs text-ink-subtle">{c.code} • {c.roleName}</div>
                      </button>
                    ))}
                  </div>
                </div>
              )}
            </div>
          </div>

          {/* Right cluster: theme + user menu */}
          <div className="flex items-center gap-2">
            <ThemeToggle />
            <div className="relative">
              <button
                onClick={() => setShowUserMenu(!showUserMenu)}
                className="flex items-center gap-2 px-3 py-2 rounded-md hover:bg-raised"
              >
                <div className="w-8 h-8 bg-brand-light text-primary-700 dark:bg-brand-900/40 dark:text-primary-300 rounded-full flex items-center justify-center">
                  <User size={16} />
                </div>
                <div className="text-right">
                  <p className="text-sm font-semibold text-ink-strong">{user.fullNameAr || user.fullName || user.email}</p>
                  <p className="text-xs text-ink-subtle">
                    {user.isSuperAdmin ? "مدير عام" : "مستخدم"}
                  </p>
                </div>
                <ChevronDown size={14} className="text-ink-subtle" />
              </button>
              {showUserMenu && (
                <div className="absolute left-0 mt-1 w-48 bg-canvas dark:bg-neutral-900 border border-edge rounded-md shadow-lg z-20">
                  <div className="p-2">
                    <div className="px-2 py-1 text-xs text-ink-subtle">{user.email}</div>
                    <button
                      onClick={logout}
                      className="w-full text-right px-3 py-2 rounded-md text-sm text-red-600 hover:bg-red-50 dark:hover:bg-red-900/20 flex items-center gap-2"
                    >
                      <LogOut size={14} />
                      تسجيل الخروج
                    </button>
                  </div>
                </div>
              )}
            </div>
          </div>
        </header>

        <main className="p-6 bg-raised dark:bg-neutral-950 min-h-[calc(100vh-64px)]">{children}</main>
      </div>
    </div>
  );
}

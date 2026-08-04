"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useAuth } from "@/lib/auth-context";
import {
  LayoutDashboard, Building2, BookOpen, FileText, Zap, BarChart3, LogOut, ChevronDown, User, FolderKanban, Users, Package, Inbox
} from "lucide-react";

export default function DashboardLayout({ children }: { children: React.ReactNode }) {
  const { user, companies, activeCompany, loading, logout, switchCompany } = useAuth();
  const router = useRouter();
  const pathname = usePathname();
  const [showCompanyMenu, setShowCompanyMenu] = useState(false);
  const [showUserMenu, setShowUserMenu] = useState(false);

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

  const navItems = [
    { href: "/dashboard", label: "الرئيسية", icon: LayoutDashboard, exact: true },
    { href: "/dashboard/companies", label: "الشركات", icon: Building2, perm: "companies.read" },
    { href: "/dashboard/accounts", label: "شجرة الحسابات", icon: BookOpen, perm: "finance.read" },
    { href: "/dashboard/products", label: "المنتجات", icon: Package, perm: "finance.read" },
    { href: "/dashboard/invoices", label: "الفواتير", icon: FileText, perm: "finance.read" },
    { href: "/dashboard/journal", label: "القيود اليومية", icon: FileText, perm: "finance.read" },
    { href: "/dashboard/journal/pending", label: "القيود المعلقة", icon: Inbox, perm: "finance.read" },
    { href: "/dashboard/projects", label: "المشاريع", icon: FolderKanban, perm: "projects.read" },
    { href: "/dashboard/users", label: "المستخدمون", icon: Users, perm: "users.read" },
    { href: "/dashboard/rules", label: "قواعد العمل", icon: Zap, perm: "rules.read" },
    { href: "/dashboard/reports/trial-balance", label: "ميزان المراجعة", icon: BarChart3, perm: "reports.read" },
    { href: "/dashboard/reports/income-statement", label: "قائمة الدخل", icon: BarChart3, perm: "reports.read" },
    { href: "/dashboard/reports/balance-sheet", label: "الميزانية", icon: BarChart3, perm: "reports.read" }
  ];

  const visibleNav = navItems.filter((item) => !item.perm || user.isSuperAdmin || true);

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

        <nav className="p-3 space-y-1">
          {visibleNav.map((item) => {
            const Icon = item.icon;
            const active = item.exact ? pathname === item.href : pathname.startsWith(item.href);
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
                <Icon size={18} />
                {item.label}
              </Link>
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

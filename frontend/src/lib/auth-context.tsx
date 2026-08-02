"use client";

import { createContext, useContext, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import Cookies from "js-cookie";
import { api } from "./api";

export interface User {
  id: string;
  email: string;
  fullName?: string;
  fullNameAr?: string;
  isSuperAdmin: boolean;
}

export interface Company {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  roleId: string;
  roleName: string;
  isPrimary: boolean;
}

interface AuthContextType {
  user: User | null;
  companies: Company[];
  activeCompany: Company | null;
  loading: boolean;
  login: (email: string, password: string) => Promise<{ ok: boolean; error?: string }>;
  logout: () => void;
  switchCompany: (companyId: string) => Promise<{ ok: boolean; error?: string }>;
  hasPermission: (code: string) => boolean;
}

const AuthContext = createContext<AuthContextType | null>(null);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [companies, setCompanies] = useState<Company[]>([]);
  const [activeCompany, setActiveCompany] = useState<Company | null>(null);
  const [loading, setLoading] = useState(true);
  const [permissions, setPermissions] = useState<string[]>([]);
  const router = useRouter();

  useEffect(() => {
    const token = Cookies.get("erp_token");
    const userData = Cookies.get("erp_user");
    const companiesData = Cookies.get("erp_companies");
    const active = Cookies.get("erp_active_company");

    if (token && userData) {
      try {
        setUser(JSON.parse(userData));
        if (companiesData) {
          const comps = JSON.parse(companiesData);
          setCompanies(comps);
          if (active) {
            const ac = comps.find((c: Company) => c.id === active);
            setActiveCompany(ac || comps[0] || null);
          } else if (comps.length > 0) {
            setActiveCompany(comps[0]);
            Cookies.set("erp_active_company", comps[0].id);
          }
        }
        // Decode JWT to get permissions (simple base64 decode)
        try {
          const payload = JSON.parse(atob(token.split(".")[1]));
          setPermissions(payload.permission || []);
        } catch { /* ignore */ }
      } catch (e) {
        // Invalid cookies
        Cookies.remove("erp_token");
        Cookies.remove("erp_user");
        Cookies.remove("erp_companies");
        Cookies.remove("erp_active_company");
      }
    }
    setLoading(false);
  }, []);

  const login = async (email: string, password: string) => {
    try {
      const res = await api.post("/auth/login", { email, password });
      const { accessToken, user, companies } = res.data;

      Cookies.set("erp_token", accessToken, { expires: 1 });
      Cookies.set("erp_user", JSON.stringify(user), { expires: 1 });
      Cookies.set("erp_companies", JSON.stringify(companies), { expires: 1 });
      if (companies.length > 0) {
        const primary = companies.find((c: Company) => c.isPrimary) || companies[0];
        Cookies.set("erp_active_company", primary.id, { expires: 1 });
        setActiveCompany(primary);
      }

      setUser(user);
      setCompanies(companies);

      try {
        const payload = JSON.parse(atob(accessToken.split(".")[1]));
        setPermissions(payload.permission || []);
      } catch { /* ignore */ }

      router.push("/dashboard");
      return { ok: true };
    } catch (err: any) {
      return { ok: false, error: err?.response?.data?.error || "بيانات الدخول غير صحيحة" };
    }
  };

  const logout = () => {
    Cookies.remove("erp_token");
    Cookies.remove("erp_user");
    Cookies.remove("erp_companies");
    Cookies.remove("erp_active_company");
    setUser(null);
    setCompanies([]);
    setActiveCompany(null);
    router.push("/auth/login");
  };

  const switchCompany = async (companyId: string) => {
    try {
      const res = await api.post("/auth/switch-company", { companyId });
      const { accessToken } = res.data;
      Cookies.set("erp_token", accessToken, { expires: 1 });
      Cookies.set("erp_active_company", companyId, { expires: 1 });
      const ac = companies.find((c) => c.id === companyId);
      setActiveCompany(ac || null);
      try {
        const payload = JSON.parse(atob(accessToken.split(".")[1]));
        setPermissions(payload.permission || []);
      } catch { /* ignore */ }
      window.location.reload();
      return { ok: true };
    } catch (err: any) {
      return { ok: false, error: err?.response?.data?.error || "فشل تبديل الشركة" };
    }
  };

  const hasPermission = (code: string) => {
    if (user?.isSuperAdmin) return true;
    return permissions.includes(code);
  };

  return (
    <AuthContext.Provider value={{ user, companies, activeCompany, loading, login, logout, switchCompany, hasPermission }}>
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be inside AuthProvider");
  return ctx;
}

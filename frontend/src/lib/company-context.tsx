"use client";

import { createContext, useContext } from "react";
import { useAuth } from "./auth-context";

interface CompanyContextType {
  activeCompanyId: string | null;
}

const CompanyContext = createContext<CompanyContextType | null>(null);

export function CompanyProvider({ children }: { children: React.ReactNode }) {
  const { activeCompany } = useAuth();
  return (
    <CompanyContext.Provider value={{ activeCompanyId: activeCompany?.id ?? null }}>
      {children}
    </CompanyContext.Provider>
  );
}

export function useCompany() {
  const ctx = useContext(CompanyContext);
  if (!ctx) throw new Error("useCompany must be inside CompanyProvider");
  return ctx;
}

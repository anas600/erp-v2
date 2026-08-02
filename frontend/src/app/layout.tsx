import type { Metadata } from "next";
import "./globals.css";
import { AuthProvider } from "@/lib/auth-context";
import { CompanyProvider } from "@/lib/company-context";

export const metadata: Metadata = {
  title: "ERP-V2 - نظام إدارة الشركات",
  description: "نظام ERP متعدد الشركات مع محرك قواعد عمل قابل للتخصيص"
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="ar" dir="rtl">
      <body>
        <AuthProvider>
          <CompanyProvider>
            {children}
          </CompanyProvider>
        </AuthProvider>
      </body>
    </html>
  );
}

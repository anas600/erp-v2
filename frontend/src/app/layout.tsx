import type { Metadata } from "next";
import "./globals.css";
import { AuthProvider } from "@/lib/auth-context";
import { CompanyProvider } from "@/lib/company-context";

export const metadata: Metadata = {
  title: "ERP-V2 - نظام إدارة الشركات",
  description: "نظام ERP متعدد الشركات مع محرك قواعد عمل قابل للتخصيص"
};

// Inline script: applied before first paint so dark-mode users
// don't see a flash of light. Reads localStorage("erp-theme")
// and applies the .dark class on <html> synchronously. The
// `system` branch uses matchMedia.
const themeInitScript = `
(function() {
  try {
    var t = localStorage.getItem('erp-theme');
    if (!t) t = 'system';
    var dark = t === 'dark' || (t === 'system' && window.matchMedia('(prefers-color-scheme: dark)').matches);
    if (dark) document.documentElement.classList.add('dark');
  } catch (e) {}
})();
`;

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="ar" dir="rtl" suppressHydrationWarning>
      <head>
        <script dangerouslySetInnerHTML={{ __html: themeInitScript }} />
      </head>
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

"use client";

/**
 * ThemeToggle — light / dark / system switcher.
 *
 * Sprint 37: the new design system supports a dark mode. This
 * component is the user-facing switch: a small button in the
 * topbar that cycles through three modes.
 *
 *   light  → forced light, .dark class removed from <html>
 *   dark   → forced dark, .dark class added to <html>
 *   system → follows prefers-color-scheme media query, set
 *            via a one-shot matchMedia check + live change listener
 *
 * The choice persists in localStorage under "erp-theme". On first
 * load (no key present) we default to "system" — the most polite
 * default because it respects whatever the OS already has.
 *
 * Why we avoid FOUC (flash of unstyled content):
 *   The `applyTheme` function runs synchronously and the toggle
 *   button is rendered in the topbar (client component), but the
 *   critical thing is that the .dark class is on <html> BEFORE
 *   first paint. We do this via a tiny inline script in
 *   `app/layout.tsx` that runs on first byte.
 */
import { useEffect, useState } from "react";
import { Sun, Moon, Monitor } from "lucide-react";

type Theme = "light" | "dark" | "system";

const STORAGE_KEY = "erp-theme";

function getSystemPrefersDark(): boolean {
  if (typeof window === "undefined") return false;
  return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function applyTheme(theme: Theme) {
  if (typeof document === "undefined") return;
  const root = document.documentElement;
  const isDark = theme === "dark" || (theme === "system" && getSystemPrefersDark());
  root.classList.toggle("dark", isDark);
}

function readStoredTheme(): Theme {
  if (typeof window === "undefined") return "system";
  const v = window.localStorage.getItem(STORAGE_KEY);
  if (v === "light" || v === "dark" || v === "system") return v;
  return "system";
}

export default function ThemeToggle() {
  const [theme, setTheme] = useState<Theme>("system");
  const [mounted, setMounted] = useState(false);

  // On mount: read stored choice, apply it, and subscribe to
  // system changes when in "system" mode.
  useEffect(() => {
    const t = readStoredTheme();
    setTheme(t);
    applyTheme(t);
    setMounted(true);

    const mq = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => {
      // Re-apply only matters when current selection is "system"
      if (readStoredTheme() === "system") applyTheme("system");
    };
    mq.addEventListener("change", onChange);
    return () => mq.removeEventListener("change", onChange);
  }, []);

  const cycle = () => {
    // Cycle: light → dark → system → light
    const next: Theme = theme === "light" ? "dark" : theme === "dark" ? "system" : "light";
    setTheme(next);
    window.localStorage.setItem(STORAGE_KEY, next);
    applyTheme(next);
  };

  // While mounting on the client, render a placeholder so the
  // SSR/CSR HTML matches (avoids hydration warnings).
  if (!mounted) {
    return (
      <button
        type="button"
        className="p-2 rounded-md text-ink-muted hover:bg-raised"
        aria-label="تبديل المظهر"
      >
        <Monitor size={18} />
      </button>
    );
  }

  const Icon = theme === "light" ? Sun : theme === "dark" ? Moon : Monitor;
  const label =
    theme === "light" ? "الوضع الفاتح" : theme === "dark" ? "الوضع الداكن" : "حسب النظام";

  return (
    <button
      type="button"
      onClick={cycle}
      className="p-2 rounded-md text-ink-muted hover:bg-raised hover:text-ink-strong transition-colors"
      aria-label={`تبديل المظهر — ${label}`}
      title={label}
    >
      <Icon size={18} />
    </button>
  );
}

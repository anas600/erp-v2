"use client";

/**
 * Sprint 35 — project status badge.
 *
 * The 5 statuses are: draft / active / on_hold / completed / closed.
 * The original Sprint 11 projects list only knew about
 * active/completed/on_hold/cancelled. The new default is "draft"
 * (per migration 020) and "closed" replaces "cancelled" for
 * post-completion archival — but we still render "cancelled"
 * as an alias for any older data in the DB.
 */
import { FileEdit, Play, Pause, CheckCircle, Archive, XCircle } from "lucide-react";

export type ProjectStatus = "draft" | "active" | "on_hold" | "completed" | "closed" | "cancelled" | string;

const META: Record<string, { label: string; cls: string; Icon: any }> = {
  draft:     { label: "مسودة",     cls: "bg-gray-100 text-gray-700 border border-gray-200",     Icon: FileEdit },
  active:    { label: "نشط",       cls: "bg-green-100 text-green-800 border border-green-200",  Icon: Play },
  on_hold:   { label: "متوقف",     cls: "bg-yellow-100 text-yellow-800 border border-yellow-200",Icon: Pause },
  completed: { label: "مكتمل",     cls: "bg-blue-100 text-blue-800 border border-blue-200",     Icon: CheckCircle },
  closed:    { label: "مغلق",      cls: "bg-slate-100 text-slate-700 border border-slate-200",  Icon: Archive },
  // Legacy alias for old rows (pre-Sprint 35 migration).
  cancelled: { label: "ملغي",      cls: "bg-red-100 text-red-800 border border-red-200",       Icon: XCircle },
};

export default function StatusBadge({ status, withIcon = true }: { status?: string | null; withIcon?: boolean }) {
  const meta = (status && META[status]) || { label: status || "—", cls: "bg-gray-100 text-gray-700 border border-gray-200", Icon: FileEdit };
  const Icon = meta.Icon;
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${meta.cls}`}>
      {withIcon && <Icon size={12} />}
      {meta.label}
    </span>
  );
}

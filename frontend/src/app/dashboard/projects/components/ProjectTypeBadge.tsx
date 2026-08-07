"use client";

/**
 * Sprint 35 — project type badge.
 *
 * The 4 project types come from the backend ProjectDto.Type field.
 * Each gets a distinct color so the list and detail pages can
 * be scanned at a glance (a site supervisor skimming on a phone
 * should be able to spot the construction jobs vs the service
 * contracts without reading every line).
 */
import { Building2, Package, Wrench, Briefcase } from "lucide-react";

export type ProjectType = "construction" | "supply" | "service" | "maintenance" | string;

const META: Record<string, { label: string; cls: string; Icon: any }> = {
  construction: { label: "مقاولات",   cls: "bg-amber-100 text-amber-800 border border-amber-200", Icon: Building2 },
  supply:       { label: "توريد",     cls: "bg-blue-100 text-blue-800 border border-blue-200",     Icon: Package },
  service:      { label: "خدمات",     cls: "bg-purple-100 text-purple-800 border border-purple-200",Icon: Briefcase },
  maintenance:  { label: "صيانة",     cls: "bg-teal-100 text-teal-800 border border-teal-200",    Icon: Wrench },
};

export default function ProjectTypeBadge({ type, withIcon = true }: { type?: string | null; withIcon?: boolean }) {
  const meta = (type && META[type]) || { label: type || "—", cls: "bg-gray-100 text-gray-700 border border-gray-200", Icon: Briefcase };
  const Icon = meta.Icon;
  return (
    <span className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${meta.cls}`}>
      {withIcon && <Icon size={12} />}
      {meta.label}
    </span>
  );
}

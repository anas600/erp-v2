"use client";

/**
 * Sprint 36 — Progress billing status badge.
 *
 * Three statuses (per backend ContractsService / BillingService):
 *   - DRAFT     — created but not approved
 *   - INVOICED  — approved; an invoice + journal entry have been
 *                 created against the customer
 *   - CANCELLED — explicitly cancelled (only allowed from DRAFT)
 *
 * Why a separate component (and not reuse StatusBadge)?
 *   StatusBadge is project-scoped (5 statuses + 1 legacy alias).
 *   Billing statuses have different semantics and different colors:
 *   DRAFT is "not yet final", INVOICED is "final + posted", CANCELLED
 *   is "voided". Sharing the same colors would confuse the user.
 */
import { FileEdit, CheckCircle, Ban } from "lucide-react";

export type BillingStatus = "DRAFT" | "INVOICED" | "CANCELLED" | string;

const META: Record<string, { label: string; cls: string; Icon: any }> = {
  DRAFT: {
    label: "مسودة",
    cls: "bg-gray-100 text-gray-700 border border-gray-200",
    Icon: FileEdit,
  },
  INVOICED: {
    label: "مُفوتر",
    cls: "bg-green-100 text-green-800 border border-green-200",
    Icon: CheckCircle,
  },
  CANCELLED: {
    label: "ملغي",
    cls: "bg-red-100 text-red-800 border border-red-200",
    Icon: Ban,
  },
};

export default function StatusBadgeBilling({
  status,
  withIcon = true,
}: {
  status?: string | null;
  withIcon?: boolean;
}) {
  const meta =
    (status && META[status]) || {
      label: status || "—",
      cls: "bg-gray-100 text-gray-700 border border-gray-200",
      Icon: FileEdit,
    };
  const Icon = meta.Icon;
  return (
    <span
      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs font-medium ${meta.cls}`}
    >
      {withIcon && <Icon size={12} />}
      {meta.label}
    </span>
  );
}

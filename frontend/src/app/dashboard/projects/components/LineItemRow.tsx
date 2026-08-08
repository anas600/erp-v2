"use client";

/**
 * Sprint 38 — Single BOQ line item row (desktop + mobile).
 *
 * Encapsulates one row's logic so the table in ContractTab doesn't
 * need to repeat the same JSX for desktop and mobile. The parent
 * supplies the row data + the action handlers; this component is
 * pure presentation + a couple of inline action buttons.
 *
 * Why expose a `canDelete` flag?
 *   The backend rule (Sprint 38 spec) is: a line item is deletable
 *   only if no billing has used it (i.e. billedQuantity == 0). The
 *   parent computes that and passes the flag — we don't want this
 *   component to need to know the row's full accounting history.
 *
 * `billed` progress bar:
 *   The row shows a thin progress bar of billed/total so the user
 *   can scan the table and see which items are partially done vs.
 *   untouched. We use the same gray primary-100/700 combo as the
 *   other progress bars in the project pages.
 */
import {
  ArrowUp,
  ArrowDown,
  Pencil,
  Trash2,
  Hash,
} from "lucide-react";
import { formatNumber, cn } from "@/lib/utils";
import type { LineItemDto } from "./LineItemModal";

interface Props {
  item: LineItemDto;
  /** false when item has been billed and cannot be removed. */
  canDelete: boolean;
  /** false when item is the first row. */
  canMoveUp: boolean;
  /** false when item is the last row. */
  canMoveDown: boolean;
  onEdit: () => void;
  onDelete: () => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  /** Render as a card (mobile) or a table row (desktop). */
  variant?: "desktop" | "mobile";
}

export default function LineItemRow({
  item,
  canDelete,
  canMoveUp,
  canMoveDown,
  onEdit,
  onDelete,
  onMoveUp,
  onMoveDown,
  variant = "desktop",
}: Props) {
  const billedPct =
    item.quantity > 0
      ? Math.min(100, (item.billedQuantity / item.quantity) * 100)
      : 0;

  if (variant === "mobile") {
    return (
      <div className="card">
        <div className="flex items-start justify-between gap-2 mb-2">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2 text-xs text-ink-muted">
              <Hash size={11} />
              <span className="font-mono">#{item.lineNumber}</span>
              <span className="text-ink-subtle">•</span>
              <span>{item.customUnit || item.unit}</span>
            </div>
            <p className="text-sm font-medium mt-1">{item.description}</p>
          </div>
          <RowActions
            canDelete={canDelete}
            canMoveUp={canMoveUp}
            canMoveDown={canMoveDown}
            onEdit={onEdit}
            onDelete={onDelete}
            onMoveUp={onMoveUp}
            onMoveDown={onMoveDown}
          />
        </div>
        <div className="grid grid-cols-3 gap-2 text-xs">
          <Cell label="الكمية" value={formatNumber(item.quantity, 3)} />
          <Cell label="سعر الوحدة" value={formatNumber(item.unitPrice, 3)} />
          <Cell label="الإجمالي" value={formatNumber(item.totalPrice)} strong />
        </div>
        <div className="mt-2 grid grid-cols-3 gap-2 text-xs">
          <Cell label="المُنجَز" value={formatNumber(item.billedQuantity, 3)} />
          <Cell label="المتبقي" value={formatNumber(item.remainingQuantity, 3)} />
          <Cell label="مُفوتر" value={formatNumber(item.amountBilled)} />
        </div>
        <div className="mt-2 h-1.5 rounded bg-raised overflow-hidden">
          <div
            className="h-full bg-primary-600"
            style={{ width: `${billedPct}%` }}
            aria-hidden
          />
        </div>
      </div>
    );
  }

  // desktop table row
  return (
    <tr
      className={cn(
        "border-b border-edge hover:bg-raised/40",
        item.remainingQuantity <= 0 && "bg-green-50/40 dark:bg-green-900/10"
      )}
    >
      <td className="py-2 px-3 font-mono text-xs text-ink-muted">
        <div className="flex items-center gap-1">
          <RowActions
            canDelete={canDelete}
            canMoveUp={canMoveUp}
            canMoveDown={canMoveDown}
            onEdit={onEdit}
            onDelete={onDelete}
            onMoveUp={onMoveUp}
            onMoveDown={onMoveDown}
            inline
          />
          <span>#{item.lineNumber}</span>
        </div>
      </td>
      <td className="py-2 px-3 text-sm max-w-xs truncate" title={item.description}>
        {item.description}
      </td>
      <td className="py-2 px-3 text-xs text-ink-muted">
        {item.customUnit || item.unit}
      </td>
      <td className="py-2 px-3 font-mono text-sm" dir="ltr">
        {formatNumber(item.quantity, 3)}
      </td>
      <td className="py-2 px-3 font-mono text-sm" dir="ltr">
        {formatNumber(item.unitPrice, 3)}
      </td>
      <td className="py-2 px-3 font-mono text-sm font-semibold" dir="ltr">
        {formatNumber(item.totalPrice)}
      </td>
      <td className="py-2 px-3 font-mono text-sm" dir="ltr">
        <div className="flex items-center gap-2">
          <span>{formatNumber(item.billedQuantity, 3)}</span>
          <div className="w-12 h-1 rounded bg-raised overflow-hidden">
            <div
              className="h-full bg-primary-600"
              style={{ width: `${billedPct}%` }}
              aria-hidden
            />
          </div>
        </div>
      </td>
      <td
        className={cn(
          "py-2 px-3 font-mono text-sm",
          item.remainingQuantity <= 0 ? "text-green-700" : ""
        )}
        dir="ltr"
      >
        {formatNumber(item.remainingQuantity, 3)}
      </td>
    </tr>
  );
}

function Cell({
  label,
  value,
  strong,
}: {
  label: string;
  value: string;
  strong?: boolean;
}) {
  return (
    <div>
      <div className="text-[10px] text-ink-muted">{label}</div>
      <div
        dir="ltr"
        className={cn("font-mono", strong ? "font-semibold" : "")}
      >
        {value}
      </div>
    </div>
  );
}

function RowActions({
  canDelete,
  canMoveUp,
  canMoveDown,
  onEdit,
  onDelete,
  onMoveUp,
  onMoveDown,
  inline = false,
}: {
  canDelete: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  onEdit: () => void;
  onDelete: () => void;
  onMoveUp: () => void;
  onMoveDown: () => void;
  inline?: boolean;
}) {
  return (
    <div className={cn("flex items-center gap-0.5", inline ? "" : "gap-1")}>
      <button
        type="button"
        onClick={onMoveUp}
        disabled={!canMoveUp}
        className="p-1 text-ink-subtle hover:text-ink-muted disabled:opacity-30"
        title="أعلى"
        aria-label="نقل لأعلى"
      >
        <ArrowUp size={12} />
      </button>
      <button
        type="button"
        onClick={onMoveDown}
        disabled={!canMoveDown}
        className="p-1 text-ink-subtle hover:text-ink-muted disabled:opacity-30"
        title="أسفل"
        aria-label="نقل لأسفل"
      >
        <ArrowDown size={12} />
      </button>
      <button
        type="button"
        onClick={onEdit}
        className="p-1 text-primary-700 hover:bg-primary-50 rounded"
        title="تعديل"
        aria-label="تعديل البند"
      >
        <Pencil size={12} />
      </button>
      <button
        type="button"
        onClick={onDelete}
        disabled={!canDelete}
        className="p-1 text-red-600 hover:bg-red-50 rounded disabled:opacity-30 disabled:cursor-not-allowed"
        title={canDelete ? "حذف" : "لا يمكن الحذف — تم استخدام البند في مستخلصات"}
        aria-label="حذف البند"
      >
        <Trash2 size={12} />
      </button>
    </div>
  );
}

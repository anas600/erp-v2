"use client";

/**
 * Sprint 38 — Read-only table of the line items a billing
 * settlement recorded.
 *
 * Shown inside the "view billing" modal. Each row is one BOQ
 * line item's portion of THIS billing:
 *   - this period quantity
 *   - previous period cumulative quantity
 *   - new cumulative quantity
 *   - this-period amount
 *
 * Why a separate component from the BOQ table?
 *   The BOQ table in the contract tab shows the *current* state
 *   (with billed-to-date across ALL billings). The billing-detail
 *   table shows the *snapshot* of what THIS billing captured.
 *   Different fields, different reader. Two components.
 */
import { formatNumber, cn } from "@/lib/utils";
import { Ruler } from "lucide-react";

export interface BillingLineItemDto {
  id: string;
  lineItemId: string;
  description: string;
  unit: string;
  customUnit?: string | null;
  unitPrice: number;
  /** Quantity billed in this specific billing (this period). */
  thisPeriodQuantity: number;
  /** Cumulative quantity BEFORE this billing. */
  previousCumulative: number;
  /** Cumulative quantity AFTER this billing = previous + this. */
  newCumulative: number;
  /** Amount billed in this period = thisPeriodQuantity * unitPrice. */
  thisPeriodAmount: number;
}

interface Props {
  items: BillingLineItemDto[];
  /** Optional fixed total to display in the footer (else we sum). */
  totalAmount?: number;
}

export default function BillingLineItemsTable({ items, totalAmount }: Props) {
  if (items.length === 0) {
    return (
      <div className="card text-center text-ink-muted py-6 text-sm">
        لا توجد بنود في هذا المستخلص
      </div>
    );
  }
  const sum = items.reduce((s, i) => s + (Number(i.thisPeriodAmount) || 0), 0);
  const total = totalAmount != null ? totalAmount : sum;

  return (
    <div className="space-y-2">
      {/* Desktop */}
      <div className="hidden sm:block card p-0 overflow-x-auto">
        <table className="w-full text-sm">
          <thead>
            <tr className="bg-raised border-b border-edge">
              <th className="text-right py-2 px-3 font-semibold text-ink-muted">#</th>
              <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوصف</th>
              <th className="text-right py-2 px-3 font-semibold text-ink-muted">الوحدة</th>
              <th className="text-left py-2 px-3 font-semibold text-ink-muted">سعر الوحدة</th>
              <th className="text-left py-2 px-3 font-semibold text-ink-muted">هذه الفترة</th>
              <th className="text-left py-2 px-3 font-semibold text-ink-muted">السابق</th>
              <th className="text-left py-2 px-3 font-semibold text-ink-muted">التراكمي</th>
              <th className="text-left py-2 px-3 font-semibold text-ink-muted">مبلغ هذه الفترة</th>
            </tr>
          </thead>
          <tbody>
            {items.map((i, idx) => (
              <tr key={i.id || idx} className="border-b border-edge">
                <td className="py-2 px-3 font-mono text-xs text-ink-muted">{idx + 1}</td>
                <td className="py-2 px-3 max-w-xs truncate" title={i.description}>
                  {i.description}
                </td>
                <td className="py-2 px-3 text-xs text-ink-muted">
                  {i.customUnit || i.unit}
                </td>
                <td className="py-2 px-3 text-left font-mono" dir="ltr">
                  {formatNumber(i.unitPrice, 3)}
                </td>
                <td className="py-2 px-3 text-left font-mono font-semibold" dir="ltr">
                  {formatNumber(i.thisPeriodQuantity, 3)}
                </td>
                <td className="py-2 px-3 text-left font-mono text-ink-muted" dir="ltr">
                  {formatNumber(i.previousCumulative, 3)}
                </td>
                <td className="py-2 px-3 text-left font-mono" dir="ltr">
                  {formatNumber(i.newCumulative, 3)}
                </td>
                <td className="py-2 px-3 text-left font-mono font-semibold" dir="ltr">
                  {formatNumber(i.thisPeriodAmount)}
                </td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr className="bg-raised font-semibold border-t-2 border-edge">
              <td colSpan={7} className="py-2 px-3 text-right">
                إجمالي هذه الفترة
              </td>
              <td className="py-2 px-3 text-left font-mono text-primary-700" dir="ltr">
                {formatNumber(total)}
              </td>
            </tr>
          </tfoot>
        </table>
      </div>

      {/* Mobile — card per line item */}
      <div className="sm:hidden space-y-2">
        {items.map((i, idx) => (
          <div key={i.id || idx} className="card">
            <div className="flex items-start justify-between gap-2 mb-2">
              <div className="min-w-0 flex-1">
                <p className="text-sm font-medium truncate" title={i.description}>
                  {idx + 1}. {i.description}
                </p>
                <p className="text-xs text-ink-muted flex items-center gap-1 mt-0.5">
                  <Ruler size={11} />
                  {i.customUnit || i.unit} • سعر:{" "}
                  <span className="font-mono" dir="ltr">
                    {formatNumber(i.unitPrice, 3)}
                  </span>
                </p>
              </div>
              <div className="text-left shrink-0">
                <div className="text-[10px] text-ink-muted">مبلغ الفترة</div>
                <div className="font-mono font-semibold" dir="ltr">
                  {formatNumber(i.thisPeriodAmount)}
                </div>
              </div>
            </div>
            <div className="grid grid-cols-3 gap-2 text-xs">
              <Cell label="هذه الفترة" value={formatNumber(i.thisPeriodQuantity, 3)} />
              <Cell label="السابق" value={formatNumber(i.previousCumulative, 3)} muted />
              <Cell label="التراكمي" value={formatNumber(i.newCumulative, 3)} />
            </div>
          </div>
        ))}
        <div className="card bg-raised flex items-center justify-between">
          <span className="text-sm font-semibold">إجمالي المستخلص</span>
          <span className="font-mono font-bold text-primary-700" dir="ltr">
            {formatNumber(total)}
          </span>
        </div>
      </div>
    </div>
  );
}

function Cell({
  label,
  value,
  muted,
}: {
  label: string;
  value: string;
  muted?: boolean;
}) {
  return (
    <div>
      <div className="text-[10px] text-ink-muted">{label}</div>
      <div
        dir="ltr"
        className={cn("font-mono", muted ? "text-ink-muted" : "font-semibold")}
      >
        {value}
      </div>
    </div>
  );
}

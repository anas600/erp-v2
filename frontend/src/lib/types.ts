/**
 * Shared TypeScript types used across multiple pages.
 *
 * Sprint 25 added the receivable/payable settlement cycle, contact detail
 * pages, account statements, and fiscal-year management. This file
 * collects the data shapes those features share.
 *
 * Sprint 26 added the `Account` interface for the new tree-view COA
 * surface, plus the `isPostable` / `level` / `accountClass` fields
 * that drive the auto-compute behavior in the create-account form.
 *
 * Conventions:
 *   - Amounts are `number` (LYD with 2 decimal places). Server sends
 *     decimal strings/numbers; we trust them to be JSON-safe.
 *   - IDs are `string` on the wire (the backend uses Guid but emits them
 *     lowercase-stringified).
 *   - Dates are ISO-8601 strings (`YYYY-MM-DD` or full timestamp). For
 *     display we use `formatDate` / `formatDateTime` from `@/lib/utils`.
 *   - Optional fields use `?` or `| null`. `| null` is used when the
 *     backend explicitly returns JSON null; `?` is used when the field
 *     may be omitted entirely.
 */

// ─── Chart of accounts (Sprint 26) ────────────────────────────────────────

/**
 * The 4-level chart of accounts. Returned by:
 *   - `GET /api/accounts?companyId=...` (flat list) — used by the tree
 *     view (frontend groups by parentId).
 *   - `GET /api/accounts/{id}` (single) — used by the contact detail
 *     "sub-ledger" badge.
 *
 * The COA hierarchy (Sprint 26 spec):
 *   Level 1: logical type (Asset/Liability/Equity/Revenue/Expense).
 *            NOT stored as accounts; surfaced as the `accountType`
 *            on every account. Used only for grouping + the top row
 *            of the tree view.
 *   Level 2: category header — e.g. "Current Assets", "Fixed Assets".
 *            accountClass = "header", isPostable = false. No postings.
 *   Level 3: sub-category / operational — e.g. "Cash", "Bank",
 *            "Accounts Receivable" (1200). The 18 seeded accounts are
 *            at this level. isPostable defaults to true but the user
 *            may disable it (for grouping without sub-ledger).
 *   Level 4: detail / sub-ledger — e.g. "AR - Customer CUST-001".
 *            Linked 1:1 to a contact via account_contact_links.
 *            isPostable is forced true. The posting engine routes
 *            receipts/payments here.
 */
export interface Account {
  id: string;
  companyId: string;
  code: string;
  /** English name (required). */
  name: string;
  /** Arabic name (optional). */
  nameAr?: string;
  /** Parent account id (null = top-level category). */
  parentId?: string | null;
  /** Asset | Liability | Equity | Revenue | Expense. */
  accountType: string;
  /** Debit | Credit — the natural balance side. */
  nature: string;
  /** 1 = logical type, 2 = category header, 3 = sub-category, 4 = detail. */
  level: number;
  /** "header" (L2 — no postings) or "detail" (L3/L4 — accepts postings). */
  accountClass: string;
  /** AR/AP control account flag (1200 = AR control, 2000 = AP control). */
  isControlAccount: boolean;
  /** Whether postings require a cost center dimension. */
  costCenterRequired: boolean;
  /**
   * Sprint 26: whether the account accepts direct journal postings.
   *   L1/L2: forced false (grouping only).
   *   L3: user choice (default true).
   *   L4: forced true (sub-ledgers always postable).
   * UI surfaces this as a green/grey badge in the tree view.
   */
  isPostable: boolean;
  isActive: boolean;
  /** Current balance in LYD. */
  balance: number;
}

/**
 * Tree node returned by `GET /api/accounts?companyId=...` when
 * the backend pre-builds the tree. The frontend may also build the
 * tree from the flat list — both shapes are supported.
 */
export interface AccountTreeNode {
  id: string;
  code: string;
  name: string;
  nameAr?: string;
  accountType: string;
  nature: string;
  level: number;
  isControlAccount: boolean;
  isPostable: boolean;
  isActive: boolean;
  balance: number;
  hasChildren: boolean;
  children: AccountTreeNode[];
}

/**
 * Request body for `POST /api/accounts`. The `level` is computed
 * server-side from the parent; the UI sends it for clarity but the
 * backend may override.
 */
export interface CreateAccountRequest {
  companyId: string;
  code: string;
  name: string;
  nameAr?: string;
  parentId?: string | null;
  accountType: string;
  nature: string;
  level: number;
  isPostable: boolean;
  accountClass: string;
  isControlAccount?: boolean;
  costCenterRequired?: boolean;
}

/**
 * Request body for `POST /api/accounts/sub-ledger`. The backend
 * auto-resolves the parent (1200 for customer, 2000 for supplier)
 * from the contact type, so we only send the contact + company.
 */
export interface CreateSubLedgerRequest {
  companyId: string;
  contactId: string;
  parentAccountCode: string;
  detailCode: string;
}

// ─── Contacts (customers & suppliers) ─────────────────────────────────────

/**
 * A contact's outstanding balance, as returned by `GET /api/contacts/{id}/balance`.
 *
 * For a customer: positive = they owe us, negative = we owe them.
 * For a supplier: positive = we owe them, negative = they owe us.
 * The frontend flips the sign/colour by `type` to match the user mental model.
 */
export interface ContactBalance {
  contactId: string;
  companyId: string;
  /** Positive number = open balance (always stored as positive amount). */
  balance: number;
  /** Convenience: "customer" | "supplier" — same value as the contact record. */
  type: "customer" | "supplier";
  /** When the snapshot was computed (ISO timestamp). */
  asOf: string;
}

// ─── Invoices (with settlement fields) ────────────────────────────────────

/**
 * Invoice summary used by the contact-detail "Invoices" tab. Returned by
 * `GET /api/contacts/{id}/invoices?status=outstanding|paid|all`.
 *
 * The full invoice (with lines) is fetched separately via `/api/invoices/{id}`.
 */
export interface InvoiceWithOutstanding {
  invoiceId: string;
  invoiceNumber: string;
  invoiceType: "purchase" | "sales";
  invoiceDate: string;
  partyName: string;
  partyNameAr?: string;
  /** Original invoice total (including tax). */
  total: number;
  /** Sum of all posted voucher amounts against this invoice. */
  amountPaid: number;
  /** total - amountPaid. */
  outstanding: number;
  /**
   * "draft" | "posted" | "partiallypaid" | "paid" | "cancelled"
   * (Sprint 25 added `partiallypaid`.)
   */
  status: string;
  /** Age in days from invoice_date → today. */
  ageDays: number;
}

// ─── Account statement (كشف حساب) ─────────────────────────────────────────

/**
 * One chronological row in a contact statement. Returned by
 * `GET /api/contacts/{id}/statement?from=&to=`.
 *
 * The `type` distinguishes invoices from vouchers; the `direction`
 * captures which side of the contact's sub-ledger the line hits.
 */
export type StatementLineType = "invoice" | "receipt" | "payment" | "opening";

export interface StatementLine {
  /** ISO date for the line. */
  date: string;
  type: StatementLineType;
  /** Document number (invoice number or voucher number) — empty for `opening`. */
  number: string;
  /** Free-text description (e.g. "فاتورة مبيعات", "سند قبض"). */
  description: string;
  /** For customer sub-ledger: amounts they owe us (invoices). For supplier: amounts we owe them. */
  debit: number;
  /** For customer sub-ledger: amounts they paid (receipts). For supplier: amounts we paid (payments). */
  credit: number;
  /** Running balance after this line. */
  runningBalance: number;
  /** Original document id — for cross-linking. */
  documentId?: string;
  /** Linked invoice number, when the voucher settles an invoice. */
  invoiceNumber?: string;
}

export interface StatementResponse {
  contactId: string;
  companyId: string;
  from: string;
  to: string;
  /** Balance carried into the [from, to] window. */
  openingBalance: number;
  /** Balance carried out of the window. */
  closingBalance: number;
  /** All lines in chronological order. */
  lines: StatementLine[];
}

// ─── Fiscal years & periods ───────────────────────────────────────────────

export interface FiscalYear {
  id: string;
  companyId: string;
  code: string;
  startDate: string;
  endDate: string;
  isClosed: boolean;
  closedAt?: string;
  createdAt: string;
}

export interface FiscalPeriod {
  id: string;
  fiscalYearId: string;
  /** 1–12 (or 1–13 for some year-end adjustments). */
  periodNumber: number;
  startDate: string;
  endDate: string;
  isClosed: boolean;
  lockedAt?: string;
  lockedByUserId?: string;
  /** Same values as the FiscalYear periods table; surfaced for the UI. */
  name?: string;
}

// ─── Vouchers with linked invoice (Sprint 25) ─────────────────────────────

/**
 * Extended voucher shape for the contact-detail "Vouchers" tab.
 * Combines receipts + payments with the optional linked invoice number.
 */
export interface VoucherWithInvoice {
  id: string;
  voucherNumber: string;
  voucherDate: string;
  contactId: string;
  contactName: string;
  contactCode: string;
  amount: number;
  paymentMethod: string;
  status: string;
  reference?: string;
  narration?: string;
  postedAt?: string;
  /** "receipt" = سند قبض (customer payment in). "payment" = سند صرف (supplier payment out). */
  voucherType: "receipt" | "payment";
  /** Linked invoice id (Sprint 25: vouchers can settle a specific invoice). */
  invoiceId?: string;
  /** Linked invoice number (resolved server-side for display). */
  invoiceNumber?: string;
  /** Bank account code (e.g. "1000" = Cash). */
  bankAccountId?: string;
  bankAccountCode?: string;
}

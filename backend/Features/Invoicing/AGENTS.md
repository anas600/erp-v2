# Invoicing Feature

## Purpose
- Manage purchase and sales invoices.
- Convert an invoice into a journal entry on posting, using the Posting Engine's Nature Logic.

## Ownership
- `InvoiceModels.cs` — DTOs (`InvoiceDto`, `InvoiceLineDto`, `CreateInvoiceRequest`).
- `InvoiceService.cs` — CRUD + posting logic.
- `InvoiceEndpoints.cs` — `GET/POST /api/invoices`, `POST /api/invoices/{id}/post`, `POST /api/invoices/{id}/cancel`.

## Local Contracts
- Two invoice types: `purchase` (company receives goods/services) and `sales` (company provides them).
- Each line carries an `accountId` referencing an `Asset`, `Expense`, or `Revenue` account.
- A global tax rate can be set on the invoice; per-line override is allowed.
- `subtotal = sum(line.quantity * line.unit_price)`. `tax_amount = sum(line.amount * line.tax_rate)`. `total = subtotal + tax_amount`.
- Invoice numbers are auto-generated per company and per type: `INV-P-YYYY-NNNN` (purchase) and `INV-S-YYYY-NNNN` (sales).
- On `POST /api/invoices/{id}/post`, the service builds a journal entry:
  - **Purchase**: each line's account is debited; `Accounts Payable` (code 2000) is credited for the total.
  - **Sales**: `Accounts Receivable` (code 1200) is debited for the total; each line's account is credited.
- The opposite-side account is currently hardcoded to `2000` / `1200`; future work will let each company configure them.
- Cancellation is allowed only for `draft` invoices. Posted invoices are reversed via the regular journal reversal flow.

## Work Guidance
- Adding tax support beyond a single rate: introduce a `tax_rates` table and let each line pick a rate.
- Adding supplier/customer management: create a `parties` table and replace the `party_name` column with `party_id`.
- Adding invoice PDFs: emit the journal entry as a side effect, then render a PDF from the DTO.

## Verification
- Creating a purchase invoice with one line and posting it creates a balanced journal entry (Σ debit = Σ credit) and updates the relevant account balances.
- The trial balance shows the new AP balance after the invoice is posted.
- Cancelling a draft invoice sets its status to `cancelled` and removes it from the active list.

## Child DOX Index
- *(No child folders; this is a leaf.)*

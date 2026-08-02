# Products Module

Reusable catalogue of line items: code, name, default unit price, default tax rate. Picking a product on an invoice auto-fills the description, unit price, and tax rate so the user only has to enter the quantity.

## Files

- `ProductService.cs` — CRUD + soft-delete (flips `is_active` to `false`).
- `ProductEndpoints.cs` — REST routes under `/api/products`, all `.RequireAuthorization()`.
- `AGENTS.md` — this file.

## Data model

| Column | Type | Notes |
|--------|------|-------|
| `id` | uuid | PK, default `uuid_generate_v4()` |
| `company_id` | uuid | FK to `companies.id`, `ON DELETE CASCADE` |
| `code` | string(50) | unique per company (`uk_products_company_code`) |
| `name` / `name_ar` | string(200) | bilingual |
| `unit_price` | decimal(18, 3) | qty up to three decimals |
| `default_tax_rate` | decimal(5, 2) | percent with two decimals (15.00) |
| `is_active` | bool | soft-delete flag |
| `created_at` | timestamp | default `CURRENT_TIMESTAMP` |

## Endpoints

| Method | Path | Auth | Body / Notes |
|--------|------|------|--------------|
| GET | `/api/products?companyId={id}` | ✓ | optional `includeInactive=true` |
| GET | `/api/products/{id}` | ✓ | 404 if not found |
| POST | `/api/products` | ✓ | `CreateProductRequest` (companyId, code, name, nameAr, unitPrice, defaultTaxRate) |
| PUT | `/api/products/{id}` | ✓ | `UpdateProductRequest` (all fields optional) |
| DELETE | `/api/products/{id}` | ✓ | soft-delete (sets `is_active = false`) |

## Integration with Invoices

`invoice_lines.product_id` is nullable. When the user picks a product on the invoice form, the frontend posts `{ productId, quantity, ... }`. The backend then auto-fills description, unit_price, and tax_rate from the product row, computes `line_total = quantity * unit_price` and `line_total_with_tax = line_total * (1 + tax_rate / 100)`, and stores both. The invoice `total` is just `SUM(invoice_lines.line_total_with_tax)`, which the business rule template uses.

## Why soft-delete?

Historical invoice lines reference `product_id` with `ON DELETE SET NULL`. Hard-deleting a product would also blank out the product reference on every past invoice it appeared on. Soft-delete keeps the audit trail intact.

# Contacts (Customers + Suppliers)

## Purpose
A per-company catalogue of business contacts. Each contact is either a **customer** (the company sells to them) or a **supplier** (the company buys from them). The same physical entity can be both — a construction firm might sell steel to you AND buy office supplies from you.

## Ownership
- `ContactService.cs` — CRUD: GetByCompany, GetById, Create, Update, Delete (soft).
- `ContactEndpoints.cs` — REST routes under `/api/contacts`. All require auth.
- Migration 007 created the `contacts` table and seeded 5 customers + 5 suppliers per company (flagged `is_demo_data = true`).

## Data Model
- `id` (uuid, PK)
- `company_id` (uuid, FK → companies, CASCADE)
- `type` (text: `'customer' | 'supplier'`)
- `code` (text, unique within (company, type))
- `name`, `name_ar` (text, the contact's display name in EN/AR)
- `tax_id` (text, optional — the Libyan tax number)
- `phone`, `email` (text, optional)
- `is_active` (bool, default true — soft delete)
- `is_demo_data` (bool — distinguishes seeded contacts from real ones)
- `created_at` (timestamp)

## Local Contracts
- The `code` field is unique per `(company_id, type)`. The same code can mean a customer in one company and a supplier in another.
- `type` must be exactly `'customer'` or `'supplier'` (validated in the service). This is a text column (not an enum) for future flexibility.
- `Delete` is soft: sets `is_active = false`. Hard delete would lose historical invoice references; we never lose accounting data.
- The user can wipe all demo contacts with: `DELETE FROM contacts WHERE is_demo_data = true`.

## How to Use
- Sprint 17 ships the API. The frontend invoice form's "customer/supplier" dropdown will be wired up in a follow-up — for now, invoice creation still uses free-text `party_name`.
- The seeded contacts are great for manual testing of the invoice form once the dropdown is wired.

## Verification
- `GET /api/contacts?companyId=<id>&type=customer` returns 5 customers
- `GET /api/contacts?companyId=<id>&type=supplier` returns 5 suppliers
- `GET /api/contacts?companyId=<id>` returns all 10 contacts (5 of each)
- Creating a duplicate `(company, type, code)` returns a 400 from the unique constraint.

## Child DOX Index
(none)

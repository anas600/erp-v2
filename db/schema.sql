-- ============================================================
-- ERP-V2 — Canonical Database Schema
-- ============================================================
-- This file is the SINGLE SOURCE OF TRUTH for the database
-- schema. It is also broken into incremental migrations under
-- `backend/Migrations/` (run automatically by FluentMigrator
-- on app startup).
--
-- Why this file exists alongside the FluentMigrator migrations:
--   1. The 005-009 migrations each touched the schema in
--      ways that occasionally drifted from the C# definitions
--      (e.g. missing unique indexes, wrong column names). When
--      that happened, the C# code expected columns that the
--      DB didn't have, and Dapper threw "parameterless
--      constructor" errors at runtime.
--   2. When the deployed binary doesn't match the DB schema
--      (e.g. the app was deployed but the migration didn't
--      run), we need a way to reconcile them WITHOUT a full
--      redeploy. This file is that reconciliation tool.
--   3. Onboarding a new dev or a new Render DB: run this once
--      to get a complete, consistent schema — no need to play
--      back 10 migrations in order.
--
-- How to apply this file (use ONE of these):
--   A. From your local machine (preferred):
--        psql "$DATABASE_URL" -f db/schema.sql
--   B. From the cloud sandbox (if you have the URL):
--        PGPASSWORD=... psql -h <host> -U <user> -d <db> -f db/schema.sql
--   C. From the Render dashboard (one-off):
--        Settings → psql Command → paste the contents.
--
-- Idempotency: every CREATE uses IF NOT EXISTS, every ALTER
-- uses IF NOT EXISTS / OR REPLACE / DO blocks. Safe to run
-- on a database that already has the schema — only missing
-- pieces will be added.
-- ============================================================


-- ============================================================
-- 1. CORE TABLES (from Migration 001)
-- ============================================================

CREATE TABLE IF NOT EXISTS companies (
    id uuid PRIMARY KEY,
    code varchar(20) UNIQUE NOT NULL,
    name varchar(200) NOT NULL,
    name_ar varchar(200),
    base_currency varchar(3) NOT NULL DEFAULT 'LYD',
    fiscal_year_start int NOT NULL DEFAULT 1,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS users (
    id uuid PRIMARY KEY,
    email varchar(200) UNIQUE NOT NULL,
    password_hash text NOT NULL,
    full_name varchar(200),
    full_name_ar varchar(200),
    is_super_admin boolean NOT NULL DEFAULT false,
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS roles (
    id uuid PRIMARY KEY,
    name varchar(100) UNIQUE NOT NULL,
    name_ar varchar(100),
    description text
);

CREATE TABLE IF NOT EXISTS user_company_memberships (
    id uuid PRIMARY KEY,
    user_id uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    role_id uuid NOT NULL REFERENCES roles(id),
    is_primary boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE (user_id, company_id)
);

CREATE TABLE IF NOT EXISTS accounts (
    id uuid PRIMARY KEY,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    code varchar(20) NOT NULL,
    name varchar(200) NOT NULL,
    name_ar varchar(200),
    parent_id uuid REFERENCES accounts(id),
    account_type varchar(50) NOT NULL,
    nature varchar(10) NOT NULL,
    is_active boolean NOT NULL DEFAULT true,
    balance decimal(18,2) NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE (company_id, code)
);
CREATE INDEX IF NOT EXISTS ix_accounts_company ON accounts(company_id);
CREATE INDEX IF NOT EXISTS ix_accounts_parent ON accounts(parent_id);

-- journal_entries — the master journal table
-- Migrations applied on top of the original 001:
--   003: added rule_id, source columns
--   010: added reverses_entry_id self-referencing FK
CREATE TABLE IF NOT EXISTS journal_entries (
    id uuid PRIMARY KEY,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    entry_number varchar(50) UNIQUE NOT NULL,
    entry_date date NOT NULL,
    narration text,
    status varchar(20) NOT NULL DEFAULT 'draft',
    source varchar(100),
    rule_id uuid,
    reverses_entry_id uuid,                  -- Sprint 18 (Migration 010)
    created_by uuid REFERENCES users(id),
    created_at timestamptz NOT NULL DEFAULT NOW(),
    posted_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_journal_company_date ON journal_entries(company_id);
CREATE INDEX IF NOT EXISTS ix_journal_status ON journal_entries(status);
CREATE INDEX IF NOT EXISTS ix_journal_rule_id ON journal_entries(rule_id);
CREATE INDEX IF NOT EXISTS ix_journal_reverses_entry_id ON journal_entries(reverses_entry_id);

-- journal_lines — lines of each journal entry
CREATE TABLE IF NOT EXISTS journal_lines (
    id uuid PRIMARY KEY,
    journal_entry_id uuid NOT NULL REFERENCES journal_entries(id) ON DELETE CASCADE,
    account_id uuid NOT NULL REFERENCES accounts(id),
    debit decimal(18,2) NOT NULL DEFAULT 0,
    credit decimal(18,2) NOT NULL DEFAULT 0,
    description text,
    line_number int NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_journal_lines_entry ON journal_lines(journal_entry_id);
CREATE INDEX IF NOT EXISTS ix_journal_lines_account ON journal_lines(account_id);


-- ============================================================
-- 2. INVOICING (from Migrations 003, 005, 006)
-- ============================================================

CREATE TABLE IF NOT EXISTS customers (
    id uuid PRIMARY KEY,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    code varchar(50) NOT NULL,
    name varchar(200) NOT NULL,
    name_ar varchar(200),
    tax_id varchar(50),
    phone varchar(50),
    email varchar(200),
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE (company_id, code)
);
-- Note: customers was renamed/refactored into `contacts` by
-- Migration 007. The contacts table is the canonical one now.
-- This customers table may exist on older DBs — leave it alone.

CREATE TABLE IF NOT EXISTS products (
    id uuid PRIMARY KEY,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    code varchar(50) NOT NULL,
    name varchar(200) NOT NULL,
    name_ar varchar(200),
    unit_price decimal(18,2) NOT NULL DEFAULT 0,
    default_tax_rate decimal(5,2) NOT NULL DEFAULT 0,
    is_active boolean NOT NULL DEFAULT true,
    is_demo_data boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE (company_id, code)
);

CREATE TABLE IF NOT EXISTS invoices (
    id uuid PRIMARY KEY,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    invoice_number varchar(50) UNIQUE NOT NULL,
    invoice_type varchar(20) NOT NULL,    -- 'sales' | 'purchase'
    invoice_date date NOT NULL,
    contact_id uuid,                       -- FK to contacts (Sprint 16+)
    customer_name varchar(200),            -- legacy free-text (pre-007)
    subtotal decimal(18,2) NOT NULL DEFAULT 0,
    tax_total decimal(18,2) NOT NULL DEFAULT 0,
    total decimal(18,2) NOT NULL DEFAULT 0,
    status varchar(20) NOT NULL DEFAULT 'draft',
    notes text,
    created_at timestamptz NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_invoices_company ON invoices(company_id);
CREATE INDEX IF NOT EXISTS ix_invoices_contact ON invoices(contact_id);

CREATE TABLE IF NOT EXISTS invoice_lines (
    id uuid PRIMARY KEY,
    invoice_id uuid NOT NULL REFERENCES invoices(id) ON DELETE CASCADE,
    product_id uuid REFERENCES products(id),
    description varchar(500),
    quantity decimal(18,3) NOT NULL DEFAULT 1,
    unit_price decimal(18,2) NOT NULL DEFAULT 0,
    tax_rate decimal(5,2) NOT NULL DEFAULT 0,
    line_total decimal(18,2) NOT NULL DEFAULT 0,
    line_number int NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_invoice_lines_invoice ON invoice_lines(invoice_id);


-- ============================================================
-- 3. PROJECTS (Migration 004, 008)
-- ============================================================

CREATE TABLE IF NOT EXISTS projects (
    id uuid PRIMARY KEY,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    code varchar(50) NOT NULL,
    name varchar(200) NOT NULL,
    name_ar varchar(200),
    description text,
    status varchar(20) NOT NULL DEFAULT 'active',
    start_date date,
    end_date date,
    budget decimal(18,2),
    is_demo_data boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE (company_id, code)
);
CREATE INDEX IF NOT EXISTS ix_projects_company ON projects(company_id);


-- ============================================================
-- 4. CONTACTS (Migration 007)
-- ============================================================

CREATE TABLE IF NOT EXISTS contacts (
    id uuid PRIMARY KEY,
    company_id uuid NOT NULL REFERENCES companies(id) ON DELETE CASCADE,
    type varchar(20) NOT NULL,             -- 'customer' | 'supplier'
    code varchar(50) NOT NULL,
    name varchar(200) NOT NULL,
    name_ar varchar(200),
    tax_id varchar(50),
    phone varchar(50),
    email varchar(200),
    is_active boolean NOT NULL DEFAULT true,
    is_demo_data boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT NOW(),
    UNIQUE (company_id, type, code)
);
CREATE INDEX IF NOT EXISTS ix_contacts_company_type ON contacts(company_id, type);


-- ============================================================
-- 5. BUSINESS RULES (Migration 002+)
-- ============================================================

CREATE TABLE IF NOT EXISTS business_rules (
    id uuid PRIMARY KEY,
    code varchar(50) UNIQUE NOT NULL,
    name varchar(200) NOT NULL,
    name_ar varchar(200),
    description text,
    trigger_event varchar(50) NOT NULL,
    rule_definition jsonb NOT NULL,         -- JSON: lines, mappings, conditions
    is_active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT NOW()
);


-- ============================================================
-- 6. AUDIT LOG
-- ============================================================

CREATE TABLE IF NOT EXISTS audit_logs (
    id uuid PRIMARY KEY,
    user_id uuid REFERENCES users(id),
    action varchar(50) NOT NULL,
    entity_type varchar(50) NOT NULL,
    entity_id uuid,
    payload_json jsonb,
    created_at timestamptz NOT NULL DEFAULT NOW()
);
CREATE INDEX IF NOT EXISTS ix_audit_entity ON audit_logs(entity_type, entity_id);
CREATE INDEX IF NOT EXISTS ix_audit_user ON audit_logs(user_id);


-- ============================================================
-- 7. SELF-REFERENCING FK for reverses_entry_id (Sprint 18)
-- ============================================================
-- The 010 migration adds this with NOT VALID for fast deploy.
-- We do the same here. The FK is enforced for new rows; old
-- rows are not validated (they're not reverses anyway).

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'fk_journal_entries_reverses'
    ) THEN
        ALTER TABLE journal_entries
        ADD CONSTRAINT fk_journal_entries_reverses
        FOREIGN KEY (reverses_entry_id)
        REFERENCES journal_entries(id)
        ON DELETE SET NULL
        NOT VALID;
    END IF;
END $$;


-- ============================================================
-- DONE. This file is safe to re-run.
-- ============================================================

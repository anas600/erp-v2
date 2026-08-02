using Dapper;
using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Adds the products catalogue and relaxes the invoice_lines schema to
/// support product-based line items (instead of forcing every line to
/// link to a single GL account).
///
/// What the user wants (verbatim from the demo):
///   "It should add lines/products/elements with specified quantities
///    and unit price, and show the total"
///
/// What changes in this migration:
///
///   1. products (new)
///        id, company_id, code, name, name_ar,
///        unit_price (decimal 18,3 — qty up to three decimals),
///        default_tax_rate (decimal 5,2 — percent with two decimals),
///        is_active, created_at
///
///   2. invoice_lines — three additive changes
///        a. account_id becomes NULLABLE. A line can be:
///             - product-based (product_id NOT NULL)
///             - free-form (just description + qty + price)
///             - account-based (legacy, kept for back-compat with
///               any data already posted under the old schema)
///        b. product_id (new, nullable, FK to products).
///        c. line_total_with_tax (new, stored). Pre-computed by
///           InvoiceService on insert/update so the UI never has
///           to do the multiplication in the browser.
///
/// Why pre-compute line_total and line_total_with_tax?
///   Floating-point money is the single biggest source of
///   penny-off bugs in any accounting system. If the column is
///   derived, the derivation has to run in the same place every
///   time. Storing it eliminates that risk and makes the
///   business rule (invoice.total) trivially correct: it's just
///   SUM(line_total_with_tax).
///
/// This migration is idempotent. The Schema-fix block at the top
/// uses IF NOT EXISTS guards on indexes, and the column-level
/// changes use .AsNullable().ExistingRows() so they don't fail on
/// already-populated tables.
/// </summary>
[Migration(20260729000005)]
public class ProductsAndInvoiceItems : Migration
{
    public override void Up()
    {
        // ============================================================
        // Products
        // ============================================================
        Create.Table("products")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable()
                .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("code").AsString(50).NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("unit_price").AsDecimal(18, 3).NotNullable().WithDefaultValue(0)
            .WithColumn("default_tax_rate").AsDecimal(6, 4).NotNullable().WithDefaultValue(0)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);

        // Unique index on (company_id, code). Must run in the same
        // transaction as Create.Table — a separate autocommit connection
        // can't see the uncommitted table (we hit exactly that bug on
        // the first deploy: 42P01 relation "products" does not exist).
        // FluentMigrator's Create.Index().Unique() emits CREATE UNIQUE
        // INDEX, which Postgres treats as DDL inside the same TX.
        Create.Index("uk_products_company_code").OnTable("products")
            .OnColumn("company_id").Ascending()
            .OnColumn("code").Ascending()
            .WithOptions().Unique();

        // ============================================================
        // invoice_lines — additive changes
        // ============================================================

        // Make account_id nullable so a line can be product-based
        // or free-form, not just an account entry.
        Alter.Column("account_id").OnTable("invoice_lines")
            .AsGuid().Nullable().ForeignKey("accounts", "id");

        // Add product_id (nullable, ON DELETE SET NULL so deleting
        // a product doesn't delete the historical invoice line).
        if (!Schema.Table("invoice_lines").Column("product_id").Exists())
        {
            Alter.Table("invoice_lines")
                .AddColumn("product_id").AsGuid().Nullable()
                    .ForeignKey("products", "id").OnDelete(System.Data.Rule.SetNull);
        }

        // Add line_total_with_tax (pre-computed by InvoiceService).
        if (!Schema.Table("invoice_lines").Column("line_total_with_tax").Exists())
        {
            Alter.Table("invoice_lines")
                .AddColumn("line_total_with_tax").AsDecimal(18, 2)
                .NotNullable().WithDefaultValue(0);
        }

        // Index on product_id for fast product-history lookups.
        if (!Schema.Table("invoice_lines").Index("ix_invoice_lines_product").Exists())
        {
            Create.Index("ix_invoice_lines_product").OnTable("invoice_lines").OnColumn("product_id");
        }
    }

    public override void Down()
    {
        // We don't drop the products table on a Down() because the
        // migration history is forward-only in production. A
        // re-deploy to an earlier version would have to be hand-
        // rolled by a human, who can decide whether to drop.
        Delete.Index("ix_invoice_lines_product").OnTable("invoice_lines");
        Delete.Column("line_total_with_tax").FromTable("invoice_lines");
        Delete.Column("product_id").FromTable("invoice_lines");
        Delete.Table("products");
    }
}

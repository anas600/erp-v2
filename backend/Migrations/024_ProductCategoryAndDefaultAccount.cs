using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 50 — Add <c>category</c> and <c>default_account_id</c> to products.
///
/// Why:
///   The 6th posting rule (PurchaseInvoiceApprovedForProject) needs to
///   route each invoice line to the right 54xx cost account. The cleanest
///   way is to tag the product itself: "this is materials", "this is
///   labor", "this is equipment_rental". The rule then reads the
///   product's default_account_id (or a category→account mapping) when
///   building the journal entry.
///
///   The same column is useful in OTHER places too — for example, a
///   sales-invoice line can auto-pick a revenue account from the
///   product's category without the user having to choose it.
///
/// After this migration:
///   <c>products.category</c>           — short enum string
///                                       ('materials' | 'labor' |
///                                       'subcontractor' | 'equipment_rental'
///                                       | 'overhead' | 'transport' | 'other')
///   <c>products.default_account_id</c> — FK to <c>accounts.id</c> for the
///                                       L3 control account that the product
///                                       typically posts to. Nullable so
///                                       uncategorised products still work.
/// </summary>
[Migration(20260814000024)]
public class ProductCategoryAndDefaultAccount : Migration
{
    public override void Up()
    {
        if (!Schema.Table("products").Column("category").Exists())
        {
            // Free-form VARCHAR (not an enum type) so the frontend can
            // add new categories without a DDL migration. The
            // application layer enforces the valid set in C#.
            Alter.Table("products")
                .AddColumn("category").AsString(50).Nullable();
        }

        if (!Schema.Table("products").Column("default_account_id").Exists())
        {
            // FK is optional — products without a default account
            // (e.g. the user always picks the account manually) are
            // still allowed. We do NOT use .ForeignKey() here because
            // the constraint FK_products_default_account already exists
            // would conflict. Just adding the column is enough.
            Alter.Table("products")
                .AddColumn("default_account_id").AsGuid().Nullable();
        }

        if (!Schema.Table("products").Index("ix_products_category").Exists())
        {
            Create.Index("ix_products_category").OnTable("products").OnColumn("category");
        }

        if (!Schema.Table("products").Index("ix_products_default_account").Exists())
        {
            Create.Index("ix_products_default_account").OnTable("products").OnColumn("default_account_id");
        }
    }

    public override void Down()
    {
        if (Schema.Table("products").Index("ix_products_default_account").Exists())
        {
            Delete.Index("ix_products_default_account").OnTable("products");
        }
        if (Schema.Table("products").Index("ix_products_category").Exists())
        {
            Delete.Index("ix_products_category").OnTable("products");
        }
        if (Schema.Table("products").Column("default_account_id").Exists())
        {
            Delete.Column("default_account_id").FromTable("products");
        }
        if (Schema.Table("products").Column("category").Exists())
        {
            Delete.Column("category").FromTable("products");
        }
    }
}

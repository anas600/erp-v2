using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Adds the Invoicing module tables: invoices + invoice_lines.
/// </summary>
[Migration(20260729000003)]
public class InvoicingSchema : Migration
{
    public override void Up()
    {
        // ============= INVOICES =============
        Create.Table("invoices")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("invoice_number").AsString(50).NotNullable()
            .WithColumn("invoice_type").AsString(20).NotNullable() // 'purchase' or 'sales'
            .WithColumn("invoice_date").AsDateTime().NotNullable()
            .WithColumn("party_name").AsString(200).NotNullable()
            .WithColumn("party_name_ar").AsString(200).Nullable()
            .WithColumn("party_tax_id").AsString(50).Nullable()
            .WithColumn("notes").AsString(500).Nullable()
            .WithColumn("subtotal").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("tax_amount").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("total").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("draft") // draft, posted, paid, cancelled
            .WithColumn("created_by").AsGuid().Nullable().ForeignKey("users", "id")
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            .WithColumn("posted_at").AsDateTime().Nullable();

        Create.Index("ix_invoices_company_date").OnTable("invoices").OnColumn("company_id");
        Create.Index("ix_invoices_type").OnTable("invoices").OnColumn("invoice_type");

        // ============= INVOICE LINES =============
        Create.Table("invoice_lines")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("invoice_id").AsGuid().NotNullable().ForeignKey("invoices", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("account_id").AsGuid().NotNullable().ForeignKey("accounts", "id")
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("quantity").AsDecimal(18, 3).NotNullable().WithDefaultValue(1)
            .WithColumn("unit_price").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("tax_rate").AsDecimal(8, 4).NotNullable().WithDefaultValue(0)
            .WithColumn("amount").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("line_number").AsInt32().NotNullable().WithDefaultValue(1);

        Create.Index("ix_invoice_lines_invoice").OnTable("invoice_lines").OnColumn("invoice_id");
        Create.Index("ix_invoice_lines_account").OnTable("invoice_lines").OnColumn("account_id");
    }

    public override void Down()
    {
        Delete.Table("invoice_lines");
        Delete.Table("invoices");
    }
}

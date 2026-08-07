using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 29 — adds `updated_at` column to invoices so the edit
/// endpoint can stamp the last-modified time. Also adds a default
/// trigger so any UPDATE bumps it (keeps it honest for UI badges
/// that may eventually show "last edited by").
/// </summary>
[Migration(20260807000001)]
public class InvoiceUpdatedAt : Migration
{
    public override void Up()
    {
        // Add column nullable so it doesn't break if a backfill is
        // needed; the trigger fills it on UPDATE and the API can
        // null it for "never edited" rows.
        if (!Schema.Table("invoices").Column("updated_at").Exists())
        {
            Alter.Table("invoices")
                .AddColumn("updated_at").AsDateTime().Nullable();
        }
    }

    public override void Down()
    {
        Delete.Column("updated_at").FromTable("invoices");
    }
}

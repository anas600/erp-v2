using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 56 — Project Technical Report (التقرير الفني).
///
/// Two additions:
///   1. Five columns on `projects` for the technical report
///      header (status flags + the two auto-computed progress
///      percentages).
///   2. The `contract_line_item_progress` table from migration
///      030 (FMB) gets a column added: the override flag, so the
///      user can manually override the auto-calc after an FMB
///      approval (e.g. an executive decision to mark something
///      100% before the engineer submits).
///
/// Sprint 55 already created `contract_line_item_progress` — this
/// migration is forward-compatible: only adds the override column
/// if the table exists and the column doesn't.
/// </summary>
[Migration(20260815000031)]
public class ProjectTechnicalReport : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) projects — 5 new columns for the technical report
        // ============================================================
        if (!Schema.Table("projects").Column("physical_progress_percent").Exists())
            Alter.Table("projects").AddColumn("physical_progress_percent")
                .AsDecimal(5, 2).NotNullable().WithDefaultValue(0);

        if (!Schema.Table("projects").Column("financial_progress_percent").Exists())
            Alter.Table("projects").AddColumn("financial_progress_percent")
                .AsDecimal(5, 2).NotNullable().WithDefaultValue(0);

        if (!Schema.Table("projects").Column("schedule_status").Exists())
            Alter.Table("projects").AddColumn("schedule_status")
                .AsString(20).NotNullable().WithDefaultValue("on_track");
        // Values: on_track | delayed | ahead | no_schedule | stopped

        if (!Schema.Table("projects").Column("execution_status").Exists())
            Alter.Table("projects").AddColumn("execution_status")
                .AsString(20).NotNullable().WithDefaultValue("in_progress");
        // Values: completed | in_progress | stopped

        if (!Schema.Table("projects").Column("tech_report_date").Exists())
            Alter.Table("projects").AddColumn("tech_report_date")
                .AsDateTime().Nullable();

        // ============================================================
        // 2) contract_line_item_progress — add is_manual_override
        //    so the user can override the FMB-driven value.
        // ============================================================
        if (Schema.Table("contract_line_item_progress").Exists() &&
            !Schema.Table("contract_line_item_progress").Column("is_manual_override").Exists())
        {
            Alter.Table("contract_line_item_progress").AddColumn("is_manual_override")
                .AsBoolean().NotNullable().WithDefaultValue(false);
        }
    }

    public override void Down()
    {
        if (Schema.Table("contract_line_item_progress").Column("is_manual_override").Exists())
            Delete.Column("is_manual_override").FromTable("contract_line_item_progress");

        if (Schema.Table("projects").Column("tech_report_date").Exists())
            Delete.Column("tech_report_date").FromTable("projects");
        if (Schema.Table("projects").Column("execution_status").Exists())
            Delete.Column("execution_status").FromTable("projects");
        if (Schema.Table("projects").Column("schedule_status").Exists())
            Delete.Column("schedule_status").FromTable("projects");
        if (Schema.Table("projects").Column("financial_progress_percent").Exists())
            Delete.Column("financial_progress_percent").FromTable("projects");
        if (Schema.Table("projects").Column("physical_progress_percent").Exists())
            Delete.Column("physical_progress_percent").FromTable("projects");
    }
}

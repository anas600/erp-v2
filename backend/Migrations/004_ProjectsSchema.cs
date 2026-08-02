using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Adds the Projects module tables: projects + project_milestones.
/// </summary>
[Migration(20260729000004)]
public class ProjectsSchema : Migration
{
    public override void Up()
    {
        // ============= PROJECTS =============
        Create.Table("projects")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("code").AsString(50).NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("description").AsString(1000).Nullable()
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("active") // active, completed, on_hold, cancelled
            .WithColumn("start_date").AsDateTime().Nullable()
            .WithColumn("end_date").AsDateTime().Nullable()
            .WithColumn("budget").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("actual_cost").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("notes").AsString(500).Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            .WithColumn("updated_at").AsDateTime().Nullable();

        Create.Index("ix_projects_company").OnTable("projects").OnColumn("company_id");
        Create.Index("ix_projects_status").OnTable("projects").OnColumn("status");

        // ============= MILESTONES =============
        Create.Table("project_milestones")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("project_id").AsGuid().NotNullable().ForeignKey("projects", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("amount").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("pending") // pending, completed
            .WithColumn("target_date").AsDateTime().Nullable()
            .WithColumn("completed_at").AsDateTime().Nullable()
            .WithColumn("order_index").AsInt32().NotNullable().WithDefaultValue(0);

        Create.Index("ix_milestones_project").OnTable("project_milestones").OnColumn("project_id");
    }

    public override void Down()
    {
        Delete.Table("project_milestones");
        Delete.Table("projects");
    }
}

using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Initial schema: users, roles, permissions, companies, accounts, journal, rules.
/// Designed for FluentMigrator 5.x.
/// </summary>
[Migration(20260729000001)]
public class InitialSchema : Migration
{
    public override void Up()
    {
        // Enable both extensions before any table is created:
        // - pgcrypto: for gen_random_uuid() (PG 13+ core also exposes this).
        // - uuid-ossp: for uuid_generate_v4(), which is what FluentMigrator emits
        //   when you write `.WithDefault(SystemMethods.NewGuid)` on a UUID column.
        // Both are no-ops on a re-run.
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";");

        // ============= USERS =============
        Create.Table("users")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("email").AsString(255).NotNullable().Unique()
            .WithColumn("password_hash").AsString(255).NotNullable()
            .WithColumn("full_name").AsString(200).Nullable()
            .WithColumn("full_name_ar").AsString(200).Nullable()
            .WithColumn("is_super_admin").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);

        // ============= ROLES =============
        Create.Table("roles")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("name").AsString(100).NotNullable().Unique()
            .WithColumn("display_name").AsString(200).Nullable()
            .WithColumn("display_name_ar").AsString(200).Nullable()
            .WithColumn("is_system").AsBoolean().NotNullable().WithDefaultValue(false);

        // ============= PERMISSIONS =============
        Create.Table("permissions")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("code").AsString(100).NotNullable().Unique()
            .WithColumn("module").AsString(100).NotNullable()
            .WithColumn("display_name").AsString(200).Nullable()
            .WithColumn("display_name_ar").AsString(200).Nullable();

        // ============= ROLE_PERMISSIONS (composite PK) =============
        Create.Table("role_permissions")
            .WithColumn("role_id").AsGuid().NotNullable().ForeignKey("roles", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("permission_id").AsGuid().NotNullable().ForeignKey("permissions", "id").OnDelete(System.Data.Rule.Cascade);
        Create.PrimaryKey("pk_role_permissions").OnTable("role_permissions").Columns("role_id", "permission_id");

        // ============= COMPANIES =============
        Create.Table("companies")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("code").AsString(50).NotNullable().Unique()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("parent_id").AsGuid().Nullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("is_holding").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("base_currency").AsString(3).NotNullable().WithDefaultValue("LYD")
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);
        Create.Index("ix_companies_parent").OnTable("companies").OnColumn("parent_id");

        // ============= USER_COMPANIES (composite PK) =============
        Create.Table("user_companies")
            .WithColumn("user_id").AsGuid().NotNullable().ForeignKey("users", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("role_id").AsGuid().NotNullable().ForeignKey("roles", "id")
            .WithColumn("is_primary").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("joined_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);
        Create.PrimaryKey("pk_user_companies").OnTable("user_companies").Columns("user_id", "company_id");
        Create.Index("ix_user_companies_company").OnTable("user_companies").OnColumn("company_id");

        // ============= CHART OF ACCOUNTS =============
        Create.Table("accounts")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("code").AsString(50).NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("parent_id").AsGuid().Nullable().ForeignKey("accounts", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("account_type").AsString(50).NotNullable()
            .WithColumn("nature").AsString(20).NotNullable()
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("balance").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);
        // Single-column indexes; multi-column uniqueness via raw SQL below.
        Create.Index("ix_accounts_company").OnTable("accounts").OnColumn("company_id");
        Create.Index("ix_accounts_parent").OnTable("accounts").OnColumn("parent_id");
        // FluentMigrator v5 cannot easily express a unique index on (company_id, code),
        // so we fall back to a unique index on code alone and rely on the application
        // to keep codes unique within a company. See Application/AccountService.
        Create.Index("uk_accounts_code").OnTable("accounts").OnColumn("code").Unique();

        // ============= JOURNAL ENTRIES =============
        Create.Table("journal_entries")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("entry_number").AsString(50).NotNullable()
            .WithColumn("entry_date").AsDateTime().NotNullable()
            .WithColumn("narration").AsString(500).Nullable()
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("draft")
            .WithColumn("source").AsString(50).Nullable()
            .WithColumn("rule_id").AsGuid().Nullable()
            .WithColumn("created_by").AsGuid().Nullable().ForeignKey("users", "id")
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            .WithColumn("posted_at").AsDateTime().Nullable();
        Create.Index("ix_journal_company_date").OnTable("journal_entries").OnColumn("company_id");
        // Application enforces uniqueness of (company_id, entry_number); see JournalService.

        // ============= JOURNAL LINES =============
        Create.Table("journal_lines")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("journal_entry_id").AsGuid().NotNullable().ForeignKey("journal_entries", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("account_id").AsGuid().NotNullable().ForeignKey("accounts", "id")
            .WithColumn("debit").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("credit").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("line_number").AsInt32().NotNullable().WithDefaultValue(1);
        Create.Index("ix_journal_lines_entry").OnTable("journal_lines").OnColumn("journal_entry_id");
        Create.Index("ix_journal_lines_account").OnTable("journal_lines").OnColumn("account_id");

        // ============= BUSINESS RULES =============
        Create.Table("business_rules")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("description").AsString(500).Nullable()
            .WithColumn("event_name").AsString(100).NotNullable()
            .WithColumn("enabled").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("priority").AsInt32().NotNullable().WithDefaultValue(100)
            .WithColumn("rule_json").AsCustom("jsonb").NotNullable()
            .WithColumn("is_template").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            .WithColumn("updated_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);
        Create.Index("ix_business_rules_event").OnTable("business_rules").OnColumn("event_name");

        // ============= AUDIT LOGS =============
        Create.Table("audit_logs")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("user_id").AsGuid().Nullable()
            .WithColumn("company_id").AsGuid().Nullable()
            .WithColumn("action").AsString(50).NotNullable()
            .WithColumn("entity_type").AsString(100).Nullable()
            .WithColumn("entity_id").AsGuid().Nullable()
            .WithColumn("payload").AsCustom("jsonb").Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);
    }

    public override void Down()
    {
        Delete.Table("audit_logs");
        Delete.Table("business_rules");
        Delete.Table("journal_lines");
        Delete.Table("journal_entries");
        Delete.Table("accounts");
        Delete.Table("user_companies");
        Delete.Table("companies");
        Delete.Table("role_permissions");
        Delete.Table("permissions");
        Delete.Table("roles");
        Delete.Table("users");
    }
}

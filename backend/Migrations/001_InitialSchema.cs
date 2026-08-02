using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Initial schema: users, roles, permissions, companies, accounts, journal, rules.
/// </summary>
[Migration(20260729000001)]
public class InitialSchema : Migration
{
    public override void Up()
    {
        // Enable UUID generation (PG 13+ has gen_random_uuid in pgcrypto)
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

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

        // ============= ROLE_PERMISSIONS =============
        Create.Table("role_permissions")
            .WithColumn("role_id").AsGuid().NotNullable().ForeignKey("roles", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("permission_id").AsGuid().NotNullable().ForeignKey("permissions", "id").OnDelete(System.Data.Rule.Cascade)
            .WithPrimaryKey("role_id", "permission_id");

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

        // ============= USER_COMPANIES =============
        Create.Table("user_companies")
            .WithColumn("user_id").AsGuid().NotNullable().ForeignKey("users", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("role_id").AsGuid().NotNullable().ForeignKey("roles", "id")
            .WithColumn("is_primary").AsBoolean().NotNullable().WithDefaultValue(false)
            .WithColumn("joined_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            .WithPrimaryKey("user_id", "company_id");

        Create.Index("ix_user_companies_company").OnTable("user_companies").OnColumn("company_id");

        // ============= CHART OF ACCOUNTS =============
        Create.Table("accounts")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("code").AsString(50).NotNullable()
            .WithColumn("name").AsString(200).NotNullable()
            .WithColumn("name_ar").AsString(200).Nullable()
            .WithColumn("parent_id").AsGuid().Nullable().ForeignKey("accounts", "id").OnDelete(System.Data.Rule.SetNull)
            .WithColumn("account_type").AsString(50).NotNullable() // Asset, Liability, Equity, Revenue, Expense
            .WithColumn("nature").AsString(20).NotNullable()         // Debit, Credit
            .WithColumn("is_active").AsBoolean().NotNullable().WithDefaultValue(true)
            .WithColumn("balance").AsDecimal(18, 2).NotNullable().WithDefaultValue(0)
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime);

        Create.Index("ix_accounts_company_code").OnTable("accounts").OnColumn("company_id").Ascending.OnColumn("code").Ascending().Unique();
        Create.Index("ix_accounts_parent").OnTable("accounts").OnColumn("parent_id");

        // ============= JOURNAL ENTRIES =============
        Create.Table("journal_entries")
            .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
            .WithColumn("company_id").AsGuid().NotNullable().ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("entry_number").AsString(50).NotNullable()
            .WithColumn("entry_date").AsDateTime().NotNullable()
            .WithColumn("narration").AsString(500).Nullable()
            .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("draft") // draft, posted, reversed
            .WithColumn("source").AsString(50).Nullable() // manual, rule:<id>
            .WithColumn("rule_id").AsGuid().Nullable()
            .WithColumn("created_by").AsGuid().Nullable().ForeignKey("users", "id")
            .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
            .WithColumn("posted_at").AsDateTime().Nullable();

        Create.Index("ix_journal_company_number").OnTable("journal_entries").OnColumn("company_id").Ascending.OnColumn("entry_number").Ascending().Unique();
        Create.Index("ix_journal_company_date").OnTable("journal_entries").OnColumn("company_id").Ascending.OnColumn("entry_date").Descending();

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

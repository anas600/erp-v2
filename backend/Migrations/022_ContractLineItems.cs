using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 38 — Bill of Quantities (BOQ) + Contract Variations.
///
/// Replaces the %-based billing calculation with an item-based one.
/// Each contract has a list of <c>contract_line_items</c> (the BOQ
/// — مقايسة الكميات / جدول الكميات). Each billing claims quantities
/// of those items in <c>billing_line_items</c>. The system sums
/// amounts and derives <c>work_completed_percent</c> from the line
/// items automatically.
///
/// <para>
/// Contract <b>variations</b> (أوامر تغيير) are separately tracked
/// in <c>contract_variations</c> + <c>contract_variation_items</c>.
/// An APPROVED variation adjusts the effective contract value
/// (addition or subtraction), so all subsequent billings see the
/// updated total.
/// </para>
///
/// <para>
/// <b>Force-migration of existing data</b> (Sprint 36 → 38):
/// every existing contract gets one synthetic <c>lump</c> line item
/// whose total_price = contract.contract_value. Every existing
/// (non-CANCELLED) billing gets one matching <c>billing_line_item</c>
/// whose quantity_cumulative = work_completed_percent (treating the
/// synthetic line item as a % gauge). This keeps the gross / net / %
/// numbers bit-identical to what Sprint 36 produced.
/// </para>
///
/// <para>
/// The migration is fully idempotent: every column / table / index
/// is guarded by Exists() checks. Re-running it is a no-op. The
/// force-migrate INSERTs also use NOT EXISTS guards so re-runs do
/// not duplicate rows.
/// </para>
/// </summary>
[Migration(20260807000007)]
public class ContractLineItems : Migration
{
    public override void Up()
    {
        // ============================================================
        // 1) contract_line_items
        //    The BOQ (مقايسة الكميات) per contract. One row per
        //    measurable item — m3 of concrete, m2 of plaster, ton of
        //    steel, etc. The "lump" unit is the catch-all for items
        //    you can't easily measure by unit.
        // ============================================================
        if (!Schema.Table("contract_line_items").Exists())
        {
            Create.Table("contract_line_items")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("contract_id").AsGuid().NotNullable()
                    .ForeignKey("contracts", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("line_number").AsInt32().NotNullable()
                .WithColumn("description").AsString(int.MaxValue).NotNullable()
                .WithColumn("unit").AsString(20).NotNullable()
                .WithColumn("custom_unit").AsString(20).Nullable()
                .WithColumn("quantity").AsDecimal(18, 3).NotNullable()
                .WithColumn("unit_price").AsDecimal(18, 3).NotNullable()
                .WithColumn("total_price").AsDecimal(18, 3).NotNullable()
                .WithColumn("notes").AsString(int.MaxValue).Nullable()
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
                .WithColumn("updated_at").AsDateTime().Nullable();

            // line_number is unique within a contract — two items with
            // the same number would be ambiguous on the UI.
            Create.Index("ux_line_items_contract_line_number")
                .OnTable("contract_line_items")
                .OnColumn("contract_id").Ascending()
                .OnColumn("line_number").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table("contract_line_items").Index("ix_line_items_contract").Exists())
        {
            Create.Index("ix_line_items_contract")
                .OnTable("contract_line_items")
                .OnColumn("contract_id");
        }

        // ============================================================
        // 2) billing_line_items
        //    The claim per billing. Each row ties a billing to a
        //    contract line item, recording the cumulative quantity
        //    done and the resulting amount. The amount column is
        //    quantity_cumulative * unit_price (snapshot from the
        //    line item at billing-creation time).
        // ============================================================
        if (!Schema.Table("billing_line_items").Exists())
        {
            Create.Table("billing_line_items")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("billing_id").AsGuid().NotNullable()
                    .ForeignKey("progress_billings", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("line_item_id").AsGuid().NotNullable()
                    .ForeignKey("contract_line_items", "id")
                .WithColumn("quantity_this_period").AsDecimal(18, 3).NotNullable()
                .WithColumn("quantity_previous").AsDecimal(18, 3).NotNullable().WithDefaultValue(0)
                .WithColumn("quantity_cumulative").AsDecimal(18, 3).NotNullable()
                .WithColumn("unit_price").AsDecimal(18, 3).NotNullable()
                .WithColumn("amount").AsDecimal(18, 3).NotNullable()
                .WithColumn("notes").AsString(int.MaxValue).Nullable();

            // One row per (billing, line_item) — re-asserting the
            // quantity for an item on a billing is an UPDATE, not an
            // INSERT.
            Create.Index("ux_billing_line_items_billing_line")
                .OnTable("billing_line_items")
                .OnColumn("billing_id").Ascending()
                .OnColumn("line_item_id").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table("billing_line_items").Index("ix_billing_line_items_billing").Exists())
        {
            Create.Index("ix_billing_line_items_billing")
                .OnTable("billing_line_items")
                .OnColumn("billing_id");
        }
        if (!Schema.Table("billing_line_items").Index("ix_billing_line_items_line_item").Exists())
        {
            Create.Index("ix_billing_line_items_line_item")
                .OnTable("billing_line_items")
                .OnColumn("line_item_id");
        }

        // ============================================================
        // 3) contract_variations (أوامر التغيير)
        //    One variation per change-order. The status follows:
        //      DRAFT    → being assembled, items can be added
        //      APPROVED → effective contract value includes this
        //      REJECTED → archived, no accounting effect
        // ============================================================
        if (!Schema.Table("contract_variations").Exists())
        {
            Create.Table("contract_variations")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("contract_id").AsGuid().NotNullable()
                    .ForeignKey("contracts", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("variation_number").AsInt32().NotNullable()
                .WithColumn("description").AsString(int.MaxValue).NotNullable()
                .WithColumn("variation_date").AsDateTime().NotNullable()
                .WithColumn("status").AsString(20).NotNullable().WithDefaultValue("DRAFT")
                .WithColumn("approved_at").AsDateTime().Nullable()
                .WithColumn("approved_by").AsGuid().Nullable()
                    .ForeignKey("users", "id")
                .WithColumn("notes").AsString(int.MaxValue).Nullable()
                .WithColumn("created_at").AsDateTime().NotNullable().WithDefaultValue(SystemMethods.CurrentDateTime)
                .WithColumn("updated_at").AsDateTime().Nullable();

            Create.Index("ux_variations_contract_number")
                .OnTable("contract_variations")
                .OnColumn("contract_id").Ascending()
                .OnColumn("variation_number").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table("contract_variations").Index("ix_variations_contract").Exists())
        {
            Create.Index("ix_variations_contract")
                .OnTable("contract_variations")
                .OnColumn("contract_id");
        }
        if (!Schema.Table("contract_variations").Index("ix_variations_status").Exists())
        {
            Create.Index("ix_variations_status")
                .OnTable("contract_variations")
                .OnColumn("status");
        }

        // ============================================================
        // 4) contract_variation_items
        //    The line items inside a variation. Each is_addition=true
        //    adds to the effective contract value, is_addition=false
        //    subtracts (omitted work). total_price = qty * unit_price.
        // ============================================================
        if (!Schema.Table("contract_variation_items").Exists())
        {
            Create.Table("contract_variation_items")
                .WithColumn("id").AsGuid().PrimaryKey().WithDefault(SystemMethods.NewGuid)
                .WithColumn("company_id").AsGuid().NotNullable()
                    .ForeignKey("companies", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("variation_id").AsGuid().NotNullable()
                    .ForeignKey("contract_variations", "id").OnDelete(System.Data.Rule.Cascade)
                .WithColumn("line_number").AsInt32().NotNullable()
                .WithColumn("description").AsString(int.MaxValue).NotNullable()
                .WithColumn("unit").AsString(20).NotNullable()
                .WithColumn("custom_unit").AsString(20).Nullable()
                .WithColumn("quantity").AsDecimal(18, 3).NotNullable()
                .WithColumn("unit_price").AsDecimal(18, 3).NotNullable()
                .WithColumn("total_price").AsDecimal(18, 3).NotNullable()
                .WithColumn("is_addition").AsBoolean().NotNullable().WithDefaultValue(true)
                .WithColumn("notes").AsString(int.MaxValue).Nullable()
                .WithColumn("created_at").AsDateTime().Nullable();

            Create.Index("ux_variation_items_variation_number")
                .OnTable("contract_variation_items")
                .OnColumn("variation_id").Ascending()
                .OnColumn("line_number").Ascending()
                .WithOptions().Unique();
        }

        if (!Schema.Table("contract_variation_items").Index("ix_variation_items_variation").Exists())
        {
            Create.Index("ix_variation_items_variation")
                .OnTable("contract_variation_items")
                .OnColumn("variation_id");
        }

        // ============================================================
        // 5) FORCE-MIGRATE existing data
        //
        //    For each existing contract, insert ONE synthetic
        //    "lump" line item whose total_price = contract_value.
        //    The Arabic description explains it's a pre-BOQ catch-all.
        //
        //    For each existing (non-CANCELLED) billing, insert ONE
        //    matching billing_line_item whose quantity_cumulative =
        //    work_completed_percent (the synthetic lump line item is
        //    treated as a % gauge — qty=1, unit_price=contract_value,
        //    so qty*unit_price == contract_value * percent ==
        //    gross_amount).
        // ============================================================
        Execute.Sql(@"
            INSERT INTO contract_line_items (
                id, company_id, contract_id, line_number,
                description, unit, quantity, unit_price, total_price
            )
            SELECT gen_random_uuid(), c.company_id, c.id, 1,
                'أعمال متنوعة (مستحقة قبل استخدام BOQ)', 'lump', 1,
                c.contract_value, c.contract_value
            FROM contracts c
            WHERE NOT EXISTS (
                SELECT 1 FROM contract_line_items WHERE contract_id = c.id
            );
        ");

        Execute.Sql(@"
            INSERT INTO billing_line_items (
                id, company_id, billing_id, line_item_id,
                quantity_this_period, quantity_previous, quantity_cumulative,
                unit_price, amount
            )
            SELECT gen_random_uuid(), b.company_id, b.id, li.id,
                b.work_completed_percent, 0, b.work_completed_percent,
                li.unit_price, b.gross_amount
            FROM progress_billings b
            JOIN contract_line_items li ON li.contract_id = b.contract_id
            WHERE NOT EXISTS (
                SELECT 1 FROM billing_line_items WHERE billing_id = b.id
            )
            AND b.status != 'CANCELLED';
        ");
    }

    public override void Down()
    {
        // Reverse in opposite order. Drop indexes first, then tables.
        // Note: dropping the tables also drops the force-migrated
        // rows (the synthetic line items and billing_line_items), so
        // the DB returns to its pre-Sprint 38 shape for that data.

        if (Schema.Table("contract_variation_items").Index("ix_variation_items_variation").Exists())
            Delete.Index("ix_variation_items_variation").OnTable("contract_variation_items");
        if (Schema.Table("contract_variation_items").Index("ux_variation_items_variation_number").Exists())
            Delete.Index("ux_variation_items_variation_number").OnTable("contract_variation_items");
        if (Schema.Table("contract_variation_items").Exists())
            Delete.Table("contract_variation_items");

        if (Schema.Table("contract_variations").Index("ix_variations_status").Exists())
            Delete.Index("ix_variations_status").OnTable("contract_variations");
        if (Schema.Table("contract_variations").Index("ix_variations_contract").Exists())
            Delete.Index("ix_variations_contract").OnTable("contract_variations");
        if (Schema.Table("contract_variations").Index("ux_variations_contract_number").Exists())
            Delete.Index("ux_variations_contract_number").OnTable("contract_variations");
        if (Schema.Table("contract_variations").Exists())
            Delete.Table("contract_variations");

        if (Schema.Table("billing_line_items").Index("ix_billing_line_items_line_item").Exists())
            Delete.Index("ix_billing_line_items_line_item").OnTable("billing_line_items");
        if (Schema.Table("billing_line_items").Index("ix_billing_line_items_billing").Exists())
            Delete.Index("ix_billing_line_items_billing").OnTable("billing_line_items");
        if (Schema.Table("billing_line_items").Index("ux_billing_line_items_billing_line").Exists())
            Delete.Index("ux_billing_line_items_billing_line").OnTable("billing_line_items");
        if (Schema.Table("billing_line_items").Exists())
            Delete.Table("billing_line_items");

        if (Schema.Table("contract_line_items").Index("ix_line_items_contract").Exists())
            Delete.Index("ix_line_items_contract").OnTable("contract_line_items");
        if (Schema.Table("contract_line_items").Index("ux_line_items_contract_line_number").Exists())
            Delete.Index("ux_line_items_contract_line_number").OnTable("contract_line_items");
        if (Schema.Table("contract_line_items").Exists())
            Delete.Table("contract_line_items");
    }
}

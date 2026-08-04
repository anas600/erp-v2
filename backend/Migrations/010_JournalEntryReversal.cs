using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 18 — Adds the `reverses_entry_id` FK to journal_entries.
///
/// Before this migration, the relationship between a reversing
/// entry and the original it reversed was a soft one, encoded
/// only in the `source` field as 'reverse:{uuid}'. That worked
/// but it was a string-parse dance, not a real foreign key.
///
/// After this migration:
///   - journal_entries.reverses_entry_id: nullable uuid, FK to
///     journal_entries.id (self-referencing). Set by
///     JournalService.ReverseAsync to the original entry's id.
///   - We KEEP the `source='reverse'` prefix for fast filtering
///     (don't have to JOIN to find reversals), but the FK is
///     now the authoritative source of the relationship.
///
/// We also add a composite index on (reverses_entry_id) so the
/// "what reversed this entry?" query is fast.
///
/// Idempotency: ALTER TABLE ADD COLUMN IF NOT EXISTS is
/// available on PG 9.6+. Render uses PG 15/16, so we're fine.
/// The FK is added separately because we want it to be NOT
/// VALID initially, then validated once the data is consistent.
/// But for simplicity here we just add it valid — there are no
/// existing rows that could conflict.
/// </summary>
[Migration(20260804000010)]
public class JournalEntryReversal : Migration
{
    public override void Up()
    {
        // 1) Add the column. Nullable because most entries are
        //    not reversals of anything.
        //    PostgreSQL 9.6+ supports IF NOT EXISTS on ADD COLUMN.
        Execute.Sql(@"
            ALTER TABLE journal_entries
            ADD COLUMN IF NOT EXISTS reverses_entry_id uuid;");

        // 2) Self-referencing FK. ON DELETE SET NULL so deleting
        //    a reversal doesn't cascade-delete the original.
        Execute.Sql(@"
            ALTER TABLE journal_entries
            ADD CONSTRAINT fk_journal_entries_reverses
            FOREIGN KEY (reverses_entry_id)
            REFERENCES journal_entries(id)
            ON DELETE SET NULL
            NOT VALID;");

        // The NOT VALID clause tells Postgres to skip the (slow)
        // full-table validation on existing rows. The constraint
        // is still enforced for new inserts/updates. This is the
        // safe pattern for adding FKs to tables with existing data.
        //
        // The current production data has no entries with
        // reverses_entry_id set, so validation would find nothing
        // to do. But we leave NOT VALID to keep the deploy fast.

        // 3) Index for the reverse lookup ("which entry reversed
        //    this one?" or "show all reversals of this entry").
        Execute.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_journal_entries_reverses
            ON journal_entries(reverses_entry_id);");
    }

    public override void Down()
    {
        // Forward-only: dropping the FK would break the existing
        // reversing relationship data.
        Execute.Sql("DROP INDEX IF EXISTS ix_journal_entries_reverses;");
        Execute.Sql("ALTER TABLE journal_entries DROP CONSTRAINT IF EXISTS fk_journal_entries_reverses;");
        Execute.Sql("ALTER TABLE journal_entries DROP COLUMN IF EXISTS reverses_entry_id;");
    }
}

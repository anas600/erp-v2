using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Sprint 31 — locks L3 accounts as non-postable per the user's new
/// design decision. This migration is data-only (no schema change) and
/// simply flips any L3 account that was marked is_postable=true to false.
///
/// Background: Sprint 26 introduced is_postable with these rules:
///   L1/L2: must be false (grouping)
///   L3:    user choice (could be true OR false)
///   L4:    must be true (detail)
///
/// The user has now decided L3 must ALWAYS be non-postable (Option A
/// from the original discussion, finally chosen). This migration locks
/// down the existing data.
///
/// The AccountService.CreateAsync now also enforces this at the API
/// level (rejects any isPostable=true on L3), so this migration just
/// fixes the existing rows.
/// </summary>
[Migration(20260807000002)]
public class LockL3NonPostable : Migration
{
    public override void Up()
    {
        // Defensive: only flip L3 rows that were erroneously postable.
        // We don't touch L1/L2/L4 — those have their own rules.
        Execute.Sql(@"
            UPDATE accounts
            SET is_postable = false
            WHERE level = 3 AND is_postable = true;");
    }

    public override void Down()
    {
        // Reversal is dangerous because we don't know which L3 accounts
        // were legitimately postable before. We refuse to auto-restore.
        throw new NotSupportedException(
            "Cannot auto-reverse LockL3NonPostable — re-mark L3 accounts manually if needed.");
    }
}

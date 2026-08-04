using FluentMigrator;

namespace ErpV2.Migrations;

/// <summary>
/// Migration 011 — Promote admin@holding.ly to is_super_admin = true.
///
/// Background:
///   The original seed (Migration 002) created admin@holding.ly with
///   is_super_admin = false. That was a bug — "Super Admin" by name
///   but not in flag. The user could log in and see all companies
///   (because they're a super_admin *role* member of all 3), but
///   they did NOT have the global is_super_admin flag set.
///
/// Why this matters for Sprint 19 (Admin endpoints):
///   The new POST /api/admin/cleanup-transactions endpoint
///   requires is_super_admin = true. The flag is the only thing
///   that distinguishes the global admin (who can do destructive
///   maintenance) from a per-company super_admin-role member.
///
/// Idempotency: ON CONFLICT semantics — UPDATE WHERE email matches,
/// no-op if already set. Safe to re-run.
/// </summary>
[Migration(20260804000011)]
public class EnsureSuperAdmin : Migration
{
    public override void Up()
    {
        Execute.Sql(@"
            UPDATE users
            SET is_super_admin = true
            WHERE email = 'admin@holding.ly'
              AND is_super_admin = false;");
    }

    public override void Down()
    {
        // No-op. We don't want to silently demote the admin
        // on rollback — that could lock them out of the system.
    }
}

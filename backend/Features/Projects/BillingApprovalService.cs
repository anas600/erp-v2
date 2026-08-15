using System.Security.Claims;
using Dapper;
using ErpV2.Common;
using Microsoft.AspNetCore.Http;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 57 — Service for the 4-party approval workflow.
///
/// On first GET for a billing, we LAZY-CREATE the 4 approval
/// rows (one per role) in PENDING state. This keeps the billing
/// creation flow unchanged (no migration of existing billings
/// needed) and avoids forcing the user to click "Initialize"
/// before seeing the approval panel.
///
/// Approve / reject updates a single row. The service enforces:
///   - The role must be one of the 4 valid roles
///   - The approver's user id is taken from the JWT (not the body)
///   - Once a role is 'rejected', it can be reset to 'pending' by
///     an admin (re-opening the approval)
///   - When the last of the 4 is approved, the billing's
///     final_approved_at column is set (atomic).
/// </summary>
public class BillingApprovalService
{
    private readonly IDbConnectionFactory _db;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<BillingApprovalService> _log;

    public BillingApprovalService(
        IDbConnectionFactory db,
        IHttpContextAccessor httpContext,
        ILogger<BillingApprovalService> log)
    {
        _db = db;
        _httpContext = httpContext;
        _log = log;
    }

    /// <summary>
    /// Lazy-create the 4 approval rows for a billing if they
    /// don't exist. Idempotent: returns the existing rows on
    /// re-call.
    /// </summary>
    public async Task<List<BillingApprovalDto>> ListAsync(Guid billingId)
    {
        using var conn = _db.CreateConnection();

        // Verify the billing exists and capture company_id
        var companyId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
            SELECT company_id FROM progress_billings WHERE id = @id;",
            new { id = billingId });
        if (companyId is null)
            throw new InvalidOperationException("المستخلص غير موجود");

        // Seed any missing roles (idempotent)
        await conn.ExecuteAsync(@"
            INSERT INTO progress_billing_approvals
                (id, company_id, billing_id, role, status, created_at)
            SELECT gen_random_uuid(), @companyId, @billingId, r.role, 'pending', NOW()
            FROM (VALUES
                ('contractor'),
                ('consultant'),
                ('pmo'),
                ('owner')
            ) AS r(role)
            WHERE NOT EXISTS (
                SELECT 1 FROM progress_billing_approvals
                WHERE billing_id = @billingId AND role = r.role
            );",
            new { companyId, billingId });

        // Read all 4 rows (always returns 4 because we just seeded missing ones)
        var rows = (await conn.QueryAsync<ApprovalRow>(@"
            SELECT id, company_id, billing_id, role, approver_user_id,
                   approver_name, status, approved_at, rejection_reason,
                   notes, created_at, updated_at
            FROM progress_billing_approvals
            WHERE billing_id = @billingId
            ORDER BY CASE role
                WHEN 'contractor' THEN 1
                WHEN 'consultant' THEN 2
                WHEN 'pmo'        THEN 3
                WHEN 'owner'      THEN 4
                ELSE 5
            END;",
            new { billingId })).ToList();

        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Mark a role as 'approved'. The approver comes from the JWT.
    /// If all 4 are now approved, the billing's final_approved_at
    /// is set in the same transaction.
    /// </summary>
    public async Task<BillingApprovalDto> ApproveAsync(
        Guid billingId, string role, string? notes)
    {
        if (!BillingApprovalRoles.All.Contains(role))
            throw new ArgumentException(
                $"الدور '{role}' غير معروف. الأدوار الصالحة: " +
                string.Join(", ", BillingApprovalRoles.All));

        var (userId, userName) = GetCurrentUser();
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // UPSERT: insert the row if missing (shouldn't happen since
            // ListAsync seeds them, but just in case) then mark approved.
            await conn.ExecuteAsync(@"
                INSERT INTO progress_billing_approvals
                    (id, company_id, billing_id, role, approver_user_id,
                     approver_name, status, approved_at, notes, created_at)
                SELECT gen_random_uuid(), b.company_id, b.id, @role, @userId,
                       @userName, 'approved', NOW(), @notes, NOW()
                FROM progress_billings b
                WHERE b.id = @billingId
                ON CONFLICT (billing_id, role) DO UPDATE SET
                    approver_user_id = EXCLUDED.approver_user_id,
                    approver_name    = EXCLUDED.approver_name,
                    status           = 'approved',
                    approved_at      = NOW(),
                    rejection_reason = NULL,
                    notes            = EXCLUDED.notes,
                    updated_at       = NOW();",
                new { billingId, role, userId, userName, notes }, tx);

            // If all 4 are now approved, set final_approved_at on the billing
            var allApproved = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM progress_billing_approvals
                WHERE billing_id = @billingId AND status = 'approved';",
                new { billingId }, tx);
            if (allApproved == 4)
            {
                await conn.ExecuteAsync(@"
                    UPDATE progress_billings
                    SET final_approved_at = NOW(), updated_at = NOW()
                    WHERE id = @billingId AND final_approved_at IS NULL;",
                    new { billingId }, tx);
            }

            tx.Commit();
            _log.LogInformation(
                "Approval: billing={BillingId} role={Role} by={UserName} (total approved: {N}/4)",
                billingId, role, userName, allApproved);

            var updated = (await ListAsync(billingId)).First(a => a.Role == role);
            return updated;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Mark a role as 'rejected'. The reason is required.
    /// </summary>
    public async Task<BillingApprovalDto> RejectAsync(
        Guid billingId, string role, string reason, string? notes)
    {
        if (!BillingApprovalRoles.All.Contains(role))
            throw new ArgumentException($"الدور '{role}' غير معروف.");

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("سبب الرفض مطلوب");

        var (userId, userName) = GetCurrentUser();
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // Make sure the row exists first (seed if needed)
            var companyId = await conn.QuerySingleOrDefaultAsync<Guid?>(@"
                SELECT company_id FROM progress_billings WHERE id = @id;",
                new { id = billingId }, tx);
            if (companyId is null)
                throw new InvalidOperationException("المستخلص غير موجود");

            await conn.ExecuteAsync(@"
                INSERT INTO progress_billing_approvals
                    (id, company_id, billing_id, role, status, created_at)
                VALUES (gen_random_uuid(), @companyId, @billingId, @role, 'pending', NOW())
                ON CONFLICT (billing_id, role) DO NOTHING;",
                new { companyId, billingId, role }, tx);

            await conn.ExecuteAsync(@"
                UPDATE progress_billing_approvals
                SET status = 'rejected',
                    approver_user_id = @userId,
                    approver_name    = @userName,
                    approved_at      = NULL,
                    rejection_reason = @reason,
                    notes            = @notes,
                    updated_at       = NOW()
                WHERE billing_id = @billingId AND role = @role;",
                new { billingId, role, userId, userName, reason, notes }, tx);

            // Reset final_approved_at if it was set (any rejection invalidates the final)
            await conn.ExecuteAsync(@"
                UPDATE progress_billings
                SET final_approved_at = NULL, updated_at = NOW()
                WHERE id = @billingId;",
                new { billingId }, tx);

            tx.Commit();
            _log.LogInformation(
                "Rejection: billing={BillingId} role={Role} by={UserName} reason={Reason}",
                billingId, role, userName, reason);

            return (await ListAsync(billingId)).First(a => a.Role == role);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Reset a role back to 'pending' (admin only). Used when the
    /// approver wants to "undo" their decision before the next stage.
    /// </summary>
    public async Task<BillingApprovalDto> ResetAsync(Guid billingId, string role)
    {
        if (!BillingApprovalRoles.All.Contains(role))
            throw new ArgumentException($"الدور '{role}' غير معروف.");

        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            UPDATE progress_billing_approvals
            SET status = 'pending',
                approver_user_id = NULL,
                approver_name = NULL,
                approved_at = NULL,
                rejection_reason = NULL,
                notes = NULL,
                updated_at = NOW()
            WHERE billing_id = @billingId AND role = @role;",
            new { billingId, role });

        // Also reset final_approved_at if it was set
        await conn.ExecuteAsync(@"
            UPDATE progress_billings
            SET final_approved_at = NULL, updated_at = NOW()
            WHERE id = @billingId;",
            new { billingId });

        return (await ListAsync(billingId)).First(a => a.Role == role);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private (Guid? userId, string? userName) GetCurrentUser()
    {
        var ctx = _httpContext.HttpContext;
        if (ctx is null) return (null, null);
        var idStr = ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? ctx.User.FindFirst("sub")?.Value;
        Guid? userId = Guid.TryParse(idStr, out var g) ? g : null;
        var name = ctx.User.FindFirst(ClaimTypes.Name)?.Value
                ?? ctx.User.FindFirst(ClaimTypes.Email)?.Value;
        return (userId, name);
    }

    private static BillingApprovalDto Map(ApprovalRow r) => new(
        r.id, r.company_id, r.billing_id,
        r.role, BillingApprovalRoles.ArabicLabel(r.role),
        r.approver_user_id, r.approver_name,
        r.status, r.approved_at, r.rejection_reason, r.notes,
        r.created_at, r.updated_at);

    // Dapper record for SELECT projection
    private record ApprovalRow(
        Guid id, Guid company_id, Guid billing_id,
        string role, Guid? approver_user_id, string? approver_name,
        string status, DateTime? approved_at, string? rejection_reason,
        string? notes, DateTime created_at, DateTime? updated_at);
}

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 57 — Models for the 4-party approval workflow.
///
/// A progress billing goes through 4 stages:
///   1. contractor   (المقاول)      — submits the billing
///   2. consultant   (الاستشاري)     — certifies the work
///   3. pmo          (إدارة المشروعات) — verifies contract compliance
///   4. owner        (المالك)        — final approval for payment
///
/// When all 4 are 'approved', the billing is "final approved" and
/// ready for the JE / invoice to be generated.
/// </summary>

/// <summary>
/// The 4 role constants. Use these instead of magic strings
/// so a typo in one place is caught at compile time.
/// </summary>
public static class BillingApprovalRoles
{
    public const string Contractor = "contractor";
    public const string Consultant = "consultant";
    public const string Pmo        = "pmo";
    public const string Owner      = "owner";

    public static readonly string[] All = { Contractor, Consultant, Pmo, Owner };

    public static string ArabicLabel(string role) => role switch
    {
        Contractor => "المقاول",
        Consultant => "الاستشاري",
        Pmo        => "إدارة المشروعات",
        Owner      => "المالك",
        _          => role
    };
}

/// <summary>
/// One approval row. There are 4 of these per billing.
/// </summary>
public record BillingApprovalDto(
    Guid Id,
    Guid CompanyId,
    Guid BillingId,
    string Role,                // contractor | consultant | pmo | owner
    string RoleLabel,           // Arabic label
    Guid? ApproverUserId,
    string? ApproverName,
    string Status,              // pending | approved | rejected
    DateTime? ApprovedAt,
    string? RejectionReason,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

/// <summary>
/// Request body for approving a role. The approver is taken from
/// the JWT token (User.FindFirst(ClaimTypes.NameIdentifier)), not
/// from the body — this prevents impersonation.
/// </summary>
public record ApproveBillingApprovalRequest(
    string? Notes
);

/// <summary>
/// Request body for rejecting a role. Reason is required.
/// </summary>
public record RejectBillingApprovalRequest(
    string Reason,
    string? Notes
);

/// <summary>
/// Status constants.
/// </summary>
public static class BillingApprovalStatuses
{
    public const string Pending  = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 57 — Print View data assembly.
///
/// The "Print Final" view is the A4-formatted document that the
/// consultant hands to the owner for signature. We aggregate
/// data from 6 tables in a single DTO so the frontend can render
/// the entire printable view from one API call:
///   - progress_billings
///   - contracts (4 parties)
///   - projects (manager, location, dates)
///   - contacts (4 party names)
///   - companies (tenant info — for the letterhead)
///   - progress_billing_approvals (4 signature boxes)
/// </summary>
public class PrintViewService
{
    private readonly IDbConnectionFactory _db;

    public PrintViewService(IDbConnectionFactory db)
    {
        _db = db;
    }

    public async Task<BillingPrintViewDto> GetPrintViewAsync(Guid billingId)
    {
        using var conn = _db.CreateConnection();

        // Main billing with project + contract + parties joined
        // COALESCE on nullable columns forces Dapper to see non-null
        // types, which matches the (non-nullable) record below. The
        // "is null" check happens in C# when we build the DTO.
        var row = await conn.QuerySingleOrDefaultAsync<PrintViewRow>(@"
            SELECT
                b.id AS billing_id,
                b.billing_number, b.billing_date,
                COALESCE(b.period_from, '0001-01-01'::timestamp) AS period_from,
                COALESCE(b.period_to, '0001-01-01'::timestamp) AS period_to,
                b.work_completed_percent, b.gross_amount,
                b.advance_deducted, b.retention_deducted,
                b.final_insurance_deducted, b.admin_fees_deducted,
                b.original_contract_deduction, b.net_amount,
                b.status AS billing_status,
                COALESCE(b.final_approved_at, '0001-01-01'::timestamp) AS final_approved_at,
                COALESCE(b.notes, '') AS notes,
                -- Project
                p.id AS project_id, p.code AS project_code, p.name AS project_name,
                COALESCE(p.name_ar, '') AS project_name_ar,
                COALESCE(p.location, '') AS project_location,
                COALESCE(p.start_date, '0001-01-01'::timestamp) AS project_start_date,
                COALESCE(p.end_date, '0001-01-01'::timestamp) AS project_end_date,
                COALESCE(p.project_manager, '') AS project_manager,
                -- Contract
                c.id AS contract_id, COALESCE(c.contract_number, '') AS contract_number,
                c.contract_value, c.advance_percent, c.retention_percent,
                COALESCE(c.start_date, '0001-01-01'::timestamp) AS contract_start_date,
                COALESCE(c.end_date, '0001-01-01'::timestamp) AS contract_end_date,
                COALESCE(c.site_handover_date, '0001-01-01'::timestamp) AS site_handover_date,
                COALESCE(c.original_contract_value, 0) AS original_contract_value,
                c.final_insurance_percent, c.admin_fee_percent,
                COALESCE(c.final_insurance_release_date, '0001-01-01'::timestamp) AS final_insurance_release_date,
                -- Parties
                COALESCE(cust.name_ar, '') AS customer_name_ar,
                COALESCE(cust.name, '') AS customer_name,
                COALESCE(contractor.name_ar, '') AS contractor_name_ar,
                COALESCE(contractor.name, '') AS contractor_name,
                COALESCE(consultant.name_ar, '') AS consultant_name_ar,
                COALESCE(consultant.name, '') AS consultant_name,
                -- Company
                comp.id AS company_id, comp.name AS company_name,
                COALESCE(comp.name_ar, '') AS company_name_ar
            FROM progress_billings b
            JOIN projects p ON p.id = b.project_id
            JOIN contracts c ON c.id = b.contract_id
            LEFT JOIN contacts cust ON cust.id = p.customer_id
            LEFT JOIN contacts contractor ON contractor.id = p.contractor_id
            LEFT JOIN contacts consultant ON consultant.id = p.consultant_id
            JOIN companies comp ON comp.id = b.company_id
            WHERE b.id = @billingId;",
            new { billingId });

        if (row is null)
            throw new InvalidOperationException("المستخلص غير موجود");

        // 4 approval rows (lazy-create missing ones)
        var approvals = await new BillingApprovalService(_db,
            new HttpContextAccessor(),
            null!).ListAsync(billingId);

        // Helper: convert COALESCE'd DateTime (DateTime.MinValue) back to null
        static DateTime? NullIfMin(DateTime d) =>
            d == DateTime.MinValue ? null : d;
        static string? NullIfEmpty(string s) =>
            string.IsNullOrEmpty(s) ? null : s;

        return new BillingPrintViewDto(
            // Billing
            row.billing_id, row.billing_number, row.billing_date,
            NullIfMin(row.period_from), NullIfMin(row.period_to),
            row.work_completed_percent, row.gross_amount,
            row.advance_deducted, row.retention_deducted,
            row.final_insurance_deducted, row.admin_fees_deducted,
            row.original_contract_deduction, row.net_amount,
            row.billing_status, NullIfMin(row.final_approved_at), NullIfEmpty(row.notes),
            // Project
            row.project_id, row.project_code, row.project_name, NullIfEmpty(row.project_name_ar),
            NullIfEmpty(row.project_location), NullIfMin(row.project_start_date), NullIfMin(row.project_end_date),
            NullIfEmpty(row.project_manager),
            // Contract
            row.contract_id, NullIfEmpty(row.contract_number), row.contract_value,
            row.advance_percent, row.retention_percent,
            NullIfMin(row.contract_start_date), NullIfMin(row.contract_end_date),
            NullIfMin(row.site_handover_date),
            row.original_contract_value == 0 ? null : row.original_contract_value,
            row.final_insurance_percent, row.admin_fee_percent,
            NullIfMin(row.final_insurance_release_date),
            // Parties
            NullIfEmpty(row.customer_name), NullIfEmpty(row.customer_name_ar),
            NullIfEmpty(row.contractor_name), NullIfEmpty(row.contractor_name_ar),
            NullIfEmpty(row.consultant_name), NullIfEmpty(row.consultant_name_ar),
            // Company
            row.company_id, row.company_name, NullIfEmpty(row.company_name_ar),
            // Approvals
            approvals);
    }

    // Dapper record (matches the SELECT projection above)
    // Note: Dapper's positional binding requires the record field type to
    // match the SQL column's effective type. If the row has a non-null
    // value, Dapper infers the type as non-null. To be safe, we use
    // non-nullable types here and convert to nullable in the DTO.
    private record PrintViewRow(
        Guid billing_id, string billing_number, DateTime billing_date,
        DateTime period_from, DateTime period_to,
        decimal work_completed_percent, decimal gross_amount,
        decimal advance_deducted, decimal retention_deducted,
        decimal final_insurance_deducted, decimal admin_fees_deducted,
        decimal original_contract_deduction, decimal net_amount,
        string billing_status, DateTime final_approved_at, string notes,
        Guid project_id, string project_code, string project_name, string project_name_ar,
        string project_location, DateTime project_start_date, DateTime project_end_date,
        string project_manager,
        Guid contract_id, string contract_number, decimal contract_value,
        decimal advance_percent, decimal retention_percent,
        DateTime contract_start_date, DateTime contract_end_date,
        DateTime site_handover_date, decimal original_contract_value,
        decimal final_insurance_percent, decimal admin_fee_percent,
        DateTime final_insurance_release_date,
        string customer_name, string customer_name_ar,
        string contractor_name, string contractor_name_ar,
        string consultant_name, string consultant_name_ar,
        Guid company_id, string company_name, string company_name_ar);
}

/// <summary>
/// Aggregated print view data. The frontend renders an A4 page
/// from this single DTO — header, parties, billing, deductions,
/// 4 approval boxes, net amount.
/// </summary>
public record BillingPrintViewDto(
    // Billing
    Guid BillingId, string BillingNumber, DateTime BillingDate,
    DateTime? PeriodFrom, DateTime? PeriodTo,
    decimal WorkCompletedPercent, decimal GrossAmount,
    decimal AdvanceDeducted, decimal RetentionDeducted,
    decimal FinalInsuranceDeducted, decimal AdminFeesDeducted,
    decimal OriginalContractDeduction, decimal NetAmount,
    string BillingStatus, DateTime? FinalApprovedAt, string? Notes,
    // Project
    Guid ProjectId, string ProjectCode, string ProjectName, string? ProjectNameAr,
    string? ProjectLocation, DateTime? ProjectStartDate, DateTime? ProjectEndDate,
    string? ProjectManager,
    // Contract
    Guid ContractId, string? ContractNumber, decimal ContractValue,
    decimal AdvancePercent, decimal RetentionPercent,
    DateTime? ContractStartDate, DateTime? ContractEndDate,
    DateTime? SiteHandoverDate, decimal? OriginalContractValue,
    decimal FinalInsurancePercent, decimal AdminFeePercent,
    DateTime? FinalInsuranceReleaseDate,
    // Parties
    string? CustomerName, string? CustomerNameAr,
    string? ContractorName, string? ContractorNameAr,
    string? ConsultantName, string? ConsultantNameAr,
    // Company
    Guid CompanyId, string CompanyName, string? CompanyNameAr,
    // Approvals
    List<BillingApprovalDto> Approvals);

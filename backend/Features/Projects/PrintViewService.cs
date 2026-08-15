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
        var row = await conn.QuerySingleOrDefaultAsync<PrintViewRow>(@"
            SELECT
                b.id AS billing_id,
                b.billing_number, b.billing_date, b.period_from, b.period_to,
                b.work_completed_percent, b.gross_amount,
                b.advance_deducted, b.retention_deducted,
                b.final_insurance_deducted, b.admin_fees_deducted,
                b.original_contract_deduction, b.net_amount,
                b.status AS billing_status, b.final_approved_at, b.notes,
                -- Project
                p.id AS project_id, p.code AS project_code, p.name AS project_name,
                p.name_ar AS project_name_ar, p.location AS project_location,
                p.start_date AS project_start_date, p.end_date AS project_end_date,
                p.project_manager,
                -- Contract
                c.id AS contract_id, c.contract_number, c.contract_value,
                c.advance_percent, c.retention_percent, c.start_date AS contract_start_date,
                c.end_date AS contract_end_date, c.site_handover_date, c.original_contract_value,
                c.final_insurance_percent, c.admin_fee_percent, c.final_insurance_release_date,
                -- Parties
                cust.name_ar AS customer_name_ar, cust.name AS customer_name,
                contractor.name_ar AS contractor_name_ar, contractor.name AS contractor_name,
                consultant.name_ar AS consultant_name_ar, consultant.name AS consultant_name,
                -- Company
                comp.id AS company_id, comp.name AS company_name, comp.name_ar AS company_name_ar
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
            new HttpContextAccessor(), // dummy — ListAsync doesn't actually use http context
            null!).ListAsync(billingId);

        return new BillingPrintViewDto(
            // Billing
            row.billing_id, row.billing_number, row.billing_date,
            row.period_from, row.period_to,
            row.work_completed_percent, row.gross_amount,
            row.advance_deducted, row.retention_deducted,
            row.final_insurance_deducted, row.admin_fees_deducted,
            row.original_contract_deduction, row.net_amount,
            row.billing_status, row.final_approved_at, row.notes,
            // Project
            row.project_id, row.project_code, row.project_name, row.project_name_ar,
            row.project_location, row.project_start_date, row.project_end_date, row.project_manager,
            // Contract
            row.contract_id, row.contract_number, row.contract_value,
            row.advance_percent, row.retention_percent,
            row.contract_start_date, row.contract_end_date,
            row.site_handover_date, row.original_contract_value,
            row.final_insurance_percent, row.admin_fee_percent, row.final_insurance_release_date,
            // Parties
            row.customer_name, row.customer_name_ar,
            row.contractor_name, row.contractor_name_ar,
            row.consultant_name, row.consultant_name_ar,
            // Company
            row.company_id, row.company_name, row.company_name_ar,
            // Approvals
            approvals);
    }

    // Dapper record (matches the SELECT projection above)
    private record PrintViewRow(
        Guid billing_id, string billing_number, DateTime billing_date,
        DateTime? period_from, DateTime? period_to,
        decimal work_completed_percent, decimal gross_amount,
        decimal advance_deducted, decimal retention_deducted,
        decimal final_insurance_deducted, decimal admin_fees_deducted,
        decimal original_contract_deduction, decimal net_amount,
        string billing_status, DateTime? final_approved_at, string? notes,
        Guid project_id, string project_code, string project_name, string? project_name_ar,
        string? project_location, DateTime? project_start_date, DateTime? project_end_date,
        string? project_manager,
        Guid contract_id, string? contract_number, decimal contract_value,
        decimal advance_percent, decimal retention_percent,
        DateTime? contract_start_date, DateTime? contract_end_date,
        DateTime? site_handover_date, decimal? original_contract_value,
        decimal final_insurance_percent, decimal admin_fee_percent,
        DateTime? final_insurance_release_date,
        string? customer_name, string? customer_name_ar,
        string? contractor_name, string? contractor_name_ar,
        string? consultant_name, string? consultant_name_ar,
        Guid company_id, string company_name, string? company_name_ar);
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

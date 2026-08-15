using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Projects;

/// <summary>
/// Contract service (Sprint 36).
///
/// One contract per project (enforced by UNIQUE (company_id, project_id)
/// in migration 021). This service is intentionally simple — the
/// heavy lifting is in <see cref="BillingService"/>, which uses the
/// contract as the source of truth for advance/retention percentages.
///
/// Validation rules (kept tight, since contracts drive every
/// downstream calculation):
///   - contract_value > 0
///   - advance_percent ∈ [0, 100]
///   - retention_percent ∈ [0, 100]
///   - retention_start_billing >= 1
///   - the project exists and is in the same company as the contract
///   - no existing contract for this project (UNIQUE constraint will
///     also catch this at the DB level — we pre-check for a friendly
///     Arabic error)
/// </summary>
public class ContractService
{
    private readonly IDbConnectionFactory _db;

    public ContractService(IDbConnectionFactory db)
    {
        _db = db;
    }

    /// <summary>
    /// Looks up the contract for a project. Returns null if the
    /// project has no contract yet (the user must POST one to start
    /// billing). Called by BillingService on every billing operation
    /// and by the GET /api/projects/{id}/contract endpoint.
    /// </summary>
    public async Task<ContractDto?> GetByProjectAsync(Guid projectId)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ContractRow>(@"
            SELECT id, company_id, project_id, contract_number, contract_value,
                   advance_percent, retention_percent, retention_start_billing,
                   start_date, end_date, notes, final_insurance_percent, admin_fee_percent, final_insurance_release_date, site_handover_date, original_contract_value, created_at, updated_at
            FROM contracts
            WHERE project_id = @projectId
            LIMIT 1;",
            new { projectId });
        return row is null ? null : MapRow(row);
    }

    /// <summary>
    /// Looks up a contract by id. Used by PUT /api/contracts/{id}
    /// and DELETE /api/contracts/{id}.
    /// </summary>
    public async Task<ContractDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ContractRow>(@"
            SELECT id, company_id, project_id, contract_number, contract_value,
                   advance_percent, retention_percent, retention_start_billing,
                   start_date, end_date, notes, final_insurance_percent, admin_fee_percent, final_insurance_release_date, site_handover_date, original_contract_value, created_at, updated_at
            FROM contracts
            WHERE id = @id;",
            new { id });
        return row is null ? null : MapRow(row);
    }

    /// <summary>
    /// Creates a contract for the given project. The projectId is
    /// taken from the URL (POST /api/projects/{id}/contract) — the
    /// request body does not carry it (avoids a class of "wrong
    /// projectId in body" bugs).
    /// </summary>
    public async Task<ContractDto> CreateAsync(Guid projectId, CreateContractRequest req)
    {
        ValidateContractTerms(req.ContractValue, req.AdvancePercent, req.RetentionPercent, req.RetentionStartBilling);

        using var conn = _db.CreateConnection();

        // 1) Verify the project exists and grab its company_id (we
        //    stamp the contract's company_id from the project, so
        //    cross-company mistakes are impossible).
        var project = await conn.QuerySingleOrDefaultAsync<(Guid id, Guid company_id)?>(@"
            SELECT id, company_id FROM projects WHERE id = @id;",
            new { id = projectId });
        if (project is null)
            throw new InvalidOperationException("المشروع غير موجود");

        // 2) Pre-check the UNIQUE constraint so we can surface a
        //    clear Arabic error instead of letting the DB throw a
        //    generic unique-violation.
        var existing = await conn.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM contracts
            WHERE company_id = @companyId AND project_id = @projectId;",
            new { companyId = project.Value.company_id, projectId });
        if (existing > 0)
        {
            throw new InvalidOperationException(
                "يوجد عقد مسبق لهذا المشروع. الرجاء تحديث العقد الحالي أو حذفه أولاً.");
        }

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contracts (
                id, company_id, project_id, contract_number, contract_value,
                advance_percent, retention_percent, retention_start_billing,
                start_date, end_date, notes,
                final_insurance_percent, admin_fee_percent, final_insurance_release_date,
                site_handover_date, original_contract_value,
                created_at
            )
            VALUES (
                @id, @companyId, @projectId, @contractNumber, @contractValue,
                @advancePercent, @retentionPercent, @retentionStartBilling,
                @startDate, @endDate, @notes,
                @finalInsurancePercent, @adminFeePercent, @finalInsuranceReleaseDate,
                @siteHandoverDate, @originalContractValue,
                NOW()
            );",
            new
            {
                id,
                companyId = project.Value.company_id,
                projectId,
                contractNumber = req.ContractNumber,
                contractValue = req.ContractValue,
                advancePercent = req.AdvancePercent,
                retentionPercent = req.RetentionPercent,
                retentionStartBilling = req.RetentionStartBilling,
                startDate = req.StartDate,
                endDate = req.EndDate,
                notes = req.Notes,
                finalInsurancePercent = req.FinalInsurancePercent,
                adminFeePercent = req.AdminFeePercent,
                finalInsuranceReleaseDate = req.FinalInsuranceReleaseDate,
                siteHandoverDate = req.SiteHandoverDate,
                originalContractValue = req.OriginalContractValue
            });

        return (await GetByIdAsync(id))!;
    }

    /// <summary>
    /// Replaces the contract's terms. PUT semantics: every field is
    /// overwritten with the new value. Updated_at is stamped.
    /// Refuses to mutate the company_id or project_id (those are
    /// the contract's identity, not its data).
    /// </summary>
    public async Task<ContractDto?> UpdateAsync(Guid id, UpdateContractRequest req)
    {
        ValidateContractTerms(req.ContractValue, req.AdvancePercent, req.RetentionPercent, req.RetentionStartBilling);

        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<ContractRow>(@"
            SELECT id, company_id, project_id, contract_number, contract_value,
                   advance_percent, retention_percent, retention_start_billing,
                   start_date, end_date, notes, final_insurance_percent, admin_fee_percent, final_insurance_release_date, site_handover_date, original_contract_value, created_at, updated_at
            FROM contracts WHERE id = @id;",
            new { id });
        if (existing is null) return null;

        await conn.ExecuteAsync(@"
            UPDATE contracts
            SET contract_number = @contractNumber,
                contract_value = @contractValue,
                advance_percent = @advancePercent,
                retention_percent = @retentionPercent,
                retention_start_billing = @retentionStartBilling,
                start_date = @startDate,
                end_date = @endDate,
                notes = @notes,
                final_insurance_percent = @finalInsurancePercent,
                admin_fee_percent = @adminFeePercent,
                final_insurance_release_date = @finalInsuranceReleaseDate,
                site_handover_date = @siteHandoverDate,
                original_contract_value = @originalContractValue,
                updated_at = NOW()
            WHERE id = @id;",
            new
            {
                id,
                contractNumber = req.ContractNumber,
                contractValue = req.ContractValue,
                advancePercent = req.AdvancePercent,
                retentionPercent = req.RetentionPercent,
                retentionStartBilling = req.RetentionStartBilling,
                startDate = req.StartDate,
                endDate = req.EndDate,
                notes = req.Notes,
                finalInsurancePercent = req.FinalInsurancePercent,
                adminFeePercent = req.AdminFeePercent,
                finalInsuranceReleaseDate = req.FinalInsuranceReleaseDate,
                siteHandoverDate = req.SiteHandoverDate,
                originalContractValue = req.OriginalContractValue
            });

        return await GetByIdAsync(id);
    }

    /// <summary>
    /// Deletes the contract. The ON DELETE CASCADE on
    /// progress_billings.contract_id will pull down any associated
    /// billings — that's the right behaviour because the billings
    /// become meaningless without a contract. Returns true if a
    /// row was actually deleted (false = id didn't exist).
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.ExecuteAsync(
            "DELETE FROM contracts WHERE id = @id;",
            new { id });
        return rows > 0;
    }

    /// <summary>
    /// Validates the contract terms. Throws InvalidOperationException
    /// (which the endpoint layer turns into 400 BadRequest) with a
    /// clear Arabic message on any violation.
    /// </summary>
    private static void ValidateContractTerms(
        decimal contractValue, decimal advancePercent,
        decimal retentionPercent, int retentionStartBilling)
    {
        if (contractValue <= 0)
            throw new InvalidOperationException("قيمة العقد يجب أن تكون أكبر من صفر");
        if (advancePercent < 0 || advancePercent > 100)
            throw new InvalidOperationException("نسبة الدفعة المقدمة يجب أن تكون بين 0 و 100");
        if (retentionPercent < 0 || retentionPercent > 100)
            throw new InvalidOperationException("نسبة الضمان المحتجز يجب أن تكون بين 0 و 100");
        if (retentionStartBilling < 1)
            throw new InvalidOperationException("رقم المستخلص الذي يبدأ بحجز الضمان يجب أن يكون 1 أو أكبر");
    }

    private static ContractDto MapRow(ContractRow r) => new(
        r.id, r.company_id, r.project_id, r.contract_number, r.contract_value,
        r.advance_percent, r.retention_percent, r.retention_start_billing,
        r.start_date, r.end_date, r.notes,
        r.final_insurance_percent, r.admin_fee_percent,
        r.final_insurance_release_date,
        r.site_handover_date, r.original_contract_value,
        r.created_at, r.updated_at);

    private record ContractRow(
        Guid id, Guid company_id, Guid project_id,
        string? contract_number, decimal contract_value,
        decimal advance_percent, decimal retention_percent,
        int retention_start_billing,
        DateTime? start_date, DateTime? end_date,
        string? notes,
        decimal final_insurance_percent, decimal admin_fee_percent,
        DateTime? final_insurance_release_date,
        DateTime? site_handover_date, decimal? original_contract_value,
        DateTime created_at, DateTime? updated_at);
}

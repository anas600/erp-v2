using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.FiscalYears;

/// <summary>
/// Sprint 25 — Fiscal Year &amp; Period service.
///
/// Owns the <c>fiscal_years</c> and <c>fiscal_periods</c> tables. Used by:
///   - <see cref="FiscalYearEndpoints"/>: CRUD + lock/unlock + close-year.
///   - <see cref="JournalService"/>: <c>EnsurePeriodOpenAsync</c> rejects
///     journal entries that land in a locked period.
///
/// Design notes:
///   - Closing a year is intentionally conservative: it only flips
///     <c>is_closed = true</c> and stamps <c>closed_at</c>. It does NOT
///     generate closing journal entries (that's an explicit future Sprint
///     item — out of scope for Sprint 25).
///   - Periods are auto-created 12-at-a-time when a year is created.
///     Splitting a year into non-calendar periods (e.g. 13 four-week
///     periods) is not supported yet; the schema allows up to 36
///     periods per year as a soft ceiling.
///   - Unlocking a period is intentionally a super-admin-only operation
///     (the endpoint enforces the check; the service trusts its caller).
///     It is the only escape hatch once a period is locked.
/// </summary>
public class FiscalYearService
{
    private readonly IDbConnectionFactory _db;

    public FiscalYearService(IDbConnectionFactory db) => _db = db;

    /// <summary>
    /// List fiscal years for a company, newest first.
    /// </summary>
    public async Task<List<FiscalYearDto>> GetYearsAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<FiscalYearRow>(@"
            SELECT id, company_id, code, start_date, end_date, is_closed, closed_at
            FROM fiscal_years
            WHERE company_id = @companyId
            ORDER BY code DESC;",
            new { companyId });
        return rows.Select(MapYear).ToList();
    }

    /// <summary>Get a single fiscal year by id, with its periods.</summary>
    public async Task<FiscalYearDetailDto?> GetYearAsync(Guid yearId)
    {
        using var conn = _db.CreateConnection();
        var year = await conn.QuerySingleOrDefaultAsync<FiscalYearRow>(@"
            SELECT id, company_id, code, start_date, end_date, is_closed, closed_at
            FROM fiscal_years WHERE id = @id;",
            new { id = yearId });
        if (year is null) return null;

        var periods = await GetPeriodsAsync(yearId);
        return new FiscalYearDetailDto(
            year.id, year.company_id, year.code, year.start_date, year.end_date,
            year.is_closed, year.closed_at, periods);
    }

    /// <summary>List periods for a fiscal year, 1..12 ascending.</summary>
    public async Task<List<FiscalPeriodDto>> GetPeriodsAsync(Guid fiscalYearId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<FiscalPeriodRow>(@"
            SELECT id, fiscal_year_id, period_number, start_date, end_date, is_closed, closed_at
            FROM fiscal_periods
            WHERE fiscal_year_id = @fiscalYearId
            ORDER BY period_number;",
            new { fiscalYearId });
        return rows.Select(MapPeriod).ToList();
    }

    /// <summary>
    /// Cycle 4: list every fiscal period for a company, optionally
    /// filtered to a specific year. Sorted by start date DESC so the
    /// most recent period (the one the user is most likely working in)
    /// comes first in dropdowns.
    ///
    /// The fiscal-years page uses this to render a single "all periods
    /// across all years" table without forcing the user to pick a year
    /// first.
    /// </summary>
    public async Task<List<FiscalPeriodDto>> GetAllPeriodsAsync(
        Guid companyId, Guid? fiscalYearId = null)
    {
        using var conn = _db.CreateConnection();
        // Join with fiscal_years to filter by company_id (periods
        // don't have company_id directly; they're scoped via their
        // parent year).
        var rows = await conn.QueryAsync<FiscalPeriodRow>(@"
            SELECT p.id, p.fiscal_year_id, p.period_number,
                   p.start_date, p.end_date, p.is_closed, p.closed_at
            FROM fiscal_periods p
            INNER JOIN fiscal_years y ON y.id = p.fiscal_year_id
            WHERE y.company_id = @companyId
              AND (@fiscalYearId IS NULL OR p.fiscal_year_id = @fiscalYearId)
            ORDER BY p.start_date DESC, p.period_number DESC;",
            new { companyId, fiscalYearId });
        return rows.Select(MapPeriod).ToList();
    }

    /// <summary>
    /// Create a new fiscal year for a company, auto-creating 12 monthly
    /// periods. The year is identified by its <c>code</c> (e.g. '2026'),
    /// which is unique per company.
    ///
    /// If the request overlaps with an existing year (same code, or
    /// overlapping date ranges), the operation fails with a clear Arabic
    /// error — the user must close the old year first.
    /// </summary>
    public async Task<FiscalYearDetailDto> CreateYearAsync(CreateFiscalYearRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            throw new InvalidOperationException("كود السنة المالية مطلوب");
        if (req.EndDate < req.StartDate)
            throw new InvalidOperationException("تاريخ نهاية السنة يجب أن يكون بعد تاريخ البداية");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            // 1) Reject duplicate code per company
            var dup = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM fiscal_years
                WHERE company_id = @companyId AND code = @code;",
                new { companyId = req.CompanyId, code = req.Code }, tx);
            if (dup > 0)
                throw new InvalidOperationException(
                    $"السنة المالية '{req.Code}' موجودة بالفعل لهذه الشركة");

            // 2) Reject overlap with any existing year
            var overlap = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(*) FROM fiscal_years
                WHERE company_id = @companyId
                  AND start_date <= @endDate
                  AND end_date >= @startDate;",
                new { companyId = req.CompanyId, startDate = req.StartDate, endDate = req.EndDate }, tx);
            if (overlap > 0)
                throw new InvalidOperationException(
                    "هناك سنة مالية تتداخل مع الفترة المطلوبة. أغلق السنة القديمة أولاً.");

            // 3) Insert the year
            var yearId = Guid.NewGuid();
            await conn.ExecuteAsync(@"
                INSERT INTO fiscal_years (id, company_id, code, start_date, end_date, is_closed)
                VALUES (@id, @companyId, @code, @startDate, @endDate, false);",
                new
                {
                    id = yearId,
                    companyId = req.CompanyId,
                    code = req.Code,
                    startDate = req.StartDate,
                    endDate = req.EndDate
                }, tx);

            // 4) Auto-create 12 monthly periods. The first period
            //    starts on req.StartDate; each subsequent period is
            //    the day after the previous one ended, ending on
            //    (req.StartDate + n months - 1 day). The last period
            //    may extend up to req.EndDate.
            //
            //    We compute boundaries in C# to keep the SQL simple
            //    and timezone-clean. Each period is inserted one at a
            //    time inside the same transaction so a failure rolls
            //    back the year too.
            for (int p = 1; p <= 12; p++)
            {
                var pStart = req.StartDate.AddMonths(p - 1);
                var pEnd   = pStart.AddMonths(1).AddDays(-1);
                // The last period must not exceed the year end.
                if (pEnd > req.EndDate) pEnd = req.EndDate;
                if (pStart > req.EndDate) break; // year is shorter than 12 months → stop

                await conn.ExecuteAsync(@"
                    INSERT INTO fiscal_periods (id, fiscal_year_id, period_number, start_date, end_date, is_closed)
                    VALUES (@id, @fiscalYearId, @periodNumber, @startDate, @endDate, false);",
                    new
                    {
                        id = Guid.NewGuid(),
                        fiscalYearId = yearId,
                        periodNumber = p,
                        startDate = pStart,
                        endDate = pEnd
                    }, tx);
            }

            tx.Commit();

            return (await GetYearAsync(yearId))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Close a fiscal year. Marks every period as locked and the year
    /// as closed. No closing journal entries are generated (Sprint 25
    /// scope: the year-close is a metadata flip, not a posting event).
    /// </summary>
    public async Task<FiscalYearDetailDto?> CloseYearAsync(Guid yearId)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var year = await conn.QuerySingleOrDefaultAsync<FiscalYearRow>(@"
                SELECT id, company_id, code, start_date, end_date, is_closed, closed_at
                FROM fiscal_years WHERE id = @id FOR UPDATE;",
                new { id = yearId }, tx);
            if (year is null) return null;

            if (year.is_closed) return await GetYearAsync(yearId); // idempotent

            await conn.ExecuteAsync(@"
                UPDATE fiscal_years SET is_closed = true, closed_at = NOW() WHERE id = @id;",
                new { id = yearId }, tx);

            await conn.ExecuteAsync(@"
                UPDATE fiscal_periods SET is_closed = true, closed_at = NOW()
                WHERE fiscal_year_id = @id AND is_closed = false;",
                new { id = yearId }, tx);

            tx.Commit();
            return await GetYearAsync(yearId);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>Lock a single period. The journal-service check rejects entries in this period from now on.</summary>
    public async Task<FiscalPeriodDto?> LockPeriodAsync(Guid periodId)
    {
        using var conn = _db.CreateConnection();
        var period = await conn.QuerySingleOrDefaultAsync<FiscalPeriodRow>(@"
            SELECT id, fiscal_year_id, period_number, start_date, end_date, is_closed, closed_at
            FROM fiscal_periods WHERE id = @id;",
            new { id = periodId });
        if (period is null) return null;
        if (period.is_closed) return MapPeriod(period); // idempotent

        await conn.ExecuteAsync(@"
            UPDATE fiscal_periods SET is_closed = true, closed_at = NOW() WHERE id = @id;",
            new { id = periodId });
        return await GetPeriodAsync(periodId);
    }

    /// <summary>Unlock a single period. Super-admin only — the endpoint enforces the check, not the service.</summary>
    public async Task<FiscalPeriodDto?> UnlockPeriodAsync(Guid periodId)
    {
        using var conn = _db.CreateConnection();
        var period = await conn.QuerySingleOrDefaultAsync<FiscalPeriodRow>(@"
            SELECT id, fiscal_year_id, period_number, start_date, end_date, is_closed, closed_at
            FROM fiscal_periods WHERE id = @id;",
            new { id = periodId });
        if (period is null) return null;
        if (!period.is_closed) return MapPeriod(period); // idempotent

        await conn.ExecuteAsync(@"
            UPDATE fiscal_periods SET is_closed = false, closed_at = NULL WHERE id = @id;",
            new { id = periodId });
        return await GetPeriodAsync(periodId);
    }

    public async Task<FiscalPeriodDto?> GetPeriodAsync(Guid periodId)
    {
        using var conn = _db.CreateConnection();
        var period = await conn.QuerySingleOrDefaultAsync<FiscalPeriodRow>(@"
            SELECT id, fiscal_year_id, period_number, start_date, end_date, is_closed, closed_at
            FROM fiscal_periods WHERE id = @id;",
            new { id = periodId });
        return period is null ? null : MapPeriod(period);
    }

    // ============================================================
    // Mapping helpers
    // ============================================================
    private static FiscalYearDto MapYear(FiscalYearRow r) => new(
        r.id, r.company_id, r.code, r.start_date, r.end_date, r.is_closed, r.closed_at);

    private static FiscalPeriodDto MapPeriod(FiscalPeriodRow r) => new(
        r.id, r.fiscal_year_id, r.period_number, r.start_date, r.end_date, r.is_closed, r.closed_at);

    private record FiscalYearRow(
        Guid id, Guid company_id, string code, DateTime start_date, DateTime end_date,
        bool is_closed, DateTime? closed_at);

    private record FiscalPeriodRow(
        Guid id, Guid fiscal_year_id, int period_number, DateTime start_date, DateTime end_date,
        bool is_closed, DateTime? closed_at);
}

public record FiscalYearDto(
    Guid Id, Guid CompanyId, string Code, DateTime StartDate, DateTime EndDate,
    bool IsClosed, DateTime? ClosedAt);

public record FiscalYearDetailDto(
    Guid Id, Guid CompanyId, string Code, DateTime StartDate, DateTime EndDate,
    bool IsClosed, DateTime? ClosedAt, List<FiscalPeriodDto> Periods);

public record FiscalPeriodDto(
    Guid Id, Guid FiscalYearId, int PeriodNumber, DateTime StartDate, DateTime EndDate,
    bool IsClosed, DateTime? ClosedAt);

public record CreateFiscalYearRequest(
    Guid CompanyId, string Code, DateTime StartDate, DateTime EndDate);

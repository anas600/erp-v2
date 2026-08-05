using Dapper;
using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Reports;

/// <summary>
/// Intercompany Elimination Report — Sprint 24.
///
/// When consolidating the books of two sister companies, the
/// accountant must subtract (eliminate) every transaction that
/// happened BETWEEN them — otherwise the consolidated income
/// statement double-counts revenue (HOLD records it; CO-A records
/// the matching expense), and the consolidated balance sheet
/// double-counts receivables/payables (HOLD shows AR from CO-A;
/// CO-A shows AP to HOLD).
///
/// This endpoint produces the list of journal entries to be
/// eliminated as of a given date. The format is:
///   {
///     pairs: [
///       { pairId, primaryInvoiceId, mirrorInvoiceId, amount, status,
///         primary: { companyId, companyName, journalEntries: [...] },
///         mirror:  { companyId, companyName, journalEntries: [...] } },
///       ...
///     ],
///     totalEliminations: 2,   // count of distinct journal entries to eliminate
///     byCompany: { "HOLD": 1000, "CO-A": 1000 }   // sum of eliminations per company
///   }
///
/// The "byCompany" map is keyed by company NAME (not id) so the
/// JSON is human-readable; the id is also included in each side
/// block for programmatic lookups.
/// </summary>
public static class IntercompanyEliminationEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        // GET /api/reports/intercompany-elimination?companyId=...&asOfDate=...
        //
        // companyId — the company whose consolidation we're running
        //             (i.e. the holding company or the consolidating
        //             entity). We return EVERY pair where either
        //             side touches the company, so this works
        //             whether the caller is the holding or a
        //             subsidiary.
        //
        // asOfDate  — cutoff for the elimination. Pairs created
        //             after asOfDate are NOT included. Optional;
        //             defaults to "now" (everything to date).
        grp.MapGet("/intercompany-elimination", async (
            [FromQuery] Guid companyId,
            [FromQuery] DateTime? asOfDate,
            [FromServices] IDbConnectionFactory db) =>
        {
            if (companyId == Guid.Empty)
                return Results.BadRequest(new { error = "companyId required" });

            var asOf = asOfDate ?? DateTime.UtcNow;

            using var conn = db.CreateConnection();

            // 1) Fetch all pairs touching the company, created on or
            //    before asOfDate. We exclude 'reversed' pairs because
            //    their journal entries have already been eliminated
            //    via the regular reversal flow.
            var pairs = (await conn.QueryAsync<PairRow>(@"
                SELECT p.id, p.primary_invoice_id, p.mirror_invoice_id,
                       p.primary_company_id, p.mirror_company_id,
                       p.amount, p.currency, p.status, p.created_at,
                       pc.name AS primary_company_name,
                       mc.name AS mirror_company_name
                FROM intercompany_pairs p
                JOIN companies pc ON pc.id = p.primary_company_id
                LEFT JOIN companies mc ON mc.id = p.mirror_company_id
                WHERE (p.primary_company_id = @companyId OR p.mirror_company_id = @companyId)
                  AND p.created_at <= @asOf
                  AND p.status <> 'reversed'
                ORDER BY p.created_at DESC;",
                new { companyId, asOf })).ToList();

            if (pairs.Count == 0)
            {
                return Results.Ok(new EliminationReport(
                    AsOfDate: asOf,
                    CompanyId: companyId,
                    Pairs: new List<EliminationPairReport>(),
                    TotalEliminations: 0,
                    ByCompany: new Dictionary<string, decimal>()
                ));
            }

            // 2) For each pair, fetch the journal entries on each
            //    side that should be eliminated. We restrict to
            //    status = 'posted' (drafts and pending don't affect
            //    financial reports, so they don't need elimination).
            var pairIds = pairs.Select(p => p.id).ToList();
            var eliminationEntries = (await conn.QueryAsync<EliminationEntryRow>(@"
                SELECT je.id AS entry_id, je.entry_number, je.entry_date, je.narration,
                       je.company_id, c.name AS company_name,
                       je.intercompany_pair_id,
                       COALESCE(SUM(jl.debit), 0) AS total_debit,
                       COALESCE(SUM(jl.credit), 0) AS total_credit
                FROM journal_entries je
                JOIN companies c ON c.id = je.company_id
                LEFT JOIN journal_lines jl ON jl.journal_entry_id = je.id
                WHERE je.intercompany_pair_id = ANY(@pairIds)
                  AND je.status = 'posted'
                GROUP BY je.id, je.entry_number, je.entry_date, je.narration,
                         je.company_id, c.name, je.intercompany_pair_id
                ORDER BY je.entry_date, je.entry_number;",
                new { pairIds })).ToList();

            // 3) Group entries by pair → side → company.
            // intercompany_pair_id is nullable in the row but in
            // practice the SQL filter restricts it to non-null ids
            // (we only fetch entries with a pair_id set). Coalesce
            // to Guid.Empty so the dictionary keyer is happy and
            // skip those degenerate rows.
            var entriesByPair = eliminationEntries
                .Where(e => e.intercompany_pair_id.HasValue)
                .GroupBy(e => e.intercompany_pair_id!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.GroupBy(e => e.company_id)
                          .ToDictionary(sg => sg.Key, sg => sg.ToList())
                );

            // 4) Build the elimination report DTOs.
            var resultPairs = new List<EliminationPairReport>();
            int totalEliminations = 0;
            var byCompany = new Dictionary<string, decimal>();

            foreach (var p in pairs)
            {
                // entriesByPair is keyed by pair id; the inner dict
                // is keyed by company_id. We need both primary and
                // mirror sides — pull them out explicitly so the
                // null-flow analysis is happy and the code is
                // easier to read.
                var primaryEntries = new List<EliminationEntryReport>();
                var mirrorEntries  = new List<EliminationEntryReport>();
                if (entriesByPair.TryGetValue(p.id, out var byCo))
                {
                    if (byCo.TryGetValue(p.primary_company_id, out var pe))
                        primaryEntries = pe.Select(MapEntry).ToList();
                    if (p.mirror_company_id.HasValue &&
                        byCo.TryGetValue(p.mirror_company_id.Value, out var me))
                        mirrorEntries = me.Select(MapEntry).ToList();
                }

                resultPairs.Add(new EliminationPairReport(
                    PairId: p.id,
                    PrimaryInvoiceId: p.primary_invoice_id,
                    MirrorInvoiceId: p.mirror_invoice_id,
                    PrimaryCompanyId: p.primary_company_id,
                    MirrorCompanyId: p.mirror_company_id,
                    PrimaryCompanyName: p.primary_company_name,
                    MirrorCompanyName: p.mirror_company_name ?? "",
                    Amount: p.amount,
                    Currency: p.currency,
                    Status: p.status,
                    CreatedAt: p.created_at,
                    PrimaryEntries: primaryEntries,
                    MirrorEntries: mirrorEntries
                ));

                totalEliminations += primaryEntries.Count + mirrorEntries.Count;

                // Sum the net amount per company. The convention:
                // "elimination amount" = the side's total debit (=
                // total credit on a balanced entry). This is what
                // the consolidated balance sheet would back out.
                foreach (var e in primaryEntries)
                {
                    var amt = e.TotalDebit;
                    if (!byCompany.ContainsKey(e.CompanyName)) byCompany[e.CompanyName] = 0;
                    byCompany[e.CompanyName] += amt;
                }
                foreach (var e in mirrorEntries)
                {
                    var amt = e.TotalDebit;
                    if (!byCompany.ContainsKey(e.CompanyName)) byCompany[e.CompanyName] = 0;
                    byCompany[e.CompanyName] += amt;
                }
            }

            return Results.Ok(new EliminationReport(
                AsOfDate: asOf,
                CompanyId: companyId,
                Pairs: resultPairs,
                TotalEliminations: totalEliminations,
                ByCompany: byCompany
            ));
        });
    }

    private static EliminationEntryReport MapEntry(EliminationEntryRow r) => new(
        EntryId: r.entry_id,
        EntryNumber: r.entry_number,
        EntryDate: r.entry_date,
        Narration: r.narration,
        CompanyId: r.company_id,
        CompanyName: r.company_name,
        TotalDebit: r.total_debit,
        TotalCredit: r.total_credit
    );

    private record PairRow(
        Guid id, Guid primary_invoice_id, Guid? mirror_invoice_id,
        Guid primary_company_id, Guid? mirror_company_id,
        decimal amount, string currency, string status, DateTime created_at,
        string primary_company_name, string? mirror_company_name);

    private record EliminationEntryRow(
        Guid entry_id, string entry_number, DateTime entry_date, string? narration,
        Guid company_id, string company_name, Guid? intercompany_pair_id,
        decimal total_debit, decimal total_credit);
}

// =================================================================
// DTOs for the elimination report.
// =================================================================

/// <summary>
/// One entry that should be eliminated in the consolidated books.
/// Carries enough metadata to show the accountant a list ("this
/// JV-2026-0042 on HOLD's books, debit 1000 to AR-CO-A") and
/// enough numbers to compute the elimination total.
/// </summary>
public record EliminationEntryReport(
    Guid EntryId,
    string EntryNumber,
    DateTime EntryDate,
    string? Narration,
    Guid CompanyId,
    string CompanyName,
    decimal TotalDebit,
    decimal TotalCredit
);

/// <summary>
/// One pair's contribution to the elimination report. Both sides
/// (primary + mirror) are returned so the user can see what
/// cancels out in the consolidation.
/// </summary>
public record EliminationPairReport(
    Guid PairId,
    Guid PrimaryInvoiceId,
    Guid? MirrorInvoiceId,
    Guid PrimaryCompanyId,
    Guid? MirrorCompanyId,
    string PrimaryCompanyName,
    string MirrorCompanyName,
    decimal Amount,
    string Currency,
    string Status,
    DateTime CreatedAt,
    List<EliminationEntryReport> PrimaryEntries,
    List<EliminationEntryReport> MirrorEntries
);

/// <summary>
/// Top-level elimination report: the per-pair details + the
/// grand totals the accountant needs to write the consolidation
/// adjusting entry.
/// </summary>
public record EliminationReport(
    DateTime AsOfDate,
    Guid CompanyId,
    List<EliminationPairReport> Pairs,
    int TotalEliminations,
    Dictionary<string, decimal> ByCompany
);

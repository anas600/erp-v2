using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Contacts;

/// <summary>
/// Sprint 25 — endpoints for the per-contact view (كشف حساب):
///   - <c>GET /api/contacts/{id}/invoices?status=...</c>
///   - <c>GET /api/contacts/{id}/statement?from=&to=</c>
///   - <c>GET /api/contacts/{id}/balance</c>
///
/// All three delegate to <see cref="ContactStatementService"/>. The
/// contact list / CRUD endpoints live in
/// <see cref="ContactEndpoints"/>; this file is additive.
/// </summary>
public static class ContactStatementEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/contacts")
                     .WithTags("Contacts")
                     .RequireAuthorization();

        // GET /api/contacts/{id}/invoices?status=outstanding|paid|all&asOf=YYYY-MM-DD
        grp.MapGet("/{id:guid}/invoices", async (
            Guid id,
            [FromQuery] string? status,
            [FromQuery] DateTime? asOf,
            [FromServices] ContactStatementService svc) =>
        {
            var asOfDate = asOf ?? DateTime.UtcNow;
            var filter = string.IsNullOrWhiteSpace(status) ? "all" : status;
            var invoices = await svc.GetInvoicesAsync(id, filter, asOfDate);
            return Results.Ok(invoices);
        });

        // GET /api/contacts/{id}/balance
        grp.MapGet("/{id:guid}/balance", async (
            Guid id,
            [FromServices] ContactStatementService svc) =>
        {
            var balance = await svc.GetBalanceAsync(id);
            return Results.Ok(balance);
        });

        // GET /api/contacts/{id}/statement?from=YYYY-MM-DD&to=YYYY-MM-DD
        grp.MapGet("/{id:guid}/statement", async (
            Guid id,
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            [FromServices] ContactStatementService svc) =>
        {
            var statement = await svc.GetStatementAsync(id, from, to);
            return statement is null
                ? Results.NotFound(new { error = "Contact not found" })
                : Results.Ok(statement);
        });
    }
}

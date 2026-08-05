using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Accounts;

public static class AccountEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/accounts").WithTags("Accounts").RequireAuthorization();

        grp.MapGet("/", async ([FromQuery] Guid companyId, AccountService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var data = await svc.GetTreeAsync(companyId);
            return Results.Ok(data);
        });

        grp.MapGet("/{id:guid}", async (Guid id, AccountService svc) =>
        {
            var a = await svc.GetByIdAsync(id);
            return a is null ? Results.NotFound() : Results.Ok(a);
        });

        grp.MapPost("/", async ([FromBody] CreateAccountRequest req, AccountService svc) =>
        {
            try
            {
                if (req.CompanyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
                if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new { error = "Code and Name required" });
                var a = await svc.CreateAsync(req);
                return Results.Created($"/api/accounts/{a.Id}", a);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateAccountRequest req, AccountService svc) =>
        {
            var a = await svc.UpdateAsync(id, req);
            return a is null ? Results.NotFound() : Results.Ok(a);
        });

        // Sub-ledger: get the detail account linked to a contact
        grp.MapGet("/sub-ledger/{contactId:guid}", async (
            [FromQuery] Guid companyId, Guid contactId, AccountService svc) =>
        {
            if (companyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
            var a = await svc.GetSubLedgerForContactAsync(companyId, contactId);
            return a is null ? Results.NotFound(new { error = "No sub-ledger for this contact" }) : Results.Ok(a);
        });

        // Sub-ledger: create one for a contact (idempotent)
        grp.MapPost("/sub-ledger", async (
            [FromBody] CreateSubLedgerRequest req, AccountService svc) =>
        {
            try
            {
                if (req.CompanyId == Guid.Empty) return Results.BadRequest(new { error = "companyId required" });
                if (req.ContactId == Guid.Empty) return Results.BadRequest(new { error = "contactId required" });
                if (string.IsNullOrWhiteSpace(req.ParentAccountCode))
                    return Results.BadRequest(new { error = "parentAccountCode required" });
                if (string.IsNullOrWhiteSpace(req.DetailCode))
                    return Results.BadRequest(new { error = "detailCode required" });
                var a = await svc.CreateSubLedgerForContactAsync(
                    req.CompanyId, req.ContactId, req.ParentAccountCode, req.DetailCode);
                return Results.Ok(a);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    public record CreateSubLedgerRequest(
        Guid CompanyId,
        Guid ContactId,
        string ParentAccountCode,
        string DetailCode
    );
}

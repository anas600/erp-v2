using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 38 — Contract Line Item endpoint group.
///
/// Routes:
///   GET    /api/contracts/{id}/line-items           → List&lt;ContractLineItemDto&gt;
///   POST   /api/contracts/{id}/line-items           → ContractLineItemDto
///   PUT    /api/line-items/{id}                     → ContractLineItemDto
///   DELETE /api/line-items/{id}                     → 204 | 404
///   POST   /api/contracts/{id}/line-items/reorder   → 200 (success)
///   POST   /api/contracts/{id}/line-items/import-excel    → ImportLineItemsResult
///   POST   /api/contracts/{id}/line-items/import-clipboard → ImportLineItemsResult
///
/// All routes require auth (RequireAuthorization). The line items
/// are children of contracts; the contract's company_id is the
/// multi-tenancy boundary.
/// </summary>
public static class LineItemEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api").WithTags("LineItems").RequireAuthorization();

        // GET /api/contracts/{id}/line-items — BOQ for a contract.
        grp.MapGet("/contracts/{contractId:guid}/line-items", async (
            Guid contractId, [FromServices] LineItemService svc) =>
        {
            var list = await svc.GetByContractAsync(contractId);
            return Results.Ok(list);
        });

        // POST /api/contracts/{id}/line-items — add a new line item.
        grp.MapPost("/contracts/{contractId:guid}/line-items", async (
            Guid contractId,
            [FromBody] CreateLineItemRequest req,
            [FromServices] LineItemService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var li = await svc.CreateAsync(contractId, req);
                return Results.Created($"/api/line-items/{li.Id}", li);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // PUT /api/line-items/{id} — update a line item.
        grp.MapPut("/line-items/{id:guid}", async (
            Guid id,
            [FromBody] UpdateLineItemRequest req,
            [FromServices] LineItemService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var li = await svc.UpdateAsync(id, req);
                return li is null ? Results.NotFound() : Results.Ok(li);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DELETE /api/line-items/{id}
        grp.MapDelete("/line-items/{id:guid}", async (
            Guid id, [FromServices] LineItemService svc) =>
        {
            try
            {
                var ok = await svc.DeleteAsync(id);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/contracts/{id}/line-items/reorder
        grp.MapPost("/contracts/{contractId:guid}/line-items/reorder", async (
            Guid contractId,
            [FromBody] ReorderLineItemsRequest req,
            [FromServices] LineItemService svc) =>
        {
            try
            {
                if (req is null)
                    return Results.BadRequest(new { error = "request body required" });
                var ok = await svc.ReorderAsync(contractId, req);
                return ok ? Results.Ok(new { success = true }) : Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/contracts/{id}/line-items/import-excel
        // Multipart upload: 'file' field. We read the bytes and
        // pass them to the service which uses ClosedXML.
        grp.MapPost("/contracts/{contractId:guid}/line-items/import-excel", async (
            Guid contractId,
            HttpRequest request,
            [FromServices] LineItemService svc) =>
        {
            try
            {
                if (!request.HasFormContentType)
                    return Results.BadRequest(new { error = "multipart/form-data required" });
                var form = await request.ReadFormAsync();
                var file = form.Files["file"];
                if (file is null || file.Length == 0)
                    return Results.BadRequest(new { error = "الملف مطلوب" });
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var req38 = new ImportLineItemsRequest(
                    FileName: file.FileName,
                    ContentType: file.ContentType,
                    Content: ms.ToArray());
                var result = await svc.ImportFromExcelAsync(contractId, req38);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // POST /api/contracts/{id}/line-items/import-clipboard
        // Body: { "data": "..." } (tab-separated). We accept a
        // raw text body so the user can paste directly from
        // Excel/Google Sheets without any encoding fuss.
        grp.MapPost("/contracts/{contractId:guid}/line-items/import-clipboard", async (
            Guid contractId,
            [FromBody] ClipboardImportRequest? body,
            [FromServices] LineItemService svc) =>
        {
            try
            {
                var data = body?.Data ?? string.Empty;
                if (string.IsNullOrWhiteSpace(data))
                    return Results.BadRequest(new { error = "البيانات مطلوبة" });
                var result = await svc.ImportFromClipboardAsync(contractId, data);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

/// <summary>
/// Body for POST /api/contracts/{id}/line-items/import-clipboard.
/// Just the raw clipboard text — the service parses it.
/// </summary>
public record ClipboardImportRequest(string Data);

using ErpV2.Common;
using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Projects;

/// <summary>
/// Sprint 55 — Field Measurement Book endpoints.
/// </summary>
public static class FieldMeasurementEndpoints
{
    public static void MapFieldMeasurementEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api").WithTags("FieldMeasurement");

        // List FMBs for a project
        grp.MapGet("/projects/{id:guid}/field-measurement-books", async (
            Guid id, [FromServices] FieldMeasurementService svc) =>
        {
            var books = await svc.ListByProjectAsync(id);
            return Results.Ok(books);
        });

        // Get one FMB (with all its entries)
        grp.MapGet("/field-measurement-books/{id:guid}", async (
            Guid id, [FromServices] FieldMeasurementService svc) =>
        {
            var book = await svc.GetByIdAsync(id);
            return book is null ? Results.NotFound() : Results.Ok(book);
        });

        // Create new FMB
        grp.MapPost("/projects/{id:guid}/field-measurement-books", async (
            Guid id,
            [FromBody] CreateFieldMeasurementBookRequest req,
            [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(req.BookNumber))
                    return Results.BadRequest(new { error = "رقم الدفتر مطلوب" });
                var book = await svc.CreateAsync(id, req);
                return Results.Created($"/api/field-measurement-books/{book.Id}", book);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Update FMB
        grp.MapPut("/field-measurement-books/{id:guid}", async (
            Guid id,
            [FromBody] UpdateFieldMeasurementBookRequest req,
            [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                var book = await svc.UpdateAsync(id, req);
                return book is null ? Results.NotFound() : Results.Ok(book);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Delete FMB
        grp.MapDelete("/field-measurement-books/{id:guid}", async (
            Guid id, [FromServices] FieldMeasurementService svc) =>
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

        // Add an entry (BOQ line item with measurements)
        grp.MapPost("/field-measurement-books/{id:guid}/entries", async (
            Guid id,
            [FromBody] CreateFieldMeasurementEntryRequest req,
            [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                var entry = await svc.AddEntryAsync(id, req);
                return Results.Created($"/api/field-measurement-entries/{entry.Id}", entry);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Update an entry
        grp.MapPut("/field-measurement-entries/{id:guid}", async (
            Guid id,
            [FromBody] UpdateFieldMeasurementEntryRequest req,
            [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                var entry = await svc.UpdateEntryAsync(id, req);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Delete an entry
        grp.MapDelete("/field-measurement-entries/{id:guid}", async (
            Guid id, [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                var ok = await svc.DeleteEntryAsync(id);
                return ok ? Results.NoContent() : Results.NotFound();
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Lifecycle: submit
        grp.MapPost("/field-measurement-books/{id:guid}/submit", async (
            Guid id,
            [FromBody] SubmitFmbRequest req,
            [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                var book = await svc.SubmitAsync(id, req?.Comments);
                return Results.Ok(book);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Lifecycle: approve
        grp.MapPost("/field-measurement-books/{id:guid}/approve", async (
            Guid id,
            [FromBody] ApproveFmbRequest req,
            [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                var book = await svc.ApproveAsync(id, req?.Comments);
                return Results.Ok(book);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // Lifecycle: reject
        grp.MapPost("/field-measurement-books/{id:guid}/reject", async (
            Guid id,
            [FromBody] RejectFmbRequest req,
            [FromServices] FieldMeasurementService svc) =>
        {
            try
            {
                var book = await svc.RejectAsync(id, req?.Reason);
                return Results.Ok(book);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}

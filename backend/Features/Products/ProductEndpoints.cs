using Microsoft.AspNetCore.Mvc;

namespace ErpV2.Features.Products;

public static class ProductEndpoints
{
    public static void Map(WebApplication app)
    {
        var grp = app.MapGroup("/api/products").WithTags("Products").RequireAuthorization();

        grp.MapGet("/", async (Guid companyId, bool? includeInactive, ProductService svc) =>
        {
            var list = await svc.GetByCompanyAsync(companyId, includeInactive ?? false);
            return Results.Ok(list);
        });

        grp.MapGet("/{id:guid}", async (Guid id, ProductService svc) =>
        {
            var p = await svc.GetByIdAsync(id);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        grp.MapPost("/", async ([FromBody] CreateProductRequest req, ProductService svc) =>
        {
            try
            {
                var p = await svc.CreateAsync(req);
                return Results.Created($"/api/products/{p.Id}", p);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        grp.MapPut("/{id:guid}", async (Guid id, [FromBody] UpdateProductRequest req, ProductService svc) =>
        {
            var p = await svc.UpdateAsync(id, req);
            return p is null ? Results.NotFound() : Results.Ok(p);
        });

        grp.MapDelete("/{id:guid}", async (Guid id, ProductService svc) =>
        {
            var ok = await svc.DeleteAsync(id);
            return ok ? Results.NoContent() : Results.NotFound();
        });
    }
}

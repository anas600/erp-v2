using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Products;

/// <summary>
/// Product / item catalogue.
///
/// A product is a reusable line-item template: code, name, default
/// unit price, default tax rate. Picking a product on an invoice
/// auto-fills description, unit_price, and tax_rate so the user
/// only has to enter the quantity.
///
/// Products are scoped per company (multi-company: the same code
/// can mean different products in HOLD vs CO-A).
/// </summary>
public class ProductService
{
    private readonly IDbConnectionFactory _db;

    public ProductService(IDbConnectionFactory db) => _db = db;

    public async Task<List<ProductDto>> GetByCompanyAsync(Guid companyId, bool includeInactive = false)
    {
        using var conn = _db.CreateConnection();
        var rows = (await conn.QueryAsync<ProductRow>(@"
            SELECT id, company_id, code, name, name_ar, unit_price, default_tax_rate, is_active, created_at
            FROM products
            WHERE company_id = @companyId
              AND (@includeInactive OR is_active = true)
            ORDER BY code;",
            new { companyId, includeInactive })).ToList();
        return rows.Select(r => new ProductDto(
            r.id, r.company_id, r.code, r.name, r.name_ar,
            r.unit_price, r.default_tax_rate, r.is_active, r.created_at
        )).ToList();
    }

    public async Task<ProductDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ProductRow>(@"
            SELECT id, company_id, code, name, name_ar, unit_price, default_tax_rate, is_active, created_at
            FROM products WHERE id = @id;", new { id });
        if (row is null) return null;
        return new ProductDto(
            row.id, row.company_id, row.code, row.name, row.name_ar,
            row.unit_price, row.default_tax_rate, row.is_active, row.created_at
        );
    }

    public async Task<ProductDto> CreateAsync(CreateProductRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code)) throw new InvalidOperationException("كود المنتج مطلوب");
        if (string.IsNullOrWhiteSpace(req.Name)) throw new InvalidOperationException("اسم المنتج مطلوب");

        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var id = Guid.NewGuid();
            await conn.ExecuteAsync(@"
                INSERT INTO products (id, company_id, code, name, name_ar, unit_price, default_tax_rate, is_active)
                VALUES (@id, @companyId, @code, @name, @nameAr, @unitPrice, @taxRate, true);",
                new
                {
                    id,
                    companyId = req.CompanyId,
                    code = req.Code.Trim(),
                    name = req.Name.Trim(),
                    nameAr = req.NameAr?.Trim(),
                    unitPrice = req.UnitPrice,
                    taxRate = req.DefaultTaxRate
                }, tx);
            tx.Commit();
            return (await GetByIdAsync(id))!;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<ProductDto?> UpdateAsync(Guid id, UpdateProductRequest req)
    {
        using var conn = _db.CreateConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            await conn.ExecuteAsync(@"
                UPDATE products SET
                    code = COALESCE(@code, code),
                    name = COALESCE(@name, name),
                    name_ar = COALESCE(@nameAr, name_ar),
                    unit_price = COALESCE(@unitPrice, unit_price),
                    default_tax_rate = COALESCE(@taxRate, default_tax_rate),
                    is_active = COALESCE(@isActive, is_active)
                WHERE id = @id;",
                new
                {
                    id,
                    code = req.Code?.Trim(),
                    name = req.Name?.Trim(),
                    nameAr = req.NameAr?.Trim(),
                    unitPrice = req.UnitPrice,
                    taxRate = req.DefaultTaxRate,
                    isActive = req.IsActive
                }, tx);
            tx.Commit();
            return await GetByIdAsync(id);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        // Soft delete: flip is_active to false. Historical invoice
        // lines that reference this product keep the FK with
        // ON DELETE SET NULL (set at the schema level), but the
        // product is hidden from pickers.
        var rows = await conn.ExecuteAsync(
            "UPDATE products SET is_active = false WHERE id = @id;",
            new { id });
        return rows > 0;
    }
}

// ==== DTOs / rows ====

public record ProductDto(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    decimal UnitPrice,
    decimal DefaultTaxRate,
    bool IsActive,
    DateTime CreatedAt
);

public record CreateProductRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    decimal UnitPrice,
    decimal DefaultTaxRate
);

public record UpdateProductRequest(
    string? Code,
    string? Name,
    string? NameAr,
    decimal? UnitPrice,
    decimal? DefaultTaxRate,
    bool? IsActive
);

internal record ProductRow(
    Guid id,
    Guid company_id,
    string code,
    string name,
    string? name_ar,
    decimal unit_price,
    decimal default_tax_rate,
    bool is_active,
    DateTime created_at
);

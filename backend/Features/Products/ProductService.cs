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
        // Sprint 50 — left join to accounts so the DTO can return
        // the default account's code + name for the UI. We still
        // need the product row even if there's no default account
        // (e.g. a brand-new product not yet categorised).
        var rows = (await conn.QueryAsync<ProductRowWithAccount>(@"
            SELECT p.id, p.company_id, p.code, p.name, p.name_ar,
                   p.unit_price, p.default_tax_rate, p.is_active, p.created_at,
                   p.category, p.default_account_id,
                   a.code AS default_account_code,
                   a.name AS default_account_name
            FROM products p
            LEFT JOIN accounts a ON a.id = p.default_account_id
            WHERE p.company_id = @companyId
              AND (@includeInactive OR p.is_active = true)
            ORDER BY p.code;",
            new { companyId, includeInactive })).ToList();
        return rows.Select(r => new ProductDto(
            r.id, r.company_id, r.code, r.name, r.name_ar,
            r.unit_price, r.default_tax_rate, r.is_active, r.created_at,
            r.category, r.default_account_id, r.default_account_code, r.default_account_name
        )).ToList();
    }

    public async Task<ProductDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ProductRowWithAccount>(@"
            SELECT p.id, p.company_id, p.code, p.name, p.name_ar,
                   p.unit_price, p.default_tax_rate, p.is_active, p.created_at,
                   p.category, p.default_account_id,
                   a.code AS default_account_code,
                   a.name AS default_account_name
            FROM products p
            LEFT JOIN accounts a ON a.id = p.default_account_id
            WHERE p.id = @id;", new { id });
        if (row is null) return null;
        return new ProductDto(
            row.id, row.company_id, row.code, row.name, row.name_ar,
            row.unit_price, row.default_tax_rate, row.is_active, row.created_at,
            row.category, row.default_account_id, row.default_account_code, row.default_account_name
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
                INSERT INTO products (id, company_id, code, name, name_ar, unit_price, default_tax_rate, is_active, category, default_account_id)
                VALUES (@id, @companyId, @code, @name, @nameAr, @unitPrice, @taxRate, true, @category, @defaultAccountId);",
                new
                {
                    id,
                    companyId = req.CompanyId,
                    code = req.Code.Trim(),
                    name = req.Name.Trim(),
                    nameAr = req.NameAr?.Trim(),
                    unitPrice = req.UnitPrice,
                    taxRate = req.DefaultTaxRate,
                    // Sprint 50 — write empty string as NULL to keep the
                    // column "unset" rather than "" for un-categorised
                    // products. The application treats "" as null.
                    category = string.IsNullOrWhiteSpace(req.Category) ? null : req.Category.Trim(),
                    defaultAccountId = req.DefaultAccountId
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
            // For category: PATCH semantics where an empty string
            // means "clear" (NULL), a non-empty string means "set",
            // and a null means "don't touch". The CASE expression
            // covers all three cases in one UPDATE.
            await conn.ExecuteAsync(@"
                UPDATE products SET
                    code = COALESCE(@code, code),
                    name = COALESCE(@name, name),
                    name_ar = COALESCE(@nameAr, name_ar),
                    unit_price = COALESCE(@unitPrice, unit_price),
                    default_tax_rate = COALESCE(@taxRate, default_tax_rate),
                    is_active = COALESCE(@isActive, is_active),
                    category = CASE
                        WHEN @categoryIsProvided = true THEN
                            CASE WHEN LENGTH(TRIM(@category)) = 0 THEN NULL
                                 ELSE TRIM(@category)
                            END
                        ELSE category
                    END,
                    default_account_id = CASE
                        WHEN @defaultAccountIdIsProvided = true THEN @defaultAccountId
                        ELSE default_account_id
                    END
                WHERE id = @id;",
                new
                {
                    id,
                    code = req.Code?.Trim(),
                    name = req.Name?.Trim(),
                    nameAr = req.NameAr?.Trim(),
                    unitPrice = req.UnitPrice,
                    taxRate = req.DefaultTaxRate,
                    isActive = req.IsActive,
                    // Sentinel pattern: separate "was the field sent?"
                    // from "what was the value?" because C# nullable
                    // can't distinguish "absent" from "null" on a
                    // string. We use a "isProvided" companion param
                    // for each PATCH field we want to support.
                    category = req.Category ?? "",
                    categoryIsProvided = req.Category != null,
                    defaultAccountId = req.DefaultAccountId,
                    defaultAccountIdIsProvided = req.DefaultAccountId != null || (req.Category != null && !string.IsNullOrEmpty(req.Category))
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
    DateTime CreatedAt,
    // Sprint 50 — category + default account. The DTO returns
    // these in plain form (the Dapper row provides them). The
    // frontend uses category to render a coloured chip and the
    // default account code/name to show what 54xx the product
    // typically posts to.
    string? Category = null,
    Guid? DefaultAccountId = null,
    string? DefaultAccountCode = null,
    string? DefaultAccountName = null
);

public record CreateProductRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    decimal UnitPrice,
    decimal DefaultTaxRate,
    // Sprint 50 — optional category. Valid values: 'materials' |
    // 'labor' | 'subcontractor' | 'equipment_rental' | 'overhead' |
    // 'transport' | 'other'. Free-form in the DB so the application
    // layer can extend without a migration.
    string? Category = null,
    // Optional FK to accounts.id. The frontend pre-fills this from
    // the category dropdown (e.g. materials → 5401) but the user can
    // override for special products.
    Guid? DefaultAccountId = null
);

public record UpdateProductRequest(
    string? Code,
    string? Name,
    string? NameAr,
    decimal? UnitPrice,
    decimal? DefaultTaxRate,
    bool? IsActive,
    // Sprint 50 — see CreateProductRequest. Both fields are
    // nullable here because PATCH semantics: only set the values
    // you want to change. To clear a category, send an empty string
    // (the service treats empty as "clear").
    string? Category = null,
    Guid? DefaultAccountId = null
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
    DateTime created_at,
    // Sprint 50 — category + default account columns
    string? category,
    Guid? default_account_id
);

internal record ProductRowWithAccount(
    Guid id,
    Guid company_id,
    string code,
    string name,
    string? name_ar,
    decimal unit_price,
    decimal default_tax_rate,
    bool is_active,
    DateTime created_at,
    string? category,
    Guid? default_account_id,
    string? default_account_code,
    string? default_account_name
);

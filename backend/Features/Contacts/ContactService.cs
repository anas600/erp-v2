using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Contacts;

/// <summary>
/// Contacts service — manages the per-company catalogue of customers
/// and suppliers. Used by the invoice form to pick a known party
/// instead of typing a free-text name every time.
/// </summary>
public class ContactService
{
    private readonly IDbConnectionFactory _db;

    public ContactService(IDbConnectionFactory db) => _db = db;

    public async Task<List<ContactDto>> GetByCompanyAsync(Guid companyId, string? type = null, bool includeInactive = false)
    {
        using var conn = _db.CreateConnection();
        var sql = @"
            SELECT id, company_id, type, code, name, name_ar, tax_id, phone, email, is_active, created_at
            FROM contacts
            WHERE company_id = @companyId
              " + (type is null ? "" : "AND type = @type ") + @"
              " + (includeInactive ? "" : "AND is_active = true") + @"
            ORDER BY type, code;";
        var rows = await conn.QueryAsync<ContactRow>(sql, new { companyId, type });
        return rows.Select(Map).ToList();
    }

    public async Task<ContactDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ContactRow>(@"
            SELECT id, company_id, type, code, name, name_ar, tax_id, phone, email, is_active, created_at
            FROM contacts WHERE id = @id;", new { id });
        return row is null ? null : Map(row);
    }

    public async Task<ContactDto> CreateAsync(CreateContactRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code)) throw new InvalidOperationException("كود العميل/المورد مطلوب");
        if (string.IsNullOrWhiteSpace(req.Name)) throw new InvalidOperationException("الاسم مطلوب");
        if (req.Type != "customer" && req.Type != "supplier")
            throw new InvalidOperationException("النوع يجب أن يكون 'customer' أو 'supplier'");

        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO contacts (id, company_id, type, code, name, name_ar, tax_id, phone, email, is_active, is_demo_data)
            VALUES (@id, @companyId, @type, @code, @name, @nameAr, @taxId, @phone, @email, true, false);",
            new
            {
                id,
                companyId = req.CompanyId,
                type = req.Type,
                code = req.Code.Trim(),
                name = req.Name.Trim(),
                nameAr = req.NameAr?.Trim(),
                taxId = req.TaxId?.Trim(),
                phone = req.Phone?.Trim(),
                email = req.Email?.Trim()
            });
        return (await GetByIdAsync(id))!;
    }

    public async Task<ContactDto?> UpdateAsync(Guid id, UpdateContactRequest req)
    {
        using var conn = _db.CreateConnection();
        var existing = await conn.QuerySingleOrDefaultAsync<ContactRow>(@"
            SELECT id, company_id, type, code, name, name_ar, tax_id, phone, email, is_active, created_at
            FROM contacts WHERE id = @id;", new { id });
        if (existing is null) return null;

        await conn.ExecuteAsync(@"
            UPDATE contacts SET
                name = COALESCE(@name, name),
                name_ar = COALESCE(@nameAr, name_ar),
                tax_id = COALESCE(@taxId, tax_id),
                phone = COALESCE(@phone, phone),
                email = COALESCE(@email, email),
                is_active = COALESCE(@isActive, is_active)
            WHERE id = @id;",
            new
            {
                id,
                name = req.Name?.Trim(),
                nameAr = req.NameAr?.Trim(),
                taxId = req.TaxId?.Trim(),
                phone = req.Phone?.Trim(),
                email = req.Email?.Trim(),
                isActive = req.IsActive
            });
        return await GetByIdAsync(id);
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        // Soft delete: set is_active=false. Hard delete would lose
        // historical invoice references (the invoice's party_name
        // is free-text so we don't actually need a hard FK, but
        // preserving the contact record keeps reporting clean).
        var rows = await conn.ExecuteAsync(
            "UPDATE contacts SET is_active = false WHERE id = @id;",
            new { id });
        return rows > 0;
    }

    private static ContactDto Map(ContactRow r) => new(
        r.id, r.company_id, r.type, r.code, r.name, r.name_ar,
        r.tax_id, r.phone, r.email, r.is_active, r.created_at);

    private record ContactRow(
        Guid id, Guid company_id, string type, string code, string name, string? name_ar,
        string? tax_id, string? phone, string? email, bool is_active, DateTime created_at);
}

public record ContactDto(
    Guid Id, Guid CompanyId, string Type, string Code, string Name, string? NameAr,
    string? TaxId, string? Phone, string? Email, bool IsActive, DateTime CreatedAt);

public record CreateContactRequest(
    Guid CompanyId, string Type, string Code, string Name, string? NameAr,
    string? TaxId, string? Phone, string? Email);

public record UpdateContactRequest(
    string? Name, string? NameAr, string? TaxId, string? Phone, string? Email, bool? IsActive);

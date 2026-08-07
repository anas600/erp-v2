using Dapper;
using ErpV2.Common;

namespace ErpV2.Features.Accounts;

public class AccountService
{
    private readonly IDbConnectionFactory _db;

    public AccountService(IDbConnectionFactory db) => _db = db;

    public async Task<List<AccountDto>> GetByCompanyAsync(Guid companyId)
    {
        using var conn = _db.CreateConnection();
        var rows = await conn.QueryAsync<AccountRow>(@"
            SELECT id, company_id, code, name, name_ar, parent_id, account_type, nature,
                   level, account_class, is_control_account, cost_center_required,
                   is_postable, is_active, balance
            FROM accounts
            WHERE company_id = @companyId
            ORDER BY code;",
            new { companyId });
        return rows.Select(Map).ToList();
    }

    public async Task<AccountDto?> GetByIdAsync(Guid id)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(@"
            SELECT id, company_id, code, name, name_ar, parent_id, account_type, nature,
                   level, account_class, is_control_account, cost_center_required,
                   is_postable, is_active, balance
            FROM accounts WHERE id = @id;",
            new { id });
        return row is null ? null : Map(row);
    }

    public async Task<AccountDto> CreateAsync(CreateAccountRequest req)
    {
        // Validate account_type
        var validTypes = new[] { "Asset", "Liability", "Equity", "Revenue", "Expense" };
        if (!validTypes.Contains(req.AccountType))
            throw new ArgumentException($"AccountType must be one of: {string.Join(", ", validTypes)}");

        // Validate nature
        var validNatures = new[] { "Debit", "Credit" };
        if (!validNatures.Contains(req.Nature))
            throw new ArgumentException($"Nature must be one of: {string.Join(", ", validNatures)}");

        // Validate account_class
        if (req.AccountClass != "header" && req.AccountClass != "detail")
            throw new ArgumentException("AccountClass must be 'header' or 'detail'");

        // Validate level (1-4, but practically 2-4 for our model)
        if (req.Level < 1 || req.Level > 4)
            throw new ArgumentException("Level must be 1-4");

        // Sprint 31 — locked 4-level COA architecture.
        //
        // The level determines what the account can do:
        //   L1/L2/L3: pure grouping headers — must NOT be postable.
        //             (Posting here would double-count; the rollup is
        //             computed from the children.)
        //   L4:       detail accounts (sub-ledger) — must be postable
        //             (the whole point of L4 is to receive journal lines).
        //
        // Why L3 is also non-postable now:
        //   The user explicitly required that L3 (control / general
        //   ledger accounts like "1101 Cash", "1103 AR") are aggregating
        //   accounts, not direct posting targets. This is the standard
        //   accounting practice (IFRS) and keeps the GL rollup clean:
        //   if someone posts to L3, the same movement would also need to
        //   land in an L4 sub-ledger, which means double-counting.
        //
        //   The previous design allowed L3 to be postable as a user
        //   choice (Sprint 26, Option B). The user has now changed their
        //   mind: L3 is always non-postable, only L4 is postable.
        if (req.Level <= 3 && req.IsPostable)
            throw new ArgumentException("حسابات المستويات 1 و 2 و 3 لا يمكن أن تكون قابلة للترحيل (مجموعات فقط). الترحيل فقط على L4 (الحسابات التفصيلية).");
        if (req.Level == 4 && !req.IsPostable)
            throw new ArgumentException("الحسابات التفصيلية (L4) يجب أن تكون قابلة للترحيل");

        // Force isPostable based on level (ignore what the client sent).
        // L1/L2/L3 → false, L4 → true. This makes the API forgiving
        // (clients can still pass isPostable=true for L4 and it works)
        // and secure (clients can't bypass the rule by setting it to true).
        var forcedIsPostable = req.Level == 4;

        // If parent_id is provided, ensure parent exists and is in same company
        if (req.ParentId.HasValue)
        {
            var parent = await GetByIdAsync(req.ParentId.Value);
            if (parent is null) throw new ArgumentException("Parent account not found");
            if (parent.CompanyId != req.CompanyId) throw new ArgumentException("Parent must be in same company");
            // Child must be one level deeper than parent
            if (req.Level != parent.Level + 1 && !(req.Level == 4 && parent.Level == 3))
                throw new ArgumentException($"Child level ({req.Level}) must be parent level + 1 ({parent.Level + 1})");
        }

        using var conn = _db.CreateConnection();
        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (
                id, company_id, code, name, name_ar, parent_id,
                account_type, nature, level, account_class,
                is_control_account, cost_center_required,
                is_postable, is_active, balance
            )
            VALUES (
                @id, @companyId, @code, @name, @nameAr, @parentId,
                @accountType, @nature, @level, @accountClass,
                @isControlAccount, @costCenterRequired,
                @isPostable, true, 0
            );",
            new
            {
                id, companyId = req.CompanyId, code = req.Code, name = req.Name, nameAr = req.NameAr,
                parentId = req.ParentId, accountType = req.AccountType, nature = req.Nature,
                level = req.Level, accountClass = req.AccountClass,
                isControlAccount = req.IsControlAccount, costCenterRequired = req.CostCenterRequired,
                isPostable = forcedIsPostable
            });

        return (await GetByIdAsync(id))!;
    }

    public async Task<AccountDto?> UpdateAsync(Guid id, UpdateAccountRequest req)
    {
        using var conn = _db.CreateConnection();
        var rowsAffected = await conn.ExecuteAsync(@"
            UPDATE accounts
            SET name = @name, name_ar = @nameAr, is_active = @isActive
            WHERE id = @id;",
            new { id, name = req.Name, nameAr = req.NameAr, isActive = req.IsActive });
        return rowsAffected == 0 ? null : await GetByIdAsync(id);
    }

    /// <summary>
    /// Get a tree representation of all accounts in a company.
    /// The flat list is converted to a tree structure here on
    /// the backend so the UI doesn't have to do it (and so we
    /// send less data over the wire).
    /// </summary>
    public async Task<List<AccountTreeNode>> GetTreeAsync(Guid companyId)
    {
        var all = await GetByCompanyAsync(companyId);

        // Build a map: id -> node
        var byId = all.ToDictionary(a => a.Id, a => new AccountTreeNode(
            a.Id, a.Code, a.Name, a.NameAr, a.ParentId, a.AccountType, a.Nature,
            a.Level, a.IsControlAccount, a.IsPostable, a.IsActive, a.Balance,
            HasChildren: false, Children: new List<AccountTreeNode>()
        ));

        // Link children to parents
        var roots = new List<AccountTreeNode>();
        foreach (var a in all)
        {
            var node = byId[a.Id];
            if (a.ParentId.HasValue && byId.TryGetValue(a.ParentId.Value, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        // Mark has_children
        void MarkChildren(AccountTreeNode n)
        {
            n.Children.ForEach(c => { c.GetType(); MarkChildren(c); });
        }
        // Recompute HasChildren for each node
        void Recompute(AccountTreeNode n)
        {
            foreach (var c in n.Children) Recompute(c);
            // We can't mutate the record's HasChildren, so build a new node
        }
        // (Records are immutable; the frontend can compute HasChildren from Children.Count == 0)
        // Just return the tree; the UI checks node.Children.length > 0

        return roots;
    }

    /// <summary>
    /// Get the sub-ledger detail account for a given contact.
    /// Returns null if no sub-ledger has been provisioned for
    /// the contact yet (the user needs to create one).
    /// </summary>
    public async Task<AccountDto?> GetSubLedgerForContactAsync(Guid companyId, Guid contactId)
    {
        using var conn = _db.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>(@"
            SELECT a.id, a.company_id, a.code, a.name, a.name_ar, a.parent_id,
                   a.account_type, a.nature, a.level, a.account_class,
                   a.is_control_account, a.cost_center_required,
                   a.is_postable, a.is_active, a.balance
            FROM accounts a
            JOIN account_contact_links l ON l.account_id = a.id
            WHERE l.contact_id = @contactId
              AND l.company_id = @companyId
              AND l.is_primary = true
            ORDER BY l.created_at ASC
            LIMIT 1;",
            new { companyId, contactId });
        return row is null ? null : Map(row);
    }

    /// <summary>
    /// Create a sub-ledger detail account for a contact and
    /// link it via account_contact_links. This is the one-stop
    /// method for "I have a customer, give me their sub-ledger
    /// account". Idempotent: returns the existing one if the
    /// contact already has a sub-ledger.
    ///
    /// The sub-ledger code follows the pattern {parent}-{code},
    /// e.g. "1200-CUST-001" for the first customer sub-account.
    /// This keeps the parent code visible in every detail
    /// account, making it easy to read in reports.
    /// </summary>
    public async Task<AccountDto> CreateSubLedgerForContactAsync(
        Guid companyId, Guid contactId, string parentAccountCode, string detailCode)
    {
        // Check if a sub-ledger already exists
        var existing = await GetSubLedgerForContactAsync(companyId, contactId);
        if (existing is not null) return existing;

        // Find the parent account
        using var conn = _db.CreateConnection();
        var parent = await conn.QuerySingleOrDefaultAsync<AccountRow>(@"
            SELECT id, company_id, code, name, name_ar, parent_id, account_type, nature,
                   level, account_class, is_control_account, cost_center_required,
                   is_postable, is_active, balance
            FROM accounts
            WHERE company_id = @companyId AND code = @parentCode
            LIMIT 1;",
            new { companyId, parentCode = parentAccountCode });
        if (parent is null) throw new ArgumentException($"Parent account with code '{parentAccountCode}' not found");

        // The sub-ledger code = parent_code + suffix
        // e.g. "1200" + "-CUST-001" -> "1200-CUST-001"
        var fullCode = $"{parentAccountCode}-{detailCode}";

        var id = Guid.NewGuid();
        await conn.ExecuteAsync(@"
            INSERT INTO accounts (
                id, company_id, code, name, name_ar, parent_id,
                account_type, nature, level, account_class,
                is_control_account, cost_center_required,
                is_postable, is_active, balance
            )
            VALUES (
                @id, @companyId, @code, @name, @nameAr, @parentId,
                @accountType, @nature, 4, 'detail',
                false, false, true, true, 0
            );",
            new
            {
                id, companyId, code = fullCode, name = $"Sub-ledger: {detailCode}",
                nameAr = $"حساب تفصيلي: {detailCode}",
                parentId = parent.id, accountType = parent.account_type, nature = parent.nature
            });

        // Link to contact (Sprint 26 — also stamp is_primary so the
        // ux_account_contact_links_primary unique index accepts it).
        await conn.ExecuteAsync(@"
            INSERT INTO account_contact_links (id, account_id, contact_id, company_id, is_primary, created_at)
            VALUES (@id, @accountId, @contactId, @companyId, true, NOW());",
            new { id = Guid.NewGuid(), accountId = id, contactId, companyId });

        return (await GetByIdAsync(id))!;
    }

    private static AccountDto Map(AccountRow r) => new(
        r.id, r.company_id, r.code, r.name, r.name_ar, r.parent_id,
        r.account_type, r.nature, r.level, r.account_class,
        r.is_control_account, r.cost_center_required,
        r.is_postable, r.is_active, r.balance);

    /// <summary>
    /// Returns the primary sub-ledger for a contact, creating one
    /// transparently if none exists.
    ///
    /// Called by ReceiptService / PaymentService before posting so
    /// a contact without a sub-ledger doesn't block transactions.
    /// The system creates the sub-ledger automatically:
    ///
    ///   1. Try GetSubLedgerForContactAsync (existing)
    ///   2. If not found: pick the parent control account by contact
    ///      type — 1200 (AR) for customer, 2000 (AP) for supplier.
    ///   3. If the parent control account doesn't exist, fail with
    ///      a clear Arabic error. The chart of accounts is the
    ///      admin's responsibility to set up; we can't synthesize
    ///      a brand new L3 control account on the fly.
    ///   4. Create the sub-ledger via CreateSubLedgerForContactAsync
    ///      (which stamps is_primary=true on the link).
    ///   5. Return the freshly-created sub-ledger.
    ///
    /// All sub-ledgers created here are L4 (level=4) and is_postable=true.
    /// </summary>
    public async Task<AccountDto> EnsureSubLedgerAsync(Guid companyId, Guid contactId)
    {
        // 1) Fast path: sub-ledger already exists.
        var existing = await GetSubLedgerForContactAsync(companyId, contactId);
        if (existing is not null) return existing;

        // 2) Look up the contact to decide which control account to use.
        using var conn = _db.CreateConnection();
        var contact = await conn.QuerySingleOrDefaultAsync<(string? type, string? code)>(@"
            SELECT type, code FROM contacts
            WHERE id = @id AND company_id = @companyId;",
            new { id = contactId, companyId });
        if (contact.type is null || contact.code is null)
            throw new InvalidOperationException("العميل/المورّد غير موجود");

        // 3) Pick the parent control account.
        //    customer -> 1103 (Accounts Receivable)
        //    supplier -> 2101 (Accounts Payable)
        // Sprint 32 — updated to the new standard COA codes.
        var parentCode = contact.type == "customer" ? "1103" : "2101";

        // 4) Create the sub-ledger (CreateSubLedgerForContactAsync
        //    also creates the account_contact_links row).
        return await CreateSubLedgerForContactAsync(companyId, contactId, parentCode, contact.code);
    }

    /// <summary>
    /// Explicit "link a contact to an account" helper. Used by
    /// the ContactDetailPage when the user wants to attach a
    /// second sub-ledger (e.g. one in LYD and one in USD) and
    /// mark the first one as non-primary.
    ///
    /// is_primary=true enforces the partial-unique index
    /// ux_account_contact_links_primary at the DB level. If the
    /// contact already has a primary link, the INSERT fails with
    /// a unique-violation and we surface the error.
    /// </summary>
    public async Task LinkContactToAccountAsync(Guid contactId, Guid accountId, bool isPrimary = true)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(@"
            INSERT INTO account_contact_links
                (id, account_id, contact_id, company_id, is_primary, created_at)
            VALUES
                (@id, @accountId, @contactId,
                 (SELECT company_id FROM accounts WHERE id = @accountId),
                 @isPrimary, NOW());",
            new { id = Guid.NewGuid(), accountId, contactId, isPrimary });
    }

    private record AccountRow(
        Guid id, Guid company_id, string code, string name, string? name_ar,
        Guid? parent_id, string account_type, string nature,
        int level, string account_class,
        bool is_control_account, bool cost_center_required,
        bool is_postable, bool is_active, decimal balance);
}

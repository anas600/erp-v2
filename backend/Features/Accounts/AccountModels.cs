namespace ErpV2.Features.Accounts;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Revenue,
    Expense
}

public enum AccountNature
{
    Debit,
    Credit
}

/// <summary>
/// The 4-level chart-of-accounts hierarchy:
///
///   Level 1 (logical type) — Asset / Liability / Equity / Revenue / Expense
///     These are NOT stored as accounts; the type comes from
///     `accountType` on every account. Used only for grouping
///     in the UI tree.
///
///   Level 2 (category) — e.g. "Current Assets", "Fixed Assets"
///     Header accounts. No postings. account_class = 'header'.
///
///   Level 3 (sub-category / operational account) — e.g. "Cash",
///     "Bank", "Accounts Receivable". The 18 accounts from the
///     seed (1000, 1100, 1200, ...) are at this level. Most of
///     them accept postings directly.
///
///   Level 4 (detail / sub-ledger) — e.g. "AR - Customer CUST-001"
///     Linked 1:1 to a contact via account_contact_links. The
///     Posting Engine routes receipts/payments to these detail
///     accounts based on which customer/supplier is involved.
///
/// 'control_account' is a separate flag (not a level). The AR
/// control account 1200 and AP control account 2000 stay at
/// level 3 but are flagged is_control_account=true. Once detail
/// sub-ledger accounts exist for a customer/supplier, postings
/// go to the detail account — never to the control account.
/// </summary>
public enum AccountClass
{
    /// <summary>Level 2 header. No postings allowed. Used for grouping.</summary>
    Header,
    /// <summary>Level 3 sub-category. Accepts postings (default).</summary>
    Detail
}

public record AccountDto(
    Guid Id,
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    Guid? ParentId,
    string AccountType,
    string Nature,
    int Level,
    string AccountClass,
    bool IsControlAccount,
    bool CostCenterRequired,
    /// <summary>
    /// Sprint 26 — whether the Posting Engine accepts journal lines
    /// against this account. L1/L2 are always false (pure grouping
    /// headers); L4 is always true (detail accounts are by definition
    /// postable); L3 is user-overrideable.
    /// </summary>
    bool IsPostable,
    bool IsActive,
    decimal Balance
);

public record CreateAccountRequest(
    Guid CompanyId,
    string Code,
    string Name,
    string? NameAr,
    Guid? ParentId,
    string AccountType,
    string Nature,
    int Level = 3,
    string AccountClass = "detail",
    bool IsControlAccount = false,
    bool CostCenterRequired = false,
    /// <summary>
    /// Sprint 26 — defaults to true. Validated by AccountService:
    ///   L1/L2: must be false
    ///   L4:    must be true
    ///   L3:    user choice (default true for "operational account")
    /// </summary>
    bool IsPostable = true
);

public record UpdateAccountRequest(
    string Name,
    string? NameAr,
    bool IsActive
);

/// <summary>
/// Tree node for the UI: a flat list of accounts with
/// parent_id resolved into a nested structure. The frontend
/// builds the tree from this.
///
/// Why a custom DTO: the existing accounts.flat list is fine
/// for a table, but for a tree view we want the indentation
/// level pre-computed (saves the UI from doing it) and we
/// want a flag for "has children" so the UI can render a
/// chevron.
/// </summary>
public record AccountTreeNode(
    Guid Id,
    string Code,
    string Name,
    string? NameAr,
    /// <summary>
    /// Parent account id (null = top-level L1 category).
    ///
    /// Sprint 33 hotfix: this field was missing in the original
    /// record, which meant the frontend's `flatten()`+`buildTree()`
    /// cycle had no way to rebuild the hierarchy and rendered all
    /// 86 accounts as a flat list. The frontend can now rebuild
    /// the parent→children tree from the parentId on each node.
    /// </summary>
    Guid? ParentId,
    string AccountType,
    string Nature,
    int Level,
    bool IsControlAccount,
    /// <summary>
    /// Sprint 26 — surfaced in the tree so the UI can render a
    /// badge (e.g. "غير قابل للترحيل" for L1/L2 headers).
    /// </summary>
    bool IsPostable,
    bool IsActive,
    decimal Balance,
    bool HasChildren,
    List<AccountTreeNode> Children
);

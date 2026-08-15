namespace ErpV2.Features.Projects;

/// <summary>
/// One sub-measurement row inside a FMB entry.
/// For "real" measurements: count × length × width × height =
/// initialQty. For "deduction" rows: deduction is the only value.
///
///   {
///     "label": "الواجهة الجنوبية",
///     "count": 1, "length": 33.8, "width": null, "height": 3.0,
///     "initialQty": 101.4, "deduction": null,
///     "notes": null
///   }
/// </summary>
public record MeasurementRow(
    string? Label,
    decimal? Count,
    decimal? Length,
    decimal? Width,
    decimal? Height,
    decimal? InitialQty,
    decimal? Deduction,
    string? Notes
);

/// <summary>
/// One BOQ line item's measurements inside an FMB. The list of
/// MeasurementRow sub-rows is stored as JSONB in the DB.
/// </summary>
public record FieldMeasurementEntryDto(
    Guid Id,
    Guid FmbId,
    Guid LineItemId,
    int LineNumber,
    string Description,
    string Unit,
    List<MeasurementRow> Measurements,
    decimal InitialTotal,
    decimal DeductionsTotal,
    decimal FinalTotal,
    decimal UnitPrice,
    decimal Amount,
    string? Notes
);

/// <summary>
/// The Field Measurement Book header (دفتر المقاسات / الدفتر الفني).
/// </summary>
public record FieldMeasurementBookDto(
    Guid Id,
    Guid CompanyId,
    Guid ProjectId,
    Guid? ContractId,
    string BookNumber,
    DateTime MeasurementDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    Guid? EngineerUserId,
    string? EngineerName,
    Guid? ConsultantUserId,
    string? ConsultantName,
    string Status,                  // "DRAFT" | "SUBMITTED" | "APPROVED" | "CANCELLED"
    DateTime? ApprovedAt,
    string? Notes,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<FieldMeasurementEntryDto> Entries
);

/// <summary>
/// Body for POST /api/projects/{id}/field-measurement-books
/// </summary>
public record CreateFieldMeasurementBookRequest(
    string BookNumber,
    DateTime MeasurementDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    Guid? ContractId,
    Guid? EngineerUserId,
    string? EngineerName,
    Guid? ConsultantUserId,
    string? ConsultantName,
    string? Notes
);

public record UpdateFieldMeasurementBookRequest(
    string BookNumber,
    DateTime MeasurementDate,
    DateTime? PeriodFrom,
    DateTime? PeriodTo,
    Guid? EngineerUserId,
    string? EngineerName,
    Guid? ConsultantUserId,
    string? ConsultantName,
    string? Notes
);

public record CreateFieldMeasurementEntryRequest(
    Guid LineItemId,
    List<MeasurementRow> Measurements,
    string? Notes
);

public record UpdateFieldMeasurementEntryRequest(
    List<MeasurementRow> Measurements,
    string? Notes
);

public record SubmitFmbRequest(string? Comments);
public record ApproveFmbRequest(string? Comments);
public record RejectFmbRequest(string Reason);

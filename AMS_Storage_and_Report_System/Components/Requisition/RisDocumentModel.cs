namespace AMS_Storage_and_Report_System.Components.Requisition;

/// One model drives the live cart preview, the admin review screen and the print/PDF.
/// Build it from the cart (draft) or from ris_transactions + ris_items (submitted).
public sealed record RisDocumentModel
{
    public required string EntityName { get; init; }
    public required string FundCluster { get; init; }
    public required string Division { get; init; }
    public required string Office { get; init; }
    public string? ResponsibilityCenterCode { get; init; }
    public string? RisNo { get; init; }              // null while still a cart
    public string? Purpose { get; init; }
    public string? Status { get; init; }
    public IReadOnlyList<RisLine> Lines { get; init; } = [];

    public RisSignatory? RequestedBy { get; init; }
    public RisSignatory? ApprovedBy { get; init; }
    public RisSignatory? IssuedBy { get; init; }
    public RisSignatory? ReceivedBy { get; init; }
}

public sealed record RisLine(
    int ItemId,
    string StockNo,
    string Unit,
    string Description,
    int RequestedQty,
    bool? StockAvailable = null,
    int? IssuedQty = null,
    string? Remarks = null);

public sealed record RisSignatory(string? PrintedName, string? Designation, DateTime? Date);

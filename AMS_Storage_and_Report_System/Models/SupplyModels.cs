namespace AMS_Storage_and_Report_System.Models;

// Tables created by the AMS Supplies portal update (Database/001_requisition_portal.sql).
// Unsigned MySQL ids are mapped to int to match Office/RoPersonnel in this project.

public static class RisStatus
{
    public const string Pending = "PendingApproval";
    public const string Approved = "ApprovedForIssuance";
    public const string Issued = "Issued";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";

    public static string Label(string status) => status switch
    {
        Pending => "Pending approval",
        Approved => "Approved for issuance",
        Issued => "Issued",
        Rejected => "Rejected",
        Cancelled => "Cancelled",
        _ => status,
    };

    public static string Badge(string status) => status switch
    {
        Pending => "badge-gold",
        Approved => "badge-navy",
        Issued => "badge-ok",
        Rejected => "badge-bad",
        _ => "badge-grey",
    };
}

public class SupplyItem
{
    public int ItemId { get; set; }
    public string StockNo { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string? Specifications { get; set; }
    public string UnitOfMeasure { get; set; } = "";
    public int? CategoryId { get; set; }
    public int? SupplierId { get; set; }
    public decimal UnitPrice { get; set; }
    public string? ImagePath { get; set; }
    public int StockOnHand { get; set; }
    public int ReorderLevel { get; set; }
    public bool IsActive { get; set; }

    public ItemCategory? Category { get; set; }
    public Supplier? Supplier { get; set; }
}

public class ItemCategory
{
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public string? Icon { get; set; }
    public int SortOrder { get; set; }
}

public class Supplier
{
    public int SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    public string? ContactPerson { get; set; }
    public string? ContactNo { get; set; }
    public bool IsActive { get; set; } = true;
}

/// Item photo shown on the AMS Supplies catalog (table item_images).
public class ItemImage
{
    public int ItemId { get; set; }
    public string ContentType { get; set; } = "";
    public byte[] Content { get; set; } = [];
}

public class SupplySuggestion
{
    public int SuggestionId { get; set; }
    public int? OfficeId { get; set; }
    public uint? UserId { get; set; }
    public string? SubmitterName { get; set; }
    public string? SubmitterEmail { get; set; }
    public string ItemName { get; set; } = "";
    public string? Description { get; set; }
    public string? Justification { get; set; }
    public int? EstimatedAnnualQty { get; set; }
    public string Status { get; set; } = "New";
    public string? AdminResponse { get; set; }
    public uint? ReviewedBy { get; set; }
    public DateTime? ReviewedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Office? Office { get; set; }
}

public class AppCseUpload
{
    public int UploadId { get; set; }
    public int FiscalYear { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int RowCount { get; set; }
    public string? Notes { get; set; }
    public uint UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
}

public class AppCseAllocation
{
    public int AllocationId { get; set; }
    public int OfficeId { get; set; }
    public int ItemId { get; set; }
    public int FiscalYear { get; set; }
    public int AllocatedQty { get; set; }
    public int? Q1Qty { get; set; }
    public int? Q2Qty { get; set; }
    public int? Q3Qty { get; set; }
    public int? Q4Qty { get; set; }
    public int IssuedQty { get; set; }
    public int? UploadId { get; set; }

    public Office? Office { get; set; }
    public SupplyItem? Item { get; set; }
}

public class RisTransaction
{
    public int RisId { get; set; }
    public string RisNo { get; set; } = "";
    public int OfficeId { get; set; }
    public int FiscalYear { get; set; }
    public string EntityName { get; set; } = "";
    public string FundCluster { get; set; } = "";
    public string DivisionName { get; set; } = "";
    public string OfficeName { get; set; } = "";
    public string? ResponsibilityCenterCode { get; set; }
    public string Purpose { get; set; } = "";
    public string Status { get; set; } = RisStatus.Pending;
    public uint RequestedByUserId { get; set; }
    public int? RequestedByPersonnelId { get; set; }
    public DateTime RequestedAt { get; set; }
    public uint? ApprovedByUserId { get; set; }
    public int? ApprovedByPersonnelId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public int? IssuedByPersonnelId { get; set; }
    public int? ReceivedByPersonnelId { get; set; }
    public DateTime? IssuedAt { get; set; }
    public uint? RejectedByUserId { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectionReason { get; set; }

    public Office? Office { get; set; }
    public RoPersonnel? RequestedByPersonnel { get; set; }
    public RoPersonnel? ApprovedByPersonnel { get; set; }
    public RoPersonnel? IssuedByPersonnel { get; set; }
    public RoPersonnel? ReceivedByPersonnel { get; set; }
    public List<RisItem> Items { get; set; } = [];
    public List<RisStatusHistory> History { get; set; } = [];

    public decimal EstimatedAmount => Items.Sum(i => i.UnitCost * (i.IssuedQty ?? i.RequestedQty));
}

public class RisItem
{
    public int RisItemId { get; set; }
    public int RisId { get; set; }
    public int ItemId { get; set; }
    public int LineNo { get; set; }
    public string StockNo { get; set; } = "";
    public string UnitOfMeasure { get; set; } = "";
    public string ItemDescription { get; set; } = "";
    public decimal UnitCost { get; set; }
    public int RequestedQty { get; set; }
    public bool? StockAvailable { get; set; }
    public int? IssuedQty { get; set; }
    public string? Remarks { get; set; }

    public SupplyItem? Item { get; set; }
}

public class RisStatusHistory
{
    public long HistoryId { get; set; }
    public int RisId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public uint ChangedBy { get; set; }
    public string? Note { get; set; }
    public DateTime ChangedAt { get; set; }

    public User? ChangedByUser { get; set; }
}

public class StockMovement
{
    public long MovementId { get; set; }
    public int ItemId { get; set; }
    public string MovementType { get; set; } = "";
    public int Quantity { get; set; }
    public int BalanceAfter { get; set; }
    public decimal? UnitCost { get; set; }
    public string? ReferenceType { get; set; }
    public string? ReferenceNo { get; set; }
    public int? RisId { get; set; }
    public int? OfficeId { get; set; }
    public string? Remarks { get; set; }
    public uint PerformedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// The wet-signed RIS scanned to PDF by AMS at issuance (table ris_signed_copies).
public class RisSignedCopy
{
    public int RisId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public byte[] Content { get; set; } = [];
    public int SizeBytes { get; set; }
    public uint UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
}

public class SystemSetting
{
    public string SettingKey { get; set; } = "";
    public string SettingValue { get; set; } = "";
}

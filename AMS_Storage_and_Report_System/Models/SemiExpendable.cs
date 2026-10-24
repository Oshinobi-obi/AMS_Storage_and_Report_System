namespace AMS_Storage_and_Report_System.Models;

/// A delivery of semi-expendable property (SPLV or SPHV), table se_deliveries.
public class SeDelivery
{
    public int DeliveryId { get; set; }
    public string Category { get; set; } = SeCategory.Splv;
    public DateTime DeliveredOn { get; set; }
    public int? SupplierId { get; set; }
    public string SupplierName { get; set; } = "";
    /// Who the items are for: "Office", "Personnel", or null if not recorded.
    public string? EndUserType { get; set; }
    public string? ReferenceNo { get; set; }
    public string? Remarks { get; set; }
    public decimal TotalAmount { get; set; }
    public uint? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<SeDeliveryLine> Lines { get; set; } = [];
}

/// One item of a delivery, with the ICS or PAR number it got, table se_delivery_items.
public class SeDeliveryLine
{
    public int LineId { get; set; }
    public int DeliveryId { get; set; }
    public int LineNo { get; set; }
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitCost { get; set; }
    /// End user of this item: an office or an RO personnel (the name is saved as it was when recorded).
    public int? OfficeId { get; set; }
    public int? PersonnelId { get; set; }
    public string? EndUser { get; set; }
    public string Category { get; set; } = SeCategory.Splv;
    public string DocType { get; set; } = SeCategory.Ics;
    public int DocYear { get; set; }
    public int SeqStart { get; set; }
    public int SeqEnd { get; set; }
    public string DocNo { get; set; } = "";

    public decimal TotalCost => Quantity * UnitCost;
}

/// Last running number used for one year and series, table se_number_series.
public class SeNumberSeries
{
    public int DocYear { get; set; }
    public string Category { get; set; } = SeCategory.Splv;
    public string DocType { get; set; } = SeCategory.Ics;
    public int LastSeq { get; set; }
}

public static class SeCategory
{
    public const string Splv = "SPLV", Sphv = "SPHV";
    public const string Ics = "ICS", Par = "PAR";
    public const string EndUserOffice = "Office", EndUserPersonnel = "Personnel";

    public static string EndUserLabel(string? type) => type switch
    {
        EndUserOffice => "Office", EndUserPersonnel => "End user", _ => "End user",
    };

    /// SPHV items that cost this much or more get a PAR instead of an ICS.
    public const decimal ParThreshold = 50_000m;
    /// SPLV is for items that cost this much or less (only shown as a reminder, not enforced).
    public const decimal SplvLimit = 5_000m;

    public static string Label(string category) => category == Sphv
        ? "Semi-Expendable Property, High Value (SPHV)"
        : "Semi-Expendable Property, Low Value (SPLV)";

    /// SPLV gets an ICS. SPHV gets an ICS below ₱50,000, and a PAR at ₱50,000 and up.
    public static string DocTypeFor(string category, decimal unitCost) =>
        category == Sphv && unitCost >= ParThreshold ? Par : Ics;

    /// The three numbering series, in the order they're shown.
    public static readonly IReadOnlyList<(string Category, string DocType)> Series =
        [(Splv, Ics), (Sphv, Ics), (Sphv, Par)];

    /// 2026-01-001, or 2026-01-001-007 when the line has several units.
    public static string Format(int year, int month, int start, int end) =>
        end > start ? $"{year}-{month:00}-{start:000}-{end:000}" : $"{year}-{month:00}-{start:000}";
}

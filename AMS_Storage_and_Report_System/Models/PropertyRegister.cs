namespace AMS_Storage_and_Report_System.Models;

/// A property in the register (table property_items): one item, or one lot of identical
/// semi-expendable items, with its status, cost and the person accountable for it.
public class PropertyItem
{
    public int PropertyId { get; set; }
    public string PropertyNo { get; set; } = "";
    public string Description { get; set; } = "";
    public string Classification { get; set; } = "";
    public int PropertyTypeId { get; set; } = PropertyStatus.PpeTypeId;
    public int Quantity { get; set; } = 1;
    public decimal UnitCost { get; set; }
    public DateTime? AcquiredOn { get; set; }
    public int? OfficeId { get; set; }
    public int? PersonnelId { get; set; }
    public string Status { get; set; } = PropertyStatus.Serviceable;
    public int? DocumentId { get; set; }
    public string? Remarks { get; set; }
    public uint? CreatedBy { get; set; }

    public Office? Office { get; set; }
    public RoPersonnel? Personnel { get; set; }
    public PropertyType? PropertyType { get; set; }

    public decimal TotalCost => Quantity * UnitCost;
}

/// The statuses AMS tracks, in the order they are shown, with their chart colours.
public static class PropertyStatus
{
    public const int SemiExpendableTypeId = 1, PpeTypeId = 2;

    public const string Serviceable = "Serviceable";

    public sealed record Info(string Name, string Color, string Description);

    public static readonly IReadOnlyList<Info> All =
    [
        new("Serviceable",         "#1e9e5a", "In use and working"),
        new("For Repair",          "#e0a400", "Needs repair, not yet sent"),
        new("Under Repair",        "#2f6fde", "Being repaired"),
        new("For Disposal",        "#f07a2a", "Waiting for disposal"),
        new("Unserviceable",       "#d63a3a", "No longer usable"),
        new("Missing/Unaccounted", "#9b7fd4", "Can't be located"),
        new("Transferred",         "#6f7a90", "Moved to another office or person"),
        new("Returned",            "#3aa1a6", "Returned to AMS"),
        new("Newly Acquired",      "#19a7c8", "Received, not yet issued"),
    ];

    public static string Color(string status) => All.FirstOrDefault(s => s.Name == status)?.Color ?? "#6f7a90";
    public static bool IsValid(string status) => All.Any(s => s.Name == status);

    public static readonly IReadOnlyList<string> Classifications =
    [
        "ICT Equipment",
        "Office Equipment",
        "Furniture and Fixtures",
        "Communication Equipment",
        "Technical and Scientific Equipment",
        "Disaster Response and Rescue Equipment",
        "Motor Vehicles",
        "Books",
        "Other Machinery and Equipment",
        "Other Property",
    ];
}

namespace AMS_Storage_and_Report_System.Models;

/// A property in the register (table property_items): one item, or one lot of the same
/// semi-expendable items, with its status, cost and the person accountable for it.
public class PropertyItem
{
    public int PropertyId { get; set; }
    public string PropertyNo { get; set; } = "";
    public string Description { get; set; } = "";
    public string Classification { get; set; } = "";
    /// The kind of equipment (Laptop, Printer, Office chair...), see PropertyStatus.EquipmentTypes.
    public string? EquipmentType { get; set; }
    public int PropertyTypeId { get; set; } = PropertyStatus.PpeTypeId;
    public int Quantity { get; set; } = 1;
    public decimal UnitCost { get; set; }
    public DateTime? AcquiredOn { get; set; }
    public int? OfficeId { get; set; }
    public int? PersonnelId { get; set; }
    public string Status { get; set; } = PropertyStatus.Serviceable;
    /// The PAR / ICS this property is on.
    public int? DocumentId { get; set; }
    /// The status it had before its PAR / ICS was returned, so "Mark as not returned" can put it back.
    public string? StatusBeforeReturn { get; set; }
    public string? Remarks { get; set; }
    public uint? CreatedBy { get; set; }

    public Office? Office { get; set; }
    public RoPersonnel? Personnel { get; set; }
    public PropertyType? PropertyType { get; set; }

    public decimal TotalCost => Quantity * UnitCost;
}

/// The statuses AMS tracks, in the order they're shown, with their chart colors.
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

    /// Kinds of equipment, grouped by classification. I show these when uploading a PAR / ICS and in the
    /// register. Anything not in the list can be typed in ("Other").
    public static readonly IReadOnlyList<(string Name, string Classification)> EquipmentTypes =
    [
        ("Desktop computer", "ICT Equipment"), ("Laptop", "ICT Equipment"), ("Tablet", "ICT Equipment"),
        ("Monitor", "ICT Equipment"), ("Printer", "ICT Equipment"), ("Scanner", "ICT Equipment"),
        ("Multifunction printer (print, scan, copy)", "ICT Equipment"), ("Projector", "ICT Equipment"),
        ("UPS (uninterruptible power supply)", "ICT Equipment"), ("Server", "ICT Equipment"),
        ("Network switch", "ICT Equipment"), ("Router", "ICT Equipment"), ("Wireless access point", "ICT Equipment"),
        ("External hard drive", "ICT Equipment"), ("Webcam", "ICT Equipment"), ("Keyboard", "ICT Equipment"),
        ("Mouse", "ICT Equipment"), ("Headset", "ICT Equipment"), ("Smart TV / display", "ICT Equipment"),
        ("Photocopier", "Office Equipment"), ("Air conditioner", "Office Equipment"), ("Electric fan", "Office Equipment"),
        ("Paper shredder", "Office Equipment"), ("Laminator", "Office Equipment"), ("Binding machine", "Office Equipment"),
        ("Typewriter", "Office Equipment"), ("Biometric / time recorder", "Office Equipment"), ("Water dispenser", "Office Equipment"),
        ("Refrigerator", "Office Equipment"), ("Microwave oven", "Office Equipment"), ("Vacuum cleaner", "Office Equipment"),
        ("Telephone", "Communication Equipment"), ("Mobile phone", "Communication Equipment"), ("Two-way radio", "Communication Equipment"),
        ("Fax machine", "Communication Equipment"), ("Sound system / speaker", "Communication Equipment"), ("Microphone", "Communication Equipment"),
        ("Office table / desk", "Furniture and Fixtures"), ("Office chair", "Furniture and Fixtures"), ("Filing cabinet", "Furniture and Fixtures"),
        ("Steel cabinet", "Furniture and Fixtures"), ("Bookshelf / rack", "Furniture and Fixtures"), ("Conference table", "Furniture and Fixtures"),
        ("Sofa / bench", "Furniture and Fixtures"), ("Locker", "Furniture and Fixtures"),
        ("Digital camera", "Technical and Scientific Equipment"), ("Video camera", "Technical and Scientific Equipment"),
        ("Drone", "Technical and Scientific Equipment"), ("Measuring instrument", "Technical and Scientific Equipment"),
        ("Generator", "Disaster Response and Rescue Equipment"), ("Fire extinguisher", "Disaster Response and Rescue Equipment"),
        ("Emergency light", "Disaster Response and Rescue Equipment"),
        ("Car / van", "Motor Vehicles"), ("Motorcycle", "Motor Vehicles"),
        ("Books", "Books"),
    ];

    /// The classification that goes with a listed equipment type (null for one that was typed in).
    public static string? ClassificationOf(string? equipment) =>
        EquipmentTypes.FirstOrDefault(e => string.Equals(e.Name, equipment, StringComparison.OrdinalIgnoreCase)).Classification;

    /// I save the equipment types of a document as one text, separated by ";".
    public static List<string> SplitEquipment(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    public static string? JoinEquipment(IEnumerable<string> names)
    {
        var joined = string.Join("; ", names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));
        return joined.Length == 0 ? null : joined[..Math.Min(joined.Length, 255)];
    }
}

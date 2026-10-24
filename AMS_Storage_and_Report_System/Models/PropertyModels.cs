namespace AMS_Storage_and_Report_System.Models;

public class Office
{
    public int OfficeId { get; set; }
    public string OfficeName { get; set; } = string.Empty;
    public string OfficeAcronym { get; set; } = string.Empty;
    public string? ResponsibilityCenterCode { get; set; }
    /// The office code for the "RIS No." column of the RSMI (01 ORD, 02 ARD, 03 ASD...).
    public string? RisCode { get; set; }
    /// Sheet name of this office in the APP-CSE workbook, if it's not the same as the acronym (like "SHNU").
    public string? AppCseSheet { get; set; }
    /// The office whose APP-CSE this office uses (like LRPO uses ESSD's). null means its own.
    public int? AppCseOfficeId { get; set; }
    public bool CanRequisition { get; set; } = true;   // new offices can request supplies
    public bool IsActive { get; set; } = true;
}

public class RoPersonnel
{
    public int PersonnelId { get; set; }
    public int OfficeId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
}

public class PropertyType
{
    public int PropertyTypeId { get; set; }
    public string TypeName { get; set; } = string.Empty;
}

public class PropertyDocument
{
    public int DocumentId { get; set; }
    public int OfficeId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public int PropertyTypeId { get; set; }
    /// What the document covers: Laptop, Printer... (several separated by ";"). See PropertyStatus.SplitEquipment.
    public string? EquipmentType { get; set; }
    public int PersonnelId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    /// The date written on the signed PAR, ICS or return slip (null for documents uploaded before I added this).
    public DateTime? DateSigned { get; set; }
    /// PAR / ICS only: the date the property was returned (date signed on its return slip). null means the person still has it.
    public DateTime? ReturnedOn { get; set; }
    /// PAR / ICS only: the Return Slip uploaded for it.
    public int? ReturnSlipId { get; set; }
    public uint UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
    public Office? Office { get; set; }
    public PropertyType? PropertyType { get; set; }
    public RoPersonnel? Personnel { get; set; }
}
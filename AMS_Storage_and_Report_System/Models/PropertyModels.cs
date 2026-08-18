namespace AMS_Storage_and_Report_System.Models;

public class Office
{
    public int OfficeId { get; set; }
    public string OfficeName { get; set; } = string.Empty;
    public string OfficeAcronym { get; set; } = string.Empty;
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
    public int PersonnelId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public uint UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
    public Office? Office { get; set; }
    public PropertyType? PropertyType { get; set; }
    public RoPersonnel? Personnel { get; set; }
}
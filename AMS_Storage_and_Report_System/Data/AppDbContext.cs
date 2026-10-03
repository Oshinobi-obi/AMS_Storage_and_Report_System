using Microsoft.EntityFrameworkCore;
using AMS_Storage_and_Report_System.Models;

namespace AMS_Storage_and_Report_System.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Office> Offices => Set<Office>();
    public DbSet<RoPersonnel> RoPersonnel => Set<RoPersonnel>();
    public DbSet<PropertyType> PropertyTypes => Set<PropertyType>();
    public DbSet<PropertyDocument> PropertyDocuments => Set<PropertyDocument>();
    public DbSet<UserActivity> UserActivities => Set<UserActivity>();
    public DbSet<SupplyItem> SupplyItems => Set<SupplyItem>();
    public DbSet<AppCseAllocation> AppCseAllocations => Set<AppCseAllocation>();
    public DbSet<RisTransaction> RisTransactions => Set<RisTransaction>();
    public DbSet<RisItem> RisItems => Set<RisItem>();
    public DbSet<RisStatusHistory> RisStatusHistory => Set<RisStatusHistory>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<RisSignedCopy> RisSignedCopies => Set<RisSignedCopy>();
    public DbSet<ItemCategory> ItemCategories => Set<ItemCategory>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ItemImage> ItemImages => Set<ItemImage>();
    public DbSet<SupplySuggestion> SupplySuggestions => Set<SupplySuggestion>();
    public DbSet<AppCseUpload> AppCseUploads => Set<AppCseUpload>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(u => u.UserId);

            entity.Property(u => u.UserId).HasColumnName("user_id");
            entity.Property(u => u.Username).HasColumnName("username");
            entity.Property(u => u.Email).HasColumnName("email");
            entity.Property(u => u.FullName).HasColumnName("full_name");
            entity.Property(u => u.DisplayName).HasColumnName("display_name");
            entity.Property(u => u.ProfilePicturePath).HasColumnName("profile_picture_path");
            entity.Property(u => u.PasswordHash).HasColumnName("password_hash");
            entity.Property(u => u.Role).HasColumnName("role").HasConversion<string>();
            entity.Property(u => u.OfficeId).HasColumnName("office_id");
            entity.Property(u => u.IsActive).HasColumnName("is_active");
            entity.Property(u => u.RequirePasswordChange).HasColumnName("require_password_change");
            entity.Property(u => u.FailedLoginAttempts).HasColumnName("failed_login_attempts");
            entity.Property(u => u.LockedUntil).HasColumnName("locked_until");
            entity.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(u => u.CreatedAt).HasColumnName("created_at");
            entity.Property(u => u.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasOne(u => u.Office).WithMany().HasForeignKey(u => u.OfficeId);
        });

        modelBuilder.Entity<Office>(entity =>
        {
            entity.ToTable("offices");
            entity.HasKey(o => o.OfficeId);
            entity.Property(o => o.OfficeId).HasColumnName("office_id");
            entity.Property(o => o.OfficeName).HasColumnName("office_name");
            entity.Property(o => o.OfficeAcronym).HasColumnName("office_acronym");
            entity.Property(o => o.ResponsibilityCenterCode).HasColumnName("responsibility_center_code");
            entity.Property(o => o.CanRequisition).HasColumnName("can_requisition");
            entity.Property(o => o.IsActive).HasColumnName("is_active");
        });

        modelBuilder.Entity<RoPersonnel>(entity =>
        {
            entity.ToTable("ro_personnel");
            entity.HasKey(p => p.PersonnelId);
            entity.Property(p => p.PersonnelId).HasColumnName("personnel_id");
            entity.Property(p => p.OfficeId).HasColumnName("office_id");
            entity.Property(p => p.FullName).HasColumnName("full_name");
            entity.Property(p => p.Position).HasColumnName("position");
        });

        modelBuilder.Entity<PropertyType>(entity =>
        {
            entity.ToTable("property_types");
            entity.HasKey(t => t.PropertyTypeId);
            entity.Property(t => t.PropertyTypeId).HasColumnName("property_type_id");
            entity.Property(t => t.TypeName).HasColumnName("type_name");
        });

        modelBuilder.Entity<PropertyDocument>(entity =>
        {
            entity.ToTable("property_documents");
            entity.HasKey(d => d.DocumentId);
            entity.Property(d => d.DocumentId).HasColumnName("document_id");
            entity.Property(d => d.OfficeId).HasColumnName("office_id");
            entity.Property(d => d.DocumentType).HasColumnName("document_type");
            entity.Property(d => d.PropertyTypeId).HasColumnName("property_type_id");
            entity.Property(d => d.PersonnelId).HasColumnName("personnel_id");
            entity.Property(d => d.FilePath).HasColumnName("file_path");
            entity.Property(d => d.OriginalFileName).HasColumnName("original_file_name");
            entity.Property(d => d.UploadedBy).HasColumnName("uploaded_by");
            entity.Property(d => d.UploadedAt).HasColumnName("uploaded_at");

            entity.HasOne(d => d.Office).WithMany().HasForeignKey(d => d.OfficeId);
            entity.HasOne(d => d.PropertyType).WithMany().HasForeignKey(d => d.PropertyTypeId);
            entity.HasOne(d => d.Personnel).WithMany().HasForeignKey(d => d.PersonnelId);
        });

        modelBuilder.Entity<UserActivity>(entity =>
        {
            entity.ToTable("user_activities");
            entity.HasKey(a => a.ActivityId);
            entity.Property(a => a.ActivityId).HasColumnName("activity_id");
            entity.Property(a => a.UserId).HasColumnName("user_id");
            entity.Property(a => a.ActivityType).HasColumnName("activity_type");
            entity.Property(a => a.Description).HasColumnName("description");
            entity.Property(a => a.IpAddress).HasColumnName("ip_address");
            entity.Property(a => a.CreatedAt).HasColumnName("created_at");

            entity.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId);
        });
    
        // ── Supply / requisition tables (from the AMS Supplies portal) ──
        modelBuilder.Entity<SupplyItem>(e =>
        {
            e.ToTable("supply_items");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.StockNo).HasColumnName("stock_no");
            e.Property(x => x.ItemName).HasColumnName("item_name");
            e.Property(x => x.Specifications).HasColumnName("specifications");
            e.Property(x => x.UnitOfMeasure).HasColumnName("unit_of_measure");
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.SupplierId).HasColumnName("supplier_id");
            e.Property(x => x.UnitPrice).HasColumnName("unit_price");
            e.Property(x => x.ImagePath).HasColumnName("image_path");
            e.Property(x => x.StockOnHand).HasColumnName("stock_on_hand");
            e.Property(x => x.ReorderLevel).HasColumnName("reorder_level");
            e.Property(x => x.IsActive).HasColumnName("is_active");
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId);
            e.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
        });

        modelBuilder.Entity<ItemCategory>(e =>
        {
            e.ToTable("item_categories");
            e.HasKey(x => x.CategoryId);
            e.Property(x => x.CategoryId).HasColumnName("category_id");
            e.Property(x => x.CategoryName).HasColumnName("category_name");
            e.Property(x => x.Icon).HasColumnName("icon");
            e.Property(x => x.SortOrder).HasColumnName("sort_order");
        });

        modelBuilder.Entity<Supplier>(e =>
        {
            e.ToTable("suppliers");
            e.HasKey(x => x.SupplierId);
            e.Property(x => x.SupplierId).HasColumnName("supplier_id");
            e.Property(x => x.SupplierName).HasColumnName("supplier_name");
            e.Property(x => x.ContactPerson).HasColumnName("contact_person");
            e.Property(x => x.ContactNo).HasColumnName("contact_no");
            e.Property(x => x.IsActive).HasColumnName("is_active");
        });

        modelBuilder.Entity<ItemImage>(e =>
        {
            e.ToTable("item_images");
            e.HasKey(x => x.ItemId);
            e.Property(x => x.ItemId).HasColumnName("item_id").ValueGeneratedNever();
            e.Property(x => x.ContentType).HasColumnName("content_type");
            e.Property(x => x.Content).HasColumnName("content");
        });

        modelBuilder.Entity<SupplySuggestion>(e =>
        {
            e.ToTable("supply_suggestions");
            e.HasKey(x => x.SuggestionId);
            e.Property(x => x.SuggestionId).HasColumnName("suggestion_id");
            e.Property(x => x.OfficeId).HasColumnName("office_id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.SubmitterName).HasColumnName("submitter_name");
            e.Property(x => x.SubmitterEmail).HasColumnName("submitter_email");
            e.Property(x => x.ItemName).HasColumnName("item_name");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Justification).HasColumnName("justification");
            e.Property(x => x.EstimatedAnnualQty).HasColumnName("estimated_annual_qty");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.AdminResponse).HasColumnName("admin_response");
            e.Property(x => x.ReviewedBy).HasColumnName("reviewed_by");
            e.Property(x => x.ReviewedAt).HasColumnName("reviewed_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
            e.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId);
        });

        modelBuilder.Entity<AppCseUpload>(e =>
        {
            e.ToTable("app_cse_uploads");
            e.HasKey(x => x.UploadId);
            e.Property(x => x.UploadId).HasColumnName("upload_id");
            e.Property(x => x.FiscalYear).HasColumnName("fiscal_year");
            e.Property(x => x.OriginalFileName).HasColumnName("original_file_name");
            e.Property(x => x.FilePath).HasColumnName("file_path");
            e.Property(x => x.RowCount).HasColumnName("row_count");
            e.Property(x => x.Notes).HasColumnName("notes");
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
        });

        modelBuilder.Entity<AppCseAllocation>(e =>
        {
            e.ToTable("app_cse_allocations");
            e.HasKey(x => x.AllocationId);
            e.Property(x => x.AllocationId).HasColumnName("allocation_id");
            e.Property(x => x.OfficeId).HasColumnName("office_id");
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.FiscalYear).HasColumnName("fiscal_year");
            e.Property(x => x.AllocatedQty).HasColumnName("allocated_qty");
            e.Property(x => x.Q1Qty).HasColumnName("q1_qty");
            e.Property(x => x.Q2Qty).HasColumnName("q2_qty");
            e.Property(x => x.Q3Qty).HasColumnName("q3_qty");
            e.Property(x => x.Q4Qty).HasColumnName("q4_qty");
            e.Property(x => x.IssuedQty).HasColumnName("issued_qty");
            e.Property(x => x.UploadId).HasColumnName("upload_id");
            e.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId);
            e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
        });

        modelBuilder.Entity<RisTransaction>(e =>
        {
            e.ToTable("ris_transactions");
            e.HasKey(x => x.RisId);
            e.Property(x => x.RisId).HasColumnName("ris_id");
            e.Property(x => x.RisNo).HasColumnName("ris_no");
            e.Property(x => x.OfficeId).HasColumnName("office_id");
            e.Property(x => x.FiscalYear).HasColumnName("fiscal_year");
            e.Property(x => x.EntityName).HasColumnName("entity_name");
            e.Property(x => x.FundCluster).HasColumnName("fund_cluster");
            e.Property(x => x.DivisionName).HasColumnName("division_name");
            e.Property(x => x.OfficeName).HasColumnName("office_name");
            e.Property(x => x.ResponsibilityCenterCode).HasColumnName("responsibility_center_code");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.RequestedByUserId).HasColumnName("requested_by_user_id");
            e.Property(x => x.RequestedByPersonnelId).HasColumnName("requested_by_personnel_id");
            e.Property(x => x.RequestedAt).HasColumnName("requested_at");
            e.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
            e.Property(x => x.ApprovedByPersonnelId).HasColumnName("approved_by_personnel_id");
            e.Property(x => x.ApprovedAt).HasColumnName("approved_at");
            e.Property(x => x.IssuedByPersonnelId).HasColumnName("issued_by_personnel_id");
            e.Property(x => x.ReceivedByPersonnelId).HasColumnName("received_by_personnel_id");
            e.Property(x => x.IssuedAt).HasColumnName("issued_at");
            e.Property(x => x.RejectedByUserId).HasColumnName("rejected_by_user_id");
            e.Property(x => x.RejectedAt).HasColumnName("rejected_at");
            e.Property(x => x.RejectionReason).HasColumnName("rejection_reason");
            e.Ignore(x => x.EstimatedAmount);

            e.HasOne(x => x.Office).WithMany().HasForeignKey(x => x.OfficeId);
            e.HasOne(x => x.RequestedByPersonnel).WithMany().HasForeignKey(x => x.RequestedByPersonnelId);
            e.HasOne(x => x.ApprovedByPersonnel).WithMany().HasForeignKey(x => x.ApprovedByPersonnelId);
            e.HasOne(x => x.IssuedByPersonnel).WithMany().HasForeignKey(x => x.IssuedByPersonnelId);
            e.HasOne(x => x.ReceivedByPersonnel).WithMany().HasForeignKey(x => x.ReceivedByPersonnelId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.RisId);
            e.HasMany(x => x.History).WithOne().HasForeignKey(h => h.RisId);
        });

        modelBuilder.Entity<RisItem>(e =>
        {
            e.ToTable("ris_items");
            e.HasKey(x => x.RisItemId);
            e.Property(x => x.RisItemId).HasColumnName("ris_item_id");
            e.Property(x => x.RisId).HasColumnName("ris_id");
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.LineNo).HasColumnName("line_no");
            e.Property(x => x.StockNo).HasColumnName("stock_no");
            e.Property(x => x.UnitOfMeasure).HasColumnName("unit_of_measure");
            e.Property(x => x.ItemDescription).HasColumnName("item_description");
            e.Property(x => x.UnitCost).HasColumnName("unit_cost");
            e.Property(x => x.RequestedQty).HasColumnName("requested_qty");
            e.Property(x => x.StockAvailable).HasColumnName("stock_available");
            e.Property(x => x.IssuedQty).HasColumnName("issued_qty");
            e.Property(x => x.Remarks).HasColumnName("remarks");
            e.HasOne(x => x.Item).WithMany().HasForeignKey(x => x.ItemId);
        });

        modelBuilder.Entity<RisStatusHistory>(e =>
        {
            e.ToTable("ris_status_history");
            e.HasKey(x => x.HistoryId);
            e.Property(x => x.HistoryId).HasColumnName("history_id");
            e.Property(x => x.RisId).HasColumnName("ris_id");
            e.Property(x => x.FromStatus).HasColumnName("from_status");
            e.Property(x => x.ToStatus).HasColumnName("to_status");
            e.Property(x => x.ChangedBy).HasColumnName("changed_by");
            e.Property(x => x.Note).HasColumnName("note");
            e.Property(x => x.ChangedAt).HasColumnName("changed_at");
            e.HasOne(x => x.ChangedByUser).WithMany().HasForeignKey(x => x.ChangedBy);
        });

        modelBuilder.Entity<StockMovement>(e =>
        {
            e.ToTable("stock_movements");
            e.HasKey(x => x.MovementId);
            e.Property(x => x.MovementId).HasColumnName("movement_id");
            e.Property(x => x.ItemId).HasColumnName("item_id");
            e.Property(x => x.MovementType).HasColumnName("movement_type");
            e.Property(x => x.Quantity).HasColumnName("quantity");
            e.Property(x => x.BalanceAfter).HasColumnName("balance_after");
            e.Property(x => x.UnitCost).HasColumnName("unit_cost");
            e.Property(x => x.ReferenceType).HasColumnName("reference_type");
            e.Property(x => x.ReferenceNo).HasColumnName("reference_no");
            e.Property(x => x.RisId).HasColumnName("ris_id");
            e.Property(x => x.OfficeId).HasColumnName("office_id");
            e.Property(x => x.Remarks).HasColumnName("remarks");
            e.Property(x => x.PerformedBy).HasColumnName("performed_by");
            e.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<RisSignedCopy>(e =>
        {
            e.ToTable("ris_signed_copies");
            e.HasKey(x => x.RisId);
            e.Property(x => x.RisId).HasColumnName("ris_id").ValueGeneratedNever();
            e.Property(x => x.OriginalFileName).HasColumnName("original_file_name");
            e.Property(x => x.Content).HasColumnName("content");
            e.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            e.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
            e.Property(x => x.UploadedAt).HasColumnName("uploaded_at");
        });

        modelBuilder.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.SettingKey);
            e.Property(x => x.SettingKey).HasColumnName("setting_key");
            e.Property(x => x.SettingValue).HasColumnName("setting_value");
        });
    }
}
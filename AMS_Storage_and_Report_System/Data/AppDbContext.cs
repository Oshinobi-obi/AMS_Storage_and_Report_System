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
            entity.Property(u => u.IsActive).HasColumnName("is_active");
            entity.Property(u => u.RequirePasswordChange).HasColumnName("require_password_change");
            entity.Property(u => u.FailedLoginAttempts).HasColumnName("failed_login_attempts");
            entity.Property(u => u.LockedUntil).HasColumnName("locked_until");
            entity.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(u => u.CreatedAt).HasColumnName("created_at");
            entity.Property(u => u.UpdatedAt).HasColumnName("updated_at");

            entity.HasIndex(u => u.Username).IsUnique();
        });

        modelBuilder.Entity<Office>(entity =>
        {
            entity.ToTable("offices");
            entity.HasKey(o => o.OfficeId);
            entity.Property(o => o.OfficeId).HasColumnName("office_id");
            entity.Property(o => o.OfficeName).HasColumnName("office_name");
            entity.Property(o => o.OfficeAcronym).HasColumnName("office_acronym");
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
    }
}
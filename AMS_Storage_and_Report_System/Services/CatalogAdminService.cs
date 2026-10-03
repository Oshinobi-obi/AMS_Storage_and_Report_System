// CatalogAdminService.cs — AMS manages the supply catalog (items, photos,
// categories, suppliers) and answers "Suggest a supply" requests from offices.

using AMS_Storage_and_Report_System.Data;
using AMS_Storage_and_Report_System.Models;
using Microsoft.EntityFrameworkCore;

namespace AMS_Storage_and_Report_System.Services;

public sealed class ItemForm
{
    public int ItemId { get; set; }
    public string StockNo { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string? Specifications { get; set; }
    public string UnitOfMeasure { get; set; } = "";
    public int CategoryId { get; set; }
    public int SupplierId { get; set; }
    public decimal UnitPrice { get; set; }
    public int ReorderLevel { get; set; } = 10;
}

public sealed class CatalogAdminService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const int MaxImageBytes = 2 * 1024 * 1024;
    public static readonly string[] ImageTypes = ["image/jpeg", "image/png", "image/webp"];

    // ── Items ───────────────────────────────────────────────────────────
    public async Task<List<SupplyItem>> ItemsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SupplyItems.AsNoTracking()
            .Include(i => i.Category).Include(i => i.Supplier)
            .OrderByDescending(i => i.IsActive).ThenBy(i => i.ItemName)
            .ToListAsync();
    }

    public async Task<OpResult> SaveItemAsync(ItemForm f, uint userId)
    {
        var stockNo = f.StockNo.Trim();
        var name = f.ItemName.Trim();
        var unit = f.UnitOfMeasure.Trim();
        if (stockNo.Length == 0 || stockNo.Length > 30) return OpResult.Fail("Enter a stock number (up to 30 characters).");
        if (name.Length == 0) return OpResult.Fail("Enter the item name.");
        if (unit.Length == 0) return OpResult.Fail("Enter the unit of measure, for example pc, ream or box.");
        if (f.UnitPrice < 0) return OpResult.Fail("The unit price can't be negative.");
        if (f.ReorderLevel < 0) return OpResult.Fail("The low-stock level can't be negative.");

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.SupplyItems.AnyAsync(i => i.StockNo == stockNo && i.ItemId != f.ItemId))
            return OpResult.Fail($"Stock no. {stockNo} is already used by another item.");

        var item = f.ItemId == 0 ? new SupplyItem { IsActive = true } : await db.SupplyItems.FindAsync(f.ItemId);
        if (item is null) return OpResult.Fail("This item no longer exists.");
        item.StockNo = stockNo;
        item.ItemName = name;
        item.Specifications = string.IsNullOrWhiteSpace(f.Specifications) ? null : f.Specifications.Trim();
        item.UnitOfMeasure = unit;
        item.CategoryId = f.CategoryId == 0 ? null : f.CategoryId;
        item.SupplierId = f.SupplierId == 0 ? null : f.SupplierId;
        item.UnitPrice = Math.Round(f.UnitPrice, 2);
        item.ReorderLevel = f.ReorderLevel;
        if (f.ItemId == 0) db.SupplyItems.Add(item);

        db.UserActivities.Add(new UserActivity
        {
            UserId = userId, ActivityType = f.ItemId == 0 ? "Create" : "Update",
            Description = $"{(f.ItemId == 0 ? "Added" : "Edited")} catalog item {stockNo} {name}", CreatedAt = PhTime.Now,
        });
        await db.SaveChangesAsync();
        return OpResult.Success(f.ItemId == 0 ? $"{name} was added to the catalog." : "Changes saved.") with { Id = item.ItemId };
    }

    public async Task<OpResult> SetActiveAsync(int itemId, bool active, uint userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.SupplyItems.FindAsync(itemId);
        if (item is null) return OpResult.Fail("This item no longer exists.");
        item.IsActive = active;
        db.UserActivities.Add(new UserActivity { UserId = userId, ActivityType = "Update", Description = $"{(active ? "Re-activated" : "Deactivated")} {item.StockNo} {item.ItemName}", CreatedAt = PhTime.Now });
        await db.SaveChangesAsync();
        return OpResult.Success(active ? $"{item.ItemName} is back in the catalog." : $"{item.ItemName} is hidden from offices.");
    }

    // ── Photos ──────────────────────────────────────────────────────────
    public async Task<OpResult> SetImageAsync(int itemId, byte[] content, string contentType)
    {
        if (!ImageTypes.Contains(contentType)) return OpResult.Fail("Use a JPG, PNG or WebP image.");
        if (content.Length == 0 || content.Length > MaxImageBytes) return OpResult.Fail("The photo must be smaller than 2 MB.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.SupplyItems.FindAsync(itemId);
        if (item is null) return OpResult.Fail("This item no longer exists.");

        var image = await db.ItemImages.FindAsync(itemId);
        if (image is null) { image = new ItemImage { ItemId = itemId }; db.ItemImages.Add(image); }
        image.ContentType = contentType;
        image.Content = content;
        // The AMS Supplies catalog shows the photo from this address (?v= forces browsers to load the new one).
        item.ImagePath = $"/images/items/{itemId}?v={DateTime.UtcNow.Ticks}";
        await db.SaveChangesAsync();
        return OpResult.Success("Photo saved.");
    }

    public async Task RemoveImageAsync(int itemId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ItemImages.Where(i => i.ItemId == itemId).ExecuteDeleteAsync();
        await db.SupplyItems.Where(i => i.ItemId == itemId).ExecuteUpdateAsync(s => s.SetProperty(i => i.ImagePath, (string?)null));
    }

    // ── Categories and suppliers ───────────────────────────────────────
    public async Task<List<ItemCategory>> CategoriesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ItemCategories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.CategoryName).ToListAsync();
    }

    public async Task<List<Supplier>> SuppliersAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Suppliers.AsNoTracking().OrderBy(s => s.SupplierName).ToListAsync();
    }

    public async Task<OpResult> SaveCategoryAsync(int id, string name, string? icon)
    {
        name = name.Trim();
        if (name.Length is 0 or > 100) return OpResult.Fail("Enter a category name (up to 100 characters).");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.ItemCategories.AnyAsync(c => c.CategoryName == name && c.CategoryId != id))
            return OpResult.Fail("A category with that name already exists.");
        var c = id == 0 ? new ItemCategory { SortOrder = await db.ItemCategories.Select(x => (int?)x.SortOrder).MaxAsync() + 1 ?? 1 }
                        : await db.ItemCategories.FindAsync(id);
        if (c is null) return OpResult.Fail("This category no longer exists.");
        c.CategoryName = name;
        c.Icon = string.IsNullOrWhiteSpace(icon) ? c.Icon ?? "bi-box-seam" : icon.Trim();
        if (id == 0) db.ItemCategories.Add(c);
        await db.SaveChangesAsync();
        return OpResult.Success("Category saved.");
    }

    public async Task<OpResult> SaveSupplierAsync(int id, string name, string? contact, string? phone)
    {
        name = name.Trim();
        if (name.Length is 0 or > 200) return OpResult.Fail("Enter the supplier name.");
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Suppliers.AnyAsync(s => s.SupplierName == name && s.SupplierId != id))
            return OpResult.Fail("A supplier with that name already exists.");
        var s = id == 0 ? new Supplier() : await db.Suppliers.FindAsync(id);
        if (s is null) return OpResult.Fail("This supplier no longer exists.");
        s.SupplierName = name;
        s.ContactPerson = string.IsNullOrWhiteSpace(contact) ? null : contact.Trim();
        s.ContactNo = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (id == 0) db.Suppliers.Add(s);
        await db.SaveChangesAsync();
        return OpResult.Success("Supplier saved.");
    }

    // ── Suggestions from offices ───────────────────────────────────────
    public async Task<List<SupplySuggestion>> SuggestionsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.SupplySuggestions.AsNoTracking().Include(s => s.Office)
            .OrderBy(s => s.Status == "New" ? 0 : s.Status == "UnderReview" ? 1 : 2)
            .ThenByDescending(s => s.CreatedAt).ToListAsync();
    }

    public async Task<OpResult> AnswerSuggestionAsync(int id, string status, string? response, uint userId)
    {
        string[] allowed = ["New", "UnderReview", "AddedToCatalog", "ForNextAPP", "Declined"];
        if (!allowed.Contains(status)) return OpResult.Fail("Choose a status.");
        if (status == "Declined" && string.IsNullOrWhiteSpace(response))
            return OpResult.Fail("Write a short reason when declining, so the office understands.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.SupplySuggestions.FindAsync(id);
        if (s is null) return OpResult.Fail("This suggestion no longer exists.");
        s.Status = status;
        s.AdminResponse = string.IsNullOrWhiteSpace(response) ? null : response.Trim()[..Math.Min(response.Trim().Length, 1000)];
        s.ReviewedBy = userId;
        s.ReviewedAt = PhTime.Now;
        await db.SaveChangesAsync();
        return OpResult.Success("Suggestion updated.");
    }

    public static string SuggestionLabel(string status) => status switch
    {
        "New" => "New",
        "UnderReview" => "Under review",
        "AddedToCatalog" => "Added to catalog",
        "ForNextAPP" => "For next APP",
        "Declined" => "Declined",
        _ => status,
    };

    public static string SuggestionBadge(string status) => status switch
    {
        "New" => "badge-gold",
        "UnderReview" => "badge-navy",
        "AddedToCatalog" => "badge-ok",
        "ForNextAPP" => "badge-navy",
        _ => "badge-grey",
    };
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace AMS_Storage_and_Report_System.Services;

public class ProfilePictureStorage
{
    public string RootPath { get; }

    public ProfilePictureStorage(IConfiguration config, IWebHostEnvironment env)
    {
        var relativePath = config["Storage:ProfilePicturesPath"];
        if (string.IsNullOrWhiteSpace(relativePath)) relativePath = "App_Data/profile-pictures";

        RootPath = Path.GetFullPath(Path.Combine(env.ContentRootPath, relativePath));
        Directory.CreateDirectory(RootPath);
    }

    /// Returns the full path only for a plain file name inside the storage folder.
    /// Blocks "..\..\appsettings.json"-style requests.
    public string? ResolveSafe(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName) return null;
        var full = Path.GetFullPath(Path.Combine(RootPath, fileName));
        return full.StartsWith(RootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public async Task<string> SaveProfilePictureAsync(Stream fileStream, uint userId, string extension)
    {
        extension = extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" ? extension.ToLowerInvariant() : ".png";
        var fileName = $"user_{userId}_{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";
        await using var fs = new FileStream(Path.Combine(RootPath, fileName), FileMode.Create);
        await fileStream.CopyToAsync(fs);
        return fileName;
    }

    public void DeleteProfilePicture(string? fileName)
    {
        var path = ResolveSafe(fileName);
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    public string? GetProfilePictureUrl(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) ? null : $"/api/profile-picture/{Uri.EscapeDataString(fileName)}";
}

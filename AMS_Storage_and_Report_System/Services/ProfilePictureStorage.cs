using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace AMS_Storage_and_Report_System.Services;

public class ProfilePictureStorage
{
    public string RootPath { get; }

    public ProfilePictureStorage(IConfiguration config, IWebHostEnvironment env)
    {
        var relativePath = config["Storage:ProfilePicturesPath"];

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = "App_Data/profile-pictures";
        }

        RootPath = Path.Combine(env.ContentRootPath, relativePath);
        Directory.CreateDirectory(RootPath);
    }

    public async Task<string> SaveProfilePictureAsync(Stream fileStream, uint userId, string extension)
    {
        var fileName = $"user_{userId}_{DateTime.UtcNow:yyyyMMddHHmmss}{extension}";
        var filePath = Path.Combine(RootPath, fileName);

        using var fs = new FileStream(filePath, FileMode.Create);
        await fileStream.CopyToAsync(fs);

        return fileName;
    }

    public void DeleteProfilePicture(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var filePath = Path.Combine(RootPath, fileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    public string GetProfilePictureUrl(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "/api/profile-picture/default";

        return $"/api/profile-picture/{fileName}";
    }
}

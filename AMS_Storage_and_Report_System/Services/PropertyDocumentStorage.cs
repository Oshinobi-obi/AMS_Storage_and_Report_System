using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.IO;

namespace AMS_Storage_and_Report_System.Services;

public class PropertyDocumentStorage
{
    public string RootPath { get; }

    public PropertyDocumentStorage(IConfiguration config, IWebHostEnvironment env)
    {
        var relativePath = config["Storage:PropertyDocumentsPath"];

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = "App_Data/property-documents";
        }

        RootPath = Path.Combine(env.ContentRootPath, relativePath);
        Directory.CreateDirectory(RootPath);
    }
}
using GenesisFIS.Core.Membership;
using Microsoft.Extensions.Configuration;

namespace GenesisFIS.Infrastructure.Membership;

/// <summary>Policy wording documents under FileStore:PolicyWordingRoot/&lt;folder&gt;/. Replace with blob storage in the cloud.</summary>
public sealed class FileSystemPolicyWordingStore(IConfiguration configuration) : IPolicyWordingStore
{
    private static readonly string[] Allowed = [".jpg", ".jpeg", ".tif", ".tiff", ".gif", ".pdf", ".png"];
    private readonly string? _root = configuration["FileStore:PolicyWordingRoot"];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_root) && Directory.Exists(_root);

    public IReadOnlyList<PolicyWordingFile> List(string folder)
    {
        var dir = Resolve(folder);
        if (dir is null || !Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateFiles()
            .Where(f => Allowed.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new PolicyWordingFile(f.Name, f.Length)).ToList();
    }

    public PolicyWordingContent? Open(string folder, string fileName)
    {
        var dir = Resolve(folder);
        if (dir is null || fileName != Path.GetFileName(fileName)) return null;
        var path = Path.Combine(dir, fileName);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!File.Exists(path) || !Allowed.Contains(ext)) return null;
        return new PolicyWordingContent(File.OpenRead(path), ContentType(ext), fileName);
    }

    private string? Resolve(string folder)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(folder) || folder != Path.GetFileName(folder.Trim())) return null;
        var root = Path.GetFullPath(_root!);
        var full = Path.GetFullPath(Path.Combine(root, folder.Trim()));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string ContentType(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream",
    };
}


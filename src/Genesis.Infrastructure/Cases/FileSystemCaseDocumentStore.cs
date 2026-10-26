using Genesis.Core.Cases;
using Microsoft.Extensions.Configuration;

namespace Genesis.Infrastructure.Cases;

/// <summary>
/// Case documents under FileStore:ScannedDocsRoot / InternalDocsRoot / EmailsRoot, one sub-folder per case number.
/// Replace with blob storage in the cloud.
/// </summary>
public sealed class FileSystemCaseDocumentStore(IConfiguration configuration) : ICaseDocumentStore
{
    private static readonly string[] Allowed = [".jpg", ".jpeg", ".tif", ".tiff", ".gif", ".png", ".pdf", ".msg", ".eml", ".doc", ".docx", ".xls", ".xlsx", ".txt"];

    private string? Root(CaseDocumentArea area) => configuration[area switch
    {
        CaseDocumentArea.Scanned => "FileStore:ScannedDocsRoot",
        CaseDocumentArea.Internal => "FileStore:InternalDocsRoot",
        _ => "FileStore:EmailsRoot",
    }];

    public bool IsConfigured(CaseDocumentArea area) => Root(area) is { Length: > 0 } r && Directory.Exists(r);

    public IReadOnlyList<CaseDocumentFile> List(CaseDocumentArea area, string caseNumber)
    {
        var dir = Resolve(area, caseNumber);
        if (dir is null || !Directory.Exists(dir)) return [];
        return new DirectoryInfo(dir).EnumerateFiles().Where(f => Allowed.Contains(f.Extension.ToLowerInvariant()))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Select(f => new CaseDocumentFile(f.Name, f.Length)).ToList();
    }

    public CaseDocumentContent? Open(CaseDocumentArea area, string caseNumber, string fileName)
    {
        var dir = Resolve(area, caseNumber);
        if (dir is null || fileName != Path.GetFileName(fileName)) return null;
        var path = Path.Combine(dir, fileName);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!File.Exists(path) || !Allowed.Contains(ext)) return null;
        return new CaseDocumentContent(File.OpenRead(path), ContentType(ext), fileName);
    }

    private string? Resolve(CaseDocumentArea area, string caseNumber)
    {
        if (!IsConfigured(area) || !CaseNumber.IsValid(caseNumber)) return null;
        var root = Path.GetFullPath(Root(area)!);
        var full = Path.GetFullPath(Path.Combine(root, caseNumber.Trim()));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static string ContentType(string ext) => ext switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".txt" => "text/plain",
        ".eml" => "message/rfc822",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "application/octet-stream",
    };
}

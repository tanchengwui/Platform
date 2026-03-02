using System.IO.Compression;

namespace Platform.Runtime.Services;

public static class ZipResourceReader
{
    public static string? ReadText(string zipPath, string entryPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            return null;

        using var fs = File.OpenRead(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        // Normalize to ZIP-style paths
        var normalized = entryPath.Replace('\\', '/');

        var entry = zip.GetEntry(normalized);
        if (entry is null) return null;

        using var es = entry.Open();
        using var sr = new StreamReader(es);
        return sr.ReadToEnd();
    }
}
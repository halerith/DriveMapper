using System;
using System.Linq;

namespace DriveMapper;

internal static class UncPath
{
    public static bool TryNormalize(string? value, out string path)
    {
        path = (value ?? "").Trim().Replace('/', '\\').TrimEnd('\\');
        if (!path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        string[] parts = path.Substring(2).Split('\\');
        return parts.Length >= 2 && parts.All(part => !string.IsNullOrWhiteSpace(part) &&
            part != "." && part != ".." && !part.Any(c => c < 32 || "<>:\"|?*".Contains(c)));
    }

    public static bool Equals(string? left, string? right) =>
        left != null && right != null && TryNormalize(left, out string a) &&
        TryNormalize(right, out string b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

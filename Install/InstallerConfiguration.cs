using System.Text.Json;
using System.Text.Json.Serialization;

namespace Install;

internal sealed record InstallerConfiguration
{
    public const string FileName = "install.config.json";
    public string TargetDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DriveMapper");
    public string ShortcutName { get; init; } = "";
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static InstallerConfiguration ReadPackage(string directory)
    {
        string file = Path.Combine(directory, FileName);
        if (File.Exists(file)) return Parse(File.ReadAllText(file));
        using var stream = typeof(InstallerConfiguration).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidDataException("Embedded installation configuration is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
    public static InstallerConfiguration Parse(string json)
        => Validate(JsonSerializer.Deserialize<InstallerConfiguration>(json, JsonOptions)
            ?? throw new InvalidDataException("Installation configuration is empty."));
    public static InstallerConfiguration Validate(InstallerConfiguration config)
    {
        string shortcut = config.ShortcutName ?? "";
        if (shortcut.Length > 100 || shortcut.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            shortcut.TrimEnd(' ', '.') != shortcut || shortcut is "." or "..")
            throw new InvalidDataException("ShortcutName must be a simple shortcut name, without a path.");
        return config with { TargetDirectory = InstallSafety.ValidateDirectory(config.TargetDirectory), ShortcutName = shortcut };
    }
}

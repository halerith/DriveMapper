using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DriveMapper;

namespace Install;

internal sealed record PayloadManifest(int SchemaVersion, string Version, string? SignerThumbprint, Dictionary<string, string> Files);

internal static class PayloadValidation
{
    private static readonly string[] Executables = { "Install.exe", Product.AgentExe, Product.ServiceExe };

    public static string Validate(string stage)
    {
        var assembly = Assembly.GetExecutingAssembly();
        string version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        using var resource = assembly.GetManifestResourceStream("DriveMapper.PayloadManifest.json");
        var manifest = resource == null ? null : JsonSerializer.Deserialize<PayloadManifest>(resource)
            ?? throw new InvalidDataException("The embedded payload manifest is empty.");
        // Plain dotnet builds remain usable for unsigned development. A signed installer must
        // always be produced through the release process with its embedded payload hashes.
        if (manifest == null && Authenticode.SignerThumbprint(Path.Combine(stage, "Install.exe")) != null)
            throw new InvalidDataException("A signed installer requires an embedded payload manifest.");
        return ValidateFiles(stage, version, manifest);
    }

    internal static string ValidateFiles(string stage, string version, PayloadManifest? manifest,
        Action<string, string>? verifyPublisher = null)
    {
        foreach (string name in Executables)
        {
            string file = Path.Combine(stage, name);
            using (var reader = new BinaryReader(File.OpenRead(file)))
            {
                if (reader.BaseStream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("Invalid executable: " + name);
                reader.BaseStream.Position = 0x3C;
                int offset = reader.ReadInt32();
                if (offset < 64 || offset > reader.BaseStream.Length - 6) throw new InvalidDataException("Invalid PE header: " + name);
                reader.BaseStream.Position = offset;
                if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
                    throw new InvalidDataException("A win-x64 executable is required: " + name);
            }
            var metadata = FileVersionInfo.GetVersionInfo(file);
            if (metadata.FileVersion != version || metadata.OriginalFilename != Path.ChangeExtension(name, ".dll") ||
                metadata.CompanyName != Product.Publisher || metadata.ProductName != Product.DisplayName)
                throw new InvalidDataException("Executable identity or release version does not match the installer: " + name);
        }
        if (manifest == null) return version;
        if (manifest.SchemaVersion != 1 || manifest.Version != version || manifest.Files == null || manifest.Files.Count != 2 ||
            manifest.SignerThumbprint != null && (manifest.SignerThumbprint.Length != 40 || !manifest.SignerThumbprint.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid embedded payload manifest.");
        foreach (string name in Executables.Skip(1))
        {
            using var file = File.OpenRead(Path.Combine(stage, name));
            string hash = Convert.ToHexString(SHA256.HashData(file));
            if (!manifest.Files.TryGetValue(name, out string? expected) || !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Executable does not match this release: " + name);
        }
        if (manifest.SignerThumbprint != null)
            foreach (string name in Executables)
                (verifyPublisher ?? Authenticode.VerifyPublisher)(Path.Combine(stage, name), manifest.SignerThumbprint);
        return version;
    }
}

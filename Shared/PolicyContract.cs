namespace DriveMapper;

// The ADMX output is the application contract, independent of Intune's internal MDM stores.
internal static class PolicyContract
{
    public const string DrivesKey = @"Software\Policies\DriveMapper\Drives";
    public const string Enabled = "Enabled";
    public const string Path = "Path";
    public const string Name = "Name";
    public const string Reconnect = "Reconnect";
    public static readonly string[] ValueNames = { Enabled, Path, Name, Reconnect };
}

namespace Install;

internal sealed record InstallerArguments(string? Command, bool Quiet, bool Force)
{
    public const string Usage = "Usage: Install.exe [-quiet] -install | -uninstall [-force]. Run without arguments for setup.";
    public static InstallerArguments Parse(string[] args)
    {
        string? command = null;
        bool quiet = false, force = false;
        foreach (string argument in args)
            switch (argument.ToLowerInvariant())
            {
                case "-quiet": quiet = true; break;
                case "-force": force = true; break;
                case "-install": case "-uninstall":
                    if (command != null) throw new FormatException(Usage);
                    command = argument[1..].ToLowerInvariant(); break;
                default: throw new FormatException(Usage);
            }
        if ((args.Length > 0 && command == null) || (force && command != "uninstall")) throw new FormatException(Usage);
        return new(command, quiet, force);
    }
}

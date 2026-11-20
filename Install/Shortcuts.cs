using System.Runtime.InteropServices;

namespace Install;

internal static class Shortcuts
{
    private static string ShortcutPath(string name) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), name + ".lnk");
    public static void Remove(string name)
    {
        if (name.Length == 0) return;
        string path = ShortcutPath(name);
        FileDeployment.EnsureNoLinks(path);
        File.Delete(path);
    }
    public static void Create(InstallerConfiguration config)
    {
        if (config.ShortcutName.Length == 0) return;
        string path = ShortcutPath(config.ShortcutName);
        FileDeployment.EnsureNoLinks(path);
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? link = null;
        try
        {
            link = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
            var type = link!.GetType();
            type.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { Path.Combine(config.TargetDirectory, "DriveMapper-Agent.exe") });
            type.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { config.TargetDirectory });
            type.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            Marshal.FinalReleaseComObject(shell);
        }
    }
}

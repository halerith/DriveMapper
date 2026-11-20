using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using DriveMapper;

namespace Install;

internal static class ServiceRegistration
{
    public static void Stop()
    {
        using var service = new ServiceController(Product.ServiceName);
        try
        {
            if (service.Status == ServiceControllerStatus.Stopped) return;
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(40));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception error && error.NativeErrorCode is 1060 or 1072) { }
    }

    public static void EnsureReadyForInstall() => ServiceLifecycle.EnsureReady(Probe, WaitForDeletion);

    private static ServicePresence Probe()
    {
        using var service = new ServiceController(Product.ServiceName);
        try { _ = service.Status; return ServicePresence.Present; }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception error && error.NativeErrorCode is 1060 or 1072)
        { return ((Win32Exception)ex.InnerException!).NativeErrorCode == 1060 ? ServicePresence.Missing : ServicePresence.PendingDeletion; }
    }

    private static bool WaitForDeletion()
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            if (Probe() == ServicePresence.Missing) return true;
            System.Threading.Thread.Sleep(200);
        } while (elapsed.Elapsed < TimeSpan.FromSeconds(5));
        return false;
    }

    public static void InstallAndStart(string directory)
    {
        string executable = Path.Combine(directory, Product.ServiceExe);
        EnsureReadyForInstall();
        bool exists = Probe() == ServicePresence.Present;
        Run(exists ? "config" : "create", Product.ServiceName, "binPath=", "\"" + executable + "\"",
            "start=", "auto", "obj=", "LocalSystem", "DisplayName=", Product.DisplayName);
        Run("description", Product.ServiceName, "Launches short-lived DriveMapper Agents in user sessions and schedules policy recovery.");
        Run("failure", Product.ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/15000/restart/60000");
        Run("failureflag", Product.ServiceName, "1");
        using var installed = new ServiceController(Product.ServiceName);
        installed.Start();
        installed.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static bool Remove()
    {
        Stop();
        return ServiceLifecycle.Remove(Probe, () => RunAllowMissing("delete", Product.ServiceName), WaitForDeletion);
    }

    private static void Run(params string[] arguments) => RunCore(false, arguments);
    private static void RunAllowMissing(params string[] arguments) => RunCore(true, arguments);
    private static void RunCore(bool allowMissing, string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Service Control.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); throw new IOException("Service Control timed out."); }
        string details = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (process.ExitCode == 1072 && !allowMissing) throw new ServicePendingDeletionException();
        if (process.ExitCode != 0 && !(allowMissing && process.ExitCode is 1060 or 1072))
            throw new IOException($"Service Control failed ({process.ExitCode}): {details}");
    }
}

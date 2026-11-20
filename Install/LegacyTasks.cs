using Microsoft.Win32.TaskScheduler;
using DriveMapper;

namespace Install;

internal static class LegacyTasks
{
    public static void Remove(string target, IEnumerable<string> additionalNames, Action<string> log)
    {
        using var tasks = new TaskService();
        var names = new[] { "DriveMapper", "DriveMapper.exe", "DriveMapper-Agent", "DriveMapper-Agent.exe" }
            .Concat(additionalNames).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
            foreach (string suffix in new[] { "", "_NetworkChange", "_Boot" })
            {
                using var task = tasks.GetTask(name + suffix);
                if (task == null) continue;
                bool owned = task.Definition.Actions.OfType<ExecAction>().Any(action => IsOwnedAction(target, action.Path));
                if (!owned) { log("Preserved unrelated scheduled task: " + task.Path); continue; }
                task.Enabled = false;
                if (task.State == TaskState.Running) task.Stop();
                tasks.RootFolder.DeleteTask(task.Path, false);
                log("Removed legacy task: " + name + suffix);
            }
    }

    private static bool IsOwnedAction(string target, string executable)
    {
        string path = Environment.ExpandEnvironmentVariables(executable.Trim('"'));
        if (!Path.IsPathFullyQualified(path)) return false;
        return new[] { "DriveMapper.exe", Product.AgentExe }.Any(name =>
            string.Equals(Path.GetFullPath(path), Path.Combine(target, name), StringComparison.OrdinalIgnoreCase));
    }
}

namespace DriveMapper;

// A cancelled/removed session still owns its worker until that task has actually exited.
internal sealed class UserAgentTasks
{
    private readonly Dictionary<Task, string> tasks = new();
    public int Count => tasks.Count;
    public void Track(string sid, Task task) => tasks.Add(task, sid);
    public bool IsRunning(string sid) => tasks.Any(entry => entry.Value == sid && !entry.Key.IsCompleted);
    public void RemoveCompleted()
    {
        foreach (var task in tasks.Keys.Where(task => task.IsCompleted).ToArray()) tasks.Remove(task);
    }
    public Task DrainAsync() => Task.WhenAll(tasks.Keys);
}

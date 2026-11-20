namespace Install;

internal enum ServicePresence { Missing, Present, PendingDeletion }

internal sealed class ServicePendingDeletionException : IOException
{
    public ServicePendingDeletionException() : base("Windows is still deleting the DriveMapper service. Restart Windows, then run the installation again.") { }
}

internal static class ServiceLifecycle
{
    public static void EnsureReady(Func<ServicePresence> probe, Func<bool> waitForDeletion)
    {
        if (probe() == ServicePresence.PendingDeletion && !waitForDeletion())
            throw new ServicePendingDeletionException();
    }

    // True means uninstall can finish, but Windows must complete service deletion at restart.
    public static bool Remove(Func<ServicePresence> probe, Action delete, Func<bool> waitForDeletion)
    {
        var state = probe();
        if (state == ServicePresence.Missing) return false;
        if (state == ServicePresence.Present) delete();
        return !waitForDeletion();
    }
}

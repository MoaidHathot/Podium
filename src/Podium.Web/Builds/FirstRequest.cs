namespace Podium.Web.Builds;

/// <summary>
/// Completes once the first request that is not a health probe has been served. <see cref="MaintenanceService"/>
/// starts its heavier one-off work (search index, installation discovery) after that moment or after a short grace
/// period, whichever comes first, so that on half a vCPU it does not compete with the first page a person opens.
/// </summary>
public sealed class FirstRequest
{
    private readonly TaskCompletionSource _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Seen => _seen.Task;
    public bool HasBeenSeen => _seen.Task.IsCompleted;
    public void Mark() => _seen.TrySetResult();
}

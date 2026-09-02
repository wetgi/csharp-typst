namespace TypstRender.Service.Render;

/// <summary>
/// Clears render workspaces that a previous process could not clean up itself.
/// Each render deletes its own directory, but a SIGKILL, an OOM kill or a crash
/// cannot — and a container's filesystem survives a restart, so without this
/// sweep the leftovers accumulate for the life of the volume.
/// </summary>
/// <remarks>
/// Only directories untouched for <see cref="StaleAfter"/> are removed: the temp
/// root is shared by every process on the host, and a developer may well have a
/// second instance (or the test suite) rendering alongside this one.
/// </remarks>
public sealed class TempWorkspaceCleaner(ILogger<TempWorkspaceCleaner> logger) : IHostedService
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RenderService.WorkRoot))
        {
            return Task.CompletedTask;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;
        var swept = 0;

        foreach (var workDir in EnumerateWorkspaces())
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(workDir) > cutoff)
                {
                    continue;
                }

                Directory.Delete(workDir, recursive: true);
                swept++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove stale render workspace {WorkDir}", workDir);
            }
        }

        if (swept > 0)
        {
            logger.LogInformation(
                "Removed {Count} render workspace(s) left behind by a previous process", swept);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<string> EnumerateWorkspaces()
    {
        try
        {
            return Directory.EnumerateDirectories(RenderService.WorkRoot).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not scan {WorkRoot} for stale render workspaces", RenderService.WorkRoot);
            return [];
        }
    }
}

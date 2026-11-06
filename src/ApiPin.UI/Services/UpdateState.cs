using Microsoft.Extensions.Logging;

namespace ApiPin.UI.Services;

/// <summary>Checks for an update once in the background after start-up; the top bar offers a restart when one is ready.</summary>
public sealed class UpdateState(IAppHost host, ILogger<UpdateState> log)
{
    private int _started;

    /// <summary>The downloaded version waiting for a restart, if any.</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>Result of the last manual check, for About.</summary>
    public string? Status { get; private set; }

    public bool Checking { get; private set; }

    public event Action? Changed;

    /// <summary>Starts the background check once per app run (a short delay keeps it off the start-up path).</summary>
    public void StartBackgroundCheck()
    {
        if (!host.SupportsUpdates || Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));
            await CheckAsync(manual: false);
        });
    }

    public async Task CheckAsync(bool manual = true)
    {
        if (!host.SupportsUpdates)
        {
            Status = "Updates aren't available here: this copy wasn't installed with the ApiPin installer.";
            Changed?.Invoke();
            return;
        }
        Checking = true;
        Status = "Checking for updates…";
        Changed?.Invoke();
        try
        {
            var version = await host.CheckAndDownloadUpdateAsync(p =>
            {
                Status = $"Downloading the update… {p}%";
                Changed?.Invoke();
            });
            ReadyVersion = version ?? ReadyVersion;
            Status = version is null ? "You have the latest version." : $"ApiPin {version} is ready: restart to update.";
            if (version is not null) log.LogInformation("Update {Version} downloaded", version);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            log.LogWarning(ex, "Update check failed");
            Status = manual ? $"Couldn't check for updates: {ex.Message}" : null;
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    public void Restart()
    {
        if (ReadyVersion is not null) host.RestartToUpdate();
    }
}

using System.Text.Json;
using CodexBarWin.Infrastructure.Settings;
using Velopack;
using Velopack.Exceptions;
using Velopack.Sources;

namespace CodexBarWin.App;

internal sealed record AppUpdateAvailability(string Version);

internal sealed class AppUpdateService
{
    private readonly object _operationGate = new();
    private readonly UpdateManager? _updateManager;
    private UpdateInfo? _availableUpdate;
    private VelopackAsset? _downloadedUpdate;
    private bool _operationInProgress;

    public AppUpdateService(Uri? repository, AppUpdateChannel channel)
    {
        if (repository is null)
        {
            return;
        }

        bool includePrereleases = channel switch
        {
            AppUpdateChannel.Stable => false,
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
        GithubSource source = new(repository.AbsoluteUri, accessToken: null, prerelease: includePrereleases);
        this._updateManager = new UpdateManager(source);
    }

    public bool IsConfigured => this._updateManager is not null;

    public bool CanCheckForUpdates => this._updateManager?.IsInstalled == true;

    public bool HasCheckedForUpdates { get; private set; }

    public AppUpdateAvailability? AvailableUpdate => this._availableUpdate is null
        ? null
        : new AppUpdateAvailability(this._availableUpdate.TargetFullRelease.Version.ToString());

    public bool IsUpdateDownloaded => this._downloadedUpdate is not null;

    public async Task<AppUpdateAvailability?> CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        UpdateManager manager = this.GetAvailableManager();
        cancellationToken.ThrowIfCancellationRequested();
        this.BeginOperation();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            this._availableUpdate = await manager.CheckForUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            this._downloadedUpdate = manager.UpdatePendingRestart;
            this.HasCheckedForUpdates = true;
            return this.AvailableUpdate;
        }
        finally
        {
            this.EndOperation();
        }
    }

    public async Task DownloadUpdateAsync(
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        UpdateManager manager = this.GetAvailableManager();
        UpdateInfo update = this._availableUpdate
            ?? throw new InvalidOperationException("No application update is available to download.");

        cancellationToken.ThrowIfCancellationRequested();
        this.BeginOperation();
        try
        {
            await manager.DownloadUpdatesAsync(
                update,
                value => progress.Report(value),
                cancellationToken);
            this._downloadedUpdate = update.TargetFullRelease;
        }
        finally
        {
            this.EndOperation();
        }
    }

    public void PrepareUpdateAndRestart()
    {
        UpdateManager manager = this.GetAvailableManager();
        VelopackAsset update = this._downloadedUpdate
            ?? manager.UpdatePendingRestart
            ?? throw new InvalidOperationException("No downloaded application update is ready to install.");

        manager.WaitExitThenApplyUpdates(update, silent: false, restart: true);
    }

    public static bool IsExpectedFailure(Exception exception) => exception is
        HttpRequestException
        or OperationCanceledException
        or IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or ArgumentException
        or FormatException
        or JsonException
        or AcquireLockFailedException
        or ChecksumFailedException
        or NotInstalledException;

    private UpdateManager GetAvailableManager()
    {
        if (this._updateManager is null)
        {
            throw new InvalidOperationException("The application update repository is not configured.");
        }

        if (!this._updateManager.IsInstalled)
        {
            throw new InvalidOperationException(
                "Application updates are only available in a Velopack release build.");
        }

        return this._updateManager;
    }

    private void BeginOperation()
    {
        lock (this._operationGate)
        {
            if (this._operationInProgress)
            {
                throw new InvalidOperationException("Another application update operation is already running.");
            }

            this._operationInProgress = true;
        }
    }

    private void EndOperation()
    {
        lock (this._operationGate)
        {
            this._operationInProgress = false;
        }
    }
}

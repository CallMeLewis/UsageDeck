namespace CodexBarWin.Infrastructure.Settings;

public sealed class AppSettingsManager : IDisposable
{
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private readonly AppSettingsStore _store;

    public AppSettingsManager(AppSettingsStore store, AppSettings initialSettings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(initialSettings);

        this._store = store;
        this.Current = initialSettings;
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public async Task UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await this._updateLock.WaitAsync(cancellationToken);
        try
        {
            AppSettings updated = update(this.Current);
            ArgumentNullException.ThrowIfNull(updated);

            await this._store.SaveAsync(updated, cancellationToken);
            this.Current = updated;
            this.Changed?.Invoke(updated);
        }
        finally
        {
            this._updateLock.Release();
        }
    }

    public void Dispose() => this._updateLock.Dispose();
}

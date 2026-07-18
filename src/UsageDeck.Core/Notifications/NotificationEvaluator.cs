using UsageDeck.Core.Providers;

namespace UsageDeck.Core.Notifications;

public sealed class NotificationEvaluator
{
    private const int UnavailableFailureThreshold = 3;

    private readonly Dictionary<ProviderId, ConnectionAlertState> _activeConnectionAlerts = [];
    private readonly Dictionary<ProviderId, int> _consecutiveFailures = [];
    private readonly object _gate = new();
    private readonly HashSet<ProviderId> _notifiedStatusIncidents = [];
    private readonly Dictionary<UsageWindowKey, HashSet<int>> _notifiedThresholds = [];
    private readonly Dictionary<ProviderId, ProviderServiceStatusSnapshot> _statusSnapshots = [];
    private readonly Dictionary<ProviderId, ProviderSnapshot> _usageSnapshots = [];

    public IReadOnlyList<UsageNotificationEvent> EvaluateUsage(
        ProviderSnapshot current,
        NotificationEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);

        lock (this._gate)
        {
            List<UsageNotificationEvent> notifications = [];
            this._usageSnapshots.TryGetValue(current.ProviderId, out ProviderSnapshot? previous);

            if (current.State == UsageDataState.Fresh)
            {
                this.EvaluateConnectionRecovery(current, options, notifications);
                if (previous?.State == UsageDataState.Fresh)
                {
                    this.EvaluateUsageWindows(previous, current, options, notifications);
                    EvaluateResetCredits(previous, current, options, notifications);
                }
                else
                {
                    this.SeedThresholds(current, options);
                }
            }
            else if (previous is not null)
            {
                this.EvaluateConnectionFailure(current, options, notifications);
            }
            else
            {
                this.SeedConnectionFailure(current);
            }

            this._usageSnapshots[current.ProviderId] = current;
            return notifications;
        }
    }

    public IReadOnlyList<UsageNotificationEvent> EvaluateStatus(
        ProviderServiceStatusSnapshot current,
        NotificationEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(options);

        lock (this._gate)
        {
            List<UsageNotificationEvent> notifications = [];
            if (this._statusSnapshots.TryGetValue(
                    current.ProviderId,
                    out ProviderServiceStatusSnapshot? previous))
            {
                if (!previous.HasProblems && current.HasProblems && !current.IsStale)
                {
                    if (options.NotifyProviderStatusChanges)
                    {
                        this._notifiedStatusIncidents.Add(current.ProviderId);
                        notifications.Add(new ProviderIncidentDetectedNotification(
                            current.ProviderId,
                            current.ProviderId.DisplayName,
                            current.Summary,
                            current.IncidentUri));
                    }
                }
                else if (previous.HasProblems
                    && current.Health == ProviderServiceHealth.Operational
                    && !current.IsStale)
                {
                    bool incidentWasNotified = this._notifiedStatusIncidents.Remove(current.ProviderId);
                    if (incidentWasNotified && options.NotifyProviderStatusChanges)
                    {
                        notifications.Add(new ProviderIncidentResolvedNotification(
                            current.ProviderId,
                            current.ProviderId.DisplayName));
                    }
                }
            }

            this._statusSnapshots[current.ProviderId] = current;
            return notifications;
        }
    }

    public void RetainProviders(IEnumerable<ProviderId> providerIds)
    {
        ArgumentNullException.ThrowIfNull(providerIds);
        HashSet<ProviderId> retained = providerIds.ToHashSet();
        lock (this._gate)
        {
            RemoveMissing(this._usageSnapshots, retained);
            RemoveMissing(this._statusSnapshots, retained);
            RemoveMissing(this._consecutiveFailures, retained);
            RemoveMissing(this._activeConnectionAlerts, retained);
            this._notifiedStatusIncidents.RemoveWhere(id => !retained.Contains(id));
            foreach (UsageWindowKey key in this._notifiedThresholds.Keys
                .Where(key => !retained.Contains(key.ProviderId))
                .ToArray())
            {
                this._notifiedThresholds.Remove(key);
            }
        }
    }

    private static void RemoveMissing<T>(Dictionary<ProviderId, T> values, HashSet<ProviderId> retained)
    {
        foreach (ProviderId providerId in values.Keys.Where(id => !retained.Contains(id)).ToArray())
        {
            values.Remove(providerId);
        }
    }

    private void EvaluateUsageWindows(
        ProviderSnapshot previous,
        ProviderSnapshot current,
        NotificationEvaluationOptions options,
        List<UsageNotificationEvent> notifications)
    {
        Dictionary<string, UsageWindow> previousWindows = previous.UsageWindows
            .ToDictionary(window => window.Id, StringComparer.Ordinal);
        foreach (UsageWindow window in current.UsageWindows)
        {
            if (!window.UsageKnown
                || window.IsUnlimited
                || !previousWindows.TryGetValue(window.Id, out UsageWindow? previousWindow)
                || !previousWindow.UsageKnown
                || previousWindow.IsUnlimited)
            {
                continue;
            }

            if (HasReset(previousWindow, window, current.CapturedAt))
            {
                this._notifiedThresholds.Remove(new UsageWindowKey(current.ProviderId, window.Id));
                if (options.NotifyLimitResets)
                {
                    notifications.Add(new UsageWindowResetNotification(
                        current.ProviderId,
                        current.DisplayName,
                        window.Id,
                        window.DisplayName,
                        window.UsedPercent));
                }

                continue;
            }

            HashSet<int> notifiedThresholds = this.GetNotifiedThresholds(current.ProviderId, window.Id);
            int? mostSevereThreshold = options.RemainingThresholds
                .Where(threshold => !notifiedThresholds.Contains(threshold))
                .Where(threshold =>
                {
                    double usedThreshold = 100 - threshold;
                    return previousWindow.UsedPercent < usedThreshold
                        && window.UsedPercent >= usedThreshold;
                })
                .Select(threshold => (int?)threshold)
                .Min();
            foreach (int crossedThreshold in options.RemainingThresholds.Where(threshold =>
                window.UsedPercent >= 100 - threshold))
            {
                notifiedThresholds.Add(crossedThreshold);
            }

            if (mostSevereThreshold is int threshold)
            {
                notifications.Add(new LimitThresholdCrossedNotification(
                    current.ProviderId,
                    current.DisplayName,
                    window.Id,
                    window.DisplayName,
                    window.UsedPercent,
                    threshold));
            }
        }
    }

    private void SeedThresholds(
        ProviderSnapshot snapshot,
        NotificationEvaluationOptions options)
    {
        foreach (UsageWindow window in snapshot.UsageWindows.Where(window =>
            window.UsageKnown && !window.IsUnlimited))
        {
            HashSet<int> notifiedThresholds = this.GetNotifiedThresholds(
                snapshot.ProviderId,
                window.Id);
            foreach (int threshold in options.RemainingThresholds.Where(threshold =>
                window.UsedPercent >= 100 - threshold))
            {
                notifiedThresholds.Add(threshold);
            }
        }
    }

    private HashSet<int> GetNotifiedThresholds(ProviderId providerId, string windowId)
    {
        UsageWindowKey key = new(providerId, windowId);
        if (!this._notifiedThresholds.TryGetValue(key, out HashSet<int>? thresholds))
        {
            thresholds = [];
            this._notifiedThresholds[key] = thresholds;
        }

        return thresholds;
    }

    private static bool HasReset(
        UsageWindow previous,
        UsageWindow current,
        DateTimeOffset capturedAt) =>
        previous.ResetsAt is DateTimeOffset previousReset
        && current.ResetsAt is DateTimeOffset currentReset
        && previousReset <= capturedAt
        && currentReset > previousReset
        && current.UsedPercent < previous.UsedPercent;

    private static void EvaluateResetCredits(
        ProviderSnapshot previous,
        ProviderSnapshot current,
        NotificationEvaluationOptions options,
        List<UsageNotificationEvent> notifications)
    {
        if (!options.NotifyCodexResetCredits
            || current.ProviderId != ProviderId.Codex
            || previous.ResetCredits is null
            || current.ResetCredits is null
            || current.ResetCredits.AvailableCount <= previous.ResetCredits.AvailableCount)
        {
            return;
        }

        notifications.Add(new CodexResetCreditGrantedNotification(
            current.ProviderId,
            current.DisplayName,
            current.ResetCredits.AvailableCount - previous.ResetCredits.AvailableCount,
            current.ResetCredits.AvailableCount));
    }

    private void EvaluateConnectionFailure(
        ProviderSnapshot current,
        NotificationEvaluationOptions options,
        List<UsageNotificationEvent> notifications)
    {
        if (current.ErrorCategory == ProviderErrorCategory.AuthenticationRequired)
        {
            this._consecutiveFailures[current.ProviderId] = 0;
            if (!this._activeConnectionAlerts.TryGetValue(
                    current.ProviderId,
                    out ConnectionAlertState? activeAlert)
                || activeAlert.Alert != ConnectionAlert.AuthenticationRequired)
            {
                this._activeConnectionAlerts[current.ProviderId] = new ConnectionAlertState(
                    ConnectionAlert.AuthenticationRequired,
                    options.NotifyProviderConnectionChanges);
                if (options.NotifyProviderConnectionChanges)
                {
                    notifications.Add(new ProviderAuthenticationRequiredNotification(
                        current.ProviderId,
                        current.DisplayName));
                }
            }

            return;
        }

        int failureCount = this._consecutiveFailures.GetValueOrDefault(current.ProviderId) + 1;
        this._consecutiveFailures[current.ProviderId] = failureCount;
        if (failureCount < UnavailableFailureThreshold
            || this._activeConnectionAlerts.ContainsKey(current.ProviderId))
        {
            return;
        }

        this._activeConnectionAlerts[current.ProviderId] = new ConnectionAlertState(
            ConnectionAlert.Unavailable,
            options.NotifyProviderConnectionChanges);
        if (options.NotifyProviderConnectionChanges)
        {
            notifications.Add(new ProviderDataUnavailableNotification(
                current.ProviderId,
                current.DisplayName));
        }
    }

    private void SeedConnectionFailure(ProviderSnapshot current)
    {
        if (current.ErrorCategory == ProviderErrorCategory.AuthenticationRequired)
        {
            this._consecutiveFailures[current.ProviderId] = 0;
            this._activeConnectionAlerts[current.ProviderId] = new ConnectionAlertState(
                ConnectionAlert.AuthenticationRequired,
                WasNotified: false);
            return;
        }

        this._consecutiveFailures[current.ProviderId] = 1;
    }

    private void EvaluateConnectionRecovery(
        ProviderSnapshot current,
        NotificationEvaluationOptions options,
        List<UsageNotificationEvent> notifications)
    {
        this._consecutiveFailures[current.ProviderId] = 0;
        bool hadAlert = this._activeConnectionAlerts.Remove(
            current.ProviderId,
            out ConnectionAlertState? activeAlert);
        if (hadAlert
            && activeAlert!.WasNotified
            && options.NotifyProviderConnectionChanges)
        {
            notifications.Add(new ProviderConnectionRecoveredNotification(
                current.ProviderId,
                current.DisplayName));
        }
    }

    private enum ConnectionAlert
    {
        AuthenticationRequired,
        Unavailable,
    }

    private sealed record ConnectionAlertState(ConnectionAlert Alert, bool WasNotified);

    private readonly record struct UsageWindowKey(ProviderId ProviderId, string WindowId);
}

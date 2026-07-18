using System.Globalization;
using System.Text.Json;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.Codex;

public sealed class CodexUsageProvider : IUsageProvider, ICliVersionProvider
{
    private const int MaxMessageCharacters = 1_048_576;
    private const int MaxMessagesPerRequest = 200;
    private readonly IProcessSessionFactory _sessionFactory;
    private readonly CodexProcessSpecFactory _processSpecFactory;
    private readonly ProviderHost _host;
    private readonly TimeProvider _timeProvider;
    private readonly ICliVersionReader? _cliVersionReader;

    public CodexUsageProvider(
        IProcessSessionFactory sessionFactory,
        CodexProcessSpecFactory processSpecFactory,
        ProviderHost host,
        TimeProvider? timeProvider = null,
        ICliVersionReader? cliVersionReader = null)
    {
        this._sessionFactory = sessionFactory;
        this._processSpecFactory = processSpecFactory;
        this._host = host;
        this._timeProvider = timeProvider ?? TimeProvider.System;
        this._cliVersionReader = cliVersionReader;
    }

    public ProviderId Id => ProviderId.Codex;

    public string DisplayName => "Codex";

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        if (this._cliVersionReader is null)
        {
            return null;
        }

        return await this._cliVersionReader.ReadAsync(
            this._processSpecFactory.CreateVersion(this._host),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        ProcessStartSpec spec = this._processSpecFactory.Create(this._host);

        IProcessSession session;
        try
        {
            session = this._sessionFactory.Start(spec);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "Codex CLI could not be started. Check the selected installation and try again.",
                exception);
        }

        await using (session.ConfigureAwait(false))
        {
            JsonElement initialize = await SendRequestAsync(
                session,
                1,
                "initialize",
                new { clientInfo = new { name = "usagedeck", version = "0.3.0" } },
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);
            _ = initialize;

            await SendNotificationAsync(session, "initialized", cancellationToken).ConfigureAwait(false);

            JsonElement rateLimitsResult = await SendRequestAsync(
                session,
                2,
                "account/rateLimits/read",
                new { },
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);

            JsonElement? accountResult = null;
            try
            {
                accountResult = await SendRequestAsync(
                    session,
                    3,
                    "account/read",
                    new { },
                    TimeSpan.FromSeconds(3),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ProviderException exception) when (exception.Category is ProviderErrorCategory.Transient or ProviderErrorCategory.InvalidResponse)
            {
                // Identity is useful but not required when authoritative rate-limit data succeeded.
            }

            return this.MapSnapshot(rateLimitsResult, accountResult);
        }
    }

    private static async Task SendNotificationAsync(
        IProcessSession session,
        string method,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { method, @params = new { } });
        await session.WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonElement> SendRequestAsync(
        IProcessSession session,
        int id,
        string method,
        object parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params = parameters });
        await session.WriteLineAsync(payload, cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            for (int messageCount = 0; messageCount < MaxMessagesPerRequest; messageCount++)
            {
                string? line = await session.ReadLineAsync(timeoutSource.Token).ConfigureAwait(false);
                if (line is null)
                {
                    throw new ProviderException(
                        ProviderErrorCategory.Transient,
                        $"Codex closed before replying to {FriendlyMethodName(method)}.");
                }

                if (line.Length > MaxMessageCharacters)
                {
                    throw new ProviderException(
                        ProviderErrorCategory.InvalidResponse,
                        "Codex returned a response that was too large to process safely.");
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    JsonElement root = document.RootElement;
                    if (!TryGetInt32(root, "id", out int responseId) || responseId != id)
                    {
                        continue;
                    }

                    if (TryGetProperty(root, "error", out JsonElement error))
                    {
                        throw MapRpcError(method, error);
                    }

                    if (!TryGetProperty(root, "result", out JsonElement result))
                    {
                        throw new ProviderException(
                            ProviderErrorCategory.InvalidResponse,
                            $"Codex returned an incomplete {FriendlyMethodName(method)} response.");
                    }

                    return result.Clone();
                }
            }

            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                $"Codex sent too many unrelated messages while reading {FriendlyMethodName(method)}.");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                $"Codex took too long to return {FriendlyMethodName(method)}.",
                exception);
        }
    }

    private ProviderSnapshot MapSnapshot(JsonElement rateLimitsResult, JsonElement? accountResult)
    {
        if (!TryGetProperty(rateLimitsResult, "rateLimits", "rate_limits", out JsonElement rateLimits))
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Codex returned no rate-limit data.");
        }

        List<UsageWindow> windows = [];
        AddWindow(windows, "session", "Session", rateLimits, "primary", inferWindowKind: true);
        AddWindow(windows, "weekly", "Weekly", rateLimits, "secondary", inferWindowKind: true);

        HashSet<string> mappedLimitIds = new(StringComparer.Ordinal);
        if (GetString(rateLimits, "limitId", "limit_id") is { } topLevelLimitId)
        {
            mappedLimitIds.Add(NormaliseWindowId(topLevelLimitId));
        }

        if (TryGetProperty(rateLimitsResult, "rateLimitsByLimitId", "rate_limits_by_limit_id", out JsonElement additional)
            && additional.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty entry in additional.EnumerateObject())
            {
                JsonElement value = entry.Value;
                string fallbackName = HumaniseIdentifier(entry.Name);
                string displayName = GetString(value, "limitName", "limit_name") ?? fallbackName;
                string normalizedId = NormaliseWindowId(GetString(value, "limitId", "limit_id") ?? entry.Name);
                if (!mappedLimitIds.Add(normalizedId))
                {
                    continue;
                }

                AddWindow(windows, normalizedId, displayName, value, "primary", inferWindowKind: true);
                AddWindow(windows, normalizedId + "-weekly", displayName + " Weekly", value, "secondary", inferWindowKind: true);
            }
        }

        if (windows.Count == 0)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Codex returned no usage windows for this account.");
        }

        AccountIdentity? identity = MapIdentity(accountResult, rateLimits);
        CreditBalance? credits = MapCredits(rateLimits);
        RateLimitResetCredits? resetCredits = MapResetCredits(rateLimitsResult);

        return new ProviderSnapshot(
            ProviderId.Codex,
            this.DisplayName,
            this._host.DisplayName,
            this._timeProvider.GetUtcNow(),
            UsageDataState.Fresh,
            windows,
            identity,
            credits,
            resetCredits);
    }

    private static AccountIdentity? MapIdentity(JsonElement? accountResult, JsonElement rateLimits)
    {
        string? plan = GetString(rateLimits, "planType", "plan_type");
        string? email = null;

        if (accountResult is JsonElement result
            && TryGetProperty(result, "account", out JsonElement account)
            && account.ValueKind == JsonValueKind.Object)
        {
            email = GetString(account, "email");
            plan ??= GetString(account, "planType", "plan_type");
        }

        return email is null && plan is null ? null : new AccountIdentity(email, plan);
    }

    private static CreditBalance? MapCredits(JsonElement rateLimits)
    {
        if (!TryGetProperty(rateLimits, "credits", out JsonElement credits) || credits.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        bool hasCredits = GetBoolean(credits, "hasCredits", "has_credits");
        bool unlimited = GetBoolean(credits, "unlimited");
        string? balance = GetString(credits, "balance");
        return new CreditBalance(balance, hasCredits, unlimited);
    }

    private static RateLimitResetCredits? MapResetCredits(JsonElement rateLimitsResult)
    {
        if (!TryGetProperty(
                rateLimitsResult,
                "rateLimitResetCredits",
                "rate_limit_reset_credits",
                out JsonElement resetCredits)
            || resetCredits.ValueKind != JsonValueKind.Object
            || !TryGetInt64(resetCredits, "availableCount", "available_count", out long availableCount)
            || availableCount < 0)
        {
            return null;
        }

        List<RateLimitResetCredit> credits = [];
        if (TryGetProperty(resetCredits, "credits", out JsonElement creditDetails)
            && creditDetails.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement credit in creditDetails.EnumerateArray())
            {
                if (credit.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                DateTimeOffset? expiresAt = null;
                if (TryGetInt64(credit, "expiresAt", "expires_at", out long expiresAtSeconds))
                {
                    try
                    {
                        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtSeconds);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                    }
                }

                credits.Add(new RateLimitResetCredit(expiresAt));
            }
        }

        return new RateLimitResetCredits(availableCount, credits);
    }

    private static void AddWindow(
        List<UsageWindow> windows,
        string id,
        string displayName,
        JsonElement container,
        string propertyName,
        bool inferWindowKind)
    {
        if (!TryGetProperty(container, propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!TryGetDouble(value, "usedPercent", "used_percent", out double usedPercent))
        {
            return;
        }

        DateTimeOffset? resetsAt = null;
        if (TryGetInt64(value, "resetsAt", "resets_at", out long resetSeconds) && resetSeconds > 0)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        TimeSpan? duration = null;
        if (TryGetInt64(value, "windowDurationMins", "window_duration_mins", out long durationMinutes)
            && durationMinutes > 0
            && durationMinutes <= (long)TimeSpan.MaxValue.TotalMinutes)
        {
            duration = TimeSpan.FromMinutes(durationMinutes);
        }

        if (inferWindowKind && duration is not null)
        {
            if (duration <= TimeSpan.FromHours(6))
            {
                if (id is "session" or "weekly")
                {
                    id = "session";
                    displayName = "Session";
                }
            }
            else if (duration >= TimeSpan.FromDays(6))
            {
                if (id is "session" or "weekly")
                {
                    id = "weekly";
                    displayName = "Weekly";
                }
                else
                {
                    if (!id.EndsWith("-weekly", StringComparison.Ordinal))
                    {
                        id += "-weekly";
                    }

                    if (!displayName.Contains("weekly", StringComparison.OrdinalIgnoreCase))
                    {
                        displayName += " Weekly";
                    }
                }
            }
        }

        if (windows.Any(window => string.Equals(window.Id, id, StringComparison.Ordinal)))
        {
            id += "-additional";
        }

        windows.Add(new UsageWindow(id, displayName, usedPercent, resetsAt, duration));
    }

    private static ProviderException MapRpcError(string method, JsonElement error)
    {
        string message = GetString(error, "message") ?? string.Empty;
        ProviderErrorCategory category = message.Contains("auth", StringComparison.OrdinalIgnoreCase)
            || message.Contains("login", StringComparison.OrdinalIgnoreCase)
            ? ProviderErrorCategory.AuthenticationRequired
            : ProviderErrorCategory.Transient;

        string safeMessage = category == ProviderErrorCategory.AuthenticationRequired
            ? "Codex needs you to sign in. Run `codex login`, then refresh."
            : $"Codex could not return {FriendlyMethodName(method)}.";

        return new ProviderException(category, safeMessage);
    }

    private static string FriendlyMethodName(string method) => method switch
    {
        "initialize" => "startup information",
        "account/read" => "account details",
        "account/rateLimits/read" => "usage limits",
        _ => "usage information",
    };

    private static string NormaliseWindowId(string value)
    {
        char[] characters = value.Trim().ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray();
        string normalized = new(characters);
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        normalized = normalized.Trim('-');
        return string.IsNullOrEmpty(normalized) ? "additional-limit" : normalized;
    }

    private static string HumaniseIdentifier(string value) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('-', ' ').Replace('_', ' '));

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.TryGetProperty(name, out value);
        }

        value = default;
        return false;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string camelName,
        string snakeName,
        out JsonElement value) =>
        TryGetProperty(element, camelName, out value) || TryGetProperty(element, snakeName, out value);

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    string? result = value.GetString()?.Trim();
                    return string.IsNullOrEmpty(result) ? null : result;
                }

                if (value.ValueKind == JsonValueKind.Number)
                {
                    return value.GetRawText();
                }
            }
        }

        return null;
    }

    private static bool GetBoolean(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out JsonElement value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return false;
    }

    private static bool TryGetDouble(
        JsonElement element,
        string camelName,
        string snakeName,
        out double value)
    {
        if (TryGetProperty(element, camelName, snakeName, out JsonElement property))
        {
            if (property.TryGetDouble(out value))
            {
                return double.IsFinite(value);
            }

            if (property.ValueKind == JsonValueKind.String
                && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return double.IsFinite(value);
            }
        }

        value = 0;
        return false;
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        if (TryGetProperty(element, name, out JsonElement property))
        {
            return property.TryGetInt32(out value);
        }

        value = 0;
        return false;
    }

    private static bool TryGetInt64(
        JsonElement element,
        string camelName,
        string snakeName,
        out long value)
    {
        if (TryGetProperty(element, camelName, snakeName, out JsonElement property))
        {
            if (property.TryGetInt64(out value))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String
                && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Processes;

namespace UsageDeck.Infrastructure.Providers.KimiCode;

/// <summary>
/// Reads Kimi Code plan usage with the sign-in the Kimi Code CLI holds. Its access token lasts
/// about 15 minutes and Kimi Code renews it only while working, so UsageDeck renews a lapsed
/// token itself, the way a second Kimi Code window would: the renewed token, including the
/// replacement refresh token, is saved straight back to Kimi Code's credentials file, which
/// Kimi Code re-reads before every request.
/// </summary>
public sealed class KimiCodeUsageProvider(
    HttpClient httpClient,
    IExecutableLocator executableLocator,
    IKimiCodeCredentialsStore? credentialsStore = null,
    TimeProvider? timeProvider = null,
    ICliVersionReader? cliVersionReader = null) : IUsageProvider, ICliVersionProvider
{
    public const string InactiveMessage = "Kimi Code usage updates while you're using Kimi Code.";

    // Kimi Code's own CLI client identifier, which its refresh tokens are issued to.
    internal const string OAuthClientId = "17e5f671-d194-4dfb-9706-5516cb48c098";

    private const int MaximumResponseBytes = 1_048_576;

    // Leaves time for the request to arrive before the token lapses. Kimi Code renews well
    // before this point while it is working, so UsageDeck only renews when it is idle.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromSeconds(30);

    // Kimi Code waits this long before re-reading after a rejected renewal, to adopt a renewal
    // another process has just saved.
    private static readonly TimeSpan RenewalRecoveryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly ProductInfoHeaderValue UserAgent = new(
        "UsageDeck",
        typeof(KimiCodeUsageProvider).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    private readonly IKimiCodeCredentialsStore _credentialsStore = credentialsStore ?? new KimiCodeCredentialsStore();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public ProviderId Id => ProviderId.KimiCode;

    public string DisplayName => ProviderId.KimiCode.DisplayName;

    public async Task<string?> ReadCliVersionAsync(CancellationToken cancellationToken)
    {
        string? executablePath = executableLocator.FindExecutable("kimi");
        if (executablePath is null || cliVersionReader is null)
        {
            return null;
        }

        return await cliVersionReader.ReadAsync(
            new ProcessStartSpec(executablePath, ["--version"]),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        // Renewal runs outside the usage timeout: abandoning it after Kimi's service has
        // replaced the refresh token would leave Kimi Code signed out.
        KimiCodeCredentials credentials = await this.GetUsableCredentialsAsync(cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            HttpResponseMessage response = await this.SendAsync(credentials, "usages", timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Kimi Code may have renewed its sign-in between the read and the request.
                response.Dispose();
                KimiCodeCredentials current = this.ReadCredentials();
                if (current.Token == credentials.Token)
                {
                    throw new ProviderException(
                        ProviderErrorCategory.AuthenticationRequired,
                        "Kimi Code needs you to sign in again. Run `kimi login`, then refresh.");
                }

                credentials = current;
                response = await this.SendAsync(credentials, "usages", timeout.Token).ConfigureAwait(false);
            }

            using (response)
            {
                this.ThrowIfUnsuccessful(response);
                byte[] body = await ReadBoundedResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
                string? plan = await this.ReadPlanAsync(credentials, timeout.Token).ConfigureAwait(false);
                return KimiCodeUsageParser.Parse(body, this._timeProvider.GetUtcNow(), plan);
            }
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Kimi Code did not return usage in time.",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Kimi Code usage could not be reached.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Kimi Code usage could not be read.",
                exception);
        }
    }

    private async Task<KimiCodeCredentials> GetUsableCredentialsAsync(CancellationToken cancellationToken)
    {
        KimiCodeCredentials credentials = this.ReadCredentials();
        if (!this.HasLapsed(credentials))
        {
            return credentials;
        }

        if (credentials.Renewal is not KimiCodeRenewal renewal)
        {
            throw new ProviderException(ProviderErrorCategory.Inactive, InactiveMessage);
        }

        KimiCodeTokenSet? renewed = await this.RenewAsync(renewal, cancellationToken).ConfigureAwait(false);
        if (renewed is null)
        {
            // A running Kimi Code may have spent the same refresh token first. Never clear or
            // rewrite the file here; adopt what Kimi Code saved, as it does itself.
            await Task.Delay(RenewalRecoveryDelay, cancellationToken).ConfigureAwait(false);
            KimiCodeCredentials current = this.ReadCredentials();
            if (!this.HasLapsed(current))
            {
                return current;
            }

            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Kimi Code's sign-in couldn't be renewed. If Kimi Code asks you to sign in, run `kimi login`.");
        }

        try
        {
            this._credentialsStore.Save(renewal.StorageName, renewed);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProviderException(
                ProviderErrorCategory.Unavailable,
                "UsageDeck renewed Kimi Code's sign-in but couldn't save it. If Kimi Code asks you to sign in, run `kimi login`.",
                exception);
        }

        return credentials with
        {
            Token = renewed.AccessToken,
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(renewed.ExpiresAt),
            Renewal = renewal with { RefreshToken = renewed.RefreshToken },
        };
    }

    /// <summary>
    /// Exchanges the refresh token for a new token set, or returns null when Kimi's sign-in
    /// service rejects it.
    /// </summary>
    private async Task<KimiCodeTokenSet?> RenewAsync(KimiCodeRenewal renewal, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri($"{renewal.OAuthHost.AbsoluteUri.TrimEnd('/')}/api/oauth/token"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = OAuthClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = renewal.RefreshToken,
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.Add(UserAgent);
            if (renewal.DeviceId is not null)
            {
                request.Headers.TryAddWithoutValidation("X-Msh-Device-Id", renewal.DeviceId);
            }

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            DateTimeOffset now = this._timeProvider.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.TooManyRequests || response.StatusCode >= HttpStatusCode.InternalServerError)
            {
                UsageRetryBackoff.ThrowIfRequested(response, this.DisplayName, now);
                throw new ProviderException(
                    ProviderErrorCategory.Transient,
                    "Kimi Code's sign-in service is temporarily unavailable.");
            }

            byte[] body = await ReadBoundedResponseAsync(response.Content, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                || ReadError(body) == "invalid_grant")
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ProviderException(
                    ProviderErrorCategory.Unavailable,
                    "Kimi Code's sign-in couldn't be renewed. If Kimi Code asks you to sign in, run `kimi login`.");
            }

            return ParseTokenSet(body, now) ?? throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Kimi Code's sign-in service returned a response UsageDeck could not read. If Kimi Code asks you to sign in, run `kimi login`.");
        }
        catch (ProviderException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException)
        {
            throw new ProviderException(
                ProviderErrorCategory.Transient,
                "Kimi Code's sign-in service could not be reached.",
                exception);
        }
    }

    internal static KimiCodeTokenSet? ParseTokenSet(ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || GetString(root, "access_token") is not { Length: > 0 } accessToken
                || GetString(root, "refresh_token") is not { Length: > 0 } refreshToken
                || !root.TryGetProperty("expires_in", out JsonElement expiresInElement))
            {
                return null;
            }

            double expiresIn = expiresInElement.ValueKind switch
            {
                JsonValueKind.Number when expiresInElement.TryGetDouble(out double number) => number,
                JsonValueKind.String when double.TryParse(
                    expiresInElement.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double parsed) => parsed,
                _ => double.NaN,
            };
            if (!double.IsFinite(expiresIn) || expiresIn <= 0 || expiresIn > TimeSpan.FromDays(366).TotalSeconds)
            {
                return null;
            }

            long seconds = (long)expiresIn;
            return new KimiCodeTokenSet(
                accessToken,
                refreshToken,
                now.ToUnixTimeSeconds() + seconds,
                GetString(root, "scope") ?? string.Empty,
                GetString(root, "token_type") ?? "Bearer",
                seconds);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadError(ReadOnlySpan<byte> body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object ? GetString(document.RootElement, "error") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private bool HasLapsed(KimiCodeCredentials credentials) =>
        credentials.ExpiresAt is DateTimeOffset expiresAt
        && expiresAt <= this._timeProvider.GetUtcNow().Add(ExpiryMargin);

    private KimiCodeCredentials ReadCredentials()
    {
        KimiCodeCredentials? credentials = this._credentialsStore.Read();
        if (credentials is not null)
        {
            return credentials;
        }

        if (!this._credentialsStore.HasKimiCodeHome && executableLocator.FindExecutable("kimi") is null)
        {
            throw new ProviderException(
                ProviderErrorCategory.NotInstalled,
                "Kimi Code CLI is not installed or `kimi` is not on PATH.");
        }

        throw new ProviderException(
            ProviderErrorCategory.AuthenticationRequired,
            "Kimi Code needs you to sign in. Run `kimi login`, then refresh.");
    }

    private void ThrowIfUnsuccessful(HttpResponseMessage response)
    {
        UsageRetryBackoff.ThrowIfRequested(response, this.DisplayName, this._timeProvider.GetUtcNow());
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ProviderException(
                ProviderErrorCategory.AuthenticationRequired,
                "Kimi Code needs you to sign in again. Run `kimi login`, then refresh."),
            HttpStatusCode.Forbidden => new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Kimi Code's usage service refused the request."),
            HttpStatusCode.NotFound => new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Kimi Code doesn't report usage for this account."),
            HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError => new ProviderException(
                ProviderErrorCategory.Transient,
                "Kimi Code usage is temporarily unavailable."),
            _ => new ProviderException(
                ProviderErrorCategory.Unavailable,
                "Kimi Code could not return usage right now."),
        };
    }

    private async Task<string?> ReadPlanAsync(KimiCodeCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await this.SendAsync(credentials, "me", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] body = await ReadBoundedResponseAsync(response.Content, cancellationToken).ConfigureAwait(false);
            return KimiCodeUsageParser.ParsePlan(body);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ProviderException
            || (exception is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // The plan name is optional, so a failed profile request leaves the usage reading intact.
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        KimiCodeCredentials credentials,
        string path,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{credentials.BaseUri.AbsoluteUri.TrimEnd('/')}/{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(UserAgent);
        return await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedResponseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        long? length = content.Headers.ContentLength;
        if (length > MaximumResponseBytes)
        {
            throw new ProviderException(
                ProviderErrorCategory.InvalidResponse,
                "Kimi Code returned a response that was too large to process safely.");
        }

        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream result = new(length is > 0 ? (int)length.Value : 4096);
        byte[] buffer = new byte[8192];
        int total = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaximumResponseBytes)
            {
                throw new ProviderException(
                    ProviderErrorCategory.InvalidResponse,
                    "Kimi Code returned a response that was too large to process safely.");
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }
}

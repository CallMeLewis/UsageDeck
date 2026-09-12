using System.Net;
using UsageDeck.Core.Providers;

namespace UsageDeck.Infrastructure.Providers;

internal static class UsageRetryBackoff
{
    public static void ThrowIfRequested(HttpResponseMessage response, string providerName, DateTimeOffset now)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        DateTimeOffset? deadline = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
        {
            TimeSpan bounded = delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue - now : delta;
            deadline = now.Add(bounded);
        }

        if (deadline is null && response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            // Without a stated deadline, avoid repeatedly probing a throttled endpoint.
            deadline = now.AddMinutes(5);
        }

        if (deadline is DateTimeOffset retryAt && retryAt > now)
        {
            bool authenticationRequired = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            throw new ProviderException(
                authenticationRequired ? ProviderErrorCategory.AuthenticationRequired : ProviderErrorCategory.Transient,
                authenticationRequired
                    ? $"{providerName} rejected authentication and requested a pause before checking usage again. Check sign-in or credentials in Settings."
                    : $"{providerName} requested a pause before checking usage again.")
            {
                RetryNotBeforeUtc = retryAt,
            };
        }
    }
}

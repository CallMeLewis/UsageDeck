using System.Net;
using System.Net.Http.Headers;
using UsageDeck.Core.Providers;
using UsageDeck.Infrastructure.Providers;

namespace UsageDeck.Infrastructure.Tests;

public sealed class UsageRetryBackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CarriesBothFormsOfRetryAfterWithoutReadingTheBody(bool absoluteDate)
    {
        using HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = absoluteDate
            ? new RetryConditionHeaderValue(Now.AddMinutes(10))
            : new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
        ProviderException failure = Assert.Throws<ProviderException>(() =>
            UsageRetryBackoff.ThrowIfRequested(response, "Provider", Now));
        Assert.Equal(Now.AddMinutes(10), failure.RetryNotBeforeUtc);
        Assert.Equal(ProviderErrorCategory.Transient, failure.Category);
    }

    [Fact]
    public void ThrottlingWithoutDeadlineUsesConservativeDefault()
    {
        using HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
        ProviderException failure = Assert.Throws<ProviderException>(() =>
            UsageRetryBackoff.ThrowIfRequested(response, "Provider", Now));
        Assert.Equal(Now.AddMinutes(5), failure.RetryNotBeforeUtc);
    }

    [Fact]
    public void OrdinaryServerFailureDoesNotRequestBackoff()
    {
        using HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);
        UsageRetryBackoff.ThrowIfRequested(response, "Provider", Now);
    }
}

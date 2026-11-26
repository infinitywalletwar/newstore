using System.Net.Http.Json;

namespace NewStore.Delivery.Tests;

public sealed record ProblemDto(string? Type, int Status, string? Title);

/// <summary>Helpers the tests share for reading the service's answers.</summary>
internal static class Api
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The time cut to whole microseconds, as the service stores it.</summary>
    public static DateTimeOffset Micro(DateTimeOffset t) => new(t.UtcTicks - (t.UtcTicks % 10), TimeSpan.Zero);

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    public static async Task<ProblemDto> ProblemAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Ct))!;
}

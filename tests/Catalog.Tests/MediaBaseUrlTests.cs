using System.Net.Http.Json;

using NewStore.Catalog.Videos;

namespace NewStore.Catalog.Tests;

public class MediaBaseUrlTests(PostgresServer postgres, KafkaServer kafka)
{
    [Fact]
    public async Task A_trailing_slash_in_the_media_base_url_does_not_double_the_slash()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new SlashFactory(postgres, kafka);
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        var video = await client.GetFromJsonAsync<VideoDetails>("/videos/00000000-0000-7000-8001-000000000011", ct);

        Assert.Equal(KafkaSettings.MediaBaseUrl + "/hls/sample/master.m3u8", video!.PlaybackUrl);
    }

    /// <summary>Sets the URL before the app first starts: a setting changed afterwards is not read.</summary>
    private sealed class SlashFactory(PostgresServer postgres, KafkaServer kafka) : SeededCatalogAppFactory(postgres, kafka)
    {
        protected override ValueTask ConfigureSettingsAsync()
        {
            Settings["Catalog:MediaBaseUrl"] = KafkaSettings.MediaBaseUrl + "/";
            return ValueTask.CompletedTask;
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Entities;
using Tesserafin.Playback.Decision;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

public sealed partial class RemuxFilmEndToEndTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Film_Transcode_ExecutesOnlyThePlannedAxesAndCleansUp(bool encodeVideo, bool encodeAudio, bool episode)
    {
        var (client, userId) = await SignInAsync($"encode-{encodeVideo}-{encodeAudio}-{episode}");
        var streams = new[]
        {
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = EndToEndMediaFixtures.Width, Height = EndToEndMediaFixtures.Height, BitRate = 500_000, IsDefault = true },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, BitRate = 128_000, IsDefault = true },
        };
        var itemId = SeedFilm(_fixturePath, streams, episode: episode);
        var (capabilities, constraints) = EndToEndCapabilityPresets.BrowserRemuxOverHls();
        capabilities = capabilities with
        {
            Decode = capabilities.Decode with
            {
                VideoCodecs = capabilities.Decode.VideoCodecs.Select(codec => codec with
                {
                    MaxResolution = encodeVideo ? new Resolution(160, 120) : null,
                }).ToArray(),
            },
        };
        constraints = constraints with
        {
            MaxAudioChannels = encodeAudio ? 1 : null,
            MaxBitrate = encodeVideo ? 400_000 : null,
        };
        var request = new CreatePlaybackSessionRequest(itemId, userId, capabilities, constraints, PlaySessionId: $"encode-{encodeVideo}-{encodeAudio}-{episode}-ps");
        using var response = await client.PostAsJsonAsync("Playback/Sessions", request, JsonDefaults.Options, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, body);
        var session = JsonSerializer.Deserialize<PlaybackSessionResponse>(body, JsonDefaults.Options)!;
        Assert.Equal(PlaybackMethod.Transcode, session.Method);
        Assert.Contains(encodeVideo ? TransformKind.TranscodeVideo : TransformKind.CopyVideo, session.Transforms);
        Assert.Contains(encodeAudio ? TransformKind.TranscodeAudio : TransformKind.CopyAudio, session.Transforms);

        var descriptor = await GetDescriptorAsync(client, session.Id);
        Assert.StartsWith($"/Playback/Sessions/{session.Id:N}/Hls/", descriptor.Url, StringComparison.Ordinal);
        var playlist = await ReadPlaylistAsync(client, descriptor.Url);
        var init = await GetBytesAsync(client, playlist.Init);
        var first = await GetBytesAsync(client, playlist.Segments[0]);
        Assert.NotEmpty(await GetBytesAsync(client, playlist.Segments[^1]));
        var produced = Path.Combine(_workDir, $"encoded-{encodeVideo}-{encodeAudio}.mp4");
        await File.WriteAllBytesAsync(produced, [.. init, .. first], TestContext.Current.CancellationToken);
        using var probed = JsonDocument.Parse(await EndToEndMediaFixtures.ProbeStreamsJsonAsync(produced));
        var encodedStreams = probed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = Assert.Single(encodedStreams, stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = Assert.Single(encodedStreams, stream => stream.GetProperty("codec_type").GetString() == "audio");
        Assert.Equal("h264", video.GetProperty("codec_name").GetString());
        Assert.Equal("aac", audio.GetProperty("codec_name").GetString());
        Assert.Equal(encodeVideo ? 160 : EndToEndMediaFixtures.Width, video.GetProperty("width").GetInt32());
        if (encodeAudio)
        {
            Assert.Equal(1, audio.GetProperty("channels").GetInt32());
        }

        var command = await File.ReadAllTextAsync(Assert.Single(FfmpegLogs(itemId)), TestContext.Current.CancellationToken);
        Assert.Equal(!encodeVideo, command.Contains("-codec:v:0 copy", StringComparison.Ordinal));
        Assert.Equal(!encodeAudio, command.Contains("-codec:a:0 copy", StringComparison.Ordinal));
        Assert.Null(Assert.Single(_sessionManager.GetAll(), current => current.Id.Value.Equals(session.Id)).Plan.StreamInfo);
        var job = _transcodeManager.GetTranscodingJob(request.PlaySessionId!);
        Assert.NotNull(job);
        // Permissions are re-read for execution; a retained decision is not a permanent grant
        // to encode after the owner loses that permission.
        await UpdatePolicyAsync(userId, policy => policy[encodeVideo ? "EnableVideoPlaybackTranscoding" : "EnableAudioPlaybackTranscoding"] = false);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, await StatusAsync(client, descriptor.Url));
        var output = job.Path;
        using var stopped = await client.DeleteAsync($"Playback/Sessions/{session.Id:N}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        Assert.Null(_transcodeManager.GetTranscodingJob(request.PlaySessionId!));
        await WaitUntilAsync(() => !File.Exists(output) && !Directory.EnumerateFiles(Path.GetDirectoryName(output!)!, Path.GetFileNameWithoutExtension(output) + "*").Any(), "the transcode output to be removed");
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(client, playlist.Segments[0]));
    }
}

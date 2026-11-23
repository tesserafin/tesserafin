using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Providers;
using Tesserafin.Model.Channels;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;

namespace Tesserafin.Server.Integration.Tests.EndToEnd;

/// <summary>
/// A channel plugin registered on the legacy-free host: it serves, for each item id a test gives
/// it, the media sources the test chose - what a real channel plugin returns from
/// <see cref="IRequiresMediaInfoCallback.GetChannelItemMediaInfo"/>.
/// </summary>
internal sealed class TestChannel : IChannel, IRequiresMediaInfoCallback
{
    /// <summary>The channel's name; its library id is derived from it.</summary>
    public const string ChannelName = "Tesserafin Test Channel";

    private readonly ConcurrentDictionary<string, MediaSourceInfo[]> _sources = new(StringComparer.Ordinal);

    public string Name => ChannelName;

    public string Description => "Media sources chosen by the integration tests.";

    public string DataVersion => "1";

    public string HomePageUrl => "https://tesserafin.invalid/";

    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <summary>Sets the media sources the channel returns for <paramref name="externalId"/>.</summary>
    /// <param name="externalId">The channel item's id within the channel.</param>
    /// <param name="sources">The sources.</param>
    public void Serve(string externalId, params MediaSourceInfo[] sources) => _sources[externalId] = sources;

    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video],
        ContentTypes = [ChannelMediaContentType.Movie],
    };

    public bool IsEnabledFor(string userId) => true;

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
        => Task.FromResult(new ChannelItemResult());

    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
        => Task.FromResult(new DynamicImageResponse { HasImage = false });

    public IEnumerable<ImageType> GetSupportedChannelImages() => [];

    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<MediaSourceInfo>>(_sources.TryGetValue(id, out var sources) ? sources.ToArray() : []);
}

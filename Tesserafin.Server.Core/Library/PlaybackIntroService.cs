using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Controller.Entities;
using Tesserafin.Controller.Library;
using Tesserafin.Data.Entities;
using Tesserafin.Model.IO;

namespace Tesserafin.Server.Core.Library;

/// <summary>Resolves pre-playback intros without library-manager orchestration or item writes.</summary>
internal sealed class PlaybackIntroService(
    IItemAccessService access,
    IItemResolutionService resolution,
    IFileSystem files,
    ILogger<PlaybackIntroService> logger) : IPlaybackIntroService
{
    private IIntroProvider[] _providers = [];

    /// <inheritdoc />
    public void AddParts(IEnumerable<IIntroProvider> providers)
        => _providers = providers.ToArray();

    /// <inheritdoc />
    public async Task<IReadOnlyList<Video>> GetIntrosAsync(BaseItem item, User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        var providers = _providers;
        var candidates = await Task.WhenAll(providers.Select(provider => GetCandidatesAsync(provider, item, user, cancellationToken))).ConfigureAwait(false);
        var videos = new List<Video>();
        foreach (var info in candidates.SelectMany(candidate => candidate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = ResolveIntro(info, user);
            if (video is not null)
            {
                videos.Add(video);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return videos.ToArray();
    }

    private async Task<IntroInfo[]> GetCandidatesAsync(IIntroProvider provider, BaseItem item, User user, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = await provider.GetIntrosAsync(item, user, cancellationToken).ConfigureAwait(false);
            // Enumerate inside the provider boundary so deferred iterator failures are isolated too.
            return candidates.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Error getting playback intros from {Provider}", provider.Name);
            return [];
        }
    }

    private Video? ResolveIntro(IntroInfo info, User user)
    {
        if (info.ItemId.HasValue)
        {
            return info.ItemId.Value.Equals(Guid.Empty) ? null : access.GetVisibleItemById<Video>(info.ItemId.Value, user);
        }

        if (string.IsNullOrEmpty(info.Path))
        {
            logger.LogError("IntroProvider returned an IntroInfo with null Path and ItemId.");
            return null;
        }

        try
        {
            if (resolution.ResolvePath(files.GetFileSystemInfo(info.Path)) is Video candidate)
            {
                // Filesystem resolution is only a candidate: return saved metadata after access checks.
                return access.GetVisibleItemById<Video>(candidate.Id, user);
            }

            logger.LogError("Intro resolver returned no video for {Path}.", info.Path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error resolving intro path {Path}.", info.Path);
        }

        return null;
    }
}

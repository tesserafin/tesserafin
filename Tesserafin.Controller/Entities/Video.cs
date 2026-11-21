#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.IO;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Class Video.
    /// </summary>
    public class Video : BaseItem,
        IHasAspectRatio,
        ISupportsPlaceHolders,
        IHasMediaSources
    {
        public Video()
        {
            AdditionalParts = [];
            LocalAlternateVersions = [];
            SubtitleFiles = [];
            AudioFiles = [];
            LinkedAlternateVersions = [];
        }

        [JsonIgnore]
        public Guid? PrimaryVersionId { get; set; }

        public string[] AdditionalParts { get; set; }

        public string[] LocalAlternateVersions { get; set; }

        public LinkedChild[] LinkedAlternateVersions { get; set; }

        [JsonIgnore]
        public override bool SupportsPlayedStatus => true;

        [JsonIgnore]
        public override bool SupportsPeople => true;

        [JsonIgnore]
        public override bool SupportsInheritedParentImages => true;

        [JsonIgnore]
        public override bool SupportsPositionTicksResume
        {
            get
            {
                var extraType = ExtraType;
                if (extraType.HasValue)
                {
                    if (extraType.Value == Tesserafin.Model.Entities.ExtraType.Sample)
                    {
                        return false;
                    }

                    if (extraType.Value == Tesserafin.Model.Entities.ExtraType.ThemeVideo)
                    {
                        return false;
                    }

                    if (extraType.Value == Tesserafin.Model.Entities.ExtraType.Trailer)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        [JsonIgnore]
        public override bool SupportsThemeMedia => true;

        /// <summary>
        /// Gets or sets the timestamp.
        /// </summary>
        /// <value>The timestamp.</value>
        public TransportStreamTimestamp? Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the subtitle paths.
        /// </summary>
        /// <value>The subtitle paths.</value>
        public string[] SubtitleFiles { get; set; }

        /// <summary>
        /// Gets or sets the audio paths.
        /// </summary>
        /// <value>The audio paths.</value>
        public string[] AudioFiles { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this instance has subtitles.
        /// </summary>
        /// <value><c>true</c> if this instance has subtitles; otherwise, <c>false</c>.</value>
        public bool HasSubtitles { get; set; }

        public bool IsPlaceHolder { get; set; }

        /// <summary>
        /// Gets or sets the default index of the video stream.
        /// </summary>
        /// <value>The default index of the video stream.</value>
        public int? DefaultVideoStreamIndex { get; set; }

        /// <summary>
        /// Gets or sets the type of the video.
        /// </summary>
        /// <value>The type of the video.</value>
        public VideoType VideoType { get; set; }

        /// <summary>
        /// Gets or sets the type of the iso.
        /// </summary>
        /// <value>The type of the iso.</value>
        public IsoType? IsoType { get; set; }

        /// <summary>
        /// Gets or sets the video3 D format.
        /// </summary>
        /// <value>The video3 D format.</value>
        public Video3DFormat? Video3DFormat { get; set; }

        /// <summary>
        /// Gets or sets the aspect ratio.
        /// </summary>
        /// <value>The aspect ratio.</value>
        public string AspectRatio { get; set; }

        [JsonIgnore]
        public override bool SupportsAddingToPlaylist => true;

        [JsonIgnore]
        public int MediaSourceCount
        {
            get
            {
                return GetMediaSourceCount();
            }
        }

        [JsonIgnore]
        public bool IsStacked => AdditionalParts.Length > 0;

        [JsonIgnore]
        public override bool HasLocalAlternateVersions => LibraryManager.GetLocalAlternateVersionIds(this).Any();

        [JsonIgnore]
        public override SourceType SourceType
        {
            get
            {
                if (IsActiveRecording())
                {
                    return SourceType.LiveTV;
                }

                return base.SourceType;
            }
        }

        [JsonIgnore]
        public bool IsCompleteMedia
        {
            get
            {
                if (SourceType == SourceType.Channel)
                {
                    return !Tags.Contains("livestream", StringComparison.OrdinalIgnoreCase);
                }

                return !IsActiveRecording();
            }
        }

        [JsonIgnore]
        protected virtual bool EnableDefaultVideoUserDataKeys => true;

        [JsonIgnore]
        public override string ContainingFolderPath
        {
            get
            {
                if (IsStacked)
                {
                    return System.IO.Path.GetDirectoryName(Path);
                }

                if (!IsPlaceHolder)
                {
                    if (VideoType == VideoType.BluRay || VideoType == VideoType.Dvd)
                    {
                        return Path;
                    }
                }

                return base.ContainingFolderPath;
            }
        }

        [JsonIgnore]
        public override string FileNameWithoutExtension
        {
            get
            {
                if (IsFileProtocol)
                {
                    if (VideoType == VideoType.BluRay || VideoType == VideoType.Dvd)
                    {
                        return System.IO.Path.GetFileName(Path);
                    }

                    return System.IO.Path.GetFileNameWithoutExtension(Path);
                }

                return null;
            }
        }

        /// <summary>
        /// Gets a value indicating whether [is3 D].
        /// </summary>
        /// <value><c>true</c> if [is3 D]; otherwise, <c>false</c>.</value>
        [JsonIgnore]
        public bool Is3D => Video3DFormat.HasValue;

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        /// <value>The type of the media.</value>
        [JsonIgnore]
        public override MediaType MediaType => MediaType.Video;

        private int GetMediaSourceCount(HashSet<Guid> callstack = null)
        {
            callstack ??= [];
            if (PrimaryVersionId.HasValue)
            {
                var item = LibraryManager.GetItemById(PrimaryVersionId.Value);
                if (item is Video video)
                {
                    if (callstack.Contains(video.Id))
                    {
                        // Count alternate versions using LibraryManager
                        var linkedCount = LibraryManager.GetLinkedAlternateVersions(video).Count();
                        var localCount = LibraryManager.GetLocalAlternateVersionIds(video).Count();
                        return linkedCount + localCount + 1;
                    }

                    callstack.Add(video.Id);
                    return video.GetMediaSourceCount(callstack);
                }
            }

            // Count alternate versions using LibraryManager
            var linkedVersionCount = LibraryManager.GetLinkedAlternateVersions(this).Count();
            var localVersionCount = LibraryManager.GetLocalAlternateVersionIds(this).Count();
            return linkedVersionCount + localVersionCount + 1;
        }

        /// <inheritdoc />
        public override string GetInheritedOriginalLanguage()
        {
            if (ExtraType.GetValueOrDefault() == Tesserafin.Model.Entities.ExtraType.Trailer)
            {
                return GetOwner()?.GetInheritedOriginalLanguage();
            }

            return OriginalLanguage ?? GetOwner()?.GetInheritedOriginalLanguage();
        }

        public override List<string> GetUserDataKeys()
        {
            var list = base.GetUserDataKeys();

            if (EnableDefaultVideoUserDataKeys)
            {
                if (ExtraType.HasValue)
                {
                    var key = this.GetProviderId(MetadataProvider.Tmdb);
                    if (!string.IsNullOrEmpty(key))
                    {
                        list.Insert(0, GetUserDataKey(key));
                    }

                    key = this.GetProviderId(MetadataProvider.Imdb);
                    if (!string.IsNullOrEmpty(key))
                    {
                        list.Insert(0, GetUserDataKey(key));
                    }
                }
                else
                {
                    var key = this.GetProviderId(MetadataProvider.Imdb);
                    if (!string.IsNullOrEmpty(key))
                    {
                        list.Insert(0, key);
                    }

                    key = this.GetProviderId(MetadataProvider.Tmdb);
                    if (!string.IsNullOrEmpty(key))
                    {
                        list.Insert(0, key);
                    }
                }
            }

            return list;
        }

        public void SetPrimaryVersionId(Guid? id)
        {
            PrimaryVersionId = id;
            PresentationUniqueKey = CreatePresentationUniqueKey();
        }

        /// <summary>
        /// Gets this video together with all of its alternate versions (local and linked and, when this
        /// is itself an alternate, the primary and the primary's other versions), deduplicated.
        /// </summary>
        /// <returns>This video and every alternate version of it.</returns>
        public IReadOnlyList<Video> GetAllVersions()
        {
            return GetAllItemsForMediaSources()
                .Select(i => i.Item)
                .OfType<Video>()
                .ToList();
        }

        /// <summary>
        /// Gets the alternate version of this video that matches the supplied item id.
        /// </summary>
        /// <param name="itemId">The version item id (the playback media source id).</param>
        /// <returns>The matching version, or <c>null</c> when the id is not a version of this video.</returns>
        public Video GetAlternateVersion(Guid itemId)
        {
            return GetAllVersions().FirstOrDefault(i => i.Id.Equals(itemId));
        }

        public override string CreatePresentationUniqueKey()
        {
            if (PrimaryVersionId.HasValue)
            {
                return PrimaryVersionId.Value.ToString("N", CultureInfo.InvariantCulture);
            }

            return base.CreatePresentationUniqueKey();
        }

        public override bool CanDownload()
        {
            if (VideoType == VideoType.Dvd || VideoType == VideoType.BluRay)
            {
                return false;
            }

            return IsFileProtocol;
        }

        protected override bool IsActiveRecording()
        {
            // An item not yet loaded from or saved to the catalogue has no read services and is not known to be recording.
            return ReadServices?.IsActiveRecording(Path) ?? false;
        }

        internal override SourceType GetSourceType(IStandaloneVisibilityService scope)
        {
            return scope.IsActiveRecording(Path) ? SourceType.LiveTV : base.SourceType;
        }

        internal override bool GetBlockUnratedValue(User user, IStandaloneVisibilityService scope)
        {
            // Only the base unrated type reads the source type; the subclasses return their own (Movie, Episode...).
            if (GetType() != typeof(Video))
            {
                return GetBlockUnratedValue(user);
            }

            var type = GetSourceType(scope) == SourceType.Channel ? UnratedItem.ChannelContent : UnratedItem.Other;
            return user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(type);
        }

        public override bool CanDelete(IChannelManager channels)
        {
            if (IsActiveRecording())
            {
                return false;
            }

            return base.CanDelete(channels);
        }

        public IEnumerable<Guid> GetAdditionalPartIds()
        {
            return AdditionalParts.Select(i => LibraryManager.GetNewItemId(i, typeof(Video)));
        }

        private string GetUserDataKey(string providerId)
        {
            var key = providerId + "-" + ExtraType.ToString().ToLowerInvariant();

            // Make sure different trailers have their own data.
            if (RunTimeTicks.HasValue)
            {
                key += "-" + RunTimeTicks.Value.ToString(CultureInfo.InvariantCulture);
            }

            return key;
        }

        /// <summary>
        /// Gets the additional parts.
        /// </summary>
        /// <param name="user">The user to apply parental restrictions for, or <c>null</c> to skip restriction checks.</param>
        /// <returns>IEnumerable{Video}.</returns>
        public IOrderedEnumerable<Video> GetAdditionalParts(User user = null)
        {
            return GetAdditionalPartIds()
                .Select(i => LibraryManager.GetItemById<Video>(i, user))
                .Where(i => i is not null)
                .OrderBy(i => i.SortName);
        }

        internal override ItemUpdateType UpdateFromResolvedItem(BaseItem newItem)
        {
            var updateType = base.UpdateFromResolvedItem(newItem);

            if (newItem is Video newVideo)
            {
                if (!AdditionalParts.SequenceEqual(newVideo.AdditionalParts, StringComparer.Ordinal))
                {
                    AdditionalParts = newVideo.AdditionalParts.ToArray();
                    updateType |= ItemUpdateType.MetadataImport;
                }

                if (!LocalAlternateVersions.SequenceEqual(newVideo.LocalAlternateVersions, StringComparer.Ordinal))
                {
                    LocalAlternateVersions = newVideo.LocalAlternateVersions.ToArray();
                    updateType |= ItemUpdateType.MetadataImport;
                }

                if (VideoType != newVideo.VideoType)
                {
                    VideoType = newVideo.VideoType;
                    updateType |= ItemUpdateType.MetadataImport;
                }
            }

            return updateType;
        }

        protected override async Task<bool> RefreshedOwnedItems(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemMetadataDispatcher providers, MetadataRefreshOptions options, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
        {
            var hasChanges = await base.RefreshedOwnedItems(deletions, writes, providers, options, fileSystemChildren, cancellationToken).ConfigureAwait(false);

            var videoChanges = await writes.RefreshOwnedVideosAsync(this, providers, deletions, options, cancellationToken).ConfigureAwait(false);
            return hasChanges || videoChanges;
        }

        public override IEnumerable<FileSystemMetadata> GetDeletePaths(IFileSystem fileSystem)
        {
            if (!IsInMixedFolder)
            {
                return new[]
                {
                    new FileSystemMetadata
                    {
                        FullName = ContainingFolderPath,
                        IsDirectory = true
                    }
                };
            }

            return base.GetDeletePaths(fileSystem);
        }

        public virtual MediaStream GetDefaultVideoStream(IMediaSourceManager mediaSourceManager)
        {
            if (!DefaultVideoStreamIndex.HasValue)
            {
                return null;
            }

            return mediaSourceManager.GetMediaStreams(new MediaStreamQuery
            {
                ItemId = Id,
                Index = DefaultVideoStreamIndex.Value
            }).FirstOrDefault();
        }

        /// <summary>
        /// Gets the ids of the items whose owned extras belong to this item.
        /// Extras are linked to a single version but need tp be surfaced for all versions.
        /// </summary>
        /// <returns>An array containing the owner ids.</returns>
        protected override Guid[] GetExtraOwnerIds()
        {
            return GetAllItemsForMediaSources()
                .Select(i => i.Item.Id)
                .Distinct()
                .ToArray();
        }

        protected override IEnumerable<(BaseItem Item, MediaSourceType MediaSourceType)> GetAllItemsForMediaSources()
        {
            var primary = PrimaryVersionId.HasValue
                ? LibraryManager.GetItemById(PrimaryVersionId.Value) as Video
                : null;

            var primaryLinked = primary is null
                ? []
                : LibraryManager.GetLinkedAlternateVersions(primary).ToList();

            // Grouping marks user-merged (splittable) sources. The primary is only such a source when
            // this video is linked onto it; for local (file-based) alternates the primary is just
            // another default source.
            var primaryType = primaryLinked.Any(i => i.Id.Equals(Id))
                ? MediaSourceType.Grouping
                : MediaSourceType.Default;

            // This video and its linked alternates, when this is itself an alternate, the primary and the primary's linked alternates.
            var grouped = new[] { ((BaseItem)this, MediaSourceType.Default) }
                .Concat(LibraryManager.GetLinkedAlternateVersions(this).Select(i => ((BaseItem)i, MediaSourceType.Grouping)))
                .Concat(primary is null
                    ? []
                    : primaryLinked.Select(i => ((BaseItem)i, MediaSourceType.Grouping)).Prepend(((BaseItem)primary, primaryType)))
                .ToList();

            // The local (file-based) alternate versions of every grouped item.
            var localAlternates = grouped
                .Select(i => i.Item1)
                .OfType<Video>()
                .SelectMany(LibraryManager.GetLocalAlternateVersionIds)
                .Select(LibraryManager.GetItemById)
                .Where(i => i is not null)
                .Select(i => (i, MediaSourceType.Default));

            // Deduplicate
            return grouped
                .Concat(localAlternates)
                .DistinctBy(i => i.Item1.Id)
                .ToList();
        }
    }
}

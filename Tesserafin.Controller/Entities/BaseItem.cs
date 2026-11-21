#nullable disable

#pragma warning disable CS1591, SA1401

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Tesserafin.Common.Extensions;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.Dto;
using Tesserafin.Controller.Entities.TV;
using Tesserafin.Controller.Library;
using Tesserafin.Controller.MediaSegments;
using Tesserafin.Controller.Persistence;
using Tesserafin.Controller.Providers;
using Tesserafin.Data;
using Tesserafin.Data.Entities;
using Tesserafin.Data.Enums;
using Tesserafin.Extensions;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Dto;
using Tesserafin.Model.Entities;
using Tesserafin.Model.Globalization;
using Tesserafin.Model.IO;
using Tesserafin.Model.Library;
using Tesserafin.Model.LiveTv;
using Tesserafin.Model.MediaInfo;

namespace Tesserafin.Controller.Entities
{
    /// <summary>
    /// Class BaseItem.
    /// </summary>
    public abstract class BaseItem : IHasProviderIds, IHasLookupInfo<ItemLookupInfo>, IEquatable<BaseItem>
    {
        private BaseItemKind? _baseItemKind;

        public const string ThemeSongFileName = "theme";

        /// <summary>
        /// The supported image extensions.
        /// </summary>
        public static readonly string[] SupportedImageExtensions
            = [".png", ".jpg", ".jpeg", ".webp", ".tbn", ".gif", ".svg"];

        private static readonly List<string> _supportedExtensions = new List<string>(SupportedImageExtensions)
        {
            ".nfo",
            ".xml",
            ".srt",
            ".vtt",
            ".sub",
            ".sup",
            ".idx",
            ".txt",
            ".edl",
            ".bif",
            ".smi",
            ".ttml",
            ".lrc",
            ".elrc"
        };

        /// <summary>
        /// Extra types that should be counted and displayed as "Special Features" in the UI.
        /// </summary>
        public static readonly IReadOnlyCollection<ExtraType> DisplayExtraTypes = new HashSet<ExtraType>
        {
            Tesserafin.Model.Entities.ExtraType.Unknown,
            Tesserafin.Model.Entities.ExtraType.BehindTheScenes,
            Tesserafin.Model.Entities.ExtraType.Clip,
            Tesserafin.Model.Entities.ExtraType.DeletedScene,
            Tesserafin.Model.Entities.ExtraType.Interview,
            Tesserafin.Model.Entities.ExtraType.Sample,
            Tesserafin.Model.Entities.ExtraType.Scene,
            Tesserafin.Model.Entities.ExtraType.Featurette,
            Tesserafin.Model.Entities.ExtraType.Short
        };

        private static readonly char[] VersionDelimiters = ['-', '_', '.'];

        private string _sortName;

        private string _forcedSortName;

        private string _name;

        public const char SlugChar = '-';

        protected BaseItem()
        {
            Tags = Array.Empty<string>();
            Genres = Array.Empty<string>();
            Studios = Array.Empty<string>();
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LockedFields = Array.Empty<MetadataField>();
            ImageInfos = Array.Empty<ItemImageInfo>();
            ProductionLocations = Array.Empty<string>();
            RemoteTrailers = Array.Empty<MediaUrl>();
            UserData = [];
        }

        /// <summary>
        /// Gets or Sets the user data collection as cached from the last Db query.
        /// </summary>
        [JsonIgnore]
        public ICollection<UserData> UserData { get; set; }

        [JsonIgnore]
        public string PreferredMetadataCountryCode { get; set; }

        [JsonIgnore]
        public string PreferredMetadataLanguage { get; set; }

        public long? Size { get; set; }

        public string Container { get; set; }

        [JsonIgnore]
        public string Tagline { get; set; }

        [JsonIgnore]
        public virtual ItemImageInfo[] ImageInfos { get; set; }

        [JsonIgnore]
        public bool IsVirtualItem { get; set; }

        /// <summary>
        /// Gets or sets the album.
        /// </summary>
        /// <value>The album.</value>
        [JsonIgnore]
        public string Album { get; set; }

        /// <summary>
        /// Gets or sets the LUFS value.
        /// </summary>
        /// <value>The LUFS Value.</value>
        [JsonIgnore]
        public float? LUFS { get; set; }

        /// <summary>
        /// Gets or sets the gain required for audio normalization.
        /// </summary>
        /// <value>The gain required for audio normalization.</value>
        [JsonIgnore]
        public float? NormalizationGain { get; set; }

        /// <summary>
        /// Gets or sets the channel identifier.
        /// </summary>
        /// <value>The channel identifier.</value>
        [JsonIgnore]
        public Guid ChannelId { get; set; }

        [JsonIgnore]
        public virtual bool SupportsAddingToPlaylist => false;

        [JsonIgnore]
        public virtual bool AlwaysScanInternalMetadataPath => false;

        /// <summary>
        /// Gets or sets a value indicating whether this instance is in mixed folder.
        /// </summary>
        /// <value><c>true</c> if this instance is in mixed folder; otherwise, <c>false</c>.</value>
        [JsonIgnore]
        public bool IsInMixedFolder { get; set; }

        [JsonIgnore]
        public virtual bool SupportsPlayedStatus => false;

        [JsonIgnore]
        public virtual bool SupportsPositionTicksResume => false;

        [JsonIgnore]
        public virtual bool SupportsRemoteImageDownloading => true;

        /// <summary>
        /// Gets or sets the name.
        /// </summary>
        /// <value>The name.</value>
        [JsonIgnore]
        public virtual string Name
        {
            get => _name;
            set
            {
                _name = value;

                // lazy load this again
                _sortName = null;
            }
        }

        [JsonIgnore]
        public bool IsUnaired => PremiereDate.HasValue && PremiereDate.Value.ToLocalTime().Date >= DateTime.Now.Date;

        [JsonIgnore]
        public int? TotalBitrate { get; set; }

        [JsonIgnore]
        public ExtraType? ExtraType { get; set; }

        [JsonIgnore]
        public bool IsThemeMedia => ExtraType.HasValue && (ExtraType.Value == Tesserafin.Model.Entities.ExtraType.ThemeSong || ExtraType.Value == Tesserafin.Model.Entities.ExtraType.ThemeVideo);

        [JsonIgnore]
        public string OriginalTitle { get; set; }

        [JsonIgnore]
        public string OriginalLanguage { get; set; }

        /// <summary>
        /// Gets or sets the id.
        /// </summary>
        /// <value>The id.</value>
        [JsonIgnore]
        public Guid Id { get; set; }

        [JsonIgnore]
        public Guid OwnerId { get; set; }

        /// <summary>
        /// Gets or sets the audio.
        /// </summary>
        /// <value>The audio.</value>
        [JsonIgnore]
        public ProgramAudio? Audio { get; set; }

        /// <summary>
        /// Gets the id that should be used to key display prefs for this item.
        /// Default is based on the type for everything except actual generic folders.
        /// </summary>
        /// <value>The display prefs id.</value>
        [JsonIgnore]
        public virtual Guid DisplayPreferencesId
        {
            get
            {
                var thisType = GetType();
                return thisType == typeof(Folder) ? Id : thisType.FullName.GetMD5();
            }
        }

        /// <summary>
        /// Gets or sets the path.
        /// </summary>
        /// <value>The path.</value>
        [JsonIgnore]
        public virtual string Path { get; set; }

        [JsonIgnore]
        public virtual SourceType SourceType
        {
            get
            {
                if (!ChannelId.IsEmpty())
                {
                    return SourceType.Channel;
                }

                return SourceType.Library;
            }
        }

        /// <summary>
        /// Gets the folder containing the item.
        /// If the item is a folder, it returns the folder itself.
        /// </summary>
        [JsonIgnore]
        public virtual string ContainingFolderPath
        {
            get
            {
                if (IsFolder)
                {
                    return Path;
                }

                return System.IO.Path.GetDirectoryName(Path);
            }
        }

        /// <summary>
        /// Gets or sets the name of the service.
        /// </summary>
        /// <value>The name of the service.</value>
        [JsonIgnore]
        public string ServiceName { get; set; }

        /// <summary>
        /// Gets or sets the external id.
        /// </summary>
        /// <remarks>
        /// If this content came from an external service, the id of the content on that service.
        /// </remarks>
        [JsonIgnore]
        public string ExternalId { get; set; }

        [JsonIgnore]
        public string ExternalSeriesId { get; set; }

        [JsonIgnore]
        public virtual bool IsHidden => false;

        /// <summary>
        /// Gets the type of the location.
        /// </summary>
        /// <value>The type of the location.</value>
        [JsonIgnore]
        public virtual LocationType LocationType
        {
            get
            {
                var path = Path;
                if (string.IsNullOrEmpty(path))
                {
                    if (SourceType == SourceType.Channel)
                    {
                        return LocationType.Remote;
                    }

                    return LocationType.Virtual;
                }

                return MediaPath.IsFile(path) ? LocationType.FileSystem : LocationType.Remote;
            }
        }

        [JsonIgnore]
        public MediaProtocol? PathProtocol
        {
            get
            {
                var path = Path;

                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                return MediaPath.GetProtocol(path);
            }
        }

        [JsonIgnore]
        public bool IsFileProtocol => PathProtocol == MediaProtocol.File;

        [JsonIgnore]
        public bool HasPathProtocol => PathProtocol.HasValue;

        [JsonIgnore]
        public virtual bool SupportsLocalMetadata
        {
            get
            {
                if (SourceType == SourceType.Channel)
                {
                    return false;
                }

                return IsFileProtocol;
            }
        }

        [JsonIgnore]
        public virtual string FileNameWithoutExtension
        {
            get
            {
                if (IsFileProtocol)
                {
                    return System.IO.Path.GetFileNameWithoutExtension(Path);
                }

                return null;
            }
        }

        [JsonIgnore]
        public virtual bool EnableAlphaNumericSorting => true;

        public virtual bool IsHD => Height >= 720;

        public bool IsShortcut { get; set; }

        public string ShortcutPath { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        /// <summary>
        /// Gets the primary image path.
        /// </summary>
        /// <remarks>
        /// This is just a helper for convenience.
        /// </remarks>
        /// <value>The primary image path.</value>
        [JsonIgnore]
        public string PrimaryImagePath => this.GetImagePath(ImageType.Primary);

        /// <summary>
        /// Gets or sets the date created.
        /// </summary>
        /// <value>The date created.</value>
        [JsonIgnore]
        public DateTime DateCreated { get; set; }

        /// <summary>
        /// Gets or sets the date modified.
        /// </summary>
        /// <value>The date modified.</value>
        [JsonIgnore]
        public DateTime DateModified { get; set; }

        public DateTime DateLastSaved { get; set; }

        [JsonIgnore]
        public DateTime DateLastRefreshed { get; set; }

        [JsonIgnore]
        public bool IsLocked { get; set; }

        /// <summary>
        /// Gets or sets the locked fields.
        /// </summary>
        /// <value>The locked fields.</value>
        [JsonIgnore]
        public MetadataField[] LockedFields { get; set; }

        /// <summary>
        /// Gets the type of the media.
        /// </summary>
        /// <value>The type of the media.</value>
        [JsonIgnore]
        public virtual MediaType MediaType => MediaType.Unknown;

        [JsonIgnore]
        public virtual string[] PhysicalLocations
        {
            get
            {
                if (!IsFileProtocol)
                {
                    return Array.Empty<string>();
                }

                return [Path];
            }
        }

        [JsonIgnore]
        public Guid ParentId { get; set; }

        /// <summary>
        /// Gets or sets the logger.
        /// </summary>
        public static ILibraryManager LibraryManager { get; set; }

        /// <summary>
        /// Gets or sets the catalogue reads this item performs on itself (children, played state, active recording).
        /// The catalogue attaches them when it loads or saves the item.
        /// </summary>
        [JsonIgnore]
        public IItemReadServices ReadServices { get; set; }

        /// <summary>
        /// Gets or sets the name of the forced sort.
        /// </summary>
        /// <value>The name of the forced sort.</value>
        [JsonIgnore]
        public string ForcedSortName
        {
            get => _forcedSortName;
            set
            {
                _forcedSortName = value;
            }
        }

        /// <summary>
        /// Gets or sets the name of the sort.
        /// </summary>
        /// <value>The name of the sort.</value>
        [JsonIgnore]
        public string SortName
        {
            get
            {
                if (_sortName is null)
                {
                    if (!string.IsNullOrEmpty(ForcedSortName))
                    {
                        // Need the ToLower because that's what CreateSortName does
                        _sortName = ModifySortChunks(ForcedSortName).ToLowerInvariant();
                    }
                    else if (ReadServices is null)
                    {
                        // A detached item may be bound to a catalogue with different sort rules later.
                        return CreateSortName();
                    }
                    else
                    {
                        _sortName = CreateSortName();
                    }
                }

                return _sortName;
            }

            set => _sortName = value;
        }

        [JsonIgnore]
        public virtual Guid DisplayParentId => ParentId;

        [JsonIgnore]
        public BaseItem DisplayParent
        {
            get
            {
                var id = DisplayParentId;
                if (id.IsEmpty())
                {
                    return null;
                }

                return LibraryManager.GetItemById(id);
            }
        }

        /// <summary>
        /// Gets or sets the date that the item first debuted. For movies this could be premiere date, episodes would be first aired.
        /// </summary>
        /// <value>The premiere date.</value>
        [JsonIgnore]
        public DateTime? PremiereDate { get; set; }

        /// <summary>
        /// Gets or sets the end date.
        /// </summary>
        /// <value>The end date.</value>
        [JsonIgnore]
        public DateTime? EndDate { get; set; }

        /// <summary>
        /// Gets or sets the official rating.
        /// </summary>
        /// <value>The official rating.</value>
        [JsonIgnore]
        public string OfficialRating { get; set; }

        [JsonIgnore]
        public int? InheritedParentalRatingValue { get; set; }

        [JsonIgnore]
        public int? InheritedParentalRatingSubValue { get; set; }

        /// <summary>
        /// Gets or sets the critic rating.
        /// </summary>
        /// <value>The critic rating.</value>
        [JsonIgnore]
        public float? CriticRating { get; set; }

        /// <summary>
        /// Gets or sets the custom rating.
        /// </summary>
        /// <value>The custom rating.</value>
        [JsonIgnore]
        public string CustomRating { get; set; }

        /// <summary>
        /// Gets or sets the overview.
        /// </summary>
        /// <value>The overview.</value>
        [JsonIgnore]
        public string Overview { get; set; }

        /// <summary>
        /// Gets or sets the studios.
        /// </summary>
        /// <value>The studios.</value>
        [JsonIgnore]
        public string[] Studios { get; set; }

        /// <summary>
        /// Gets or sets the genres.
        /// </summary>
        /// <value>The genres.</value>
        [JsonIgnore]
        public string[] Genres { get; set; }

        /// <summary>
        /// Gets or sets the tags.
        /// </summary>
        /// <value>The tags.</value>
        [JsonIgnore]
        public string[] Tags { get; set; }

        [JsonIgnore]
        public string[] ProductionLocations { get; set; }

        /// <summary>
        /// Gets or sets the home page URL.
        /// </summary>
        /// <value>The home page URL.</value>
        [JsonIgnore]
        public string HomePageUrl { get; set; }

        /// <summary>
        /// Gets or sets the community rating.
        /// </summary>
        /// <value>The community rating.</value>
        [JsonIgnore]
        public float? CommunityRating { get; set; }

        /// <summary>
        /// Gets or sets the run time ticks.
        /// </summary>
        /// <value>The run time ticks.</value>
        [JsonIgnore]
        public long? RunTimeTicks { get; set; }

        /// <summary>
        /// Gets or sets the production year.
        /// </summary>
        /// <value>The production year.</value>
        [JsonIgnore]
        public int? ProductionYear { get; set; }

        /// <summary>
        /// Gets or sets the index number. If the item is part of a series, this is it's number in the series.
        /// This could be episode number, album track number, etc.
        /// </summary>
        /// <value>The index number.</value>
        [JsonIgnore]
        public int? IndexNumber { get; set; }

        /// <summary>
        /// Gets or sets the parent index number. For an episode this could be the season number, or for a song this could be the disc number.
        /// </summary>
        /// <value>The parent index number.</value>
        [JsonIgnore]
        public int? ParentIndexNumber { get; set; }

        [JsonIgnore]
        public virtual bool HasLocalAlternateVersions => false;

        [JsonIgnore]
        public string OfficialRatingForComparison
        {
            get
            {
                return GetOfficialRatingForComparison(null);
            }
        }

        [JsonIgnore]
        public string CustomRatingForComparison
        {
            get
            {
                return GetCustomRatingForComparision(null);
            }
        }

        /// <summary>
        /// Gets or sets the provider ids.
        /// </summary>
        /// <value>The provider ids.</value>
        [JsonIgnore]
        public Dictionary<string, string> ProviderIds { get; set; }

        [JsonIgnore]
        public virtual Folder LatestItemsIndexContainer => null;

        [JsonIgnore]
        public string PresentationUniqueKey { get; set; }

        [JsonIgnore]
        public virtual bool EnableRememberingTrackSelections => true;

        [JsonIgnore]
        public virtual bool IsTopParent
        {
            get
            {
                if (this is BasePluginFolder || this is Channel)
                {
                    return true;
                }

                if (this is IHasCollectionType view)
                {
                    if (view.CollectionType == CollectionType.livetv)
                    {
                        return true;
                    }
                }

                if (GetParent() is AggregateFolder)
                {
                    return true;
                }

                return false;
            }
        }

#pragma warning disable SA1201 // kept adjacent to IsTopParent for readability; it mirrors that property's logic
        /// <summary>
        /// Gets a value indicating whether this item is a top-level parent, resolving its parent
        /// via the given lookup service instead of the static <see cref="LibraryManager"/>. Mirrors
        /// the <see cref="IsTopParent"/> property but keeps the check off the static path.
        /// </summary>
        /// <param name="lookup">The lookup service used to resolve the parent.</param>
        /// <returns><c>true</c> if this item is a top parent; otherwise, <c>false</c>.</returns>
        public bool IsTopParentVia(IItemLookupService lookup)
        {
            ArgumentNullException.ThrowIfNull(lookup);

            if (this is BasePluginFolder || this is Channel)
            {
                return true;
            }

            if (this is IHasCollectionType view && view.CollectionType == CollectionType.livetv)
            {
                return true;
            }

            if (GetParent(lookup) is AggregateFolder)
            {
                return true;
            }

            return false;
        }

        [JsonIgnore]
        public virtual bool SupportsAncestors => true;
#pragma warning restore SA1201

        [JsonIgnore]
        protected virtual bool SupportsOwnedItems => !ParentId.IsEmpty() && IsFileProtocol;

        [JsonIgnore]
        public virtual bool SupportsPeople => false;

        [JsonIgnore]
        public virtual bool SupportsThemeMedia => false;

        [JsonIgnore]
        public virtual bool SupportsInheritedParentImages => false;

        /// <summary>
        /// Gets a value indicating whether this instance is folder.
        /// </summary>
        /// <value><c>true</c> if this instance is folder; otherwise, <c>false</c>.</value>
        [JsonIgnore]
        public virtual bool IsFolder => false;

        [JsonIgnore]
        public virtual bool IsDisplayedAsFolder => false;

        /// <summary>
        /// Gets or sets the remote trailers.
        /// </summary>
        /// <value>The remote trailers.</value>
        public IReadOnlyList<MediaUrl> RemoteTrailers { get; set; }

        private string GetOfficialRatingForComparison(IStandaloneVisibilityService scope)
        {
            var officialRating = OfficialRating;
            if (!string.IsNullOrEmpty(officialRating))
            {
                return officialRating;
            }

            var parent = scope is null ? DisplayParent : scope.GetDisplayParent(this);
            if (parent is not null)
            {
                return parent.GetOfficialRatingForComparison(scope);
            }

            return null;
        }

        private string GetCustomRatingForComparision(IStandaloneVisibilityService scope, HashSet<Guid> callstack = null)
        {
            callstack ??= new();
            var customRating = CustomRating;
            if (!string.IsNullOrEmpty(customRating))
            {
                return customRating;
            }

            callstack.Add(Id);

            var parent = scope is null ? DisplayParent : scope.GetDisplayParent(this);
            if (parent is not null && !callstack.Contains(parent.Id))
            {
                return parent.GetCustomRatingForComparision(scope, callstack);
            }

            return null;
        }

        public virtual double GetDefaultPrimaryImageAspectRatio()
        {
            return 0;
        }

        public virtual string CreatePresentationUniqueKey()
        {
            return Id.ToString("N", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Gets the attached catalogue reads.
        /// </summary>
        /// <returns>The read services.</returns>
        /// <exception cref="InvalidOperationException">The item was neither loaded from nor saved to the catalogue.</exception>
        protected IItemReadServices RequireReadServices()
            => ReadServices ?? throw new InvalidOperationException($"{GetType().Name} {Id} has no catalogue read services: it was neither loaded from nor saved to the catalogue.");

        public virtual bool CanDelete(IChannelManager channels)
        {
            if (SourceType == SourceType.Channel)
            {
                return channels.CanDelete(this);
            }

            return IsFileProtocol;
        }

        public virtual bool IsAuthorizedToDelete(User user, List<Folder> allCollectionFolders)
        {
            if (user.HasPermission(PermissionKind.EnableContentDeletion))
            {
                return true;
            }

            var allowed = user.GetPreferenceValues<Guid>(PreferenceKind.EnableContentDeletionFromFolders);

            if (SourceType == SourceType.Channel)
            {
                return allowed.Contains(ChannelId);
            }

            var collectionFolders = LibraryManager.GetCollectionFolders(this, allCollectionFolders);

            foreach (var folder in collectionFolders)
            {
                if (allowed.Contains(folder.Id))
                {
                    return true;
                }
            }

            return false;
        }

        public BaseItem GetOwner()
        {
            var ownerId = OwnerId;
            return ownerId.IsEmpty() ? null : LibraryManager.GetItemById(ownerId);
        }

        /// <summary>
        /// Gets the owner via the given lookup service, without touching the static
        /// <see cref="LibraryManager"/>.
        /// </summary>
        /// <param name="lookup">The lookup service.</param>
        /// <returns>BaseItem.</returns>
        public BaseItem GetOwner(IItemLookupService lookup)
        {
            ArgumentNullException.ThrowIfNull(lookup);

            var ownerId = OwnerId;
            return ownerId.IsEmpty() ? null : lookup.GetItemById(ownerId);
        }

        public bool CanDelete(User user, List<Folder> allCollectionFolders, IChannelManager channels)
        {
            return CanDelete(channels) && IsAuthorizedToDelete(user, allCollectionFolders);
        }

        public virtual bool CanDelete(User user, IChannelManager channels)
        {
            var allCollectionFolders = LibraryManager.GetUserRootFolder().Children.OfType<Folder>().ToList();

            return CanDelete(user, allCollectionFolders, channels);
        }

        public virtual bool CanDownload()
        {
            return false;
        }

        public virtual bool IsAuthorizedToDownload(User user)
        {
            return user.HasPermission(PermissionKind.EnableContentDownloading);
        }

        public bool CanDownload(User user)
        {
            return CanDownload() && IsAuthorizedToDownload(user);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Name;
        }

        /// <summary>Builds the item's stable metadata path using an explicitly supplied root.</summary>
        /// <param name="basePath">The internal metadata root.</param>
        /// <returns>The item-owned metadata directory.</returns>
        public virtual string GetInternalMetadataPath(string basePath)
        {
            if (!ChannelId.Equals(Guid.Empty))
            {
                return System.IO.Path.Join(basePath, "channels", ChannelId.ToString("N", CultureInfo.InvariantCulture), Id.ToString("N", CultureInfo.InvariantCulture));
            }

            ReadOnlySpan<char> idString = Id.ToString("N", CultureInfo.InvariantCulture);

            return System.IO.Path.Join(basePath, "library", idString[..2], idString);
        }

        /// <summary>
        /// Creates the name of the sort.
        /// </summary>
        /// <returns>System.String.</returns>
        protected virtual string CreateSortName()
        {
            if (Name is null)
            {
                return null; // some items may not have name filled in properly
            }

            if (!EnableAlphaNumericSorting)
            {
                return Name.TrimStart();
            }

            var sortable = Name.Trim().ToLowerInvariant();
            var defaults = ReadServices?.MetadataDefaults ?? ItemMetadataDefaults.BuiltIn;

            foreach (var search in defaults.SortRemoveWords)
            {
                // Remove from beginning if a space follows
                if (sortable.StartsWith(search + " ", StringComparison.Ordinal))
                {
                    sortable = sortable.Remove(0, search.Length + 1);
                }

                // Remove from middle if surrounded by spaces
                sortable = sortable.Replace(" " + search + " ", " ", StringComparison.Ordinal);

                // Remove from end if preceeded by a space
                if (sortable.EndsWith(" " + search, StringComparison.Ordinal))
                {
                    sortable = sortable.Remove(sortable.Length - (search.Length + 1));
                }
            }

            foreach (var removeChar in defaults.SortRemoveCharacters)
            {
                sortable = sortable.Replace(removeChar, string.Empty, StringComparison.Ordinal);
            }

            foreach (var replaceChar in defaults.SortReplaceCharacters)
            {
                sortable = sortable.Replace(replaceChar, " ", StringComparison.Ordinal);
            }

            return ModifySortChunks(sortable);
        }

        internal static string ModifySortChunks(ReadOnlySpan<char> name)
        {
            static void AppendChunk(StringBuilder builder, bool isDigitChunk, ReadOnlySpan<char> chunk)
            {
                if (isDigitChunk && chunk.Length < 10)
                {
                    builder.Append('0', 10 - chunk.Length);
                }

                builder.Append(chunk);
            }

            if (name.IsEmpty)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(name.Length);

            int chunkStart = 0;
            bool isDigitChunk = char.IsDigit(name[0]);
            for (int i = 0; i < name.Length; i++)
            {
                var isDigit = char.IsDigit(name[i]);
                if (isDigit != isDigitChunk)
                {
                    AppendChunk(builder, isDigitChunk, name.Slice(chunkStart, i - chunkStart));
                    chunkStart = i;
                    isDigitChunk = isDigit;
                }
            }

            AppendChunk(builder, isDigitChunk, name.Slice(chunkStart));

            // logger.LogDebug("ModifySortChunks Start: {0} End: {1}", name, builder.ToString());
            var result = builder.ToString().RemoveDiacritics();
            if (!result.All(char.IsAscii))
            {
                result = result.Transliterated();
            }

            return result;
        }

        public BaseItem GetParent()
        {
            var parentId = ParentId;
            if (parentId.IsEmpty())
            {
                return null;
            }

            return LibraryManager.GetItemById(parentId);
        }

        /// <summary>
        /// Gets the parent via the given lookup service, without touching the static
        /// <see cref="LibraryManager"/>.
        /// </summary>
        /// <param name="lookup">The lookup service.</param>
        /// <returns>BaseItem.</returns>
        public BaseItem GetParent(IItemLookupService lookup)
        {
            ArgumentNullException.ThrowIfNull(lookup);

            var parentId = ParentId;
            if (parentId.IsEmpty())
            {
                return null;
            }

            return lookup.GetItemById(parentId);
        }

        public IEnumerable<BaseItem> GetParents()
        {
            var parent = GetParent();

            while (parent is not null)
            {
                yield return parent;

                parent = parent.GetParent();
            }
        }

        /// <summary>
        /// Gets the parents via the given lookup service, without touching the static
        /// <see cref="LibraryManager"/>. Each hop up the chain is resolved through
        /// <paramref name="lookup"/>.
        /// </summary>
        /// <param name="lookup">The lookup service.</param>
        /// <returns>IEnumerable{BaseItem}.</returns>
        public IEnumerable<BaseItem> GetParents(IItemLookupService lookup)
        {
            var parent = GetParent(lookup);

            while (parent is not null)
            {
                yield return parent;

                parent = parent.GetParent(lookup);
            }
        }

        /// <summary>
        /// Finds a parent of a given type.
        /// </summary>
        /// <typeparam name="T">Type of parent.</typeparam>
        /// <returns>``0.</returns>
        public T FindParent<T>()
            where T : Folder
        {
            foreach (var parent in GetParents())
            {
                if (parent is T item)
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a parent of a given type via the given lookup service, without touching the
        /// static <see cref="LibraryManager"/>.
        /// </summary>
        /// <param name="lookup">The lookup service.</param>
        /// <typeparam name="T">Type of parent.</typeparam>
        /// <returns>``0.</returns>
        public T FindParent<T>(IItemLookupService lookup)
            where T : Folder
        {
            ArgumentNullException.ThrowIfNull(lookup);

            foreach (var parent in GetParents(lookup))
            {
                if (parent is T item)
                {
                    return item;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the play access.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <returns>PlayAccess.</returns>
        public PlayAccess GetPlayAccess(User user)
        {
            if (!user.HasPermission(PermissionKind.EnableMediaPlayback))
            {
                return PlayAccess.None;
            }

            // if (!user.IsParentalScheduleAllowed())
            // {
            //    return PlayAccess.None;
            // }

            return PlayAccess.Full;
        }

        public virtual IReadOnlyList<MediaStream> GetMediaStreams(IMediaSourceManager mediaSourceManager)
        {
            return mediaSourceManager.GetMediaStreams(new MediaStreamQuery
            {
                ItemId = Id
            });
        }

        /// <summary>
        /// Gets a value indicating whether media sources should be displayed for this item.
        /// Was a property; converted to a method (a property getter can't take a parameter) so
        /// <see cref="IChannelManager"/> is passed explicitly instead of read from the removed
        /// <c>BaseItem.ChannelManager</c> static.
        /// </summary>
        /// <param name="channelManager">
        /// Instance of the <see cref="IChannelManager"/> interface, needed only when this item's
        /// <see cref="SourceType"/> is <see cref="SourceType.Channel"/>.
        /// </param>
        /// <returns><c>true</c> if media sources should be displayed; otherwise, <c>false</c>.</returns>
        public bool GetEnableMediaSourceDisplay(IChannelManager channelManager)
        {
            if (SourceType == SourceType.Channel)
            {
                return channelManager.EnableMediaSourceDisplay(this);
            }

            return true;
        }

        protected virtual bool IsActiveRecording()
        {
            return false;
        }

        public virtual IReadOnlyList<MediaSourceInfo> GetMediaSources(bool enablePathSubstitution, IMediaSourceManager mediaSourceManager, IMediaSegmentManager mediaSegmentManager, IChannelManager channelManager)
        {
            if (SourceType == SourceType.Channel)
            {
                var sources = channelManager.GetStaticMediaSources(this, CancellationToken.None)
                           .ToList();

                if (sources.Count > 0)
                {
                    return sources;
                }
            }

            var list = GetAllItemsForMediaSources().ToList();
            var commonPrefix = GetCommonNamePrefix(list);
            var result = list.Select(i => GetVersionInfo(enablePathSubstitution, i.Item, i.MediaSourceType, mediaSourceManager, mediaSegmentManager, commonPrefix)).ToList();

            if (IsActiveRecording())
            {
                foreach (var mediaSource in result)
                {
                    mediaSource.Type = MediaSourceType.Placeholder;
                }
            }

            // The source belonging to the item being queried sorts first so it is the default the client plays.
            var selfId = Id.ToString("N", CultureInfo.InvariantCulture);

            return result
                .OrderByDescending(i => string.Equals(i.Id, selfId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(i => i.VideoType == VideoType.VideoFile ? 0 : 1)
                .ThenBy(i => i.Video3DFormat.HasValue ? 1 : 0)
                .ThenByDescending(i => i, new MediaSourceWidthComparator())
                .ToArray();
        }

        protected virtual IEnumerable<(BaseItem Item, MediaSourceType MediaSourceType)> GetAllItemsForMediaSources()
        {
            return Enumerable.Empty<(BaseItem, MediaSourceType)>();
        }

        private MediaSourceInfo GetVersionInfo(bool enablePathSubstitution, BaseItem item, MediaSourceType type, IMediaSourceManager mediaSourceManager, IMediaSegmentManager mediaSegmentManager, string commonPrefix = null)
        {
            ArgumentNullException.ThrowIfNull(item);

            var protocol = item.PathProtocol;
            var itemPath = item.Path;

            var info = new MediaSourceInfo
            {
                Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
                Protocol = protocol ?? MediaProtocol.File,
                MediaStreams = mediaSourceManager.GetMediaStreams(item.Id),
                MediaAttachments = mediaSourceManager.GetMediaAttachments(item.Id),
                Name = GetMediaSourceName(item, commonPrefix),
                Path = enablePathSubstitution && protocol == MediaProtocol.File ? mediaSourceManager.MapFilePath(itemPath) : itemPath,
                RunTimeTicks = item.RunTimeTicks,
                Container = item.Container,
                Size = item.Size,
                Type = type,
                HasSegments = mediaSegmentManager.IsTypeSupported(item)
                    && (protocol is null or MediaProtocol.File)
                    && mediaSegmentManager.HasSegments(item.Id)
            };

            if (string.IsNullOrEmpty(info.Path))
            {
                info.Type = MediaSourceType.Placeholder;
            }

            if (info.Protocol == MediaProtocol.File)
            {
                info.ETag = item.DateModified.Ticks.ToString(CultureInfo.InvariantCulture).GetMD5().ToString("N", CultureInfo.InvariantCulture);
            }

            var video = item as Video;
            if (video is not null)
            {
                info.IsoType = video.IsoType;
                info.VideoType = video.VideoType;
                info.Video3DFormat = video.Video3DFormat;
                info.Timestamp = video.Timestamp;

                if (video.IsShortcut && !string.IsNullOrEmpty(video.ShortcutPath))
                {
                    var shortcutProtocol = mediaSourceManager.GetPathProtocol(video.ShortcutPath);

                    // Only allow remote shortcut paths — local file paths in .strm files
                    // could be used to read arbitrary files from the server.
                    if (shortcutProtocol != MediaProtocol.File)
                    {
                        info.IsRemote = true;
                        info.Path = video.ShortcutPath;
                        info.Protocol = shortcutProtocol;
                    }
                }

                if (string.IsNullOrEmpty(info.Container))
                {
                    if (video.VideoType == VideoType.VideoFile || video.VideoType == VideoType.Iso)
                    {
                        if (protocol.HasValue && protocol.Value == MediaProtocol.File)
                        {
                            info.Container = System.IO.Path.GetExtension(item.Path).TrimStart('.');
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(info.Container))
            {
                if (protocol.HasValue && protocol.Value == MediaProtocol.File)
                {
                    info.Container = System.IO.Path.GetExtension(item.Path).TrimStart('.');
                }
            }

            if (info.SupportsDirectStream && !string.IsNullOrEmpty(info.Path))
            {
                info.SupportsDirectStream = mediaSourceManager.SupportsDirectStream(info.Path, info.Protocol);
            }

            if (video is not null && video.VideoType != VideoType.VideoFile)
            {
                info.SupportsDirectStream = false;
            }

            info.Bitrate = item.TotalBitrate;
            info.InferTotalBitrate();

            return info;
        }

        internal string GetMediaSourceName(BaseItem item, string commonPrefix = null)
        {
            var terms = new List<string>();

            var path = item.Path;
            if (item.IsFileProtocol && !string.IsNullOrEmpty(path))
            {
                var displayName = System.IO.Path.GetFileNameWithoutExtension(path);

                // Prefer the suffix that differs from the other versions: strip the prefix shared by
                // all sibling files. This works regardless of folder layout, so it also labels episode
                // versions that share a season folder (e.g. "Greyscale" instead of the full
                // "Show - S01E02 - Title - Greyscale"). The prefix is already retreated to a delimiter
                // boundary (see GetCommonVersionPrefix).
                if (!string.IsNullOrEmpty(commonPrefix)
                    && displayName.Length > commonPrefix.Length
                    && displayName.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var name = displayName.AsSpan(commonPrefix.Length).TrimStart([' ', .. VersionDelimiters]);
                    if (!name.IsWhiteSpace())
                    {
                        terms.Add(name.ToString());
                    }
                }

                // Fall back to the containing folder name (the common layout for movie versions, and
                // the path taken when no common prefix could be derived).
                if (terms.Count == 0 && HasLocalAlternateVersions)
                {
                    var containingFolderName = System.IO.Path.GetFileName(ContainingFolderPath);
                    if (displayName.Length > containingFolderName.Length && displayName.StartsWith(containingFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        var name = displayName.AsSpan(containingFolderName.Length).TrimStart([' ', .. VersionDelimiters]);
                        if (!name.IsWhiteSpace())
                        {
                            terms.Add(name.ToString());
                        }
                    }
                }

                if (terms.Count == 0)
                {
                    terms.Add(displayName);
                }
            }

            if (terms.Count == 0)
            {
                terms.Add(item.Name);
            }

            if (item is Video video)
            {
                if (video.Video3DFormat.HasValue)
                {
                    terms.Add("3D");
                }

                if (video.VideoType == VideoType.BluRay)
                {
                    terms.Add("Bluray");
                }
                else if (video.VideoType == VideoType.Dvd)
                {
                    terms.Add("DVD");
                }
                else if (video.VideoType == VideoType.Iso)
                {
                    if (video.IsoType.HasValue)
                    {
                        if (video.IsoType.Value == IsoType.BluRay)
                        {
                            terms.Add("Bluray");
                        }
                        else if (video.IsoType.Value == IsoType.Dvd)
                        {
                            terms.Add("DVD");
                        }
                    }
                    else
                    {
                        terms.Add("ISO");
                    }
                }
            }

            return string.Join('/', terms);
        }

        /// <summary>
        /// Derives the prefix shared by the supplied media source items' file names, used to strip the
        /// common part and surface a short version label per source. Returns null when there are fewer
        /// than two file-based sources, since there is nothing to differentiate.
        /// </summary>
        /// <param name="items">The media source items.</param>
        /// <returns>The shared prefix, or null when no useful prefix exists.</returns>
        private static string GetCommonNamePrefix(IReadOnlyList<(BaseItem Item, MediaSourceType MediaSourceType)> items)
        {
            var fileNames = new List<string>();
            foreach (var (item, _) in items)
            {
                if (item.IsFileProtocol && !string.IsNullOrEmpty(item.Path))
                {
                    fileNames.Add(System.IO.Path.GetFileNameWithoutExtension(item.Path));
                }
            }

            if (fileNames.Count < 2)
            {
                return null;
            }

            var prefix = GetCommonVersionPrefix(fileNames);
            return string.IsNullOrEmpty(prefix) ? null : prefix;
        }

        /// <summary>
        /// Computes the case-insensitive longest common prefix of the supplied version file names,
        /// retreated to the last delimiter boundary. Retreating keeps the differing suffix intact:
        /// it avoids slicing through a word every version shares (e.g. "Grey" in "Greyscale" and
        /// "Greyish") while still trimming the common part when every version is suffixed (e.g.
        /// "- Greyscale" / "- Colorized"). It prefers a structural delimiter ('-', '_', '.') so a
        /// token shared by the descriptors but separated only by spaces (e.g. a common "2160p ") is
        /// kept in the label, falling back to a space only when no structural delimiter is shared. The
        /// separators mirror the version delimiters recognised by the naming layer (Tesserafin.Naming
        /// VideoFlagDelimiters).
        /// </summary>
        /// <param name="fileNames">The version file names without extension; must contain at least one entry.</param>
        /// <returns>The shared prefix retreated to a separator boundary, or an empty string when none is shared.</returns>
        internal static string GetCommonVersionPrefix(IReadOnlyList<string> fileNames)
        {
            var prefix = fileNames[0];
            for (var i = 1; i < fileNames.Count && prefix.Length > 0; i++)
            {
                var name = fileNames[i];
                var length = Math.Min(prefix.Length, name.Length);
                var common = 0;
                while (common < length && char.ToUpperInvariant(prefix[common]) == char.ToUpperInvariant(name[common]))
                {
                    common++;
                }

                prefix = prefix[..common];
            }

            // If the common prefix is itself a whole file name then one version is unlabelled (the
            // base name); the boundary already sits at the end of that name, so don't retreat into it.
            var prefixIsWholeName = false;
            for (var i = 0; i < fileNames.Count; i++)
            {
                if (fileNames[i].Length == prefix.Length)
                {
                    prefixIsWholeName = true;
                    break;
                }
            }

            if (!prefixIsWholeName)
            {
                // Retreat to the last structural delimiter ('-', '_', '.').
                var cut = prefix.Length;
                while (cut > 0 && Array.IndexOf(VersionDelimiters, prefix[cut - 1]) < 0)
                {
                    cut--;
                }

                if (cut == 0)
                {
                    cut = prefix.Length;
                    while (cut > 0 && prefix[cut - 1] != ' ')
                    {
                        cut--;
                    }
                }

                prefix = prefix[..cut];
            }

            return prefix;
        }

        /// <summary>
        /// The base implementation to refresh metadata.
        /// </summary>
        /// <param name="deletions">Explicit deletion operations for this refresh.</param>
        /// <param name="writes">Explicit item write/resolution ownership.</param>
        /// <param name="providers">The provider manager executing this refresh.</param>
        /// <param name="options">The options.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>true if a provider reports we changed.</returns>
        public async Task<ItemUpdateType> RefreshMetadata(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemMetadataDispatcher providers, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var requiresSave = false;

            if (IsFileProtocol && SupportsOwnedItems)
            {
                try
                {
                    requiresSave = await RefreshOwnedItemsAsync(deletions, writes, providers, options, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    providers.ReportOwnedItemRefreshFailure(this, ex);
                }
            }

            var refreshOptions = requiresSave
                ? new MetadataRefreshOptions(options)
                {
                    ForceSave = true
                }
                : options;

            return await providers.RefreshSingleItem(this, refreshOptions, cancellationToken).ConfigureAwait(false);
        }

        protected bool IsVisibleStandaloneInternal(User user, bool checkFolders, IStandaloneVisibilityService scope)
        {
            if (!IsVisible(user, false, scope))
            {
                return false;
            }

            var parents = scope.GetParents(this);
            if (parents.Any(i => !i.IsVisible(user, true, scope)))
            {
                return false;
            }

            if (checkFolders)
            {
                var topParent = parents.Count > 0 ? parents[^1] : this;

                if (string.IsNullOrEmpty(topParent.Path))
                {
                    return true;
                }

                var itemCollectionFolders = scope.GetCollectionFolders(this).Select(i => i.Id).ToList();

                if (itemCollectionFolders.Count > 0)
                {
                    var blockedMediaFolders = user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders);
                    IEnumerable<Guid> userCollectionFolderIds;
                    if (blockedMediaFolders.Length > 0)
                    {
                        // User has blocked folders - get all library folders and exclude blocked ones
                        userCollectionFolderIds = scope.GetLibraryIds()
                            .Where(id => !blockedMediaFolders.Contains(id));
                    }
                    else if (user.HasPermission(PermissionKind.EnableAllFolders))
                    {
                        // User can access all folders - no need to filter
                        return true;
                    }
                    else
                    {
                        // User has specific enabled folders
                        userCollectionFolderIds = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
                    }

                    if (!itemCollectionFolders.Any(userCollectionFolderIds.Contains))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public void SetParent(Folder parent)
        {
            ParentId = parent is null ? Guid.Empty : parent.Id;
        }

        /// <summary>Discovers and refreshes relationships owned by this item.</summary>
        /// <param name="deletions">Explicit deletion operations for this refresh.</param>
        /// <param name="writes">Explicit catalogue write operations.</param>
        /// <param name="providers">The provider manager executing this refresh.</param>
        /// <param name="options">The metadata refresh options.</param>
        /// <param name="cancellationToken">Cancellation for discovery and writes.</param>
        /// <returns>Whether the metadata provider should save after refreshing owned items.</returns>
        protected virtual Task<bool> RefreshOwnedItemsAsync(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemMetadataDispatcher providers, MetadataRefreshOptions options, CancellationToken cancellationToken)
            => RefreshedOwnedItems(deletions, writes, providers, options, GetFileSystemChildren(options.DirectoryService), cancellationToken);

        /// <summary>
        /// Refreshes owned items such as trailers, theme videos, special features, etc.
        /// Returns true or false indicating if changes were found.
        /// </summary>
        /// <param name="deletions">Explicit deletion operations for this refresh.</param>
        /// <param name="writes">Explicit item write/resolution ownership.</param>
        /// <param name="providers">The provider manager executing this refresh.</param>
        /// <param name="options">The metadata refresh options.</param>
        /// <param name="fileSystemChildren">The list of filesystem children.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><c>true</c> if any items have changed, else <c>false</c>.</returns>
        protected virtual async Task<bool> RefreshedOwnedItems(IItemDeletionCommands deletions, IItemWriteCommands writes, IItemMetadataDispatcher providers, MetadataRefreshOptions options, IReadOnlyList<FileSystemMetadata> fileSystemChildren, CancellationToken cancellationToken)
        {
            if (!IsFileProtocol || !SupportsOwnedItems || IsInMixedFolder || this is ICollectionFolder or UserRootFolder or AggregateFolder || this.GetType() == typeof(Folder))
            {
                return false;
            }

            return await writes.RefreshExtrasAsync(this, providers, deletions, options, fileSystemChildren, cancellationToken).ConfigureAwait(false);
        }

        protected virtual FileSystemMetadata[] GetFileSystemChildren(IDirectoryService directoryService)
        {
            return directoryService.GetFileSystemEntries(ContainingFolderPath);
        }

        public string GetPresentationUniqueKey()
        {
            return PresentationUniqueKey ?? CreatePresentationUniqueKey();
        }

        public virtual bool RequiresRefresh(MetadataRefreshServices services)
        {
            if (string.IsNullOrEmpty(Path) || DateModified == DateTime.MinValue)
            {
                return false;
            }

            var info = services.FileSystem.GetFileSystemInfo(Path);

            return info.Exists && this.HasChanged(info.LastWriteTimeUtc);
        }

        public virtual List<string> GetUserDataKeys()
        {
            var list = new List<string>();

            if (SourceType == SourceType.Channel)
            {
                if (!string.IsNullOrEmpty(ExternalId))
                {
                    list.Add(ExternalId);
                }
            }

            list.Add(Id.ToString());
            return list;
        }

        /// <summary>Copies this item and applies resolved file metadata without changing this instance.</summary>
        /// <param name="resolved">The same identity and concrete item type after discovery.</param>
        /// <param name="changes">The metadata changes applied to the copy.</param>
        /// <returns>A detached item carrying the resolved changes.</returns>
        public BaseItem CopyWithResolvedMetadata(BaseItem resolved, out ItemUpdateType changes)
        {
            ArgumentNullException.ThrowIfNull(resolved);
            if (!Id.Equals(resolved.Id) || GetType() != resolved.GetType())
            {
                throw new ArgumentException("Resolved metadata must have the same identity and concrete type.", nameof(resolved));
            }

            var staged = CopyForWrite();
            changes = staged.UpdateFromResolvedItem(resolved);
            return staged;
        }

        internal virtual ItemUpdateType UpdateFromResolvedItem(BaseItem newItem)
        {
            var updateType = ItemUpdateType.None;

            if (IsInMixedFolder != newItem.IsInMixedFolder)
            {
                IsInMixedFolder = newItem.IsInMixedFolder;
                updateType |= ItemUpdateType.MetadataImport;
            }

            return updateType;
        }

        public void AfterMetadataRefresh()
        {
        }

        /// <summary>
        /// Gets the preferred metadata language.
        /// </summary>
        /// <returns>System.String.</returns>
        public string GetPreferredMetadataLanguage()
            => GetPreferredMetadataLanguage(
                GetParents,
                () => LibraryManager.GetCollectionFolders(this),
                () => LibraryManager.GetLibraryOptions(this).PreferredMetadataLanguage,
                () => (ReadServices?.MetadataDefaults ?? ItemMetadataDefaults.BuiltIn).Language);

        /// <summary>Resolves the item's language followed by the parent, library and server defaults.</summary>
        /// <param name="getParents">Reads the item's parents in nearest-first order.</param>
        /// <param name="getLibraries">Resolves the item's libraries in catalogue order.</param>
        /// <param name="getLibraryLanguage">Reads the language in the effective library options.</param>
        /// <param name="getServerLanguage">Reads the server's default language.</param>
        /// <returns>The preferred metadata language.</returns>
        protected string GetPreferredMetadataLanguage(
            Func<IEnumerable<BaseItem>> getParents,
            Func<IEnumerable<Folder>> getLibraries,
            Func<string> getLibraryLanguage,
            Func<string> getServerLanguage)
            => ResolveMetadataPreference(
                PreferredMetadataLanguage,
                getParents,
                getLibraries,
                static item => item.PreferredMetadataLanguage,
                getLibraryLanguage,
                getServerLanguage);

        /// <summary>Gets the preferred metadata country code.</summary>
        /// <returns>The country code.</returns>
        public string GetPreferredMetadataCountryCode()
            => GetPreferredMetadataCountryCode(null);

        private string GetPreferredMetadataCountryCode(IStandaloneVisibilityService scope)
            => ResolveMetadataPreference(
                PreferredMetadataCountryCode,
                () => scope?.GetParents(this) ?? GetParents(),
                () => scope?.GetCollectionFolders(this) ?? LibraryManager.GetCollectionFolders(this),
                static item => item.PreferredMetadataCountryCode,
                () => (scope is null ? LibraryManager.GetLibraryOptions(this) : scope.GetLibraryOptions(this))?.MetadataCountryCode,
                () => scope is null
                    ? (ReadServices?.MetadataDefaults ?? ItemMetadataDefaults.BuiltIn).CountryCode
                    : scope.GetServerMetadataCountryCode());

        /// <summary>Resolves inherited metadata preferences from the supplied catalogue and settings.</summary>
        /// <param name="lookup">The item identity lookup.</param>
        /// <param name="collections">The library membership resolver.</param>
        /// <param name="options">The effective library options.</param>
        /// <param name="configuration">The server-wide metadata defaults.</param>
        /// <returns>The preferred metadata language and country code.</returns>
        public (string Language, string CountryCode) GetMetadataLocale(
            IItemLookupService lookup,
            ICollectionFolderResolver collections,
            LibraryOptions options,
            ServerConfiguration configuration)
            => ResolveMetadataLocale(lookup, collections, () => options, configuration);

        /// <summary>Resolves inherited preferences, reading effective library options only if needed.</summary>
        /// <param name="lookup">The item identity lookup.</param>
        /// <param name="collections">The library membership resolver.</param>
        /// <param name="itemOptions">The effective library options service.</param>
        /// <param name="configuration">The server-wide metadata defaults.</param>
        /// <returns>The preferred metadata language and country code.</returns>
        public (string Language, string CountryCode) GetMetadataLocale(
            IItemLookupService lookup,
            ICollectionFolderResolver collections,
            ILibraryItemOptions itemOptions,
            ServerConfiguration configuration)
            => ResolveMetadataLocale(lookup, collections, () => itemOptions.GetLibraryOptions(this), configuration);

        private (string Language, string CountryCode) ResolveMetadataLocale(
            IItemLookupService lookup,
            ICollectionFolderResolver collections,
            Func<LibraryOptions> getOptions,
            ServerConfiguration configuration)
        {
            List<Folder> libraries = null;
            LibraryOptions options = null;
            IEnumerable<BaseItem> ReadParents() => GetParents(lookup);
            IEnumerable<Folder> ReadLibraries() => libraries ??= collections.GetCollectionFolders(this);
            LibraryOptions ReadOptions() => options ??= getOptions();

            var language = GetPreferredMetadataLanguage(
                ReadParents,
                ReadLibraries,
                () => ReadOptions().PreferredMetadataLanguage,
                () => configuration.PreferredMetadataLanguage);
            var country = ResolveMetadataPreference(
                PreferredMetadataCountryCode,
                ReadParents,
                ReadLibraries,
                static item => item.PreferredMetadataCountryCode,
                () => ReadOptions()?.MetadataCountryCode,
                () => configuration.MetadataCountryCode);
            return (language, country);
        }

        private static string ResolveMetadataPreference(
            string value,
            Func<IEnumerable<BaseItem>> getParents,
            Func<IEnumerable<Folder>> getLibraries,
            Func<BaseItem, string> getPreference,
            Func<string> getLibraryDefault,
            Func<string> getServerDefault)
        {
            if (string.IsNullOrEmpty(value))
            {
                value = getParents().Select(getPreference).FirstOrDefault(preference => !string.IsNullOrEmpty(preference));
            }

            if (string.IsNullOrEmpty(value))
            {
                value = getLibraries().Select(getPreference).FirstOrDefault(preference => !string.IsNullOrEmpty(preference));
            }

            if (string.IsNullOrEmpty(value))
            {
                value = getLibraryDefault();
            }

            return string.IsNullOrEmpty(value) ? getServerDefault() : value;
        }

        /// <summary>
        /// Gets the original language of the item, inheriting from parent items if necessary.
        /// </summary>
        /// <returns>System.String.</returns>
        public virtual string GetInheritedOriginalLanguage()
        {
            return OriginalLanguage;
        }

        public virtual bool IsSaveLocalMetadataEnabled()
        {
            if (SourceType == SourceType.Channel)
            {
                return false;
            }

            var libraryOptions = LibraryManager.GetLibraryOptions(this);

            return libraryOptions.SaveLocalMetadata;
        }

        /// <summary>
        /// Determines if a given user has access to this item.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="skipAllowedTagsCheck">Don't check for allowed tags.</param>
        /// <param name="scope">Supplies tags, parents, libraries and rating scores.</param>
        /// <returns><c>true</c> if [is parental allowed] [the specified user]; otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">If user or scope is null.</exception>
        public bool IsParentalAllowed(User user, bool skipAllowedTagsCheck, IStandaloneVisibilityService scope)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(scope);

            if (!scope.IsVisibleViaTags(this, user, skipAllowedTagsCheck))
            {
                return false;
            }

            var maxAllowedRating = user.MaxParentalRatingScore;
            var maxAllowedSubRating = user.MaxParentalRatingSubScore;
            var rating = GetCustomRatingForComparision(scope);

            if (string.IsNullOrEmpty(rating))
            {
                rating = GetOfficialRatingForComparison(scope);
            }

            if (string.IsNullOrEmpty(rating))
            {
                return !GetBlockUnratedValue(user, scope);
            }

            var countryCode = GetPreferredMetadataCountryCode(scope);
            var ratingScore = scope.GetRatingScore(rating, countryCode);

            // Could not determine rating level
            if (ratingScore is null)
            {
                var isAllowed = !GetBlockUnratedValue(user, scope);

                if (!isAllowed)
                {
                    scope.Logger.LogDebug("{0} has an unrecognized parental rating of {1}.", Name, rating);
                }

                return isAllowed;
            }

            if (!maxAllowedRating.HasValue)
            {
                return true;
            }

            if (ratingScore.Score != maxAllowedRating.Value)
            {
                return ratingScore.Score < maxAllowedRating.Value;
            }

            return !maxAllowedSubRating.HasValue || (ratingScore.SubScore ?? 0) <= maxAllowedSubRating.Value;
        }

        public ParentalRatingScore GetParentalRatingScore(ILocalizationManager localization)
        {
            var rating = CustomRatingForComparison;

            if (string.IsNullOrEmpty(rating))
            {
                rating = OfficialRatingForComparison;
            }

            if (string.IsNullOrEmpty(rating))
            {
                return null;
            }

            return localization.GetRatingScore(rating, GetPreferredMetadataCountryCode());
        }

        public List<string> GetInheritedTags()
        {
            return CombineInheritedTags(this, GetParents(), LibraryManager.GetCollectionFolders(this)).ToList();
        }

        /// <summary>
        /// Combines the item's own tags, then each parent's in the given order, then each library's,
        /// deduplicated ignoring case (the first spelling is kept).
        /// </summary>
        /// <param name="item">The item.</param>
        /// <param name="parents">The item's parents, nearest first.</param>
        /// <param name="libraries">The libraries the item belongs to.</param>
        /// <returns>The inherited tags.</returns>
        public static IReadOnlyList<string> CombineInheritedTags(BaseItem item, IEnumerable<BaseItem> parents, IEnumerable<Folder> libraries)
        {
            var list = new List<string>();
            list.AddRange(item.Tags);

            foreach (var parent in parents)
            {
                list.AddRange(parent.Tags);
            }

            foreach (var folder in libraries)
            {
                list.AddRange(folder.Tags);
            }

            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        protected bool IsVisibleViaTags(User user, bool skipAllowedTagsCheck)
        {
            return EvaluateTagVisibility(user, skipAllowedTagsCheck, GetInheritedTags, () => GetParents().FirstOrDefault() ?? this);
        }

        /// <summary>
        /// Applies the user's blocked tags, then allowed tags, to an item's inherited tags. Blocked tags
        /// always apply; allowed tags are skipped on request and for a direct child of a root folder.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="skipAllowedTagsCheck">Whether to ignore the user's allowed tags.</param>
        /// <param name="getInheritedTags">Gets the item's inherited tags; only called when the user has tag preferences.</param>
        /// <param name="getParentOrSelf">Gets the item's direct parent, or the item itself without one; only called past the blocked tags.</param>
        /// <returns>Whether the tags admit the item.</returns>
        public static bool EvaluateTagVisibility(User user, bool skipAllowedTagsCheck, Func<IEnumerable<string>> getInheritedTags, Func<BaseItem> getParentOrSelf)
        {
            ArgumentNullException.ThrowIfNull(user);
            ArgumentNullException.ThrowIfNull(getInheritedTags);
            ArgumentNullException.ThrowIfNull(getParentOrSelf);

            var blockedTags = user.GetPreference(PreferenceKind.BlockedTags);
            var allowedTags = user.GetPreference(PreferenceKind.AllowedTags);

            if (blockedTags.Length == 0 && allowedTags.Length == 0)
            {
                return true;
            }

            // Normalize tags using the same logic as database queries
            var normalizedBlockedTags = blockedTags
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.GetCleanValue())
                .ToHashSet(StringComparer.Ordinal);

            var normalizedItemTags = getInheritedTags()
                .Select(t => t.GetCleanValue())
                .ToHashSet(StringComparer.Ordinal);

            // Check blocked tags - item is hidden if it has any blocked tag
            if (normalizedBlockedTags.Overlaps(normalizedItemTags))
            {
                return false;
            }

            var parent = getParentOrSelf();
            if (parent is UserRootFolder or AggregateFolder or UserView)
            {
                return true;
            }

            // Check allowed tags - item must have at least one allowed tag
            if (!skipAllowedTagsCheck && allowedTags.Length > 0)
            {
                var normalizedAllowedTags = allowedTags
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.GetCleanValue())
                    .ToHashSet(StringComparer.Ordinal);

                if (!normalizedAllowedTags.Overlaps(normalizedItemTags))
                {
                    return false;
                }
            }

            return true;
        }

        public virtual UnratedItem GetBlockUnratedType()
        {
            if (SourceType == SourceType.Channel)
            {
                return UnratedItem.ChannelContent;
            }

            return UnratedItem.Other;
        }

        /// <summary>
        /// Gets a bool indicating if access to the unrated item is blocked or not.
        /// </summary>
        /// <param name="user">The configuration.</param>
        /// <returns><c>true</c> if blocked, <c>false</c> otherwise.</returns>
        protected virtual bool GetBlockUnratedValue(User user)
        {
            // Don't block plain folders that are unrated. Let the media underneath get blocked
            // Special folders like series and albums will override this method.
            if (IsFolder || this is IItemByName)
            {
                return false;
            }

            return user.GetPreferenceValues<UnratedItem>(PreferenceKind.BlockUnratedItems).Contains(GetBlockUnratedType());
        }

        /// <summary>
        /// As <see cref="GetBlockUnratedValue(User)"/>, for the parental check: a plain video reads its source type
        /// through the service when it supplies it (DEBT-SERVER-9).
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="scope">The service of the check; <c>null</c> when it has none.</param>
        /// <returns><c>true</c> if blocked, <c>false</c> otherwise.</returns>
        internal virtual bool GetBlockUnratedValue(User user, IStandaloneVisibilityService scope)
        {
            return GetBlockUnratedValue(user);
        }

        /// <summary>
        /// Gets the <see cref="SourceType"/> the visibility check reads: a video asks the service whether it is an
        /// active recording when the service supplies it (DEBT-SERVER-9).
        /// </summary>
        /// <param name="scope">The service of the check; <c>null</c> when it has none.</param>
        /// <returns>The source type.</returns>
        internal virtual SourceType GetSourceType(IStandaloneVisibilityService scope)
        {
            return SourceType;
        }

        /// <summary>
        /// Determines if this folder should be visible to a given user.
        /// Default is just parental allowed. Can be overridden for more functionality.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="skipAllowedTagsCheck">Don't check for allowed tags.</param>
        /// <param name="tagVisibility">Supplies tags, parents, libraries and rating scores. Overrides pass it on.</param>
        /// <returns><c>true</c> if the specified user is visible; otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="user" /> is <c>null</c>.</exception>
        public virtual bool IsVisible(User user, bool skipAllowedTagsCheck, IStandaloneVisibilityService tagVisibility)
        {
            ArgumentNullException.ThrowIfNull(user);

            return IsParentalAllowed(user, skipAllowedTagsCheck, tagVisibility);
        }

        /// <summary>
        /// Determines if this item, its parents and its libraries admit a given user.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="tagVisibility">Supplies tags, parents, libraries and rating scores. Overrides pass it on.</param>
        /// <returns><c>true</c> if the item is visible to the user.</returns>
        public virtual bool IsVisibleStandalone(User user, IStandaloneVisibilityService tagVisibility)
        {
            if (GetSourceType(tagVisibility) == SourceType.Channel)
            {
                return IsVisibleStandaloneInternal(user, false, tagVisibility) && Channel.IsChannelVisible(this, user, tagVisibility);
            }

            return IsVisibleStandaloneInternal(user, true, tagVisibility);
        }

        public virtual string GetClientTypeName()
        {
            if (IsFolder && SourceType == SourceType.Channel && this is not Channel && this is not Season && this is not Series)
            {
                return "ChannelFolderItem";
            }

            return GetType().Name;
        }

        public BaseItemKind GetBaseItemKind()
        {
            return _baseItemKind ??= Enum.Parse<BaseItemKind>(GetClientTypeName());
        }

        /// <summary>
        /// Adds a studio to the item.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <exception cref="ArgumentNullException">Throws if name is null.</exception>
        public void AddStudio(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            var current = Studios;

            if (!current.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                int curLen = current.Length;
                if (curLen == 0)
                {
                    Studios = [name];
                }
                else
                {
                    Studios = [.. current, name];
                }
            }
        }

        public void SetStudios(IEnumerable<string> names)
        {
            Studios = names.Trimmed().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>
        /// Adds a genre to the item.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <exception cref="ArgumentNullException">Throws if name is null.</exception>
        public void AddGenre(string name)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);

            var genres = Genres;
            if (!genres.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                Genres = [.. genres, name];
            }
        }

        /// <summary>
        /// Gets an image.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <returns><c>true</c> if the specified type has image; otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentException">Backdrops should be accessed using Item.Backdrops.</exception>
        public bool HasImage(ImageType type, int imageIndex)
        {
            return GetImageInfo(type, imageIndex) is not null;
        }

        public void SetImage(ItemImageInfo image, int index)
        {
            if (image.Type == ImageType.Chapter)
            {
                throw new ArgumentException("Cannot set chapter images using SetImagePath");
            }

            var existingImage = GetImageInfo(image.Type, index);

            if (existingImage is null)
            {
                AddImage(image);
            }
            else
            {
                existingImage.Path = image.Path;
                existingImage.DateModified = image.DateModified;
                existingImage.Width = image.Width;
                existingImage.Height = image.Height;
                existingImage.BlurHash = image.BlurHash;
            }
        }

        public void SetImagePath(ImageType type, int index, FileSystemMetadata file)
        {
            if (type == ImageType.Chapter)
            {
                throw new ArgumentException("Cannot set chapter images using SetImagePath");
            }

            var image = GetImageInfo(type, index);

            if (image is null)
            {
                AddImage(GetImageInfo(file, type));
            }
            else
            {
                var imageInfo = GetImageInfo(file, type);

                image.Path = file.FullName;
                image.DateModified = imageInfo.DateModified;

                // reset these values
                image.Width = 0;
                image.Height = 0;
            }
        }

        /// <summary>
        /// Deletes the image.
        /// </summary>
        /// <param name="writes">Explicit item write/resolution ownership.</param>
        /// <param name="fileSystem">The filesystem used to delete local images.</param>
        /// <param name="type">The type.</param>
        /// <param name="index">The index.</param>
        /// <returns>A task.</returns>
        public async Task DeleteImageAsync(IItemWriteCommands writes, IFileSystem fileSystem, ImageType type, int index)
        {
            var info = GetImageInfo(type, index);

            if (info is null)
            {
                // Nothing to do
                return;
            }

            // Remove from file system
            var path = info.Path;
            if (info.IsLocalFile && !string.IsNullOrWhiteSpace(path))
            {
                fileSystem.DeleteFile(path);
            }

            // Remove from item
            RemoveImage(info);

            await UpdateToRepositoryAsync(writes, ItemUpdateType.ImageUpdate, CancellationToken.None).ConfigureAwait(false);
        }

        public void RemoveImage(ItemImageInfo image)
        {
            RemoveImages([image]);
        }

        public void RemoveImages(IEnumerable<ItemImageInfo> deletedImages)
        {
            ImageInfos = ImageInfos.Except(deletedImages).ToArray();
        }

        public void AddImage(ItemImageInfo image)
        {
            ImageInfos = [.. ImageInfos, image];
        }

        /// <summary>Copies item state for write preparation without changing the published instance.</summary>
        /// <returns>A shallow copy; mutable child objects remain shared.</returns>
        public BaseItem CopyForWrite() => (BaseItem)MemberwiseClone();

        public virtual async Task UpdateToRepositoryAsync(IItemWriteCommands writes, ItemUpdateType updateReason, CancellationToken cancellationToken)
         => await writes.UpdateItemAsync(this, null, updateReason, cancellationToken).ConfigureAwait(false);

        public async Task ReattachUserDataAsync(IItemWriteCommands writes, CancellationToken cancellationToken) =>
            await writes.ReattachUserDataAsync(this, cancellationToken).ConfigureAwait(false);

        /// <summary>
        /// Validates that images within the item are still on the filesystem.
        /// </summary>
        /// <returns><c>true</c> if the images validate, <c>false</c> if not.</returns>
        public bool ValidateImages()
        {
            List<ItemImageInfo> deletedImages = null;
            foreach (var imageInfo in ImageInfos)
            {
                if (!imageInfo.IsLocalFile)
                {
                    continue;
                }

                if (File.Exists(imageInfo.Path))
                {
                    continue;
                }

                (deletedImages ??= []).Add(imageInfo);
            }

            var anyImagesRemoved = deletedImages?.Count > 0;
            if (anyImagesRemoved)
            {
                RemoveImages(deletedImages);
            }

            return anyImagesRemoved;
        }

        /// <summary>
        /// Gets the image path.
        /// </summary>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <returns>System.String.</returns>
        /// <exception cref="ArgumentNullException">Item is null.</exception>
        public string GetImagePath(ImageType imageType, int imageIndex)
            => GetImageInfo(imageType, imageIndex)?.Path;

        /// <summary>
        /// Gets the image information.
        /// </summary>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="imageIndex">Index of the image.</param>
        /// <returns>ItemImageInfo.</returns>
        public ItemImageInfo GetImageInfo(ImageType imageType, int imageIndex)
            => GetImages(imageType).ElementAtOrDefault(imageIndex);

        public IEnumerable<ItemImageInfo> GetImages(ImageType imageType)
        {
            if (imageType == ImageType.Chapter)
            {
                throw new ArgumentException("No image info for chapter images");
            }

            // Yield return is more performant than LINQ Where on an Array
            for (var i = 0; i < ImageInfos.Length; i++)
            {
                var imageInfo = ImageInfos[i];
                if (imageInfo.Type == imageType)
                {
                    yield return imageInfo;
                }
            }
        }

        /// <summary>
        /// Adds the images, updating metadata if they already are part of this item.
        /// </summary>
        /// <param name="imageType">Type of the image.</param>
        /// <param name="images">The images.</param>
        /// <returns><c>true</c> if images were added or updated, <c>false</c> otherwise.</returns>
        /// <exception cref="ArgumentException">Cannot call AddImages with chapter images.</exception>
        public bool AddImages(ImageType imageType, List<FileSystemMetadata> images)
        {
            if (imageType == ImageType.Chapter)
            {
                throw new ArgumentException("Cannot call AddImages with chapter images");
            }

            var existingImages = GetImages(imageType)
                .ToList();

            var newImageList = new List<FileSystemMetadata>();
            var imageUpdated = false;

            foreach (var newImage in images)
            {
                if (newImage is null)
                {
                    throw new ArgumentException("null image found in list");
                }

                var existing = existingImages
                    .Find(i => string.Equals(i.Path, newImage.FullName, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    newImageList.Add(newImage);
                }
                else
                {
                    if (existing.IsLocalFile)
                    {
                        var newDateModified = newImage.LastWriteTimeUtc;

                        // If date changed then we need to reset saved image dimensions
                        if (existing.DateModified != newDateModified && (existing.Width > 0 || existing.Height > 0))
                        {
                            existing.Width = 0;
                            existing.Height = 0;
                            imageUpdated = true;
                        }

                        existing.DateModified = newDateModified;
                    }
                }
            }

            if (newImageList.Count > 0)
            {
                ImageInfos = ImageInfos.Concat(newImageList.Select(i => GetImageInfo(i, imageType))).ToArray();
            }

            return imageUpdated || newImageList.Count > 0;
        }

        private static ItemImageInfo GetImageInfo(FileSystemMetadata file, ImageType type)
        {
            return new ItemImageInfo
            {
                Path = file.FullName,
                Type = type,
                DateModified = file.LastWriteTimeUtc
            };
        }

        /// <summary>
        /// Gets the file system path to delete when the item is to be deleted.
        /// </summary>
        /// <param name="fileSystem">The filesystem that supplies source and sidecar metadata.</param>
        /// <returns>The metadata for the deleted paths.</returns>
        public virtual IEnumerable<FileSystemMetadata> GetDeletePaths(IFileSystem fileSystem)
        {
            return new[]
            {
                fileSystem.GetFileSystemInfo(Path)
            }.Concat(GetLocalMetadataFilesToDelete(fileSystem));
        }

        protected List<FileSystemMetadata> GetLocalMetadataFilesToDelete(IFileSystem fileSystem)
        {
            if (IsFolder || !IsInMixedFolder)
            {
                return [];
            }

            var filename = System.IO.Path.GetFileNameWithoutExtension(Path);

            return fileSystem.GetFiles(System.IO.Path.GetDirectoryName(Path), _supportedExtensions, false, false)
                .Where(i => System.IO.Path.GetFileNameWithoutExtension(i.FullName).StartsWith(filename, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public bool AllowsMultipleImages(ImageType type)
        {
            return type == ImageType.Backdrop || type == ImageType.Chapter;
        }

        public Task SwapImagesAsync(IItemWriteCommands writes, IFileSystem fileSystem, ImageType type, int index1, int index2)
        {
            if (!AllowsMultipleImages(type))
            {
                throw new ArgumentException("The change index operation is only applicable to backdrops and screen shots");
            }

            var info1 = GetImageInfo(type, index1);
            var info2 = GetImageInfo(type, index2);

            if (info1 is null || info2 is null)
            {
                // Nothing to do
                return Task.CompletedTask;
            }

            if (!info1.IsLocalFile || !info2.IsLocalFile)
            {
                // TODO: Not supported  yet
                return Task.CompletedTask;
            }

            var path1 = info1.Path;
            var path2 = info2.Path;

            fileSystem.SwapFiles(path1, path2);

            // Refresh these values
            info1.DateModified = fileSystem.GetLastWriteTimeUtc(info1.Path);
            info2.DateModified = fileSystem.GetLastWriteTimeUtc(info2.Path);

            info1.Width = 0;
            info1.Height = 0;
            info2.Width = 0;
            info2.Height = 0;

            return UpdateToRepositoryAsync(writes, ItemUpdateType.ImageUpdate, CancellationToken.None);
        }

        public virtual bool IsPlayed(User user, IUserDataManager userDataManager, UserItemData userItemData = null)
        {
            userItemData ??= userDataManager.GetUserData(user, this);

            return userItemData is not null && userItemData.Played;
        }

        public bool IsFavoriteOrLiked(User user, IUserDataManager userDataManager, UserItemData userItemData = null)
        {
            userItemData ??= userDataManager.GetUserData(user, this);

            return userItemData is not null && (userItemData.IsFavorite || (userItemData.Likes ?? false));
        }

        public virtual bool IsUnplayed(User user, IUserDataManager userDataManager, UserItemData userItemData = null)
        {
            ArgumentNullException.ThrowIfNull(user);

            userItemData ??= userDataManager.GetUserData(user, this);

            return userItemData is null || !userItemData.Played;
        }

        ItemLookupInfo IHasLookupInfo<ItemLookupInfo>.GetLookupInfo()
        {
            return GetItemLookupInfo<ItemLookupInfo>();
        }

        protected T GetItemLookupInfo<T>((string Language, string CountryCode)? locale = null)
            where T : ItemLookupInfo, new()
        {
            return new T
            {
                Path = Path,
                MetadataCountryCode = locale.HasValue ? locale.Value.CountryCode : GetPreferredMetadataCountryCode(),
                MetadataLanguage = locale.HasValue ? locale.Value.Language : GetPreferredMetadataLanguage(),
                Name = GetNameForMetadataLookup(),
                OriginalTitle = OriginalTitle,
                ProviderIds = ProviderIds,
                IndexNumber = IndexNumber,
                ParentIndexNumber = ParentIndexNumber,
                Year = ProductionYear,
                PremiereDate = PremiereDate
            };
        }

        protected virtual string GetNameForMetadataLookup()
        {
            return Name;
        }

        /// <summary>
        /// This is called before any metadata refresh and returns true if changes were made.
        /// </summary>
        /// <param name="replaceAllMetadata">Whether to replace all metadata.</param>
        /// <param name="services">The metadata refresh services.</param>
        /// <returns>true if the item has change, else false.</returns>
        public virtual bool BeforeMetadataRefresh(bool replaceAllMetadata, MetadataRefreshServices services)
        {
            var hasChanges = false;

            if (string.IsNullOrEmpty(Name) && !string.IsNullOrEmpty(Path))
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(Path);
                hasChanges = true;
            }

            return hasChanges;
        }

        public virtual void FillUserDataDtoValues(
            UserItemDataDto dto,
            UserItemData userData,
            BaseItemDto itemDto,
            User user,
            DtoOptions fields,
            IItemCountService itemCountService,
            (int Played, int Total)? precomputedCounts = null)
        {
            if (RunTimeTicks.HasValue)
            {
                double pct = RunTimeTicks.Value;

                if (pct > 0)
                {
                    pct = userData.PlaybackPositionTicks / pct;

                    if (pct > 0)
                    {
                        dto.PlayedPercentage = 100 * pct;
                    }
                }
            }
        }

        public string GetEtag(User user)
        {
            var list = GetEtagValues(user);

            return string.Join('|', list).GetMD5().ToString("N", CultureInfo.InvariantCulture);
        }

        protected virtual List<string> GetEtagValues(User user)
        {
            return
            [
                DateLastSaved.Ticks.ToString(CultureInfo.InvariantCulture)
            ];
        }

        public virtual IEnumerable<Guid> GetAncestorIds()
        {
            return GetParents().Select(i => i.Id).Concat(LibraryManager.GetCollectionFolders(this).Select(i => i.Id));
        }

        public BaseItem GetTopParent()
        {
            if (IsTopParent)
            {
                return this;
            }

            return GetParents().FirstOrDefault(parent => parent.IsTopParent);
        }

        public virtual IEnumerable<Guid> GetIdsForAncestorQuery()
        {
            return [Id];
        }

        public virtual ItemUpdateType OnMetadataChanged(ILocalizationManager localization)
        {
            var updateType = ItemUpdateType.None;

            var item = this;

            var rating = item.GetParentalRatingScore(localization);
            if (rating is not null)
            {
                if (rating.Score != item.InheritedParentalRatingValue)
                {
                    item.InheritedParentalRatingValue = rating.Score;
                    updateType |= ItemUpdateType.MetadataImport;
                }

                if (rating.SubScore != item.InheritedParentalRatingSubValue)
                {
                    item.InheritedParentalRatingSubValue = rating.SubScore;
                    updateType |= ItemUpdateType.MetadataImport;
                }
            }
            else
            {
                if (item.InheritedParentalRatingValue is not null)
                {
                    item.InheritedParentalRatingValue = null;
                    item.InheritedParentalRatingSubValue = null;
                    updateType |= ItemUpdateType.MetadataImport;
                }
            }

            return updateType;
        }

        /// <summary>
        /// Updates the official rating based on content and returns true or false indicating if it changed.
        /// </summary>
        /// <param name="children">Media children.</param>
        /// <param name="localization">Scores the ratings.</param>
        /// <returns><c>true</c> if the rating was updated; otherwise <c>false</c>.</returns>
        public bool UpdateRatingToItems(IReadOnlyList<BaseItem> children, ILocalizationManager localization)
        {
            var currentOfficialRating = OfficialRating;

            // Gather all possible ratings
            var ratings = children
                .Select(i => i.OfficialRating)
                .Where(i => !string.IsNullOrEmpty(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(rating => (rating, localization.GetRatingScore(rating, GetPreferredMetadataCountryCode())))
                .OrderBy(i => i.Item2 is null ? 1001 : i.Item2.Score)
                .ThenBy(i => i.Item2 is null ? 1001 : i.Item2.SubScore)
                .Select(i => i.rating);

            OfficialRating = ratings.FirstOrDefault() ?? currentOfficialRating;

            return !string.Equals(
                currentOfficialRating ?? string.Empty,
                OfficialRating ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }

        [Obsolete("Use GetExtras plus IItemSortService.Sort instead. Kept for plugin compatibility until Plugin SDK v2.")]
        public IReadOnlyList<BaseItem> GetThemeSongs(User user = null)
        {
#pragma warning disable CS0618
            return GetThemeSongs(user, Array.Empty<(ItemSortBy, SortOrder)>());
#pragma warning restore CS0618
        }

        [Obsolete("Use GetExtras plus IItemSortService.Sort instead. Kept for plugin compatibility until Plugin SDK v2.")]
        public IReadOnlyList<BaseItem> GetThemeSongs(User user, IEnumerable<(ItemSortBy SortBy, SortOrder SortOrder)> orderBy)
        {
#pragma warning disable CS0618 // static LibraryManager.Sort facade left in place pending Folder.GetChildren cascade, see docs/major-rewrite-plan-v13.md § PR49/N
            return LibraryManager.Sort(GetExtras(user).Where(e => e.ExtraType == Tesserafin.Model.Entities.ExtraType.ThemeSong), user, orderBy).ToArray();
#pragma warning restore CS0618
        }

        [Obsolete("Use GetExtras plus IItemSortService.Sort instead. Kept for plugin compatibility until Plugin SDK v2.")]
        public IReadOnlyList<BaseItem> GetThemeVideos(User user = null)
        {
#pragma warning disable CS0618
            return GetThemeVideos(user, Array.Empty<(ItemSortBy, SortOrder)>());
#pragma warning restore CS0618
        }

        [Obsolete("Use GetExtras plus IItemSortService.Sort instead. Kept for plugin compatibility until Plugin SDK v2.")]
        public IReadOnlyList<BaseItem> GetThemeVideos(User user, IEnumerable<(ItemSortBy SortBy, SortOrder SortOrder)> orderBy)
        {
#pragma warning disable CS0618 // static LibraryManager.Sort facade left in place pending Folder.GetChildren cascade, see docs/major-rewrite-plan-v13.md § PR49/N
            return LibraryManager.Sort(GetExtras(user).Where(e => e.ExtraType == Tesserafin.Model.Entities.ExtraType.ThemeVideo), user, orderBy).ToArray();
#pragma warning restore CS0618
        }

        /// <summary>
        /// Gets the ids of the items whose owned extras belong to this item.
        /// </summary>
        /// <returns>An array containing the owner ids.</returns>
        protected virtual Guid[] GetExtraOwnerIds()
        {
            return [Id];
        }

        /// <summary>
        /// Get all extras associated with this item, sorted by <see cref="SortName"/>.
        /// </summary>
        /// <param name="user">The user to apply parental restrictions for, or <c>null</c> to skip restriction checks.</param>
        /// <returns>An enumerable containing the items.</returns>
        public IEnumerable<BaseItem> GetExtras(User user = null)
        {
            return LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                OwnerIds = GetExtraOwnerIds(),
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
            });
        }

        /// <summary>
        /// Get all extras with specific types that are associated with this item.
        /// </summary>
        /// <param name="extraTypes">The types of extras to retrieve.</param>
        /// <param name="user">The user to apply parental restrictions for, or <c>null</c> to skip restriction checks.</param>
        /// <returns>An enumerable containing the extras.</returns>
        public IEnumerable<BaseItem> GetExtras(IReadOnlyCollection<ExtraType> extraTypes, User user = null)
        {
            return LibraryManager.GetItemList(new InternalItemsQuery(user)
            {
                OwnerIds = GetExtraOwnerIds(),
                ExtraTypes = extraTypes.ToArray(),
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
            });
        }

        public virtual long GetRunTimeTicksForPlayState()
        {
            return RunTimeTicks ?? 0;
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is BaseItem baseItem && this.Equals(baseItem);
        }

        /// <inheritdoc />
        public bool Equals(BaseItem other) => other is not null && other.Id.Equals(Id);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Id);
    }
}

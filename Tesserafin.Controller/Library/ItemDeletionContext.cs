using System;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Model.IO;

namespace Tesserafin.Controller.Library;

/// <summary>Explicit dependencies for testing whether an item can be deleted.</summary>
/// <param name="FileSystem">The filesystem used to classify local paths.</param>
/// <param name="Channels">The channel manager, resolved for items from a channel.</param>
/// <param name="Recordings">The recordings in progress.</param>
/// <param name="Libraries">The item's top-level library resolver.</param>
public sealed record ItemDeletionContext(
    IFileSystem FileSystem,
    Lazy<IChannelManager> Channels,
    ActiveRecordings Recordings,
    ICollectionFolderResolver Libraries);

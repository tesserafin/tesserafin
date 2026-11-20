using System;
using Tesserafin.Controller.Channels;
using Tesserafin.Controller.LiveTv;
using Tesserafin.Controller.Persistence;

namespace Tesserafin.Controller.Library;

/// <summary>Explicit catalogue and live-source reads used to describe playable sources.</summary>
/// <param name="Items">The item lookup.</param>
/// <param name="Links">The persisted version links.</param>
/// <param name="Channels">The deferred channel manager, resolved for channel items.</param>
/// <param name="Recordings">The current recordings.</param>
/// <param name="MapFilePath">The configured network substitution for a file path.</param>
public sealed record MediaSourceReadContext(
    IItemLookupService Items,
    ILinkedChildrenService Links,
    Lazy<IChannelManager> Channels,
    ActiveRecordings Recordings,
    Func<string, string> MapFilePath);

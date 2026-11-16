using System;

namespace Tesserafin.Controller.Providers;

/// <summary>The image payload acquired without changing an item or persisting metadata.</summary>
/// <param name="ContentType">Validated image media type.</param>
/// <param name="Bytes">The read-only payload.</param>
public sealed record RemoteImageContent(string ContentType, ReadOnlyMemory<byte> Bytes);

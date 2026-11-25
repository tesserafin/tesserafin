using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Tesserafin.Model.Entities;

namespace Tesserafin.Controller.MediaEncoding;

/// <summary>
/// The set of encoders, decoders, hwaccels and filters compiled into a specific ffmpeg build, as
/// reported by <c>ffmpeg -encoders</c>/<c>-decoders</c>/<c>-hwaccels</c>/<c>-filters</c>. This is
/// build-level support - "is it compiled in" - not a statement about any particular device's
/// runtime capability. Callers gating on hardware acceleration availability (for example
/// <c>EncodingHelper.GetH26xOrAv1Encoder</c>) check this first, before any device-specific check.
/// </summary>
public sealed record FfmpegBuildCapabilities
{
    /// <summary>
    /// An empty snapshot with no encoders, decoders, hwaccels, or filters. Used before ffmpeg has
    /// been probed.
    /// </summary>
    public static readonly FfmpegBuildCapabilities Empty = new();

    /// <summary>
    /// Gets the names of the video/audio encoders this ffmpeg build supports.
    /// </summary>
    public ImmutableArray<string> Encoders { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Gets the names of the video/audio decoders this ffmpeg build supports.
    /// </summary>
    public ImmutableArray<string> Decoders { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Gets the names of the hardware acceleration methods this ffmpeg build supports.
    /// </summary>
    public ImmutableArray<string> Hwaccels { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Gets the names of the filters this ffmpeg build supports.
    /// </summary>
    public ImmutableArray<string> Filters { get; init; } = ImmutableArray<string>.Empty;

    /// <summary>
    /// Gets which optional filter parameters this ffmpeg build's filters support.
    /// </summary>
    public ImmutableDictionary<FilterOptionType, bool> FiltersWithOption { get; init; } = ImmutableDictionary<FilterOptionType, bool>.Empty;

    /// <summary>
    /// Gets which optional bitstream filter parameters this ffmpeg build's filters support.
    /// </summary>
    public ImmutableDictionary<BitStreamFilterOptionType, bool> BitStreamFiltersWithOption { get; init; } = ImmutableDictionary<BitStreamFilterOptionType, bool>.Empty;

    /// <summary>
    /// Gets the disc image kinds this build opens: <see cref="IsoType.BluRay"/> through the
    /// <c>bluray</c> protocol (libbluray), <see cref="IsoType.Dvd"/> through the <c>dvdvideo</c>
    /// demuxer (libdvdnav).
    /// </summary>
    public ImmutableArray<IsoType> DiscImageReaders { get; init; } = ImmutableArray<IsoType>.Empty;

    /// <summary>
    /// Whether the given encoder is supported by this ffmpeg build.
    /// </summary>
    /// <param name="encoder">The encoder name.</param>
    /// <returns><c>true</c> if supported, <c>false</c> otherwise.</returns>
    public bool SupportsEncoder(string encoder) => Encoders.Contains(encoder, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the given decoder is supported by this ffmpeg build.
    /// </summary>
    /// <param name="decoder">The decoder name.</param>
    /// <returns><c>true</c> if supported, <c>false</c> otherwise.</returns>
    public bool SupportsDecoder(string decoder) => Decoders.Contains(decoder, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the given hwaccel is supported by this ffmpeg build.
    /// </summary>
    /// <param name="hwaccel">The hwaccel name.</param>
    /// <returns><c>true</c> if supported, <c>false</c> otherwise.</returns>
    public bool SupportsHwaccel(string hwaccel) => Hwaccels.Contains(hwaccel, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the given filter is supported by this ffmpeg build.
    /// </summary>
    /// <param name="filter">The filter name.</param>
    /// <returns><c>true</c> if supported, <c>false</c> otherwise.</returns>
    public bool SupportsFilter(string filter) => Filters.Contains(filter, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the given optional filter parameter is supported by this ffmpeg build.
    /// </summary>
    /// <param name="option">The filter option.</param>
    /// <returns><c>true</c> if supported, <c>false</c> otherwise.</returns>
    public bool SupportsFilterWithOption(FilterOptionType option) => FiltersWithOption.TryGetValue(option, out var value) && value;

    /// <summary>
    /// Whether the given optional bitstream filter parameter is supported by this ffmpeg build.
    /// </summary>
    /// <param name="option">The bitstream filter option.</param>
    /// <returns><c>true</c> if supported, <c>false</c> otherwise.</returns>
    public bool SupportsBitStreamFilterWithOption(BitStreamFilterOptionType option) => BitStreamFiltersWithOption.TryGetValue(option, out var value) && value;

    /// <summary>
    /// Whether this build opens a disc image of the given kind.
    /// </summary>
    /// <param name="isoType">The disc image kind.</param>
    /// <returns><c>true</c> if supported, <c>false</c> otherwise.</returns>
    public bool SupportsDiscImage(IsoType isoType) => DiscImageReaders.Contains(isoType);

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="Encoders"/> replaced.
    /// </summary>
    /// <param name="encoders">The new encoder list.</param>
    /// <returns>The updated snapshot.</returns>
    public FfmpegBuildCapabilities WithEncoders(IEnumerable<string> encoders) => this with { Encoders = [.. encoders] };

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="Decoders"/> replaced.
    /// </summary>
    /// <param name="decoders">The new decoder list.</param>
    /// <returns>The updated snapshot.</returns>
    public FfmpegBuildCapabilities WithDecoders(IEnumerable<string> decoders) => this with { Decoders = [.. decoders] };

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="Hwaccels"/> replaced.
    /// </summary>
    /// <param name="hwaccels">The new hwaccel list.</param>
    /// <returns>The updated snapshot.</returns>
    public FfmpegBuildCapabilities WithHwaccels(IEnumerable<string> hwaccels) => this with { Hwaccels = [.. hwaccels] };

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="Filters"/> replaced.
    /// </summary>
    /// <param name="filters">The new filter list.</param>
    /// <returns>The updated snapshot.</returns>
    public FfmpegBuildCapabilities WithFilters(IEnumerable<string> filters) => this with { Filters = [.. filters] };

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="FiltersWithOption"/> replaced.
    /// </summary>
    /// <param name="filtersWithOption">The new filter-option map.</param>
    /// <returns>The updated snapshot.</returns>
    public FfmpegBuildCapabilities WithFiltersWithOption(IDictionary<FilterOptionType, bool> filtersWithOption)
        => this with { FiltersWithOption = filtersWithOption.ToImmutableDictionary() };

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="BitStreamFiltersWithOption"/> replaced.
    /// </summary>
    /// <param name="bitStreamFiltersWithOption">The new bitstream filter-option map.</param>
    /// <returns>The updated snapshot.</returns>
    public FfmpegBuildCapabilities WithBitStreamFiltersWithOption(IDictionary<BitStreamFilterOptionType, bool> bitStreamFiltersWithOption)
        => this with { BitStreamFiltersWithOption = bitStreamFiltersWithOption.ToImmutableDictionary() };

    /// <summary>
    /// Returns a copy of this snapshot with <see cref="DiscImageReaders"/> replaced.
    /// </summary>
    /// <param name="discImageReaders">The disc image kinds the build opens.</param>
    /// <returns>The updated snapshot.</returns>
    public FfmpegBuildCapabilities WithDiscImageReaders(IEnumerable<IsoType> discImageReaders) => this with { DiscImageReaders = [.. discImageReaders] };
}

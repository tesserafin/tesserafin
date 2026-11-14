using System;
using System.Collections.Generic;
using System.Linq;
using Tesserafin.Data.Enums;
using Xunit;

namespace Tesserafin.Model.Tests.Users;

/// <summary>
/// <see cref="PermissionKind"/> values are persisted as integers in the <c>Permissions</c> table.
/// Renumbering a member silently re-points every stored row at a different permission, which is a
/// privilege change no test would otherwise notice. New members may only be appended.
/// </summary>
public static class PermissionKindStabilityTests
{
    private static readonly Dictionary<PermissionKind, int> _pinnedValues = new()
    {
        [PermissionKind.IsAdministrator] = 0,
        [PermissionKind.IsHidden] = 1,
        [PermissionKind.IsDisabled] = 2,
        [PermissionKind.EnableSharedDeviceControl] = 3,
        [PermissionKind.EnableRemoteAccess] = 4,
        [PermissionKind.EnableLiveTvManagement] = 5,
        [PermissionKind.EnableLiveTvAccess] = 6,
        [PermissionKind.EnableMediaPlayback] = 7,
        [PermissionKind.EnableAudioPlaybackTranscoding] = 8,
        [PermissionKind.EnableVideoPlaybackTranscoding] = 9,
        [PermissionKind.EnableContentDeletion] = 10,
        [PermissionKind.EnableContentDownloading] = 11,
        [PermissionKind.EnableSyncTranscoding] = 12,
        [PermissionKind.EnableMediaConversion] = 13,
        [PermissionKind.EnableAllDevices] = 14,
        [PermissionKind.EnableAllChannels] = 15,
        [PermissionKind.EnableAllFolders] = 16,
        [PermissionKind.EnablePublicSharing] = 17,
        [PermissionKind.EnableRemoteControlOfOtherUsers] = 18,
        [PermissionKind.EnablePlaybackRemuxing] = 19,
        [PermissionKind.ForceRemoteSourceTranscoding] = 20,
        [PermissionKind.EnableCollectionManagement] = 21,
        [PermissionKind.EnableSubtitleManagement] = 22,
        [PermissionKind.EnableLyricManagement] = 23,
        [PermissionKind.EnableContentPackManagement] = 24
    };

    public static TheoryData<PermissionKind, int> PinnedValues()
    {
        var data = new TheoryData<PermissionKind, int>();
        foreach (var (kind, value) in _pinnedValues)
        {
            data.Add(kind, value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PinnedValues))]
    public static void MemberKeepsItsStoredValue(PermissionKind kind, int expected)
    {
        Assert.Equal(expected, (int)kind);
    }

    [Fact]
    public static void NoMemberIsAddedWithoutBeingPinnedHere()
    {
        var declared = Enum.GetValues<PermissionKind>().ToHashSet();

        Assert.Equal(declared.Count, _pinnedValues.Count);
        Assert.All(declared, kind => Assert.Contains(kind, _pinnedValues.Keys));
    }
}

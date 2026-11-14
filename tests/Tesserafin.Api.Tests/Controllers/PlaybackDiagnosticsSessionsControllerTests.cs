using System;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Tesserafin.Api.Controllers;
using Tesserafin.Api.Models.PlaybackSessionDtos;
using Tesserafin.Api.Tests.Models.PlaybackSessionDtos;
using Tesserafin.Common.Api;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Model.Dlna;
using Tesserafin.Model.Session;
using Tesserafin.Playback.Shadow;
using Xunit;

namespace Tesserafin.Api.Tests.Controllers;

public class PlaybackDiagnosticsSessionsControllerTests
{
    private readonly Mock<IPlaybackSessionManager> _playbackSessionManager = new();
    private readonly Mock<IShadowDiagnosticsStore> _diagnosticsStore = new();
    private readonly Mock<IPlaybackLiveWiringDiagnosticsStore> _liveWiringDiagnosticsStore = new();

    private PlaybackDiagnosticsSessionsController CreateController()
        => new(_playbackSessionManager.Object, _diagnosticsStore.Object, _liveWiringDiagnosticsStore.Object);

    /// <summary>
    /// This admin surface must never share the client controller's authorization scope: it
    /// requires elevation at the class level, same as the old <c>System/PlaybackSessions</c> GET
    /// did before the split (docs/pr92-design-playback-api-and-diagnostics.md §2).
    /// </summary>
    [Fact]
    public void Controller_RequiresElevation()
    {
        var attribute = typeof(PlaybackDiagnosticsSessionsController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single();

        Assert.Equal(Policies.RequiresElevation, attribute.Policy);
    }

    [Fact]
    public void GetPlaybackSessions_ReturnsListItemsWithHasDiagnosticFlags()
    {
        var withDiagnostic = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        var withoutDiagnostic = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Audio, null, null, new PlaybackPlan(PlayMethod.Transcode, TranscodeReason.AudioCodecNotSupported), default, default);
        _playbackSessionManager.Setup(m => m.GetAll()).Returns([withDiagnostic, withoutDiagnostic]);

        var record = FakeShadowDiagnosticRecordFactory.Create();
        _diagnosticsStore.Setup(s => s.TryGet(withDiagnostic.Id, out record)).Returns(true);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(withoutDiagnostic.Id, out none)).Returns(false);

        var result = CreateController().GetPlaybackSessions();

        var items = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<PlaybackSessionListItem>>(
            Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(2, items.Count);
        Assert.True(items.Single(i => i.Session.Id.Equals(withDiagnostic.Id.Value)).HasDiagnostic);
        Assert.False(items.Single(i => i.Session.Id.Equals(withoutDiagnostic.Id.Value)).HasDiagnostic);
    }

    /// <summary>
    /// PR114: a session tracked directly (no <see cref="PlaybackSessionRequest"/> attached, the
    /// <c>IPlaybackSessionManager.Track</c> path) must yield <see langword="null"/> list-item
    /// <c>ItemId</c>/<c>DeviceId</c> rather than throw or fabricate a value, while a session planned
    /// from a real request surfaces both raw identifiers as-is.
    /// </summary>
    [Fact]
    public void GetPlaybackSessions_PopulatesItemIdAndDeviceIdFromRequestWhenPresent()
    {
        var itemId = Guid.NewGuid();
        const string deviceId = "device-abc";
        var options = new MediaOptions { ItemId = itemId, DeviceId = deviceId, Profile = new DeviceProfile() };
        var withRequest = new PlaybackSession(
            PlaybackSessionId.NewId(),
            PlaybackMediaKind.Video,
            null,
            new PlaybackSessionRequest(PlaybackMediaKind.Video, options),
            new PlaybackPlan(PlayMethod.DirectPlay, default),
            default,
            default);
        var trackedOnly = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.GetAll()).Returns([withRequest, trackedOnly]);

        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(It.IsAny<PlaybackSessionId>(), out none)).Returns(false);

        var result = CreateController().GetPlaybackSessions();

        var items = Assert.IsAssignableFrom<System.Collections.Generic.IReadOnlyList<PlaybackSessionListItem>>(
            Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        var requestedItem = items.Single(i => i.Session.Id.Equals(withRequest.Id.Value));
        Assert.Equal(itemId, requestedItem.ItemId);
        Assert.Equal(deviceId, requestedItem.DeviceId);

        var trackedItem = items.Single(i => i.Session.Id.Equals(trackedOnly.Id.Value));
        Assert.Null(trackedItem.ItemId);
        Assert.Null(trackedItem.DeviceId);
    }

    [Fact]
    public void GetPlaybackSession_ExistingSessionWithDiagnostic_ReturnsFullDetail()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        var record = FakeShadowDiagnosticRecordFactory.Create();
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out record)).Returns(true);

        var result = CreateController().GetPlaybackSession(session.Id);

        var detail = Assert.IsAssignableFrom<PlaybackDiagnosticDetail>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(session.Id.Value, detail.Id);
        Assert.NotNull(detail.RequestContext);
        Assert.NotNull(detail.Capabilities);
        Assert.NotNull(detail.SourceSnapshot);
        Assert.NotNull(detail.Reasoning);
        Assert.NotNull(detail.Comparison);
        Assert.NotNull(detail.Comparison!.DivergenceSummary);
        Assert.Equal(record.Decision.Method, detail.Comparison.V2Method);
    }

    [Fact]
    public void GetPlaybackSession_ExistingSessionWithoutDiagnostic_ReturnsBaseOnlyDetail()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out none)).Returns(false);

        var result = CreateController().GetPlaybackSession(session.Id);

        var detail = Assert.IsAssignableFrom<PlaybackDiagnosticDetail>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Equal(session.Id.Value, detail.Id);
        Assert.Null(detail.RequestContext);
        Assert.Null(detail.Capabilities);
        Assert.Null(detail.SourceSnapshot);
        Assert.Null(detail.Reasoning);
        Assert.Null(detail.Comparison);
    }

    /// <summary>
    /// PR115c: the live-wiring outcome retained by <c>MediaInfoHelper</c> for a session must reach
    /// the mapped diagnostic detail, independent of whether a shadow diagnostic was ever retained
    /// for that same session.
    /// </summary>
    [Fact]
    public void GetPlaybackSession_WithRecordedLiveWiringOutcome_DetailIncludesIt()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out none)).Returns(false);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Fallback(PlaybackLiveFallbackReason.KillSwitch, DateTimeOffset.UnixEpoch);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var result = CreateController().GetPlaybackSession(session.Id);

        var detail = Assert.IsAssignableFrom<PlaybackDiagnosticDetail>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.NotNull(detail.LiveWiring);
        Assert.False(detail.LiveWiring!.ServedByV2);
        Assert.Equal(PlaybackLiveFallbackReason.KillSwitch, detail.LiveWiring.FallbackReason);
    }

    [Fact]
    public void GetPlaybackSession_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().GetPlaybackSession(id);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// PR113b: real, observed lifecycle events retrieved from the store must reach the mapped
    /// detail's timeline, appended after the always-present <c>Created</c>/<c>Updated</c> entries.
    /// </summary>
    [Fact]
    public void GetPlaybackSession_WithRecordedEvents_TimelineIncludesThem()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out none)).Returns(false);
        var lifecycleEvent = new PlaybackLifecycleEvent("PlaybackStarted", DateTimeOffset.UnixEpoch);
        _diagnosticsStore.Setup(s => s.GetEvents(session.Id)).Returns([lifecycleEvent]);

        var result = CreateController().GetPlaybackSession(session.Id);

        var detail = Assert.IsAssignableFrom<PlaybackDiagnosticDetail>(Assert.IsAssignableFrom<OkObjectResult>(result.Result).Value);
        Assert.Contains(detail.Timeline, e => e.Stage == "PlaybackStarted" && e.At == DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void ExportFixture_UnknownSession_ReturnsNotFound()
    {
        var id = PlaybackSessionId.NewId();
        _playbackSessionManager.Setup(m => m.Get(id)).Returns((PlaybackSession?)null);

        var result = CreateController().ExportFixture(id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public void ExportFixture_SessionWithoutRetainedDiagnostic_ReturnsUnprocessableEntity()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        ShadowDiagnosticRecord? none = null;
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out none)).Returns(false);

        var result = CreateController().ExportFixture(session.Id);

        Assert.IsType<UnprocessableEntityResult>(result);
    }

    /// <summary>
    /// PR117 (design doc §1.4/§4.3): a type-level, reflection-based complement to the JSON test
    /// below - <see cref="PlaybackSessionStreamDescriptor"/> must never appear as a property type on
    /// either admin DTO, regardless of property naming, so a future rename can't defeat the JSON
    /// substring check.
    /// </summary>
    [Fact]
    public void PlaybackSessionListItemAndDiagnosticDetail_NeverReferenceStreamDescriptorType()
    {
        foreach (var type in new[] { typeof(PlaybackSessionListItem), typeof(PlaybackDiagnosticDetail) })
        {
            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                Assert.NotEqual(typeof(PlaybackSessionStreamDescriptor), property.PropertyType);
            }
        }
    }

    /// <summary>
    /// PR117 (docs/pr116d-url-contract-design.md §4.3, mandatory test): a structural, JSON-level
    /// non-leak guard - <see cref="PlaybackSessionListItem"/> (list) and
    /// <see cref="PlaybackDiagnosticDetail"/> (detail) must never carry the
    /// <see cref="PlaybackSessionStreamDescriptor.Url"/>/<see cref="PlaybackSessionStreamDescriptor.SubtitleUrl"/>
    /// property names, at any nesting level - the descriptor is composed ONLY into the new
    /// <c>GET Playback/Sessions/{id}/Stream</c> response, never into either admin surface (§1.4's
    /// leak - a URL there would hand any administrator every user's access token). A silent
    /// regression here (someone later wraps <see cref="PlaybackSessionStreamDescriptor"/> into one of
    /// these two types) would otherwise go undetected without this dedicated assertion.
    /// </summary>
    [Fact]
    public void DiagnosticsSurface_SerializedListAndDetail_NeverCarryStreamDescriptorFields()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, "play-session-1", null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.GetAll()).Returns([session]);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        var record = FakeShadowDiagnosticRecordFactory.Create();
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out record)).Returns(true);
        PlaybackLiveWiringOutcome? outcome = PlaybackLiveWiringOutcome.Served(DateTimeOffset.UtcNow);
        _liveWiringDiagnosticsStore.Setup(s => s.TryGet(session.Id, out outcome)).Returns(true);

        var listResult = CreateController().GetPlaybackSessions();
        var listJson = JsonSerializer.Serialize(Assert.IsAssignableFrom<OkObjectResult>(listResult.Result).Value);

        var detailResult = CreateController().GetPlaybackSession(session.Id);
        var detailJson = JsonSerializer.Serialize(Assert.IsAssignableFrom<OkObjectResult>(detailResult.Result).Value);

        foreach (var json in new[] { listJson, detailJson })
        {
            Assert.DoesNotContain("\"Url\":", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"SubtitleUrl\":", json, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(PlaybackSessionStreamDescriptor), json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExportFixture_SessionWithRetainedDiagnostic_ReturnsCamelCaseJsonContent()
    {
        var session = new PlaybackSession(PlaybackSessionId.NewId(), PlaybackMediaKind.Video, null, null, new PlaybackPlan(PlayMethod.DirectPlay, default), default, default);
        _playbackSessionManager.Setup(m => m.Get(session.Id)).Returns(session);
        var record = FakeShadowDiagnosticRecordFactory.Create();
        _diagnosticsStore.Setup(s => s.TryGet(session.Id, out record)).Returns(true);

        var result = Assert.IsType<ContentResult>(CreateController().ExportFixture(session.Id));

        Assert.Equal("application/json", result.ContentType);
        Assert.Contains("\"fixtureVersion\"", result.Content, StringComparison.Ordinal);
        Assert.Contains("\"engineVersion\"", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FixtureVersion\"", result.Content, StringComparison.Ordinal);
    }
}

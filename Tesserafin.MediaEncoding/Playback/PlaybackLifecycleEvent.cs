using System;

namespace Tesserafin.MediaEncoding.Playback;

/// <summary>
/// One real, observed lifecycle event for a tracked playback session (PR113b): a signal actually
/// received from <see cref="Tesserafin.Controller.MediaEncoding.ITranscodeManager"/> or
/// <see cref="Tesserafin.Controller.Session.ISessionManager"/>, stamped with the wall-clock time it was
/// received - never a value approximated or backfilled from some other timestamp. Retained by
/// the <see cref="IPlaybackSessionDiagnostics"/> observation sink, independently of planning
/// comparisons, so the admin timeline (docs/pr92-design-playback-api-and-diagnostics.md
/// §4.3) is populated even when shadow mode itself is disabled.
/// </summary>
/// <param name="Stage">
/// The stage name, for example <c>"FfmpegStarted"</c>, <c>"PlaybackStarted"</c>, or
/// <c>"PlaybackStopped"</c>. Free-form; the admin API layer maps it onto its own timeline entry's
/// stage name verbatim.
/// </param>
/// <param name="At">The real time this event was received, captured at the moment of observation.</param>
/// <param name="RequestId">
/// Issue #42: the correlation id of the HTTP request in flight when this event was observed, or
/// <c>null</c> when none was — the common case for the two signals that arrive from ffmpeg/session
/// callbacks rather than from a request. Optional and trailing, so every existing 2-argument
/// construction keeps compiling and keeps meaning exactly what it meant.
/// <para>
/// Per EVENT, not per session: the grouping key remains the <see cref="Tesserafin.Controller.MediaEncoding.PlaybackSessionId"/>
/// this event is filed under. Several events of one session will normally carry several different
/// request ids, or none. It is deliberately not an attempt identifier — see issue #43 for
/// <c>PlaybackAttemptId</c>, the value that IS stable across a whole attempt.
/// </para>
/// </param>
public sealed record PlaybackLifecycleEvent(string Stage, DateTimeOffset At, string? RequestId = null);

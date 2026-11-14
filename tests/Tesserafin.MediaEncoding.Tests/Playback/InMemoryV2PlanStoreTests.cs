using System;
using Tesserafin.Controller.MediaEncoding;
using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Decision;
using Tesserafin.Playback.Engine;
using Xunit;

namespace Tesserafin.MediaEncoding.Tests.Playback;

/// <summary>
/// Tests for <see cref="InMemoryV2PlanStore"/> (PR115a): the ambient capture handshake and the
/// retention lifecycle, mirroring the diagnostics store tests - same
/// mechanics, but this store is execution authority rather than observability, so its correctness
/// is what keeps a canary session on the engine its configuration granted it.
/// </summary>
public class InMemoryV2PlanStoreTests
{
    [Fact]
    public void Publish_WithoutOpenScope_IsSilentlyDropped()
    {
        var store = new InMemoryV2PlanStore();

        store.Publish(BuildRecord());

        Assert.Null(store.TakeCaptured());
    }

    [Fact]
    public void Publish_InsideOpenScope_IsCaptured()
    {
        var store = new InMemoryV2PlanStore();
        var record = BuildRecord();

        using (store.BeginCapture())
        {
            store.Publish(record);
            Assert.Same(record, store.TakeCaptured());
        }
    }

    [Fact]
    public void BeginCapture_FreshScope_SeesNoStaleCapture()
    {
        var store = new InMemoryV2PlanStore();

        using (store.BeginCapture())
        {
            store.Publish(BuildRecord());
        }

        using (store.BeginCapture())
        {
            Assert.Null(store.TakeCaptured());
        }
    }

    [Fact]
    public void BeginCapture_Nested_RestoresOuterScopeStateOnDispose()
    {
        var store = new InMemoryV2PlanStore();
        var outer = BuildRecord();

        using (store.BeginCapture())
        {
            store.Publish(outer);

            using (store.BeginCapture())
            {
                Assert.Null(store.TakeCaptured());
                store.Publish(BuildRecord());
            }

            Assert.Same(outer, store.TakeCaptured());
        }
    }

    [Fact]
    public void Attach_TryGet_Remove_Lifecycle()
    {
        var store = new InMemoryV2PlanStore();
        var id = PlaybackSessionId.NewId();
        var record = BuildRecord();

        Assert.False(store.TryGet(id, out _));

        store.Attach(id, record);
        Assert.True(store.TryGet(id, out var retained));
        Assert.Same(record, retained);

        store.Remove(id);
        Assert.False(store.TryGet(id, out _));
    }

    [Fact]
    public void Attach_SameId_ReplacesPreviousRecord()
    {
        var store = new InMemoryV2PlanStore();
        var id = PlaybackSessionId.NewId();
        var replacement = BuildRecord();

        store.Attach(id, BuildRecord());
        store.Attach(id, replacement);

        Assert.True(store.TryGet(id, out var retained));
        Assert.Same(replacement, retained);
    }

    [Fact]
    public void Remove_UnknownId_IsANoOp()
    {
        var store = new InMemoryV2PlanStore();

        store.Remove(PlaybackSessionId.NewId());
    }

    /// <summary>
    /// A record whose decision the builder refused carries a null plan - the store must retain it
    /// as-is ("v2 was authoritative but produced nothing executable"), never treat null-plan as
    /// nothing-to-retain.
    /// </summary>
    [Fact]
    public void Attach_RecordWithNullExecutionPlan_IsRetained()
    {
        var store = new InMemoryV2PlanStore();
        var id = PlaybackSessionId.NewId();

        store.Attach(id, BuildRecord());

        Assert.True(store.TryGet(id, out var retained));
        Assert.Null(retained!.ExecutionPlan);
    }

    private static V2PlanRecord BuildRecord()
    {
        var notViable = PlaybackDecision.NotViable(
            PlaybackMethod.Transcode,
            new ReasonNode(ReasonCode.NoViablePlan, ReasonOutcome.Rejected, ReasonSubject.Method(), null, []),
            engineVersion: PlaybackEngine.EngineVersion);
        return new V2PlanRecord(notViable, ExecutionPlan: null, DateTimeOffset.UtcNow);
    }
}

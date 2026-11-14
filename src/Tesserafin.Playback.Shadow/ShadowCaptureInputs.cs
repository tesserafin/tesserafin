using Tesserafin.MediaEncoding.Playback;
using Tesserafin.Playback.Contract.Diagnostics;
using Tesserafin.Playback.Decision;

namespace Tesserafin.Playback.Shadow;

/// <summary>
/// Issue #75: the request-scoped facts the shadow run cannot recover on its own, handed to it
/// through the ambient capture scope <see cref="IShadowDiagnosticsStore.BeginCapture(ShadowCaptureInputs?)"/>
/// already opens around a synchronous planning call - deliberately reusing that existing channel
/// rather than adding a second one.
/// </summary>
/// <remarks>
/// Both members are optional, and both are absent on the legacy <c>MediaInfoHelper</c> path, which
/// has no v2 request body: the diagnostic is then simply not produced, rather than produced with
/// invented values.
/// </remarks>
/// <param name="DeclaredCapabilities">
/// The <see cref="ClientCapabilities"/> the client sent verbatim in the v2 request body - the
/// "before" side of issue #75's mapping comparison. Never retained: only counts and presence flags
/// derived from it survive into <see cref="ShadowDiagnosticRecord.ContractMapping"/>.
/// </param>
/// <param name="PayloadSizeBytes">
/// The request's <c>Content-Length</c> header value, or <see langword="null"/> when the header is
/// absent. Read from the header only - the header read is not what buffers the body.
/// </param>
/// <param name="StructuralScan">
/// Issue #75 slice 75b: the result of the bounded, single-pass structural scan of the raw request
/// body, or <see langword="null"/> when the request was not scanned. The scan runs on the Tesserafin.Api
/// side, strictly before model binding and ONLY behind the same shadow gate + sampling this capture
/// scope is opened under, and its closed result rides in through this member so the shadow run can
/// fold it into the retained <see cref="ContractMappingDiagnostic"/>. Nothing here can carry a
/// client key or value - see <see cref="ContractStructuralScan"/>.
/// </param>
public sealed record ShadowCaptureInputs(
    ClientCapabilities? DeclaredCapabilities,
    long? PayloadSizeBytes,
    ContractStructuralScan? StructuralScan = null);

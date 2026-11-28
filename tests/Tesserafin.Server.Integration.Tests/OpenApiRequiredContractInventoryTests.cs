using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// Pins the complete effect of <c>PlaybackDecisionRequiredSchemaFilter</c> on the published
    /// contract (issue #51).
    ///
    /// <para>
    /// The expected sets below are written out by hand from the C# declarations in
    /// <c>src/Tesserafin.Playback.Decision/</c>, deliberately <b>not</b> derived by re-running the
    /// filter's own rule. A test that recomputed the rule would pass for any rule; this one fails
    /// if the filter's behaviour ever stops matching the reviewed inventory in
    /// <c>docs/pr-openapi-required-audit.md</c> §3 (approach C: 29 entries, 13 schemas; 18 entries, 8 schemas since the diagnostic snapshot schemas went with the shadow comparison, tesserafin#129 step 7).
    /// </para>
    /// </summary>
    public sealed class OpenApiRequiredContractInventoryTests
    {
        private const string SchemaPrefix = "PlaybackDecision";

        /// <summary>
        /// Number of schemas outside the <c>Tesserafin.Playback.Decision</c> namespace that declare
        /// <c>required</c>. All of them come from an explicit <c>[Required]</c> attribute, so the
        /// namespace filter must leave this figure untouched. Adding a brand new schema that
        /// declares <c>required</c> does move it, and moving it then is the intended maintenance:
        /// what this pins is that the filter never adds or removes <c>required</c> on a schema
        /// outside its namespace.
        /// </summary>
        /// <remarks>
        /// 27 before the content pack surface. The four content pack request and response schemas
        /// each declare a required member of their own, which is additive and does not touch any
        /// existing schema's <c>required</c> array.
        ///
        /// 31 before R1-P (#248). <c>RemoteAccessDiagnosticsRequestDto</c> declares
        /// <c>IPv4Policy</c> and <c>IPv6Policy</c> required through explicit <c>[Required]</c>
        /// attributes, which is the same additive shape: a census of the schemas outside the
        /// namespace shows exactly one addition and no removal, and no existing schema's
        /// <c>required</c> array differs from the frozen base.
        ///
        /// 32 before #153-A0. <c>PlaybackCapabilityRequestDto</c> declares <c>PlaySessionId</c> and
        /// <c>Scopes</c> required through explicit <c>[Required]</c> attributes — again the additive
        /// shape: one addition, no removal, and no existing schema's <c>required</c> array moves.
        /// The census is deliberately a single number rather than a list, so an addition has to be
        /// noticed and accounted for here rather than sliding in unremarked.
        /// 32 again since tesserafin#129 step 7: <c>CastReceiverApplication</c> (required
        /// <c>Id</c>, <c>Name</c>) left the contract with the Cast fields; no remaining schema's
        /// <c>required</c> array moves.
        /// </remarks>
        private const int RequiredOutsideNamespace = 32;

        /// <summary>
        /// The 18 members, across 8 schemas, that are primary-constructor parameters of
        /// non-nullable reference type with no default value - and therefore the exact set the
        /// server answers 400 for when absent.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string[]> _expectedRequired =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["PlaybackDecisionAudioCodecCapability"] = ["Codec"],
                ["PlaybackDecisionClientCapabilities"] = ["Decode", "OutputProfiles"],
                ["PlaybackDecisionDecodeCapabilities"] = ["AudioCodecs", "DirectPlayProfiles", "SubtitleDelivery", "VideoCodecs"],
                ["PlaybackDecisionDecodeProfile"] = ["AudioCodecs", "Containers", "VideoCodecs"],
                ["PlaybackDecisionPlaybackConstraints"] = ["PreferredSubtitleLanguages"],
                ["PlaybackDecisionPlaybackOutputProfile"] = ["AudioCodecs", "Container", "VideoCodecs"],
                ["PlaybackDecisionSubtitleCapability"] = ["Format"],
                ["PlaybackDecisionVideoCodecCapability"] = ["Codec", "Profiles", "VideoRangeTypes"],
            };

        /// <summary>
        /// The object schemas of the same namespace that must carry <b>no</b> <c>required</c> array,
        /// because every one of their primary-constructor parameters is either a value type or
        /// nullable. Listing them explicitly is what makes the inventory above exhaustive: a schema
        /// silently skipped by the filter would show up here rather than go unnoticed.
        /// </summary>
        private static readonly string[] _expectedWithoutRequired =
        [
            "PlaybackDecisionOutputSpec",
            "PlaybackDecisionResolution",
            "PlaybackDecisionSelectedStreams",
            "PlaybackDecisionSelectedSubtitle",
        ];

        [Fact]
        public void PlaybackDecisionSchemas_DeclareExactlyTheProvablyRequiredMembers()
        {
            var schemas = ReadSchemas();

            var actual = schemas.EnumerateObject()
                .Where(p => p.Name.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                .Where(p => p.Value.TryGetProperty("required", out _))
                .ToDictionary(
                    p => p.Name,
                    p => p.Value.GetProperty("required")
                        .EnumerateArray()
                        .Select(e => e.GetString())
                        .OrderBy(e => e, StringComparer.Ordinal)
                        .ToArray(),
                    StringComparer.Ordinal);

            Assert.Equal(
                _expectedRequired.Keys.OrderBy(k => k, StringComparer.Ordinal),
                actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

            foreach (var (schemaId, expectedMembers) in _expectedRequired)
            {
                Assert.Equal(expectedMembers.OrderBy(m => m, StringComparer.Ordinal), actual[schemaId]);
            }

            Assert.Equal(18, actual.Sum(entry => entry.Value.Length));
            Assert.Equal(8, actual.Count);
        }

        [Fact]
        public void PlaybackDecisionSchemas_WithoutAProvableMember_DeclareNoRequired()
        {
            var schemas = ReadSchemas();

            foreach (var schemaId in _expectedWithoutRequired)
            {
                Assert.True(
                    schemas.TryGetProperty(schemaId, out var schema),
                    FormattableString.Invariant($"Schema '{schemaId}' is absent from {OpenApiContract.SpecRelativePath}."));

                Assert.False(
                    schema.TryGetProperty("required", out _),
                    FormattableString.Invariant(
                        $"{schemaId} declares 'required', but every primary-constructor parameter it has is a value type or nullable, so the server accepts them all absent."));
            }
        }

        /// <summary>
        /// The control the whole approach rests on: members the server accepts absent must not be
        /// declared required. A value type binds to its default and a nullable member binds to
        /// <see langword="null"/>; in neither case does MVC's implicit <c>[Required]</c> fire.
        /// </summary>
        /// <param name="schemaId">The schema to inspect.</param>
        /// <param name="propertyName">The member that must stay optional.</param>
        [Theory]
        // Non-nullable value types (bool/enum): absent yields false / the zero enum member.
        [InlineData("PlaybackDecisionDecodeCapabilities", "SupportsHls")]
        [InlineData("PlaybackDecisionDecodeCapabilities", "SupportsDash")]
        [InlineData("PlaybackDecisionDecodeProfile", "Type")]
        [InlineData("PlaybackDecisionPlaybackOutputProfile", "Protocol")]
        [InlineData("PlaybackDecisionPlaybackConstraints", "AllowDirectPlay")]
        [InlineData("PlaybackDecisionPlaybackConstraints", "StartTimeTicks")]
        // Nullable reference / value types: absent and null are indistinguishable, both accepted.
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxLevel")]
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxBitDepth")]
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxResolution")]
        [InlineData("PlaybackDecisionVideoCodecCapability", "MaxBitrate")]
        public void MembersTheServerAcceptsAbsent_AreNotRequired(string schemaId, string propertyName)
        {
            var schemas = ReadSchemas();

            Assert.True(
                schemas.TryGetProperty(schemaId, out var schema),
                FormattableString.Invariant($"Schema '{schemaId}' is absent from {OpenApiContract.SpecRelativePath}."));

            Assert.True(
                schema.GetProperty("properties").TryGetProperty(propertyName, out _),
                FormattableString.Invariant($"{schemaId} has no property '{propertyName}' at all."));

            var required = schema.TryGetProperty("required", out var requiredElement)
                ? requiredElement.EnumerateArray().Select(e => e.GetString()).ToList()
                : [];

            Assert.False(
                required.Contains(propertyName, StringComparer.Ordinal),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{schemaId}.{propertyName} is declared required, but the server accepts it absent - it is a value type or a nullable member, so MVC's implicit [Required] never fires. Declaring it required would break clients that legitimately omit it."));
        }

        /// <summary>
        /// The filter is scoped to one namespace by exact match. Sibling vocabularies and every
        /// other schema in the document must be byte-unaffected, so the population of schemas that
        /// declare <c>required</c> outside the namespace stays exactly what explicit
        /// <c>[Required]</c> attributes produced.
        /// </summary>
        [Fact]
        public void SchemasOutsideTheNamespace_KeepTheirRequiredArraysUnchanged()
        {
            var schemas = ReadSchemas();

            var outside = schemas.EnumerateObject()
                .Where(p => !p.Name.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                .Count(p => p.Value.TryGetProperty("required", out _));

            Assert.Equal(RequiredOutsideNamespace, outside);
        }

        private static JsonElement ReadSchemas()
        {
            var specPath = Path.Combine(
                OpenApiContract.FindRepositoryRoot(),
                OpenApiContract.SpecRelativePath);

            Assert.True(
                File.Exists(specPath),
                FormattableString.Invariant(
                    $"{OpenApiContract.SpecRelativePath} is missing. Generate it with: {OpenApiContract.RegenerateCommand}"));

            using var document = JsonDocument.Parse(File.ReadAllBytes(specPath));

            // JsonDocument owns the backing buffer, so hand back a detached copy.
            return document.RootElement.GetProperty("components").GetProperty("schemas").Clone();
        }
    }
}

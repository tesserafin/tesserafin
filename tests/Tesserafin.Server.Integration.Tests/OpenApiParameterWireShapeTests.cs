using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// Holds the published contract to the <b>wire</b> shape of its parameters rather than the CLR
    /// shape of the arguments they bind to — issue #226.
    ///
    /// <para>
    /// Two families of parameter were described in a way no conforming generator could transcribe
    /// into a request this server accepts, and neither failed loudly:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// eight <c>streamOptions</c> query parameters declared <c>style: deepObject</c> with no
    /// <c>explode</c>. OpenAPI 3.0.4 defaults <c>explode</c> to <see langword="false"/> for every
    /// style but <c>form</c>, and then states that <c>deepObject</c> with <see langword="false"/> is
    /// <b>undefined</b>. A generator resolving that to the nearest defined reading emits
    /// <c>?streamOptions=k,v</c>; the model binder answers HTTP 200 and binds an <b>empty</b>
    /// dictionary, so the caller's options disappear with no error at any layer.
    /// </description></item>
    /// <item><description>
    /// five <c>PlaybackSessionId</c> path parameters emitted as a <c>type: object</c> component with
    /// a <c>Value</c> property, because Swashbuckle reflects over the record struct's properties. The
    /// binder treats the type as a simple type (it is <c>IParsable&lt;T&gt;</c>) and answers 400 to
    /// every object serialization the contract implied.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// The assertions resolve <c>$ref</c> and <c>allOf</c> through
    /// <see cref="OpenApiParameterResolution"/> instead of matching text. That is the whole point: on
    /// the defective document the word <c>object</c> was two indirections away from the parameter,
    /// so a snippet-matching test would have been green throughout.
    /// </para>
    ///
    /// <para>
    /// These read the committed <c>openapi/openapi.json</c>, which
    /// <c>OpenApiContractTests.CommittedContract_MatchesRunningServer</c> already pins to what the
    /// server generates. <see cref="OpenApiParameterWireShapeLiveTests"/> re-asserts the two central
    /// invariants against a freshly booted server, so the correction is proven to live in the
    /// generation pipeline and not in a committed file somebody could hand-edit.
    /// </para>
    /// </summary>
    public sealed class OpenApiParameterWireShapeTests
    {
        /// <summary>
        /// The description carried by the <c>PlaybackSessionId</c> schema. The type is opaque on
        /// purpose, and that statement is the only thing the contract says about its meaning; a
        /// scalar mapping that dropped it would be a documentation regression.
        /// </summary>
        private const string PlaybackSessionIdDescription =
            "Opaque identifier for a Tesserafin.Controller.MediaEncoding.PlaybackSession.";

        /// <summary>
        /// The five path parameters bound to <c>PlaybackSessionId</c>.
        /// </summary>
        /// <returns>Method, path and the operation's own <c>description</c> for the parameter.</returns>
        public static TheoryData<string, string, string> PlaybackSessionIdSites() => new()
        {
            { "DELETE", "/Playback/Sessions/{id}", "The session to remove." },
            { "PUT", "/Playback/Sessions/{id}", "The session to replace." },
            { "GET", "/Playback/Sessions/{id}/Stream", "The session to resolve a stream URL for." },
        };

        /// <summary>
        /// The global invariant: no parameter may declare the undefined <c>deepObject</c> /
        /// non-<see langword="true"/> <c>explode</c> combination.
        ///
        /// <para>
        /// This is the guard that makes #226 unrepeatable. A future object-shaped query parameter
        /// gets <c>style: deepObject</c> from Swashbuckle automatically; without
        /// <c>DeepObjectExplodeParameterFilter</c> it would silently reintroduce the same defect at a
        /// new site, and a site-by-site test would not notice.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryDeepObjectParameter_DeclaresExplodeTrue()
        {
            using var document = OpenApiParameterResolution.ReadCommittedContract();
            var offenders = OpenApiParameterResolution
                .EnumerateParameters(document.RootElement)
                .Where(site => string.Equals(site.Style, "deepObject", StringComparison.Ordinal))
                .Where(site => site.Explode is not true)
                .Select(site => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{site.Describe}: style=deepObject, explode={(site.Explode is null ? "<absent>" : "false")}"))
                .ToList();

            Assert.True(
                offenders.Count == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"OpenAPI 3.0.4 leaves `deepObject` with `explode: false` (its own default) undefined, so these {offenders.Count} parameter(s) name no serialization a generator can transcribe:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", offenders)}"));
        }

        /// <summary>
        /// The complementary invariant, stated over the resolved schema rather than over the declared
        /// style: anything a consumer resolves to an object must say how it is serialized.
        /// </summary>
        [Fact]
        public void EveryObjectShapedParameter_DeclaresADefinedSerialization()
        {
            using var document = OpenApiParameterResolution.ReadCommittedContract();
            var root = document.RootElement;

            var offenders = OpenApiParameterResolution
                .EnumerateParameters(root)
                .Where(site => string.Equals(
                    OpenApiParameterResolution.TypeOf(OpenApiParameterResolution.ResolveParameterSchema(site, root)),
                    "object",
                    StringComparison.Ordinal))
                .Where(site => !string.Equals(site.Style, "deepObject", StringComparison.Ordinal) || site.Explode is not true)
                .Select(site => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{site.Describe}: resolves to type=object with style={site.Style ?? "<default>"}, explode={(site.Explode is null ? "<absent>" : site.Explode.Value.ToString())}"))
                .ToList();

            Assert.True(
                offenders.Count == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{offenders.Count} parameter(s) resolve to an object without declaring deepObject/explode:true:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", offenders)}"));
        }

        /// <summary>
        /// No path parameter may resolve to an object. A path parameter is serialized into a URL
        /// segment, and every object serialization <c>simple</c> defines for one — <c>Value,&lt;uuid&gt;</c>
        /// and <c>Value=&lt;uuid&gt;</c> — is answered 400 by this server. This is the direct
        /// regression guard for finding 2.
        /// </summary>
        [Fact]
        public void NoPathParameter_ResolvesToAnObject()
        {
            using var document = OpenApiParameterResolution.ReadCommittedContract();
            var root = document.RootElement;

            var offenders = OpenApiParameterResolution
                .EnumerateParameters(root)
                .Where(site => string.Equals(site.In, "path", StringComparison.Ordinal))
                .Where(site =>
                {
                    var resolved = OpenApiParameterResolution.ResolveParameterSchema(site, root);
                    return string.Equals(OpenApiParameterResolution.TypeOf(resolved), "object", StringComparison.Ordinal)
                        || resolved.ContainsKey("properties");
                })
                .Select(site => site.Describe)
                .ToList();

            Assert.True(
                offenders.Count == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{offenders.Count} path parameter(s) resolve to an object shape the server rejects with 400:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", offenders)}"));
        }

        /// <summary>
        /// Each of the five <c>PlaybackSessionId</c> path parameters resolves to the scalar the binder
        /// accepts, keeps its opaque-identifier description, and stays required.
        /// </summary>
        /// <param name="method">The HTTP method.</param>
        /// <param name="path">The templated path.</param>
        /// <param name="parameterDescription">The operation's own description of the parameter.</param>
        [Theory]
        [MemberData(nameof(PlaybackSessionIdSites))]
        public void PlaybackSessionIdPathParameter_ResolvesToAScalarUuid(string method, string path, string parameterDescription)
        {
            using var document = OpenApiParameterResolution.ReadCommittedContract();
            var root = document.RootElement;

            var site = OpenApiParameterResolution
                .EnumerateParameters(root)
                .SingleOrDefault(candidate =>
                    string.Equals(candidate.Method, method, StringComparison.Ordinal)
                    && string.Equals(candidate.Path, path, StringComparison.Ordinal)
                    && string.Equals(candidate.Name, "id", StringComparison.Ordinal));

            Assert.True(
                site is not null,
                string.Create(CultureInfo.InvariantCulture, $"{method} {path} declares no 'id' parameter."));
            Assert.Equal("path", site!.In);

            Assert.True(
                site.Parameter.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True,
                string.Create(CultureInfo.InvariantCulture, $"{site.Describe} is no longer required."));

            Assert.True(
                site.Parameter.TryGetProperty("description", out var description)
                && string.Equals(description.GetString(), parameterDescription, StringComparison.Ordinal),
                string.Create(CultureInfo.InvariantCulture, $"{site.Describe} lost its description."));

            var resolved = OpenApiParameterResolution.ResolveParameterSchema(site, root);

            Assert.Equal("string", OpenApiParameterResolution.TypeOf(resolved));
            Assert.Equal("uuid", OpenApiParameterResolution.FormatOf(resolved));
            Assert.False(
                resolved.ContainsKey("properties"),
                string.Create(CultureInfo.InvariantCulture, $"{site.Describe} still resolves to a schema with properties."));

            Assert.True(
                resolved.TryGetValue("description", out var schemaDescription)
                && string.Equals(schemaDescription.GetString(), PlaybackSessionIdDescription, StringComparison.Ordinal),
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{site.Describe} lost the opaque-identifier description; the scalar mapping must keep it."));
        }

        /// <summary>
        /// <c>ProblemDetails</c> keeps its five named members <b>and</b> an unrestricted
        /// <c>additionalProperties</c>. Recorded as accurate by the #226 probe, and load-bearing: the
        /// framework's own <c>errors</c> and <c>traceId</c> arrive as extensions, of arbitrary JSON
        /// type. A "normalisation" that dropped <c>additionalProperties</c> would delete a capability
        /// the server exercises on every validation failure.
        /// </summary>
        [Fact]
        public void ProblemDetails_KeepsNamedMembersAndUnrestrictedExtensions()
        {
            using var document = OpenApiParameterResolution.ReadCommittedContract();
            var schema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ProblemDetails");

            var properties = schema.GetProperty("properties");
            foreach (var expected in new[] { "type", "title", "status", "detail", "instance" })
            {
                Assert.True(
                    properties.TryGetProperty(expected, out _),
                    string.Create(CultureInfo.InvariantCulture, $"ProblemDetails lost its named member '{expected}'."));
            }

            Assert.True(
                schema.TryGetProperty("additionalProperties", out var additional),
                "ProblemDetails no longer declares additionalProperties, so the contract now forbids the extension members the server actually emits.");

            // `{}` — an empty schema — is what "any JSON value" is spelled as. `false` would forbid
            // extensions outright; a typed schema would restrict them.
            Assert.Equal(JsonValueKind.Object, additional.ValueKind);
            Assert.False(
                additional.EnumerateObject().Any(),
                "ProblemDetails.additionalProperties gained a constraint; extension members are not restricted to any one type.");
        }

        /// <summary>
        /// The operation inventory survives the correction: every operation still carries a unique,
        /// non-empty <c>operationId</c>, which is the handle every generated client names a method by.
        /// </summary>
        [Fact]
        public void OperationInventory_KeepsUniqueOperationIds()
        {
            using var document = OpenApiParameterResolution.ReadCommittedContract();
            var operations = OpenApiParameterResolution.EnumerateOperations(document.RootElement);

            var missing = operations
                .Where(entry => !entry.Operation.TryGetProperty("operationId", out var id)
                    || string.IsNullOrEmpty(id.GetString()))
                .Select(entry => string.Create(CultureInfo.InvariantCulture, $"{entry.Method} {entry.Path}"))
                .ToList();

            Assert.True(
                missing.Count == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{missing.Count} operation(s) carry no operationId:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", missing)}"));

            var duplicates = operations
                .GroupBy(entry => entry.Operation.GetProperty("operationId").GetString(), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key!)
                .ToList();

            Assert.True(
                duplicates.Count == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Duplicate operationId(s): {string.Join(", ", duplicates)}"));
        }

        /// <summary>
        /// Every parameter in the document keeps a description. Swashbuckle derives these from XML
        /// documentation, and a schema-generation change that bypassed the comment filters would
        /// strip them wholesale without changing any type.
        /// </summary>
        [Fact]
        public void EveryParameter_KeepsItsDescription()
        {
            using var document = OpenApiParameterResolution.ReadCommittedContract();
            var root = document.RootElement;

            var undocumented = OpenApiParameterResolution
                .EnumerateParameters(root)
                .Where(site =>
                {
                    if (site.Parameter.TryGetProperty("description", out var description)
                        && !string.IsNullOrWhiteSpace(description.GetString()))
                    {
                        return false;
                    }

                    // A parameter may instead carry its documentation on the schema it resolves to.
                    var resolved = OpenApiParameterResolution.ResolveParameterSchema(site, root);
                    return !(resolved.TryGetValue("description", out var schemaDescription)
                        && !string.IsNullOrWhiteSpace(schemaDescription.GetString()));
                })
                .Select(site => site.Describe)
                .ToList();

            Assert.True(
                undocumented.Count == 0,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{undocumented.Count} parameter(s) carry no description at all:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", undocumented.Take(20))}"));
        }
    }
}

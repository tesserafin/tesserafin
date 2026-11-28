using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Tesserafin.Server.Integration.Tests
{
    /// <summary>
    /// Re-asserts the two #226 wire-shape invariants against the document a freshly booted server
    /// generates, not against the committed file.
    ///
    /// <para>
    /// Why both: <see cref="OpenApiParameterWireShapeTests"/> reads
    /// <c>openapi/openapi.json</c>, which is a <b>generated output</b> that happens to be committed.
    /// If the correction ever regressed to a hand-edit of that file — the exact failure mode the
    /// issue warns about, since the drift gate would then revert it — every committed-document
    /// assertion would still be green while the running server emitted the defective shapes. These
    /// two tests close that gap by asking the generation pipeline directly.
    /// </para>
    ///
    /// <para>
    /// Deliberately only the two central invariants. Duplicating the whole suite against a booted
    /// server would double its cost for no additional coverage:
    /// <c>OpenApiContractTests.CommittedContract_MatchesRunningServer</c> already proves the two
    /// documents are byte-identical, so any assertion that holds here holds there — the value of
    /// this class is that it does not depend on that proof.
    /// </para>
    /// </summary>
    public sealed class OpenApiParameterWireShapeLiveTests : IClassFixture<TesserafinApplicationFactory>
    {
        private readonly TesserafinApplicationFactory _factory;

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenApiParameterWireShapeLiveTests"/> class.
        /// </summary>
        /// <param name="factory">The application factory.</param>
        public OpenApiParameterWireShapeLiveTests(TesserafinApplicationFactory factory)
        {
            _factory = factory;
        }

        /// <summary>
        /// The running generation pipeline emits no object-shaped path parameter, so the
        /// <c>PlaybackSessionId</c> scalar mapping is proven registered and effective.
        /// </summary>
        /// <returns>A task.</returns>
        [Fact]
        public async Task LiveDocument_EmitsNoObjectShapedPathParameter()
        {
            using var document = await GenerateAsync();
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
                    $"The running server emits {offenders.Count} object-shaped path parameter(s), which it answers 400 for:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", offenders)}"));
        }

        private async Task<JsonDocument> GenerateAsync()
        {
            using HttpClient client = _factory.CreateClient();
            using var response = await client.GetAsync("/api-docs/openapi.json", TestContext.Current.CancellationToken);
            response.EnsureSuccessStatusCode();
            var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return JsonDocument.Parse(raw);
        }
    }
}

using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Tesserafin.Api.Models.LibraryStructureDto;
using Tesserafin.Extensions.Json;
using Tesserafin.Model.Configuration;
using Tesserafin.Model.Entities;
using Xunit;

namespace Tesserafin.Server.Integration.Tests.Controllers;

[TestMethodOrderer(typeof(PriorityOrderer))]
public sealed class LibraryStructureControllerTests : IClassFixture<TesserafinApplicationFactory>
{
    private readonly TesserafinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
    private static string? _accessToken;

    public LibraryStructureControllerTests(TesserafinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Priority(-1)]
    public async Task Post_NewVirtualFolder_NotFound()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var body = new AddVirtualFolderDto()
        {
            LibraryOptions = new LibraryOptions()
            {
                Enabled = false
            }
        };

        using var response = await client.PostAsJsonAsync("Library/VirtualFolders?name=test&refreshLibrary=true", body, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    [Priority(-2)]
    public async Task UpdateLibraryOptions_Invalid_NotFound()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var body = new UpdateLibraryOptionsDto()
        {
            Id = Guid.NewGuid(),
            LibraryOptions = new LibraryOptions()
        };

        using var response = await client.PostAsJsonAsync("Library/VirtualFolders/LibraryOptions", body, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Priority(-2)]
    public async Task UpdateLibraryOptions_Valid_Success()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var createBody = new AddVirtualFolderDto()
        {
            LibraryOptions = new LibraryOptions()
            {
                Enabled = false
            }
        };

        using var createResponse = await client.PostAsJsonAsync("Library/VirtualFolders?name=test&refreshLibrary=true", createBody, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, createResponse.StatusCode);

        await Task.Delay(2000, TestContext.Current.CancellationToken).ConfigureAwait(true);

        using var response = await client.GetAsync("Library/VirtualFolders", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var library = await response.Content.ReadFromJsonAsAsyncEnumerable<VirtualFolderInfo>(_jsonOptions, TestContext.Current.CancellationToken)
            .FirstOrDefaultAsync(x => string.Equals(x?.Name, "test", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        Assert.NotNull(library);

        var options = library.LibraryOptions;
        Assert.NotNull(options);
        Assert.False(options.Enabled);
        options.Enabled = true;

        var body = new UpdateLibraryOptionsDto()
        {
            Id = Guid.Parse(library.ItemId),
            LibraryOptions = options
        };

        using var response2 = await client.PostAsJsonAsync("Library/VirtualFolders/LibraryOptions", body, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response2.StatusCode);
    }

    [Fact]
    [Priority(1)]
    public async Task DeleteLibrary_Invalid_NotFound()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        using var response = await client.DeleteAsync("Library/VirtualFolders?name=doesntExist", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Priority(1)]
    public async Task DeleteLibrary_Valid_Success()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        using var response = await client.DeleteAsync("Library/VirtualFolders?name=test&refreshLibrary=true", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // A caller-supplied virtual folder name that selects a location rather than naming one is
    // refused with an explicit client error, on create as on every other operation, even for an
    // administrator.
    [Theory]
    [Priority(2)]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("/etc")]
    [InlineData("a/b")]
    public async Task Post_HostileVirtualFolderName_BadRequest(string name)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var body = new AddVirtualFolderDto() { LibraryOptions = new LibraryOptions() { Enabled = false } };

        using var response = await client.PostAsJsonAsync(
            "Library/VirtualFolders?name=" + Uri.EscapeDataString(name),
            body,
            _jsonOptions,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [Priority(2)]
    [InlineData("../escape")]
    [InlineData("/etc")]
    public async Task Delete_HostileVirtualFolderName_BadRequest(string name)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        using var response = await client.DeleteAsync(
            "Library/VirtualFolders?name=" + Uri.EscapeDataString(name),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [Priority(2)]
    [InlineData("../escape", "ok")]
    [InlineData("ok", "../escape")]
    public async Task Rename_HostileVirtualFolderName_BadRequest(string name, string newName)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        using var response = await client.PostAsync(
            "Library/VirtualFolders/Name?name=" + Uri.EscapeDataString(name) + "&newName=" + Uri.EscapeDataString(newName),
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // After onboarding, the privileged virtual-folder surface answers no caller that lacks a valid
    // administrator credential — neither a tokenless one nor one presenting a malformed token.
    [Fact]
    [Priority(2)]
    public async Task Get_AfterOnboarding_NoToken_IsRejected()
    {
        var client = _factory.CreateClient();
        _accessToken ??= await AuthHelper.CompleteStartupAsync(client);

        using var response = await client.GetAsync("Library/VirtualFolders", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    [Priority(2)]
    public async Task Get_AfterOnboarding_MalformedToken_IsRejected()
    {
        var client = _factory.CreateClient();
        _accessToken ??= await AuthHelper.CompleteStartupAsync(client);
        client.DefaultRequestHeaders.AddAuthHeader("not-a-real-token");

        using var response = await client.GetAsync("Library/VirtualFolders", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // A legitimate name, including Unicode and spaces, still works end to end.
    [Fact]
    [Priority(2)]
    public async Task Post_LegitimateUnicodeName_Succeeds()
    {
        const string Name = "Musique française 動画";

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var body = new AddVirtualFolderDto() { LibraryOptions = new LibraryOptions() { Enabled = false } };

        using var response = await client.PostAsJsonAsync(
            "Library/VirtualFolders?name=" + Uri.EscapeDataString(Name),
            body,
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var cleanup = await client.DeleteAsync(
            "Library/VirtualFolders?name=" + Uri.EscapeDataString(Name),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, cleanup.StatusCode);
    }
}

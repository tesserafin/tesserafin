using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Tesserafin.Model.Configuration;
using Tesserafin.Server.Core.AppBase;
using Tesserafin.Server.Core.Serialization;
using Xunit;

namespace Tesserafin.Server.Implementations.Tests.Serialization;

/// <summary>
/// <see cref="MyXmlSerializer"/> is the single boundary through which the server reads every XML
/// configuration document it owns — server configuration, per-library options, plugin
/// configuration and the legacy user configuration/policy files. It calls
/// <c>XmlReader.Create(stream)</c> without an explicit <see cref="XmlReaderSettings"/>, which is
/// what a static analyser sees; these tests pin what the reader created that way actually does at
/// runtime, so the behaviour cannot silently regress if the boundary is ever rewritten.
///
/// The properties that matter, each with an executable control below:
/// <list type="bullet">
/// <item>a DTD subset is refused outright, so entity expansion never begins;</item>
/// <item>no resolver is consulted, so no external entity — file or network — is ever fetched;</item>
/// <item>malformed input fails closed with <see cref="XmlException"/> rather than a partial model;</item>
/// <item>documents the server itself has written in the past keep deserialising.</item>
/// </list>
/// </summary>
public sealed class MyXmlSerializerTests
{
    [Fact]
    public void DeserializeFromBytes_RepresentativeDocument_RoundTrips()
    {
        var original = new LibraryOptions
        {
            Enabled = true,
            EnablePhotos = true,
            PreferredMetadataLanguage = "en",
            MetadataCountryCode = "US",
            SeasonZeroDisplayName = "Specials",
            AutomaticRefreshIntervalDays = 7,
        };

        var serializer = new MyXmlSerializer();
        using var buffer = new MemoryStream();
        serializer.SerializeToStream(original, buffer);

        var restored = Assert.IsType<LibraryOptions>(
            serializer.DeserializeFromBytes(typeof(LibraryOptions), buffer.ToArray()));

        Assert.True(restored.Enabled);
        Assert.True(restored.EnablePhotos);
        Assert.Equal("en", restored.PreferredMetadataLanguage);
        Assert.Equal("US", restored.MetadataCountryCode);
        Assert.Equal("Specials", restored.SeasonZeroDisplayName);
        Assert.Equal(7, restored.AutomaticRefreshIntervalDays);
    }

    [Theory]
    [InlineData("<LibraryOptions><Enabled>true</Enabled>")]
    [InlineData("<LibraryOptions><Enabled>true</Disabled></LibraryOptions>")]
    [InlineData("not xml at all")]
    [InlineData("")]
    [InlineData("<?xml version=\"1.0\"?>")]
    [InlineData("<LibraryOptions><Enabled></Enabled></LibraryOptions>")]
    public void DeserializeFromBytes_MalformedDocument_FailsClosed(string document)
    {
        var serializer = new MyXmlSerializer();

        var exception = Record.Exception(
            () => serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(document)));

        Assert.NotNull(exception);
        Assert.True(
            exception is XmlException or InvalidOperationException,
            $"Expected a parse failure, got {exception.GetType()}.");
    }

    [Fact]
    public void DeserializeFromBytes_DocumentTypeDeclaration_IsRefusedBeforeAnyEntityIsExpanded()
    {
        const string Document = """
            <?xml version="1.0"?>
            <!DOCTYPE LibraryOptions [ <!ENTITY harmless "x"> ]>
            <LibraryOptions><SeasonZeroDisplayName>&harmless;</SeasonZeroDisplayName></LibraryOptions>
            """;

        AssertDtdIsProhibited(Document);
    }

    [Fact]
    public void DeserializeFromBytes_RecursiveEntityExpansion_IsRefusedAndDoesNotExpand()
    {
        // The classic quadratic-expansion document. If the reader honoured the internal subset this
        // would allocate gigabytes; because the DTD itself is refused, it fails immediately.
        const string Document = """
            <?xml version="1.0"?>
            <!DOCTYPE LibraryOptions [
              <!ENTITY a "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
              <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
              <!ENTITY e "&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;">
              <!ENTITY f "&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;">
            ]>
            <LibraryOptions><SeasonZeroDisplayName>&f;</SeasonZeroDisplayName></LibraryOptions>
            """;

        var stopwatch = Stopwatch.StartNew();
        AssertDtdIsProhibited(Document);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expansion was attempted: the document took {stopwatch.Elapsed} to reject.");
    }

    [Fact]
    public void DeserializeFromBytes_ExternalFileEntity_NeverReadsTheFile()
    {
        const string Secret = "SENTINEL-THAT-MUST-NOT-BE-DISCLOSED";
        var secretFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllText(secretFile, Secret);

        try
        {
            var document = $"""
                <?xml version="1.0"?>
                <!DOCTYPE LibraryOptions [ <!ENTITY leak SYSTEM "file://{secretFile}"> ]>
                <LibraryOptions><SeasonZeroDisplayName>&leak;</SeasonZeroDisplayName></LibraryOptions>
                """;

            var exception = AssertDtdIsProhibited(document);
            Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Fact]
    public async Task DeserializeFromBytes_ExternalHttpEntity_NeverOpensAConnection()
    {
        // A listener that fails the test if it is ever contacted. Bound to the loopback interface
        // on an ephemeral port so nothing leaves this machine.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var contacted = 0;
        using var stop = new CancellationTokenSource();
        var accepting = Task.Run(
            async () =>
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref contacted);
                }
                catch (OperationCanceledException)
                {
                    // Never contacted: the expected outcome.
                }
            },
            CancellationToken.None);

        try
        {
            var document = $"""
                <?xml version="1.0"?>
                <!DOCTYPE LibraryOptions SYSTEM "http://127.0.0.1:{port}/external.dtd">
                <LibraryOptions><SeasonZeroDisplayName>x</SeasonZeroDisplayName></LibraryOptions>
                """;

            AssertDtdIsProhibited(document);

            // Give a resolver, if one existed, more than enough time to make the request.
            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            Assert.Equal(0, Volatile.Read(ref contacted));
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await accepting;
        }
    }

    [Fact]
    public void DeserializeFromBytes_ExcessiveNesting_FailsWithoutExhaustingTheStack()
    {
        var document = new StringBuilder("<LibraryOptions>");
        const int Depth = 50_000;
        for (var i = 0; i < Depth; i++)
        {
            document.Append("<n>");
        }

        for (var i = 0; i < Depth; i++)
        {
            document.Append("</n>");
        }

        document.Append("</LibraryOptions>");

        var serializer = new MyXmlSerializer();
        var exception = Record.Exception(
            () => serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(document.ToString())));

        // The serializer skips unknown content iteratively, so deep nesting is refused (or ignored)
        // without a StackOverflowException taking the process down with it.
        Assert.True(
            exception is null or XmlException or InvalidOperationException,
            $"Unexpected failure mode for deep nesting: {exception?.GetType()}.");
    }

    [Fact]
    public void DeserializeFromBytes_OversizedTextContent_IsBoundedByTheCallerSuppliedBuffer()
    {
        // There is no MaxCharactersInDocument on a default reader, so the only bound is the buffer
        // the caller already holds in memory. Pin that a large-but-finite document still completes
        // rather than degenerating.
        var padding = new string('x', 4 * 1024 * 1024);
        var document = $"<LibraryOptions><SeasonZeroDisplayName>{padding}</SeasonZeroDisplayName></LibraryOptions>";

        var serializer = new MyXmlSerializer();
        var restored = Assert.IsType<LibraryOptions>(
            serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(document)));

        Assert.Equal(padding.Length, restored.SeasonZeroDisplayName.Length);
    }

    [Fact]
    public void DeserializeFromBytes_UnexpectedElementsAndAttributes_AreIgnored()
    {
        const string Document = """
            <LibraryOptions unexpectedAttribute="1">
              <Enabled>true</Enabled>
              <ThisElementDoesNotExist><Nested>1</Nested></ThisElementDoesNotExist>
              <SeasonZeroDisplayName>Specials</SeasonZeroDisplayName>
            </LibraryOptions>
            """;

        var serializer = new MyXmlSerializer();
        var restored = Assert.IsType<LibraryOptions>(
            serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(Document)));

        // Unknown content is dropped, not merged: an attacker who could write one of these files
        // still cannot reach a member the model does not declare.
        Assert.True(restored.Enabled);
        Assert.Equal("Specials", restored.SeasonZeroDisplayName);
    }

    [Fact]
    public void DeserializeFromBytes_ForeignNamespace_IsNotAcceptedAsTheDocumentRoot()
    {
        const string Document = """
            <LibraryOptions xmlns="urn:not-tesserafin"><Enabled>true</Enabled></LibraryOptions>
            """;

        var serializer = new MyXmlSerializer();

        Assert.Throws<InvalidOperationException>(
            () => serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(Document)));
    }

    [Fact]
    public void DeserializeFromBytes_UnknownEnumValue_IsRejected()
    {
        const string Document = """
            <LibraryOptions><AllowEmbeddedSubtitles>NotAMemberOfTheEnum</AllowEmbeddedSubtitles></LibraryOptions>
            """;

        var serializer = new MyXmlSerializer();

        // A value outside the declared enumeration is refused rather than coerced to the default.
        Assert.Throws<InvalidOperationException>(
            () => serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(Document)));
    }

    [Theory]
    // An empty document: every member falls back to its constructor default.
    [InlineData("<LibraryOptions />")]
    // A document written before members were added: the missing ones keep their defaults.
    [InlineData("<LibraryOptions><Enabled>false</Enabled></LibraryOptions>")]
    // A document carrying the xsi/xsd declarations the serializer itself emits.
    [InlineData("""
        <LibraryOptions xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Enabled>true</Enabled>
        </LibraryOptions>
        """)]
    // A byte-order mark, as written by some external editors.
    [InlineData("﻿<LibraryOptions><Enabled>true</Enabled></LibraryOptions>")]
    public void DeserializeFromBytes_HistoricalDocument_StillLoads(string document)
    {
        var serializer = new MyXmlSerializer();

        var restored = serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(document));

        Assert.IsType<LibraryOptions>(restored);
    }

    [Fact]
    public void DeserializeFromFile_MissingFile_CarriesTheFilenameOnTheException()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var serializer = new MyXmlSerializer();

        var exception = Assert.Throws<FileNotFoundException>(
            () => serializer.DeserializeFromFile(typeof(LibraryOptions), missing));

        Assert.Equal(missing, exception.Data["Filename"]);
    }

    private static XmlException AssertDtdIsProhibited(string document)
    {
        var serializer = new MyXmlSerializer();

        // XmlSerializer wraps every reader failure in InvalidOperationException; the reason lives
        // on the inner exception.
        var outer = Assert.Throws<InvalidOperationException>(
            () => serializer.DeserializeFromBytes(typeof(LibraryOptions), Encoding.UTF8.GetBytes(document)));

        var exception = Assert.IsType<XmlException>(outer.InnerException);

        // The reader created by XmlReader.Create(stream) defaults to DtdProcessing.Prohibit with a
        // null XmlResolver. That is the property the whole class relies on: the declaration is
        // refused before any entity — internal or external — can be looked up.
        Assert.Contains("DTD is prohibited", exception.Message, StringComparison.Ordinal);
        return exception;
    }

    /// <summary>
    /// tesserafin#129 step 7: a <c>system.xml</c> written before the shadow comparison and the cast
    /// receivers were removed still carries <c>PlaybackShadow</c> and <c>CastReceiverApplications</c>.
    /// Loading it must keep every setting the server still has. <see cref="ConfigurationHelper"/>
    /// falls back to a DEFAULT configuration, silently, when deserialisation throws, so "it did not
    /// throw" is not enough: a value next to the removed elements has to survive, and the file the
    /// helper writes back drops what the server no longer has.
    /// </summary>
    [Fact]
    public void ServerConfiguration_WrittenWithRemovedElements_LoadsKeepingItsSettings()
    {
        const string Legacy = """
            <?xml version="1.0" encoding="utf-8"?>
            <ServerConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <ParallelImageEncodingLimit>7</ParallelImageEncodingLimit>
              <CastReceiverApplications>
                <CastReceiverApplication>
                  <Id>F007D354</Id>
                  <Name>Stable</Name>
                </CastReceiverApplication>
              </CastReceiverApplications>
              <PlaybackShadow>
                <Enabled>false</Enabled>
                <Mode>V2</Mode>
                <SampleRate>0.5</SampleRate>
                <StopThresholds>
                  <Enabled>true</Enabled>
                </StopThresholds>
              </PlaybackShadow>
              <EnableLegacyAuthorization>true</EnableLegacyAuthorization>
            </ServerConfiguration>
            """;
        var directory = Directory.CreateTempSubdirectory("removed-config-elements-");
        try
        {
            var path = Path.Combine(directory.FullName, "system.xml");
            File.WriteAllText(path, Legacy, new UTF8Encoding(false));
            var serializer = new MyXmlSerializer();

            var direct = Assert.IsType<ServerConfiguration>(serializer.DeserializeFromBytes(typeof(ServerConfiguration), Encoding.UTF8.GetBytes(Legacy)));
            Assert.Equal(7, direct.ParallelImageEncodingLimit);
            Assert.True(direct.EnableLegacyAuthorization);

            var loaded = Assert.IsType<ServerConfiguration>(ConfigurationHelper.GetXmlConfiguration(typeof(ServerConfiguration), path, serializer));
            Assert.Equal(7, loaded.ParallelImageEncodingLimit);
            Assert.True(loaded.EnableLegacyAuthorization);

            var rewritten = File.ReadAllText(path);
            Assert.DoesNotContain("PlaybackShadow", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("CastReceiver", rewritten, StringComparison.Ordinal);
            Assert.Contains("<ParallelImageEncodingLimit>7</ParallelImageEncodingLimit>", rewritten, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The same for a legacy user configuration file carrying the removed <c>CastReceiverId</c>.
    /// </summary>
    [Fact]
    public void UserConfiguration_WrittenWithCastReceiverId_LoadsKeepingItsSettings()
    {
        const string Legacy = """
            <?xml version="1.0" encoding="utf-8"?>
            <UserConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <PlayDefaultAudioTrack>false</PlayDefaultAudioTrack>
              <CastReceiverId>F007D354</CastReceiverId>
              <EnableNextEpisodeAutoPlay>false</EnableNextEpisodeAutoPlay>
            </UserConfiguration>
            """;

        var loaded = Assert.IsType<UserConfiguration>(new MyXmlSerializer().DeserializeFromBytes(typeof(UserConfiguration), Encoding.UTF8.GetBytes(Legacy)));

        Assert.False(loaded.PlayDefaultAudioTrack);
        Assert.False(loaded.EnableNextEpisodeAutoPlay);
    }
}

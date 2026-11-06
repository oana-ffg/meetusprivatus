using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Meetu;
using Xunit;

namespace Meetu.Tests;

public sealed class ModelDownloaderTests
{
    private static readonly byte[] Content = Enumerable.Range(0, 4096).Select(index => (byte)index).ToArray();
    private static readonly ModelAsset Asset = new("model.gguf", Content.Length, Convert.ToHexStringLower(SHA256.HashData(Content)), new Uri("https://example.invalid/model.gguf"));

    private sealed class ScriptedServer(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RangeHeaderValue?> Ranges { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Ranges.Add(request.Headers.Range);
            return Task.FromResult(respond(request, Ranges.Count));
        }
    }

    // A stream that delivers part of the body, then fails as a dropped connection does.
    private sealed class DroppingStream(byte[] content, int failAfter) : MemoryStream(content)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= failAfter) throw new IOException("Connection dropped.");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, failAfter - (int)Position)], cancellationToken);
        }
    }

    private static HttpResponseMessage Whole(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Remainder(HttpRequestMessage request)
    {
        var from = (int)request.Headers.Range!.Ranges.Single().From!.Value;
        var content = new ByteArrayContent(Content[from..]);
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, Content.Length - 1, Content.Length);
        return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content };
    }

    private static Task Download(ScriptedServer server, string directory, Action<long, long>? progress = null) =>
        new ModelDownloader(new HttpClient(server), (_, _) => Task.CompletedTask).DownloadAsync(Asset, directory, progress ?? ((_, _) => { }), CancellationToken.None);

    [Fact]
    public async Task VerifiedDownloadLandsUnderItsRealNameAndReportsProgress()
    {
        using var directory = new TestDirectory();
        var reported = new List<long>();
        await Download(new ScriptedServer((_, _) => Whole(Content)), directory.Path, (received, _) => reported.Add(received));
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(directory.Path, Asset.FileName)));
        Assert.False(File.Exists(Path.Combine(directory.Path, Asset.FileName + ".partial")));
        Assert.Equal(Content.Length, reported[^1]);
    }

    [Fact]
    public async Task SeveralFilesReportAsOneDownloadAndFinishedOnesAreNotFetchedAgain()
    {
        using var directory = new TestDirectory();
        var second = new ModelAsset("second.bin", Content.Length, Asset.Sha256, new Uri("https://example.invalid/second.bin"));
        File.WriteAllBytes(Path.Combine(directory.Path, Asset.FileName), Content);
        var server = new ScriptedServer((_, _) => Whole(Content));
        var reported = new List<(long Received, long Total)>();
        await new ModelDownloader(new HttpClient(server), (_, _) => Task.CompletedTask).DownloadAllAsync([Asset, second], directory.Path,
            (received, total) => reported.Add((received, total)), CancellationToken.None);
        Assert.Single(server.Ranges);
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(directory.Path, second.FileName)));
        Assert.All(reported, report => Assert.Equal(Content.Length * 2L, report.Total));
        Assert.Equal(Content.Length * 2L, reported[^1].Received);
        Assert.Equal(reported.Select(report => report.Received).Order(), reported.Select(report => report.Received));
    }

    [Fact]
    public async Task ADroppedConnectionResumesWhereItStopped()
    {
        using var directory = new TestDirectory();
        var server = new ScriptedServer((request, attempt) => attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream(Content, 1000)) }
            : Remainder(request));
        await Download(server, directory.Path);
        Assert.Null(server.Ranges[0]);
        Assert.Equal(1000, server.Ranges[1]!.Ranges.Single().From);
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(directory.Path, Asset.FileName)));
    }

    [Fact]
    public async Task AnEarlierPartialDownloadContinuesInALaterRun()
    {
        using var directory = new TestDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, Asset.FileName + ".partial"), Content[..1500]);
        var server = new ScriptedServer((request, _) => Remainder(request));
        await Download(server, directory.Path);
        Assert.Equal(1500, server.Ranges[0]!.Ranges.Single().From);
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(directory.Path, Asset.FileName)));
    }

    [Fact]
    public async Task AServerIgnoringTheResumeRequestRestartsTheFileInsteadOfCorruptingIt()
    {
        using var directory = new TestDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, Asset.FileName + ".partial"), Content[..1500]);
        await Download(new ScriptedServer((_, _) => Whole(Content)), directory.Path);
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(directory.Path, Asset.FileName)));
    }

    [Fact]
    public async Task AWrongChecksumNeverBecomesTheModelAndLeavesNoPartialBehind()
    {
        using var directory = new TestDirectory();
        var corrupted = Content.ToArray(); corrupted[7] ^= 0xFF;
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(new ScriptedServer((_, _) => Whole(corrupted)), directory.Path));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task AResumeFromTheWrongPositionStartsOverInsteadOfCorruptingTheFile()
    {
        using var directory = new TestDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, Asset.FileName + ".partial"), Content[..1500]);
        var server = new ScriptedServer((_, attempt) =>
        {
            if (attempt > 1) return Whole(Content);
            var wrong = new ByteArrayContent(Content[100..]);
            wrong.Headers.ContentRange = new ContentRangeHeaderValue(100, Content.Length - 1, Content.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = wrong };
        });
        await Download(server, directory.Path);
        Assert.Null(server.Ranges[1]);
        Assert.Equal(Content, File.ReadAllBytes(Path.Combine(directory.Path, Asset.FileName)));
    }

    [Fact]
    public async Task AServerSendingTooMuchIsStoppedAtThePinnedSize()
    {
        using var directory = new TestDirectory();
        var oversized = new byte[Content.Length * 3];
        var server = new ScriptedServer((_, _) => Whole(oversized));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(server, directory.Path));
        Assert.Single(server.Ranges);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ALinkedPartialFileIsRefusedBeforeAnythingIsWritten()
    {
        using var directory = new TestDirectory();
        var outside = Path.Combine(directory.Path, "outside.bin");
        File.WriteAllBytes(outside, [9]);
        try { File.CreateSymbolicLink(Path.Combine(directory.Path, Asset.FileName + ".partial"), outside); }
        catch (IOException) { return; } // Creating links needs Developer Mode or elevation, so this cannot run everywhere.
        catch (UnauthorizedAccessException) { return; }
        var server = new ScriptedServer((_, _) => Whole(Content));
        await Assert.ThrowsAsync<ArgumentException>(() => Download(server, directory.Path));
        Assert.Empty(server.Ranges);
        Assert.Equal([9], File.ReadAllBytes(outside));
    }

    [Fact]
    public async Task AnExistingMismatchedFileIsRefusedAndNotOverwritten()
    {
        using var directory = new TestDirectory();
        var target = Path.Combine(directory.Path, Asset.FileName);
        File.WriteAllBytes(target, [1, 2, 3]);
        var server = new ScriptedServer((_, _) => Whole(Content));
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(server, directory.Path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(target));
        Assert.Empty(server.Ranges);
    }

    [Fact]
    public async Task AnExistingVerifiedFileNeedsNoDownload()
    {
        using var directory = new TestDirectory();
        File.WriteAllBytes(Path.Combine(directory.Path, Asset.FileName), Content);
        var server = new ScriptedServer((_, _) => Whole(Content));
        await Download(server, directory.Path);
        Assert.Empty(server.Ranges);
    }

    [Fact]
    public async Task AMissingFileFailsAtOnceButServerTroubleIsRetriedAWhile()
    {
        using var missing = new TestDirectory();
        var notFound = new ScriptedServer((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(notFound, missing.Path));
        Assert.Single(notFound.Ranges);

        using var busy = new TestDirectory();
        var unavailable = new ScriptedServer((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => Download(unavailable, busy.Path));
        Assert.Equal(5, unavailable.Ranges.Count);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// The sound scheme gallery (#937): everything it fetches is outside content, so these mostly try to
/// get something past it.
/// </summary>
public sealed class SoundGalleryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"QM-GAL-{Guid.NewGuid():N}");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicPem;

    public SoundGalleryTests()
    {
        Directory.CreateDirectory(_root);
        _publicPem = _key.ExportSubjectPublicKeyInfoPem();
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ── A gallery folder, built and signed the way the gallery repo does ──────

    private static byte[] SchemeFile(string name, Dictionary<string, string> events, Dictionary<string, byte[]>? sounds = null)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("scheme.json").Open()))
                w.Write(JsonSerializer.Serialize(new { format = 1, name, events }));
            foreach (var (file, bytes) in sounds ?? [])
            {
                using var es = zip.CreateEntry("sounds/" + file).Open();
                es.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    private static string Hash(byte[] file) => Convert.ToHexStringLower(SHA256.HashData(file));

    /// <summary>A scheme's file name in the gallery: its id and the start of its hash.</summary>
    private static string FileName(string id, byte[] file) => $"{id}-{Hash(file)[..16]}.qmsounds";

    private static object Entry(string id, byte[] file, string? name = null, string? fileName = null, string? sha = null, long? size = null) => new
    {
        id,
        name = name ?? id,
        author = "Pat",
        description = "Soft.",
        license = "CC0-1.0",
        file = fileName ?? FileName(id, file),
        size = size ?? file.LongLength,
        sha256 = sha ?? Hash(file),
        eventsWithSound = 2,
    };

    private string Sign(byte[] data, ECDsa? key = null) =>
        Convert.ToBase64String((key ?? _key).SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>index.signed: the signature line, then the exact index bytes.</summary>
    private byte[] Signed(byte[] index, ECDsa? key = null) => [.. Encoding.ASCII.GetBytes(Sign(index, key) + "\n"), .. index];

    private static string SignedPath(string dir) => Path.Combine(dir, "index.signed");

    /// <summary>Writes a gallery folder holding these schemes, its index signed with <paramref name="key"/>.</summary>
    private string Gallery(IEnumerable<(string Id, byte[] File)> schemes, ECDsa? key = null, Func<string, string>? rewriteIndex = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "gallery-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var entries = new List<object>();
        foreach (var (id, file) in schemes)
        {
            File.WriteAllBytes(Path.Combine(dir, FileName(id, file)), file);
            entries.Add(Entry(id, file));
        }
        var json = JsonSerializer.Serialize(new { format = 1, app = "quickmail", schemes = entries });
        if (rewriteIndex is not null) json = rewriteIndex(json);
        var index = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(SignedPath(dir), Signed(index, key));
        return dir;
    }

    private static readonly Dictionary<string, string> TwoSounds = new()
    {
        ["NewMail"] = "builtin:wood/arrived",
        ["SendFailed"] = "builtin:glass/failed",
    };

    private SoundGalleryService Service(string location) => new(location, _publicPem);

    // ── The signature ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ASignedGalleryLists()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var entries = await Service(dir).LoadAsync(TestContext.Current.CancellationToken);
        var entry = Assert.Single(entries);
        Assert.Equal("rain", entry.Id);
        Assert.Equal($"rain, by Pat. 2 of {SoundCatalog.Events.Count} events have a sound.", entry.ToString());
    }

    [Fact]
    public async Task AnIndexChangedAfterSigningIsRefused()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var path = SignedPath(dir);
        File.WriteAllText(path, File.ReadAllText(path).Replace("Soft.", "Loud!", StringComparison.Ordinal));
        await Assert.ThrowsAsync<SoundGalleryException>(() => Service(dir).LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnIndexWithOneByteChangedIsRefused()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var path = SignedPath(dir);
        var bytes = File.ReadAllBytes(path);
        bytes[^2] ^= 1;
        File.WriteAllBytes(path, bytes);
        await Assert.ThrowsAsync<SoundGalleryException>(() => Service(dir).LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnIndexSignedWithAnotherKeyIsRefused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))], key: other);
        var ex = await Assert.ThrowsAsync<SoundGalleryException>(() => Service(dir).LoadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("could not be checked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherIndexsSignatureDoesNotVouchForThisOne()
    {
        var a = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var b = Gallery([("wind", SchemeFile("Wind", TwoSounds))]);
        // a's signature line in front of b's index.
        var aLine = File.ReadAllText(SignedPath(a)).Split('\n')[0];
        var bIndex = File.ReadAllText(SignedPath(b)).Split('\n', 2)[1];
        File.WriteAllText(SignedPath(b), aLine + "\n" + bIndex);
        await Assert.ThrowsAsync<SoundGalleryException>(() => Service(b).LoadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public async Task AMissingOrJunkSignatureIsRefused(string? signature)
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var path = SignedPath(dir);
        var index = File.ReadAllText(path).Split('\n', 2)[1];
        if (signature is null) File.WriteAllText(path, index);   // no signature line at all
        else File.WriteAllText(path, signature + "\n" + index);
        await Assert.ThrowsAsync<SoundGalleryException>(() => Service(dir).LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThePublishedGalleryKeyIsAValidP256Key()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(SoundGalleryService.GalleryPublicKeyPem);
        Assert.Equal(256, key.KeySize);
        // And a gallery signed with a test key fails against it: nothing but the real key verifies.
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        await Assert.ThrowsAsync<SoundGalleryException>(() => new SoundGalleryService(dir).LoadAsync(TestContext.Current.CancellationToken));
    }

    // ── The index ──────────────────────────────────────────────────────────────

    private IReadOnlyList<SoundGalleryEntry> ParseOne(object entry) =>
        SoundGalleryService.Parse(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = 1, app = "quickmail", schemes = new[] { entry } })));

    public static TheoryData<string, string> BadNames => new()
    {
        { "rain", "rain.qmsounds" },   // the name without its hash
        { "rain", "rain-0000000000000001.qmsounds" },   // a hash that isn't this one's
        { "rain", "../rain.qmsounds" },
        { "rain", "C:/rain.qmsounds" },
        { "rain", "https://evil.example/rain.qmsounds" },
        { "rain", "wind.qmsounds" },
        { "rain", "rain.zip" },
        { "../rain", "../rain.qmsounds" },
        { "Rain", "Rain.qmsounds" },
        { "rain/x", "rain/x.qmsounds" },
        { "", ".qmsounds" },
    };

    [Theory]
    [MemberData(nameof(BadNames))]
    public void AnEntryThatNamesAnyOtherFileIsDropped(string id, string file) =>
        Assert.Empty(ParseOne(Entry(id, [1], fileName: file)));

    [Theory]
    [InlineData("ABC")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("G000000000000000000000000000000000000000000000000000000000000000")]
    public void AnEntryWithABadHashIsDropped(string sha) => Assert.Empty(ParseOne(Entry("rain", [1], sha: sha)));

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(SoundSchemeFile.MaxFileBytes + 1)]
    public void AnEntryWithABadSizeIsDropped(long size) => Assert.Empty(ParseOne(Entry("rain", [1], size: size)));

    [Fact]
    public void AnEntryMissingItsAuthorOrLicenceIsDropped()
    {
        var json = """{"format":1,"app":"quickmail","schemes":[{"id":"rain","name":"Rain","file":"rain.qmsounds","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""";
        Assert.Empty(SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void TextIsCleanedAndCut()
    {
        var entry = Assert.Single(ParseOne(new
        {
            id = "rain", name = "Rain\r\nInjected", author = new string('a', 100), description = "Line\u0007one" + new string('d', 400),
            license = "CC0-1.0", file = "rain-0000000000000000.qmsounds", size = 1, sha256 = new string('0', 64), eventsWithSound = 99,
        }));
        Assert.Equal("RainInjected", entry.Name);
        Assert.Equal(60, entry.Author.Length);
        Assert.Equal(300, entry.Description.Length);
        Assert.DoesNotContain('\u0007', entry.Description);
        Assert.Equal(SoundCatalog.Events.Count, entry.EventsWithSound);
    }

    [Fact]
    public void DuplicateIdsKeepTheFirst()
    {
        var json = JsonSerializer.Serialize(new { format = 1, app = "quickmail", schemes = new[] { Entry("rain", [1], name: "First"), Entry("rain", [2], name: "Second") } });
        Assert.Equal("First", Assert.Single(SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json))).Name);
    }

    [Fact]
    public void AtMostTwoHundredSchemesAreRead()
    {
        var entries = Enumerable.Range(0, 250).Select(i => Entry($"s{i}", [1])).ToArray();
        var json = JsonSerializer.Serialize(new { format = 1, app = "quickmail", schemes = entries });
        Assert.Equal(SoundGalleryService.MaxSchemes, SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json)).Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schemes":[]}""")]
    [InlineData("""{"format":"1","schemes":[]}""")]
    [InlineData("""{"format":1,"app":"quickmail","schemes":{}}""")]
    [InlineData("""{"format":1,"app":"quickmail","schemes":[[[[[[[[[[[[1]]]]]]]]]]]]}""")]
    public void AMalformedIndexIsRefused(string json) =>
        Assert.Throws<SoundGalleryException>(() => SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void AnIndexFromANewerGallerySaysToUpdate()
    {
        var ex = Assert.Throws<SoundGalleryException>(() => SoundGalleryService.Parse("""{"format":2,"schemes":[]}"""u8.ToArray()));
        Assert.Contains("newer version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedIndexIsRefusedBeforeItIsRead()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))],
            rewriteIndex: json => json[..^1] + ",\"pad\":\"" + new string('x', (int)SoundGalleryService.MaxIndexBytes) + "\"}");
        await Assert.ThrowsAsync<SoundGalleryException>(() => Service(dir).LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AWronglyTypedNumberDropsTheEntryRatherThanFailing()
    {
        var file = new byte[] { 1 };
        var sha = Hash(file);
        object WithSize(object size) => new
        {
            id = "rain", name = "Rain", author = "Pat", description = "", license = "CC0-1.0",
            file = FileName("rain", file), size, sha256 = sha, eventsWithSound = 2,
        };
        Assert.Empty(ParseOne(WithSize("1")));
        Assert.Empty(ParseOne(WithSize(true)));
        Assert.Empty(ParseOne(WithSize(1.5)));

        var json = JsonSerializer.Serialize(new { format = 1, app = "quickmail", schemes = new[] { new
        {
            id = "rain", name = "Rain", author = "Pat", description = "", license = "CC0-1.0",
            file = FileName("rain", file), size = 1, sha256 = sha, eventsWithSound = (int?)null,
        } } });
        Assert.Null(Assert.Single(SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json))).EventsWithSound);
    }

    [Fact]
    public void ASignedFileWithNoIndexOrALongFirstLineIsRefused()
    {
        Assert.False(SoundGalleryService.TrySplitSigned("abc\n"u8.ToArray(), out _, out _));
        Assert.False(SoundGalleryService.TrySplitSigned("\n{}"u8.ToArray(), out _, out _));
        Assert.False(SoundGalleryService.TrySplitSigned(Encoding.ASCII.GetBytes(new string('A', 600) + "\n{}"), out _, out _));
        Assert.True(SoundGalleryService.TrySplitSigned("sig\n{}"u8.ToArray(), out var sig, out var index));
        Assert.Equal("sig"u8.ToArray(), sig);
        Assert.Equal("{}"u8.ToArray(), index);
    }

    // ── Downloading ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADownloadMatchingTheIndexIsHandedOnAndImports()
    {
        var file = SchemeFile("Rain", TwoSounds);
        var dir = Gallery([("rain", file)]);
        var service = Service(dir);
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));

        var path = await service.DownloadAsync(entry, TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(file, File.ReadAllBytes(path));
            var vm = new SettingsViewModel(new StubConfigService(), new StubCommandRegistry(), sounds: new RecordingSoundService());
            Assert.True(vm.ImportSchemeFrom(path));
            Assert.Equal("Rain", vm.SelectedSoundScheme!.Name);
            Assert.Equal("builtin:wood/arrived", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.NewMail).Choice.Stored);
        }
        finally
        {
            SoundGalleryService.DeleteDownload(path);
        }
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task AFileSwappedAfterSigningIsRefused()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var service = Service(dir);
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        // Same length, different content: only the hash can tell.
        var swapped = File.ReadAllBytes(Path.Combine(dir, entry.File));
        swapped[^1] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(dir, entry.File), swapped);

        var ex = await Assert.ThrowsAsync<SoundGalleryException>(() => service.DownloadAsync(entry, TestContext.Current.CancellationToken));
        Assert.Contains("not the file the gallery lists", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task AFileOfAnotherSizeIsRefused(int delta)
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var service = Service(dir);
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        var path = Path.Combine(dir, entry.File);
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, delta > 0 ? [.. bytes, 0] : bytes[..^1]);
        await Assert.ThrowsAsync<SoundGalleryException>(() => service.DownloadAsync(entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEntryMadeUpOutsideTheIndexIsNotFetched()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        File.WriteAllBytes(Path.Combine(_root, "secret.qmsounds"), [1, 2, 3]);
        var forged = new SoundGalleryEntry("../secret", "x", "x", "", "CC0-1.0", "../secret.qmsounds", 3, new string('0', 64), 1);
        await Assert.ThrowsAsync<SoundGalleryException>(() => Service(dir).DownloadAsync(forged, TestContext.Current.CancellationToken));
    }

    // ── Over HTTP ──────────────────────────────────────────────────────────────

    /// <summary>Answers requests by file name, from a table, and records what was asked for.</summary>
    private sealed class FakeServer(Func<string, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request.RequestUri!.AbsolutePath.Split('/')[^1]));
        }
    }

    private static HttpResponseMessage Ok(byte[] body, long? claimedLength = null)
    {
        var content = new ByteArrayContent(body);
        if (claimedLength is { } length) content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private (byte[] Signed, string FileName, byte[] File) Published(string id = "rain")
    {
        var file = SchemeFile("Rain", TwoSounds);
        var index = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = 1, app = "quickmail", schemes = new[] { Entry(id, file) } }));
        return (Signed(index), FileName(id, file), file);
    }

    [Fact]
    public async Task OverHttpsItReadsTheIndexAndTheFileFromTheGalleryOnly()
    {
        var (signed, fileName, file) = Published();
        var server = new FakeServer(name =>
            name == "index.signed" ? Ok(signed)
            : name == fileName ? Ok(file)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var service = new SoundGalleryService("https://gallery.example/sounds", _publicPem, server);

        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        var path = await service.DownloadAsync(entry, TestContext.Current.CancellationToken);
        SoundGalleryService.DeleteDownload(path);

        Assert.All(server.Asked, uri => Assert.Equal("gallery.example", uri.Host));
        Assert.Equal(
            ["/sounds/index.signed", "/sounds/" + fileName],
            server.Asked.Select(u => u.AbsolutePath));
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ARedirectOrErrorIsNotFollowed(HttpStatusCode status)
    {
        var server = new FakeServer(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Location = new Uri("https://evil.example/index.json");
            return response;
        });
        using var service = new SoundGalleryService("https://gallery.example/", _publicPem, server);
        var ex = await Assert.ThrowsAsync<SoundGalleryException>(() => service.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("could not be reached", ex.Message, StringComparison.Ordinal);
        Assert.All(server.Asked, uri => Assert.Equal("gallery.example", uri.Host));
    }

    [Fact]
    public async Task AServerSendingMoreThanItClaimsIsCutOff()
    {
        var (signed, _, file) = Published();
        var server = new FakeServer(name =>
            name == "index.signed" ? Ok(signed) : Ok([.. file, .. new byte[1000]], claimedLength: null));
        using var service = new SoundGalleryService("https://gallery.example/", _publicPem, server);
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SoundGalleryException>(() => service.DownloadAsync(entry, TestContext.Current.CancellationToken));
    }

    /// <summary>A body that sends nothing after the headers, like a stalled connection.</summary>
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Fact]
    public async Task AConnectionThatStallsAfterTheHeadersTimesOut()
    {
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var service = new SoundGalleryService("https://gallery.example/", _publicPem, server, readTimeout: TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<SoundGalleryException>(() => service.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("could not be reached", ex.Message, StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ClosingTheWindowCancelsRatherThanReportingAFailure()
    {
        var server = new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        using var service = new SoundGalleryService("https://gallery.example/", _publicPem, server);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.LoadAsync(cts.Token));
    }

    [Fact]
    public async Task AnIndexClaimingToBeHugeIsNotRead()
    {
        var server = new FakeServer(_ => Ok([1, 2, 3], claimedLength: SoundGalleryService.MaxIndexBytes + 1024));
        using var service = new SoundGalleryService("https://gallery.example/", _publicPem, server);
        await Assert.ThrowsAsync<SoundGalleryException>(() => service.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANetworkFailureSaysToCheckTheConnection()
    {
        var server = new FakeServer(_ => throw new HttpRequestException("no route"));
        using var service = new SoundGalleryService("https://gallery.example/", _publicPem, server);
        var ex = await Assert.ThrowsAsync<SoundGalleryException>(() => service.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Check your connection", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://gallery.example/")]
    [InlineData("ftp://gallery.example/")]
    [InlineData("javascript:alert(1)")]
    public void OnlyHttpsOrAFolderIsAccepted(string location) =>
        Assert.Throws<ArgumentException>(() => new SoundGalleryService(location));

    [Fact]
    public async Task AFileUrlIsTreatedAsTheFolderItNames()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        using var service = new SoundGalleryService(new Uri(dir).AbsoluteUri, _publicPem);
        Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PlainHttpIsAllowedOnlyOnThisComputer()
    {
        using var service = new SoundGalleryService("http://localhost:8080/", _publicPem);
        Assert.NotNull(service);
    }

    // ── The window's view model ────────────────────────────────────────────────

    private sealed class FakeGallery : ISoundGalleryService
    {
        public IReadOnlyList<SoundGalleryEntry> Entries { get; set; } = [];
        public SoundGalleryException? LoadFailure { get; set; }
        public SoundGalleryException? DownloadFailure { get; set; }

        public Task<IReadOnlyList<SoundGalleryEntry>> LoadAsync(CancellationToken ct) =>
            LoadFailure is null ? Task.FromResult(Entries) : Task.FromException<IReadOnlyList<SoundGalleryEntry>>(LoadFailure);

        public Task<string> DownloadAsync(SoundGalleryEntry entry, CancellationToken ct) =>
            DownloadFailure is null ? Task.FromResult(@"C:\temp\" + entry.File) : Task.FromException<string>(DownloadFailure);
    }

    private static SoundGalleryEntry Listed(string id) =>
        new(id, id, "Pat", "Soft drops.", "CC-BY-4.0", id + ".qmsounds", 10, new string('0', 64), 3);

    [Fact]
    public async Task TheWindowListsTheGalleryAndSelectsTheFirst()
    {
        var vm = new SoundGalleryViewModel(new FakeGallery { Entries = [Listed("rain"), Listed("wind")] });
        var said = new List<(string, AnnouncementCategory)>();
        vm.AnnouncementRequested += (t, c) => said.Add((t, c));

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, vm.Entries.Count);
        Assert.Equal("rain", vm.SelectedEntry!.Id);
        Assert.True(vm.CanAdd);
        Assert.Equal("Soft drops. By Pat. Licence: CC-BY-4.0.", vm.Details);
        Assert.Equal("Soft drops. Licence: CC-BY-4.0.", vm.SelectedEntry.HelpText);
        Assert.True(vm.HasDetails);
        Assert.Equal([("Loading the sound scheme gallery…", AnnouncementCategory.Status)], said);
        Assert.Equal(string.Empty, vm.Status);
    }

    [Fact]
    public async Task AGalleryThatFailsSaysWhyAndOffersNothingToAdd()
    {
        var vm = new SoundGalleryViewModel(new FakeGallery { LoadFailure = new SoundGalleryException("The gallery could not be reached.") });
        string? said = null;
        vm.AnnouncementRequested += (t, c) => { if (c == AnnouncementCategory.Result) said = t; };

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(vm.Entries);
        Assert.False(vm.CanAdd);
        Assert.Equal("The gallery could not be reached.", vm.Status);
        Assert.Equal(vm.Status, said);
        Assert.False(vm.IsBusy);
    }

    /// <summary>Loads that finish when the test says.</summary>
    private sealed class SlowGallery : ISoundGalleryService
    {
        public Queue<TaskCompletionSource<IReadOnlyList<SoundGalleryEntry>>> Pending { get; } = new();

        public Task<IReadOnlyList<SoundGalleryEntry>> LoadAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<SoundGalleryEntry>>();
            Pending.Enqueue(tcs);
            return tcs.Task;
        }

        public Task<string> DownloadAsync(SoundGalleryEntry entry, CancellationToken ct) => Task.FromResult(entry.File);
    }

    [Fact]
    public async Task TryAgainDuringALoadShowsOnlyTheLatestList()
    {
        var gallery = new SlowGallery();
        var vm = new SoundGalleryViewModel(gallery);
        var first = vm.LoadAsync(TestContext.Current.CancellationToken);
        var second = vm.LoadAsync(TestContext.Current.CancellationToken);
        var firstLoad = gallery.Pending.Dequeue();
        var secondLoad = gallery.Pending.Dequeue();

        secondLoad.SetResult([Listed("wind")]);
        await second;
        firstLoad.SetResult([Listed("rain"), Listed("hail")]);   // finishes late
        await first;

        Assert.Equal(["wind"], vm.Entries.Select(e => e.Id));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task AnEarlierLoadFinishingLateDoesNotEndTheBusyState()
    {
        var gallery = new SlowGallery();
        var vm = new SoundGalleryViewModel(gallery);
        var first = vm.LoadAsync(TestContext.Current.CancellationToken);
        _ = vm.LoadAsync(TestContext.Current.CancellationToken);
        gallery.Pending.Dequeue().SetResult([Listed("rain")]);
        await first;
        Assert.True(vm.IsBusy);    // the second load is still going
        Assert.False(vm.CanAdd);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task AnEmptyGallerySaysSo()
    {
        var vm = new SoundGalleryViewModel(new FakeGallery());
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("The gallery has no schemes yet.", vm.Status);
    }

    [Fact]
    public async Task AddingFetchesTheSelectedSchemeOrSaysWhyNot()
    {
        var gallery = new FakeGallery { Entries = [Listed("rain")] };
        var vm = new SoundGalleryViewModel(gallery);
        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\temp\rain.qmsounds", await vm.DownloadSelectedAsync(TestContext.Current.CancellationToken));

        gallery.DownloadFailure = new SoundGalleryException("The download of rain was not the file the gallery lists.");
        Assert.Null(await vm.DownloadSelectedAsync(TestContext.Current.CancellationToken));
        Assert.Equal("The download of rain was not the file the gallery lists.", vm.Status);
        Assert.True(vm.CanAdd);   // can try again
    }

    // ── The list is QuickMail's, read strictly (app-kit sounds/SCHEMES.md) ─────

    [Theory]
    [InlineData("""{"format":1,"schemes":[]}""")]
    [InlineData("""{"format":1,"app":null,"schemes":[]}""")]
    [InlineData("""{"format":1,"app":"thechatplace","schemes":[]}""")]
    [InlineData("""{"format":1,"app":"QuickMail","schemes":[]}""")]
    [InlineData("""{"format":1,"app":"quickmail","app":"thechatplace","schemes":[]}""")]
    [InlineData("""{"format":1.0,"app":"quickmail","schemes":[]}""")]
    [InlineData("""{"format":99999999999,"app":"quickmail","schemes":[]}""")]
    [InlineData("""{"format":1,"app":"quickmail","schemes":[],"x":NaN}""")]
    public void AListThatIsNotQuickMailsOrNotStrictJsonIsNotTrusted(string json)
    {
        var ex = Assert.Throws<SoundGalleryException>(() => SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(SoundGalleryProblem.Untrusted, ex.Kind);
    }

    [Fact]
    public void AListWithAByteOrderMarkIsNotTrusted()
    {
        var ex = Assert.Throws<SoundGalleryException>(() => SoundGalleryService.Parse([0xEF, 0xBB, 0xBF, .. """{"format":1,"app":"quickmail","schemes":[]}"""u8]));
        Assert.Equal(SoundGalleryProblem.Untrusted, ex.Kind);
    }

    [Fact]
    public void ANewerListSaysToUpdate() =>
        Assert.Equal(SoundGalleryProblem.NeedsUpdate,
            Assert.Throws<SoundGalleryException>(() => SoundGalleryService.Parse("""{"format":2,"app":"quickmail","schemes":[]}"""u8.ToArray())).Kind);

    [Theory]
    [InlineData("rain\n")]
    [InlineData("-rain")]
    [InlineData("r" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void AnIdMustMatchTheWholeText(string id) => Assert.False(SoundGalleryService.IsValidId(id));

    [Fact]
    public void AHashWithATrailingNewlineIsDropped()
    {
        var sha = new string('0', 64);
        Assert.False(SoundGalleryService.IsValidSha256(sha + "\n"));
        Assert.Empty(ParseOne(new { id = "rain", name = "Rain", author = "Pat", license = "CC0-1.0", file = $"rain-{sha[..16]}.qmsounds", size = 1, sha256 = sha + "\n" }));
    }

    [Fact]
    public void AnEntryWhoseNameIsEmptyOnceCleanedIsDropped()
    {
        var sha = new string('0', 64);
        Assert.Empty(ParseOne(new { id = "rain", name = " \u0007 ", author = "Pat", license = "CC0-1.0", file = $"rain-{sha[..16]}.qmsounds", size = 1, sha256 = sha }));
    }

    [Fact]
    public void ASizeThatDoesNotFitThirtyTwoBitsDropsTheEntry()
    {
        var sha = new string('0', 64);
        Assert.Empty(ParseOne(new { id = "rain", name = "Rain", author = "Pat", license = "CC0-1.0", file = $"rain-{sha[..16]}.qmsounds", size = 5_000_000_000L, sha256 = sha }));
    }

    [Theory]
    [InlineData("\r")]   // a CRLF after the signature
    [InlineData(" ")]
    public void ASignatureLineWithAnythingButBase64IsRefused(string extra)
    {
        var index = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = 1, app = "quickmail", schemes = Array.Empty<object>() }));
        byte[] signed = [.. Encoding.ASCII.GetBytes(Sign(index) + extra + "\n"), .. index];
        Assert.Equal(SoundGalleryProblem.Untrusted, Assert.Throws<SoundGalleryException>(() => SoundGalleryService.ReadIndex(signed, _publicPem)).Kind);
        Assert.Empty(SoundGalleryService.ReadIndex([.. Encoding.ASCII.GetBytes(Sign(index) + "\n"), .. index], _publicPem));
    }

    [Theory]
    [InlineData(@"\\server\share\gallery")]
    [InlineData("//server/share/gallery")]
    [InlineData("file://server/share/gallery")]
    [InlineData("https://user:secret@gallery.example/")]
    [InlineData("https://user@gallery.example/")]
    public void ANetworkShareOrAUserNameIsRefused(string location) =>
        Assert.Throws<ArgumentException>(() => new SoundGalleryService(location));

    [Fact]
    public async Task ADownloadOfTheWrongSizeIsNotTheListedFile()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var service = Service(dir);
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        var path = Path.Combine(dir, entry.File);
        File.WriteAllBytes(path, [.. File.ReadAllBytes(path), 0]);
        var ex = await Assert.ThrowsAsync<SoundGalleryException>(() => service.DownloadAsync(entry, TestContext.Current.CancellationToken));
        Assert.Equal(SoundGalleryProblem.NotTheListedFile, ex.Kind);
        Assert.Equal("The download of rain was not the file the gallery lists, so it was not used. Try again later.", ex.Message);
    }

    // ── Added, and updates ─────────────────────────────────────────────────────

    private static SoundGalleryEntry Listed(string id, string sha) =>
        new(id, id, "Pat", "Soft drops.", "CC-BY-4.0", id + ".qmsounds", 10, sha, 3);

    [Fact]
    public async Task TheListMarksSchemesAlreadyAddedAndOnesWithUpdates()
    {
        var oldSha = new string('1', 64);
        var vm = new SoundGalleryViewModel(
            new FakeGallery { Entries = [Listed("rain"), Listed("wind", new string('2', 64)), Listed("hail")] },
            new Dictionary<string, string> { ["rain"] = new string('0', 64), ["wind"] = oldSha });

        await vm.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            $"rain, by Pat. 3 of {SoundCatalog.Events.Count} events have a sound. Added.",
            $"wind, by Pat. 3 of {SoundCatalog.Events.Count} events have a sound. Update available.",
            $"hail, by Pat. 3 of {SoundCatalog.Events.Count} events have a sound.",
        ], vm.Entries.Select(r => r.ToString()));

        // Added: nothing to add, and Enter says so.
        Assert.Equal(SoundGalleryState.Added, vm.SelectedEntry!.State);
        Assert.False(vm.CanAdd);
        Assert.Equal("Add", vm.AddName);
        string? said = null;
        vm.AnnouncementRequested += (t, c) => { if (c == AnnouncementCategory.Result) said = t; };
        Assert.True(vm.SayAlreadyAdded());
        Assert.Equal("You've already added rain.", said);
        Assert.Null(await vm.DownloadSelectedAsync(TestContext.Current.CancellationToken));

        // An update: the button says Update.
        vm.SelectedEntry = vm.Entries[1];
        Assert.True(vm.CanAdd);
        Assert.Equal("_Update", vm.AddLabel);
        Assert.Equal("Update", vm.AddName);
        Assert.False(vm.SayAlreadyAdded());

        vm.SelectedEntry = vm.Entries[2];
        Assert.Equal("_Add", vm.AddLabel);
        Assert.True(vm.CanAdd);
    }

    [Fact]
    public void AnEntryWithNoEventCountLeavesItOut() =>
        Assert.Equal("Rain, by Pat.", new SoundGalleryEntry("rain", "Rain", "Pat", "", "CC0-1.0", "rain.qmsounds", 1, new string('0', 64), null).ToString());

    [Fact]
    public async Task AddingFromTheGalleryRemembersItAndAnUpdateKeepsTheCopysChangesAfterAsking()
    {
        var first = SchemeFile("Rain", TwoSounds);
        var config = new StubConfigService();
        var vm = new SettingsViewModel(config, new StubCommandRegistry(), sounds: new RecordingSoundService());
        var asked = new List<SoundSchemeUpdateQuestion>();
        var answer = SoundSchemeUpdateChoice.Apply;
        vm.UpdateChoiceRequested = question => { asked.Add(question); return answer; };

        // Add it.
        var service = Service(Gallery([("rain", first)]));
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        var path = await service.DownloadAsync(entry, TestContext.Current.CancellationToken);
        Assert.True(vm.ImportSchemeFrom(path, entry));
        SoundGalleryService.DeleteDownload(path);
        var added = vm.SelectedSoundScheme!;
        Assert.Equal(new SoundGalleryOrigin("rain", Hash(first)), added.GalleryOrigin);
        // The gallery version's own sounds, every event, recorded with it (#971).
        Assert.Equal("builtin:wood/arrived", added.GalleryBaseline!["NewMail"]);
        Assert.Equal("builtin:glass/failed", added.GalleryBaseline["SendFailed"]);
        Assert.Equal("none", added.GalleryBaseline["MessageSent"]);
        Assert.Equal(SoundCatalog.Events.Count, added.GalleryBaseline.Count);
        Assert.Empty(asked);
        Assert.Equal(Hash(first), vm.GalleryOrigins()["rain"]);
        vm.RenameScheme(added.Id, "My rain");
        // The person changes one event the new version also changes, and one it doesn't.
        vm.SelectedSoundEvent = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.SendFailed);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Stored == "builtin:wood/failed");
        vm.SelectedSoundEvent = vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.TrashEmptied);
        vm.SelectedSoundChoice = vm.SoundChoices.Single(c => c.Stored == "builtin:glass/emptied");

        // The gallery's copy changes: NewMail and SendFailed get new sounds, and ListEnd gets one.
        var second = SchemeFile("Rain", new Dictionary<string, string>
        {
            ["NewMail"] = "builtin:softbell/arrived",
            ["SendFailed"] = "builtin:softbell/failed",
            ["ListEnd"] = "builtin:glass/edge",
        });
        var newer = Service(Gallery([("rain", second)]));
        var newerEntry = Assert.Single(await newer.LoadAsync(TestContext.Current.CancellationToken));
        var galleryVm = new SoundGalleryViewModel(new FakeGallery { Entries = [newerEntry] }, vm.GalleryOrigins());
        await galleryVm.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SoundGalleryState.UpdateAvailable, galleryVm.SelectedEntry!.State);

        var newerPath = await newer.DownloadAsync(newerEntry, TestContext.Current.CancellationToken);
        try
        {
            // Saying no changes nothing.
            answer = SoundSchemeUpdateChoice.Cancel;
            Assert.False(vm.ImportSchemeFrom(newerPath, newerEntry));
            var question = Assert.Single(asked);
            Assert.False(question.OffersReplaceEverything);
            // NewMail and ListEnd were left as the gallery had them; SendFailed and TrashEmptied were changed.
            Assert.Equal("Update My rain with the gallery's newer version? 2 events get the new version's sounds, 2 events you changed are kept. Nothing changes until you save Settings.",
                question.Text);
            Assert.Equal(Hash(first), vm.GalleryOrigins()["rain"]);
            Assert.Equal("builtin:wood/failed", vm.SoundEventRows.Single(r => r.Info.Event == SoundEvent.SendFailed).Choice.Stored);

            // Saying yes keeps the person's changes, takes the rest, keeps its name and place, and
            // remembers the new file and its sounds.
            answer = SoundSchemeUpdateChoice.Apply;
            var count = vm.SoundSchemes.Count;
            Assert.True(vm.ImportSchemeFrom(newerPath, newerEntry));
            Assert.Equal(count, vm.SoundSchemes.Count);
            var updated = vm.SelectedSoundScheme!;
            Assert.Equal(added.Id, updated.Id);
            Assert.Equal("My rain", updated.Name);
            Assert.Equal(new SoundGalleryOrigin("rain", Hash(second)), updated.GalleryOrigin);
            Assert.Equal("builtin:softbell/arrived", updated.GalleryBaseline!["NewMail"]);
            string Row(SoundEvent e) => vm.SoundEventRows.Single(r => r.Info.Event == e).Choice.Stored;
            Assert.Equal("builtin:softbell/arrived", Row(SoundEvent.NewMail));    // left alone: the new version's
            Assert.Equal("builtin:wood/failed", Row(SoundEvent.SendFailed));      // changed: theirs
            Assert.Equal("builtin:glass/emptied", Row(SoundEvent.TrashEmptied));  // changed, and the gallery didn't: theirs
            Assert.Equal("builtin:glass/edge", Row(SoundEvent.ListEnd));          // new in this version
            Assert.Equal("none", Row(SoundEvent.MessageSent));
            Assert.StartsWith("Updated My rain.", vm.SoundSchemeMessage, StringComparison.Ordinal);
        }
        finally
        {
            SoundGalleryService.DeleteDownload(newerPath);
        }

        // And it's kept with the scheme when Settings saves, with the new version's sounds.
        vm.SaveCommand.Execute(null);
        var saved = config.Load().SoundSchemes[added.Id];
        Assert.Equal(new SoundGalleryOrigin("rain", Hash(second)), saved.GalleryOrigin);
        Assert.Equal("builtin:softbell/failed", saved.GalleryBaseline!["SendFailed"]);
        Assert.Equal("builtin:wood/failed", saved.Events["SendFailed"]);
    }

    [Fact]
    public async Task AnUpdateToASchemeAddedBeforeBaselinesOnlyAddsWhatsMissingUnlessTheyChooseToReplaceEverything()
    {
        // As 0.8.71 saved it: an origin, every event it knew, no recorded sounds.
        var config = new StubConfigService();
        var events = SoundCatalog.Events.Where(i => i.ConfigKey != "ListEnd")
            .ToDictionary(i => i.ConfigKey, _ => "none", StringComparer.OrdinalIgnoreCase);
        events["NewMail"] = "builtin:wood/arrived";
        config.Load().SoundSchemes["user-rain"] = new SoundScheme("user-rain", "My rain", false, events: events,
            galleryOrigin: new SoundGalleryOrigin("rain", new string('1', 64)));
        var newer = SchemeFile("Rain", new Dictionary<string, string>
        {
            ["NewMail"] = "builtin:softbell/arrived",
            ["ListEnd"] = "builtin:glass/edge",
        });
        var service = Service(Gallery([("rain", newer)]));
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        var path = await service.DownloadAsync(entry, TestContext.Current.CancellationToken);
        try
        {
            string Row(SettingsViewModel vm, SoundEvent e) => vm.SoundEventRows.Single(r => r.Info.Event == e).Choice.Stored;

            // The first choice: only what's missing.
            var vm = new SettingsViewModel(config, new StubCommandRegistry(), sounds: new RecordingSoundService());
            SoundSchemeUpdateQuestion? asked = null;
            vm.UpdateChoiceRequested = q => { asked = q; return SoundSchemeUpdateChoice.Apply; };
            Assert.True(vm.ImportSchemeFrom(path, entry));
            Assert.True(asked!.OffersReplaceEverything);
            Assert.Contains("Yes adds only what's missing from your copy: 1 event gets a sound, and nothing else changes.", asked.Text, StringComparison.Ordinal);
            Assert.Contains("No replaces every sound with the new version's, so any changes you made are lost.", asked.Text, StringComparison.Ordinal);
            Assert.Equal("builtin:wood/arrived", Row(vm, SoundEvent.NewMail));
            Assert.Equal("builtin:glass/edge", Row(vm, SoundEvent.ListEnd));
            Assert.NotNull(vm.SelectedSoundScheme!.GalleryBaseline);   // from now on, changes are tracked

            // The second: everything replaced, as before 0.8.72.
            var replacing = new SettingsViewModel(config, new StubCommandRegistry(), sounds: new RecordingSoundService());
            replacing.UpdateChoiceRequested = _ => SoundSchemeUpdateChoice.ReplaceEverything;
            Assert.True(replacing.ImportSchemeFrom(path, entry));
            Assert.Equal("builtin:softbell/arrived", Row(replacing, SoundEvent.NewMail));
            Assert.Equal("builtin:glass/edge", Row(replacing, SoundEvent.ListEnd));

            // Cancel, or no answer at all, changes nothing.
            var cancelled = new SettingsViewModel(config, new StubCommandRegistry(), sounds: new RecordingSoundService());
            Assert.False(cancelled.ImportSchemeFrom(path, entry));
            cancelled.SelectedSoundScheme = cancelled.SoundSchemes.Single(s => s.Id == "user-rain");
            Assert.Equal("builtin:wood/arrived", Row(cancelled, SoundEvent.NewMail));
            Assert.Equal("none", Row(cancelled, SoundEvent.ListEnd));
            Assert.Equal(new string('1', 64), cancelled.GalleryOrigins()["rain"]);
        }
        finally
        {
            SoundGalleryService.DeleteDownload(path);
        }
    }
}

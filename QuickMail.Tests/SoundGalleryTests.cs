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

    private static object Entry(string id, byte[] file, string? name = null, string? fileName = null, string? sha = null, long? size = null) => new
    {
        id,
        name = name ?? id,
        author = "Pat",
        description = "Soft.",
        license = "CC0-1.0",
        file = fileName ?? id + ".qmsounds",
        size = size ?? file.LongLength,
        sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(file)),
        eventsWithSound = 2,
    };

    private string Sign(byte[] data, ECDsa? key = null) =>
        Convert.ToBase64String((key ?? _key).SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>Writes a gallery folder holding these schemes, its index signed with <paramref name="key"/>.</summary>
    private string Gallery(IEnumerable<(string Id, byte[] File)> schemes, ECDsa? key = null, Func<string, string>? rewriteIndex = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "gallery-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        var entries = new List<object>();
        foreach (var (id, file) in schemes)
        {
            File.WriteAllBytes(Path.Combine(dir, id + ".qmsounds"), file);
            entries.Add(Entry(id, file));
        }
        var json = JsonSerializer.Serialize(new { format = 1, schemes = entries });
        if (rewriteIndex is not null) json = rewriteIndex(json);
        var index = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(Path.Combine(dir, "index.json"), index);
        File.WriteAllText(Path.Combine(dir, "index.json.sig"), Sign(index, key));
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
        Assert.Equal($"rain, by Pat. 2 of {SoundCatalog.Events.Count} events have a sound", entry.ToString());
    }

    [Fact]
    public async Task AnIndexChangedAfterSigningIsRefused()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var path = Path.Combine(dir, "index.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Soft.", "Loud!", StringComparison.Ordinal));
        await Assert.ThrowsAsync<SoundGalleryException>(() => Service(dir).LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnIndexWithOneByteChangedIsRefused()
    {
        var dir = Gallery([("rain", SchemeFile("Rain", TwoSounds))]);
        var path = Path.Combine(dir, "index.json");
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
        File.Copy(Path.Combine(a, "index.json.sig"), Path.Combine(b, "index.json.sig"), overwrite: true);
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
        var sig = Path.Combine(dir, "index.json.sig");
        if (signature is null) File.Delete(sig);
        else File.WriteAllText(sig, signature);
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
        SoundGalleryService.Parse(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = 1, schemes = new[] { entry } })));

    public static TheoryData<string, string> BadNames => new()
    {
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
        var json = """{"format":1,"schemes":[{"id":"rain","name":"Rain","file":"rain.qmsounds","size":1,"sha256":"0000000000000000000000000000000000000000000000000000000000000000"}]}""";
        Assert.Empty(SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void TextIsCleanedAndCut()
    {
        var entry = Assert.Single(ParseOne(new
        {
            id = "rain", name = "Rain\r\nInjected", author = new string('a', 100), description = "Line\u0007one" + new string('d', 400),
            license = "CC0-1.0", file = "rain.qmsounds", size = 1, sha256 = new string('0', 64), eventsWithSound = 99,
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
        var json = JsonSerializer.Serialize(new { format = 1, schemes = new[] { Entry("rain", [1], name: "First"), Entry("rain", [2], name: "Second") } });
        Assert.Equal("First", Assert.Single(SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json))).Name);
    }

    [Fact]
    public void AtMostTwoHundredSchemesAreRead()
    {
        var entries = Enumerable.Range(0, 250).Select(i => Entry($"s{i}", [1])).ToArray();
        var json = JsonSerializer.Serialize(new { format = 1, schemes = entries });
        Assert.Equal(SoundGalleryService.MaxSchemes, SoundGalleryService.Parse(Encoding.UTF8.GetBytes(json)).Count);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schemes":[]}""")]
    [InlineData("""{"format":"1","schemes":[]}""")]
    [InlineData("""{"format":1,"schemes":{}}""")]
    [InlineData("""{"format":1,"schemes":[[[[[[[[[[[[1]]]]]]]]]]]]}""")]
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
        var swapped = File.ReadAllBytes(Path.Combine(dir, "rain.qmsounds"));
        swapped[^1] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(dir, "rain.qmsounds"), swapped);

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
        var path = Path.Combine(dir, "rain.qmsounds");
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

    private (byte[] Index, string Sig, byte[] File) Published(string id = "rain")
    {
        var file = SchemeFile("Rain", TwoSounds);
        var index = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { format = 1, schemes = new[] { Entry(id, file) } }));
        return (index, Sign(index), file);
    }

    [Fact]
    public async Task OverHttpsItReadsTheIndexAndTheFileFromTheGalleryOnly()
    {
        var (index, sig, file) = Published();
        var server = new FakeServer(name => name switch
        {
            "index.json" => Ok(index),
            "index.json.sig" => Ok(Encoding.ASCII.GetBytes(sig)),
            "rain.qmsounds" => Ok(file),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var service = new SoundGalleryService("https://gallery.example/sounds", _publicPem, server);

        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        var path = await service.DownloadAsync(entry, TestContext.Current.CancellationToken);
        SoundGalleryService.DeleteDownload(path);

        Assert.All(server.Asked, uri => Assert.Equal("gallery.example", uri.Host));
        Assert.Equal(
            ["/sounds/index.json", "/sounds/index.json.sig", "/sounds/rain.qmsounds"],
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
        var (index, sig, file) = Published();
        var server = new FakeServer(name => name switch
        {
            "index.json" => Ok(index),
            "index.json.sig" => Ok(Encoding.ASCII.GetBytes(sig)),
            _ => Ok([.. file, .. new byte[1000]], claimedLength: null),
        });
        using var service = new SoundGalleryService("https://gallery.example/", _publicPem, server);
        var entry = Assert.Single(await service.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<SoundGalleryException>(() => service.DownloadAsync(entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnIndexClaimingToBeHugeIsNotRead()
    {
        var server = new FakeServer(_ => Ok([1, 2, 3], claimedLength: SoundGalleryService.MaxIndexBytes + 1));
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
        Assert.Equal("Soft drops. Licence: CC-BY-4.0.", vm.Details);
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
}

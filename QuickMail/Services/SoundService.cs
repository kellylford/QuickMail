using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using QuickMail.Models;

namespace QuickMail.Services;

/// <inheritdoc cref="ISoundService"/>
public sealed class SoundService : ISoundService
{
    /// <param name="Clip">What to play.</param>
    /// <param name="IsPreview">Asked for by name, so mute doesn't apply.</param>
    /// <param name="Fallback">What to play instead if a custom file turns out not to play.</param>
    /// <param name="VolumePercent">A volume to use in place of the saved one.</param>
    private sealed record Request(SoundClip Clip, bool IsPreview, SoundClip? Fallback = null, int? VolumePercent = null);

    private static readonly TimeSpan MuteCheckEvery = TimeSpan.FromMilliseconds(100);

    private readonly IConfigService _config;
    private readonly string _soundsFolder;
    private readonly ISoundOutput _output;
    private readonly Func<string, string, Stream?> _openBuiltIn;
    private readonly Func<bool> _isQuiet;
    private readonly SoundPolicy _policy;

    private readonly BlockingCollection<Request> _queue = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<string> _loggedFailures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _ruleSoundedKeys = new(StringComparer.Ordinal);
    private readonly Thread _worker;
    private readonly object _currentGate = new();
    private CancellationTokenSource? _current;
    private int _disposed;

    /// <param name="config">Settings, read at each sound so a change applies at once.</param>
    /// <param name="soundsFolder">The profile's folder of custom sounds.</param>
    /// <param name="output">The audio engine.</param>
    /// <param name="openBuiltIn">Opens a built-in sound by pack and name; defaults to the app's resources.</param>
    /// <param name="isQuiet">Whether Windows reports it isn't a time for background sounds.</param>
    /// <param name="utcNow">The clock for the flood rules.</param>
    public SoundService(IConfigService config, string soundsFolder, ISoundOutput output,
        Func<string, string, Stream?>? openBuiltIn = null, Func<bool>? isQuiet = null, Func<DateTime>? utcNow = null)
    {
        _config = config;
        _soundsFolder = soundsFolder;
        _output = output;
        _openBuiltIn = openBuiltIn ?? OpenBuiltInResource;
        _isQuiet = isQuiet ?? (() => false);
        _policy = new SoundPolicy(utcNow ?? (() => DateTime.UtcNow));

        // A background thread: a sound must never keep the process alive, and the test host fails a
        // run in which a foreground thread outlives the tests.
        _worker = new Thread(RunWorker) { IsBackground = true, Name = "QuickMail sounds" };
        _worker.Start();
    }

    public void Play(SoundEvent soundEvent, bool background = false)
    {
        try
        {
            var config = _config.Load();
            if (!config.PlayEventSounds) return;
            var info = SoundCatalog.Info(soundEvent);
            var origin = background && info.Origin == SoundOrigin.Direct ? SoundOrigin.Background : info.Origin;
            var scheme = SoundCatalog.ActiveScheme(config);
            Enqueue(SoundCatalog.Resolve(soundEvent, scheme), info.BuiltInSound, origin, config, scheme);
        }
        catch (Exception ex)
        {
            LogService.Log($"Sound for {soundEvent} failed", ex);
        }
    }

    public void PlayRule(string? storedSound, IEnumerable<string>? messageKeys = null)
    {
        try
        {
            var config = _config.Load();
            var scheme = SoundCatalog.ActiveScheme(config);
            var sound = RuleSound(storedSound, scheme);
            // A scheme can set its rule sound to None. Then nothing plays, so the new-mail sound and the
            // toast's own sound aren't held back for this mail either.
            if (sound.Kind == SoundRefKind.None) return;

            foreach (var key in messageKeys ?? [])
            {
                // Bounded, as the new-mail key set is. Clearing risks at most one message sounding twice.
                if (_ruleSoundedKeys.Count > 10_000) _ruleSoundedKeys.Clear();
                _ruleSoundedKeys[key] = 0;
            }

            Enqueue(sound, SoundCatalog.DefaultRuleSound, SoundOrigin.Rule, config, scheme);
        }
        catch (Exception ex)
        {
            LogService.Log("Rule sound failed", ex);
        }
    }

    /// <summary>
    /// What a rule plays. The editor has no "None" for a rule, so a rule told to play a sound plays one:
    /// its own, or, for the default (stored as an unqualified <c>builtin:matched</c>) or a value that
    /// can't be read, the scheme's "A rule matched" sound (#932).
    /// </summary>
    internal static SoundRef RuleSound(string? storedSound, SoundScheme scheme)
    {
        if (SoundRef.TryParse(storedSound, out var parsed) && parsed.Kind != SoundRefKind.None && !IsDefaultRuleSound(parsed))
            return parsed;
        return SoundCatalog.Resolve(SoundEvent.RuleMatched, scheme);
    }

    /// <summary>A rule's "default sound": the unqualified built-in rule sound, which is what every rule stored before #932.</summary>
    public static bool IsDefaultRuleSound(SoundRef sound) =>
        sound.Kind == SoundRefKind.BuiltIn && sound.Pack is null && sound.Name == SoundCatalog.DefaultRuleSound;

    public bool WasSoundedByRule(string messageKey) => _ruleSoundedKeys.ContainsKey(messageKey);

    public void Preview(string storedSound, int? volumePercent = null, string? pack = null)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            StopAll();   // Previewing "None" is how someone stops the last preview.
            if (!SoundRef.TryParse(storedSound, out var sound) || sound.Kind == SoundRefKind.None) return;
            var schemePack = pack ?? SoundCatalog.PackFor(SoundRef.None, SoundCatalog.ActiveScheme(_config.Load()));
            var clip = ToClip(sound, SoundCatalog.DefaultRuleSound, schemePack);
            if (clip is not null) TryAdd(new Request(clip, IsPreview: true, VolumePercent: volumePercent));
        }
        catch (Exception ex)
        {
            LogService.Log("Sound preview failed", ex);
        }
    }

    public void StopAll()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            while (_queue.TryTake(out _)) { }
            lock (_currentGate) _current?.Cancel();
        }
        catch (Exception ex)
        {
            LogService.Log("Stopping sounds failed", ex);
        }
    }

    public bool SilencesToasts
    {
        get
        {
            try
            {
                var config = _config.Load();
                return config.PlayEventSounds || config.MuteSounds;
            }
            catch (Exception ex)
            {
                LogService.Log("Checking a sound setting failed", ex);
                return false;
            }
        }
    }

    public bool IsAudible(SoundEvent soundEvent)
    {
        try
        {
            var config = _config.Load();
            return config.PlayEventSounds && !config.MuteSounds && config.SoundVolume > 0
                && SoundCatalog.Resolve(soundEvent, SoundCatalog.ActiveScheme(config)).Kind != SoundRefKind.None;
        }
        catch (Exception ex)
        {
            LogService.Log("Checking a sound setting failed", ex);
            return false;
        }
    }

    public IReadOnlyList<string> CustomSoundFiles()
    {
        try
        {
            if (!Directory.Exists(_soundsFolder)) return [];
            return [.. Directory.GetFiles(_soundsFolder)
                .Select(f => Path.GetFileName(f)!)
                .Where(SoundRef.IsSafeCustomFileName)
                .Order(StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Listing custom sounds failed", ex);
            return [];
        }
    }

    public string? CustomSoundPath(string fileName)
    {
        if (!SoundRef.IsSafeCustomFileName(fileName)) return null;
        var path = Path.Combine(_soundsFolder, fileName);
        return File.Exists(path) ? path : null;
    }

    public bool TryRemoveCustomSound(string fileName, out string refusal)
    {
        refusal = string.Empty;
        if (!SoundRef.IsSafeCustomFileName(fileName)) return true;   // can't be in the folder, so nothing to remove
        try
        {
            File.Delete(Path.Combine(_soundsFolder, fileName));   // no error if it's already gone
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log($"Removing the custom sound {fileName} failed", ex);
            refusal = $"{fileName} could not be removed. It may be open in another program; try again later.";
            return false;
        }
    }

    public bool TryAddCustomSound(string sourcePath, out string fileName, out string refusal)
    {
        fileName = string.Empty;
        refusal = string.Empty;
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".wav" or ".mp3"))
        {
            refusal = "QuickMail can play WAV and MP3 files. Choose one of those.";
            return false;
        }

        try
        {
            if (new FileInfo(sourcePath).Length > SoundCatalog.MaxCustomFileBytes)
            {
                refusal = $"That file is larger than {SoundCatalog.MaxCustomFileBytes / (1024 * 1024)} MB. Choose a shorter sound.";
                return false;
            }

            try
            {
                _output.Check(sourcePath);
            }
            catch (SoundTooLongException ex)
            {
                LogService.Log("A chosen sound was too long", ex);
                refusal = $"That sound is longer than {SoundCatalog.MaxCustomSoundLength.TotalSeconds:0} seconds. Choose a shorter one.";
                return false;
            }
            catch (Exception ex) when (ex is not (UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException))
            {
                // Includes EndOfStreamException, an IOException, from a file too short to have a header.
                LogService.Log("A chosen sound could not be decoded", ex);
                refusal = "That file could not be played. It may be damaged, or not really a sound file.";
                return false;
            }

            Directory.CreateDirectory(_soundsFolder);
            fileName = NameInFolder(sourcePath, extension);
            var target = Path.Combine(_soundsFolder, fileName);
            if (!File.Exists(target)) File.Copy(sourcePath, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log("Adding a custom sound failed", ex);
            fileName = string.Empty;
            refusal = "That file could not be copied into QuickMail's sounds folder. Check that it can be opened, and try again.";
            return false;
        }
    }

    /// <summary>
    /// The name to file a chosen sound under: its own, or that name numbered when a different sound
    /// already has it. Choosing the same file again finds the copy already there.
    /// </summary>
    private string NameInFolder(string sourcePath, string extension)
    {
        var original = Path.GetFileName(sourcePath);
        // A name that isn't safe here, such as COM1.wav, gets a plain one rather than refusing a good sound.
        var stem = SoundRef.IsSafeCustomFileName(original) ? Path.GetFileNameWithoutExtension(original) : "Custom sound";
        var candidate = stem + extension;
        for (var n = 2; ; n++)
        {
            var target = Path.Combine(_soundsFolder, candidate);
            if (!File.Exists(target) || SameBytes(target, sourcePath)) return candidate;
            // Shortened if need be: past the length limit, the name would be refused when next read.
            var shortened = stem.Length > 90 ? stem[..90] : stem;
            candidate = $"{shortened} {n}{extension}";
        }
    }

    private static bool SameBytes(string a, string b) =>
        new FileInfo(a).Length == new FileInfo(b).Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

    private void Enqueue(SoundRef sound, string fallback, SoundOrigin origin, ConfigModel config, SoundScheme scheme)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (sound.Kind == SoundRefKind.None || config.SoundVolume <= 0) return;
        if (!_policy.ShouldPlay(sound.ToStored(), origin, config.MuteSounds, _isQuiet(), _queue.Count)) return;
        var schemePack = SoundCatalog.PackFor(SoundRef.None, scheme);
        var clip = ToClip(sound, fallback, schemePack);
        if (clip is null) return;
        var backup = clip.FilePath is null ? null : BuiltInClip(SoundRef.BuiltIn(fallback), schemePack);
        TryAdd(new Request(clip, IsPreview: false, Fallback: backup));
    }

    private void TryAdd(Request request)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _queue.Add(request);
        }
        catch (InvalidOperationException)
        {
            // Shutting down between the check and the add: nothing left to play it.
        }
    }

    /// <summary>The clip for a sound. A custom file that has gone falls back to the built-in sound.</summary>
    /// <param name="schemePack">The pack a built-in sound with none of its own plays in.</param>
    private SoundClip? ToClip(SoundRef sound, string fallback, string schemePack)
    {
        if (sound.Kind == SoundRefKind.Custom)
        {
            var path = Path.GetFullPath(Path.Combine(_soundsFolder, sound.Name));
            var inFolder = string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(_soundsFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            if (inFolder && File.Exists(path)) return new SoundClip(sound.ToStored(), path, null);
            LogOnce($"missing:{sound.Name}", $"Custom sound '{sound.Name}' is missing; using the built-in sound.");
            sound = SoundRef.BuiltIn(fallback);
        }

        return BuiltInClip(sound, schemePack);
    }

    private SoundClip BuiltInClip(SoundRef sound, string schemePack)
    {
        var name = sound.Name;
        var pack = sound.Pack ?? schemePack;
        return new SoundClip(sound.ToStored(), null, () => _openBuiltIn(pack, name)
            ?? _openBuiltIn(SoundCatalog.DefaultPack, name)
            ?? throw new FileNotFoundException($"No built-in sound '{name}'."));
    }

    private void RunWorker()
    {
        try
        {
            foreach (var request in _queue.GetConsumingEnumerable(_shutdown.Token))
                PlayOne(request);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void PlayOne(Request request)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        lock (_currentGate) _current = cts;
        try
        {
            var config = _config.Load();
            if (config.MuteSounds && !request.IsPreview) return;   // Muted while it waited.

            // Muting cuts off what is playing too: a long custom sound shouldn't carry on over speech
            // for half a minute after "Sounds muted." A preview was asked for, so it plays regardless.
            using var muteWatch = request.IsPreview ? null : new Timer(_ => CancelIfMuted(cts), null, MuteCheckEvery, MuteCheckEvery);
            var volume = Math.Clamp(request.VolumePercent ?? config.SoundVolume, 0, 100) / 100f;
            try
            {
                _output.Play(request.Clip, volume, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && request.Fallback is not null)
            {
                // A custom sound that's damaged or too long, which a settings file or a copy by hand
                // can bring in. The toast has been silenced for this sound, so something must play.
                LogPlayFailure(request.Clip, ex);
                _output.Play(request.Fallback, volume, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a preview, or shutting down.
        }
        catch (Exception ex)
        {
            LogPlayFailure(request.Fallback ?? request.Clip, ex);
        }
        finally
        {
            lock (_currentGate) _current = null;
        }
    }

    // No device, or a file that won't decode. Once per sound: this could otherwise repeat for every message.
    private void LogPlayFailure(SoundClip clip, Exception ex)
    {
        bool first;
        lock (_loggedFailures) first = _loggedFailures.Add(clip.Key);
        if (first) LogService.Log($"Could not play sound {clip.Key}", ex);
    }

    private void CancelIfMuted(CancellationTokenSource playing)
    {
        try
        {
            if (_config.Load().MuteSounds) playing.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sound ended just as this fired: nothing left to cancel.
        }
    }

    private void LogOnce(string key, string message)
    {
        lock (_loggedFailures)
        {
            if (!_loggedFailures.Add(key)) return;
        }
        LogService.Log(message);
    }

    internal static Stream? OpenBuiltInResource(string pack, string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream($"QuickMail.Sounds.BuiltIn.{pack}.{name}.wav");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _queue.CompleteAdding();
        _worker.Join(TimeSpan.FromSeconds(1));   // A stuck audio driver must not hold up exit.
        if (_worker.IsAlive) return;             // Still using these; the process is ending anyway.
        _queue.Dispose();
        _shutdown.Dispose();
    }
}

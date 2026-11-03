using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using QuickMail.Models;

namespace QuickMail.Services;

/// <inheritdoc cref="ISoundService"/>
public sealed class SoundService : ISoundService
{
    private sealed record Request(SoundClip Clip, bool IsPreview);

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

    public void Play(SoundEvent soundEvent)
    {
        try
        {
            var config = _config.Load();
            if (!config.PlayEventSounds) return;
            var info = SoundCatalog.Info(soundEvent);
            Enqueue(SoundCatalog.Resolve(soundEvent, config.SoundEvents), info.BuiltInSound, info.Origin, config);
        }
        catch (Exception ex)
        {
            LogService.Log($"Sound for {soundEvent} failed", ex);
        }
    }

    public void PlayRule(string? storedSound)
    {
        try
        {
            var sound = SoundRef.TryParse(storedSound, out var parsed) ? parsed : SoundRef.BuiltIn(SoundCatalog.DefaultRuleSound);
            Enqueue(sound, SoundCatalog.DefaultRuleSound, SoundOrigin.Rule, _config.Load());
        }
        catch (Exception ex)
        {
            LogService.Log("Rule sound failed", ex);
        }
    }

    public void Preview(string storedSound)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!SoundRef.TryParse(storedSound, out var sound) || sound.Kind == SoundRefKind.None) return;
            var clip = ToClip(sound, SoundCatalog.DefaultRuleSound, _config.Load());
            if (clip is null) return;

            while (_queue.TryTake(out _)) { }
            lock (_currentGate) _current?.Cancel();
            TryAdd(new Request(clip, IsPreview: true));
        }
        catch (Exception ex)
        {
            LogService.Log("Sound preview failed", ex);
        }
    }

    public bool IsAudible(SoundEvent soundEvent)
    {
        try
        {
            var config = _config.Load();
            return config.PlayEventSounds && !config.MuteSounds && config.SoundVolume > 0
                && SoundCatalog.Resolve(soundEvent, config.SoundEvents).Kind != SoundRefKind.None;
        }
        catch (Exception ex)
        {
            LogService.Log("Checking a sound setting failed", ex);
            return false;
        }
    }

    private void Enqueue(SoundRef sound, string fallback, SoundOrigin origin, ConfigModel config)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (sound.Kind == SoundRefKind.None || config.SoundVolume <= 0) return;
        if (!_policy.ShouldPlay(sound.ToStored(), origin, config.MuteSounds, _isQuiet(), _queue.Count)) return;
        var clip = ToClip(sound, fallback, config);
        if (clip is not null) TryAdd(new Request(clip, IsPreview: false));
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
    private SoundClip? ToClip(SoundRef sound, string fallback, ConfigModel config)
    {
        if (sound.Kind == SoundRefKind.Custom)
        {
            var path = Path.GetFullPath(Path.Combine(_soundsFolder, sound.Name));
            var inFolder = string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(_soundsFolder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            if (inFolder && File.Exists(path)) return new SoundClip(sound.ToStored(), path, null);
            LogOnce($"missing:{sound.Name}", $"Custom sound '{sound.Name}' is missing; using the built-in sound.");
            sound = SoundRef.BuiltIn(fallback);
        }

        var name = sound.Name;
        var pack = config.SoundPack;
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
            _output.Play(request.Clip, Math.Clamp(config.SoundVolume, 0, 100) / 100f, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a preview, or shutting down.
        }
        catch (Exception ex)
        {
            // No device, or a file that won't decode. Once per sound: this could otherwise repeat for every message.
            bool first;
            lock (_loggedFailures) first = _loggedFailures.Add(request.Clip.Key);
            if (first) LogService.Log($"Could not play sound {request.Clip.Key}", ex);
        }
        finally
        {
            lock (_currentGate) _current = null;
        }
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

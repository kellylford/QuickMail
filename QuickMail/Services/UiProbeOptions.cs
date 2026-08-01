using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QuickMail.Services;

/// <summary>
/// Parsed <c>--ui-probe</c> launch options (#180). The flag is a developer/CI
/// automation hook, never user-reachable: it implies /debug, forces the app
/// offline, drives to each named surface, captures a PNG, and exits.
/// </summary>
public sealed class UiProbeOptions
{
    public IReadOnlyList<string> Surfaces { get; private init; } = [];

    /// <summary>Theme id to apply before the first render (null = configured/system).</summary>
    public string? ThemeId { get; private init; }

    /// <summary>Text scale factor 1.0–2.0 (from <c>--text-scale &lt;percent&gt;</c>).</summary>
    public double? TextScale { get; private init; }

    /// <summary>Folder receiving the PNGs (default: current directory).</summary>
    public string CaptureDir { get; private init; } = "";

    /// <summary>Optional exact file-name stem for the shot (orchestrator-controlled naming).</summary>
    public string? CaptureTag { get; private init; }

    /// <summary>
    /// Returns the parsed options, null when <c>--ui-probe</c> is absent. A
    /// malformed invocation (missing value, bad scale) yields null with
    /// <paramref name="error"/> set — the caller must exit non-zero, not limp on.
    /// </summary>
    public static UiProbeOptions? Parse(string[] args, out string? error)
    {
        error = null;

        string? TakeValue(string flag)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        var hasFlag = args.Any(a => string.Equals(a, "--ui-probe", StringComparison.OrdinalIgnoreCase));
        if (!hasFlag) return null;

        var surfacesRaw = TakeValue("--ui-probe");
        if (string.IsNullOrWhiteSpace(surfacesRaw) || surfacesRaw.StartsWith("--", StringComparison.Ordinal))
        {
            error = "--ui-probe requires a surface name (e.g. --ui-probe inbox).";
            return null;
        }
        var surfaces = surfacesRaw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant())
            .ToList();
        if (surfaces.Count == 0)
        {
            error = "--ui-probe requires at least one surface name.";
            return null;
        }

        double? scale = null;
        var scaleRaw = TakeValue("--text-scale");
        if (scaleRaw != null)
        {
            if (!int.TryParse(scaleRaw, out var pct) || pct < 100 || pct > 200)
            {
                error = $"--text-scale must be a percentage 100–200, got \"{scaleRaw}\".";
                return null;
            }
            scale = pct / 100.0;
        }

        var captureDir = TakeValue("--capture-dir");
        if (string.IsNullOrWhiteSpace(captureDir))
            captureDir = Directory.GetCurrentDirectory();

        return new UiProbeOptions
        {
            Surfaces = surfaces,
            ThemeId = TakeValue("--theme"),
            TextScale = scale,
            CaptureDir = captureDir,
            CaptureTag = TakeValue("--capture-tag"),
        };
    }
}

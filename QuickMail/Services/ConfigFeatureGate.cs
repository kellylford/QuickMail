using System;
using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Resolves feature flags from (in order of precedence, highest first):
///   1. CLI --feature flags passed at startup
///   2. config.ini [features] section
///   3. Built-in defaults
/// </summary>
public class ConfigFeatureGate : IFeatureGate
{
    /// <summary>Built-in defaults. Every flag MUST appear here.</summary>
    private static readonly IReadOnlyDictionary<FeatureFlag, bool> Defaults = new Dictionary<FeatureFlag, bool>
    {
        [FeatureFlag.GraphBackend] = false,
    };

    private readonly IReadOnlyDictionary<string, string> _configFlags;
    private readonly IReadOnlySet<string> _cliEnable;
    private readonly IReadOnlySet<string> _cliDisable;

    public ConfigFeatureGate(ConfigModel config, IEnumerable<string> cliEnable, IEnumerable<string>? cliDisable = null)
    {
        // Case-insensitive so "GraphBackend" / "graphbackend" in config.ini resolve identically.
        _configFlags = new Dictionary<string, string>(
            config.Features ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        _cliEnable  = new HashSet<string>(cliEnable  ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _cliDisable = new HashSet<string>(cliDisable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnabled(FeatureFlag flag)
    {
        var name = flag.ToString();

        // 1. CLI override (highest precedence). An explicit --no-feature wins over --feature.
        if (_cliDisable.Contains(name)) return false;
        if (_cliEnable.Contains(name)) return true;

        // 2. config.ini [features] section.
        if (_configFlags.TryGetValue(name, out var raw) && bool.TryParse(raw, out var configValue))
            return configValue;

        // 3. Built-in default.
        return Defaults[flag];
    }
}

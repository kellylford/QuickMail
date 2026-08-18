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
    private static readonly Dictionary<FeatureFlag, bool> Defaults = new Dictionary<FeatureFlag, bool>
    {
        [FeatureFlag.GraphBackend] = true,
        // Off until personal-Graph has mileage (#529 step 3): with it on, a NEW Microsoft account —
        // personal included — defaults to the Graph backend instead of IMAP. IMAP stays hand-selectable
        // under Advanced. Testers opt in with MicrosoftGraphDefault=true under [features]; the default
        // flips in a later release.
        [FeatureFlag.MicrosoftGraphDefault] = false,
        // Off while the opt-in IMAP→Graph convert (#529 step 4) is exercised by testers — it purges and
        // re-downloads the account's local cache, so it stays opt-in. Set MicrosoftGraphMigration=true
        // under [features] to test.
        [FeatureFlag.MicrosoftGraphMigration] = false,
        // Off since v0.8.37: Google no longer grants QuickMail new authorizations, so offering the
        // option to everyone only produced sign-ins that could not succeed. Opt-in for the users
        // whose authorization predates the block (#369).
        [FeatureFlag.GoogleAuth]   = false,
        // On by default: server-rule list/create/edit/delete/reorder and the unified per-account rules
        // window are complete and tested (#333). Set ServerRules=false under [features] to hide it again.
        [FeatureFlag.ServerRules]  = true,
        // Off while shared mailboxes (#31) is built across multiple PRs — the "Add shared…" button, the
        // sole creation path, stays hidden until the feature is whole. Set SharedMailboxes=true under
        // [features] to test.
        [FeatureFlag.SharedMailboxes] = false,
        // Off until POP3 (#128) has run against real servers. The local store holds the only copy of
        // POP3 mail, so the cost of a wrong assumption is a user's mail, not a re-sync. Set
        // Pop3Backend=true under [features] to test.
        [FeatureFlag.Pop3Backend] = false,
    };

    private readonly Dictionary<string, string> _configFlags;
    private readonly HashSet<string> _cliEnable;
    private readonly HashSet<string> _cliDisable;

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

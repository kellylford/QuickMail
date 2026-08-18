using System;
using System.Collections.Generic;
using System.Linq;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// #529 step 4 (§5.2): after an account is converted from IMAP to Graph, its settings that name a folder
/// by IMAP path — move-to-folder rules, real-folder saved views, and the startup folder — no longer
/// resolve, because Graph folders are keyed by opaque id, not path. This rewrites those references to the
/// matching Graph folder, or invalidates them safely (never silently pointing at a folder that no longer
/// exists).
///
/// Matching (spec §9.3, no guessing): an exact display-name match on the full reference first, then a leaf
/// name match only when it is unique; if two Graph folders share the name, the reference is left unmatched
/// rather than retargeted to a guess. Pure — the caller loads the models, calls this, and persists them.
/// </summary>
public static class FolderReferenceRemapper
{
    public sealed class Report
    {
        /// <summary>Move-to-folder rules whose target was rewritten to the matching Graph folder.</summary>
        public List<string> RemappedRules { get; } = [];
        /// <summary>Move-to-folder rules disabled because their target could not be matched.</summary>
        public List<string> DisabledRules { get; } = [];
        /// <summary>Saved views a real-folder entry was remapped or dropped from.</summary>
        public List<string> AffectedViews { get; } = [];
        /// <summary>True when the startup folder was remapped or fell back to All Mail.</summary>
        public bool StartupFolderChanged { get; set; }

        public bool AnythingChanged =>
            RemappedRules.Count > 0 || DisabledRules.Count > 0 || AffectedViews.Count > 0 || StartupFolderChanged;

        /// <summary>A one-line human/screen-reader summary, or empty when nothing changed. The
        /// disabled-rules part is actionable — it names the rules the user must reset a folder on.</summary>
        public string Summary()
        {
            if (!AnythingChanged) return string.Empty;
            var parts = new List<string>();
            if (RemappedRules.Count > 0) parts.Add($"{RemappedRules.Count} rule{(RemappedRules.Count == 1 ? "" : "s")} updated");
            if (AffectedViews.Count > 0) parts.Add($"{AffectedViews.Count} view{(AffectedViews.Count == 1 ? "" : "s")} updated");
            if (StartupFolderChanged) parts.Add("startup folder updated");
            var s = string.Join(", ", parts);
            if (DisabledRules.Count > 0)
                s += (s.Length > 0 ? ". " : "") +
                     $"Turned off {(DisabledRules.Count == 1 ? "rule" : "rules")} whose folder could not be matched — set a folder to re-enable: {string.Join(", ", DisabledRules)}";
            return s;
        }
    }

    /// <summary>
    /// Rewrites this account's folder references against the account's freshly-synced Graph folders,
    /// mutating <paramref name="allRules"/>, <paramref name="allViews"/>, and <paramref name="config"/> in
    /// place. Returns what changed. Virtual-folder views (an account-agnostic key) are untouched.
    /// </summary>
    public static Report Remap(
        Guid accountId,
        IReadOnlyList<MailFolderModel> graphFolders,
        List<MailRule> allRules,
        List<SavedView> allViews,
        ConfigModel config)
    {
        var report = new Report();

        string? Match(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            // Already a Graph folder id — an idempotent re-run (e.g. a crash between saving the rewrite and
            // clearing the marker re-runs this). Keep it as-is rather than failing to match an opaque id
            // (which would wrongly disable an already-remapped rule).
            if (graphFolders.Any(f => string.Equals(f.FullName, reference, StringComparison.Ordinal)))
                return reference;
            // Exact match on the whole reference (top-level folders whose path is just their name).
            var exact = graphFolders
                .Where(f => string.Equals(f.DisplayName, reference, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1) return exact[0].FullName;
            // Otherwise the leaf (last path segment), only if it resolves to exactly one folder.
            var slash = reference.LastIndexOf('/');
            var leaf = slash >= 0 ? reference[(slash + 1)..] : reference;
            var byLeaf = graphFolders
                .Where(f => string.Equals(f.DisplayName, leaf, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return byLeaf.Count == 1 ? byLeaf[0].FullName : null;
        }

        // Move-to-folder rules for this account.
        foreach (var rule in allRules.Where(r =>
                     r.AccountId == accountId && r.Action == RuleAction.MoveToFolder && !string.IsNullOrEmpty(r.TargetFolder)))
        {
            var match = Match(rule.TargetFolder);
            if (match != null)
            {
                rule.TargetFolder = match;
                report.RemappedRules.Add(rule.Name);
            }
            else
            {
                rule.IsEnabled = false;   // disabled, not deleted — the user can set a folder to re-enable
                report.DisabledRules.Add(rule.Name);
            }
        }

        // Real-folder saved views (skip virtual-folder views — their key is account-agnostic).
        foreach (var view in allViews.Where(v => string.IsNullOrEmpty(v.VirtualFolderKey)))
        {
            var drop = new List<ViewFolder>();
            var touched = false;
            foreach (var vf in view.Folders.Where(f => f.AccountId == accountId))
            {
                var match = Match(vf.FolderFullName);
                if (match != null) { vf.FolderFullName = match; touched = true; }
                else drop.Add(vf);
            }
            foreach (var vf in drop) view.Folders.Remove(vf);
            if (touched || drop.Count > 0) report.AffectedViews.Add(view.Name);
        }

        // Startup folder, when it names a real folder on this account.
        if (Guid.TryParse(config.StartupFolderAccount, out var sfa) && sfa == accountId
            && !string.IsNullOrEmpty(config.StartupFolder))
        {
            var match = Match(config.StartupFolder);
            if (match != null)
            {
                config.StartupFolder = match;   // StartupFolderLabel stays; it is display text
            }
            else
            {
                // Fall back to the "sync wide" / All Mail default rather than pointing at nothing.
                config.StartupFolder = string.Empty;
                config.StartupFolderAccount = string.Empty;
                config.StartupFolderLabel = string.Empty;
            }
            report.StartupFolderChanged = true;
        }

        return report;
    }
}

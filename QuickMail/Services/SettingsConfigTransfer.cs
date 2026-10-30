using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Which <see cref="ConfigModel"/> settings travel in a settings file, and the copy that applies
/// them (issue #507, spec §5.2 and Revision 2 R7).
///
/// <para>The rule: a setting that records a preference travels; a setting that records something
/// about this computer, this install, or a secret stays. Every property must appear in exactly
/// one of the sets below — <c>ConfigKeyClassificationTests</c> fails on a new property that is
/// not, which is what keeps this list from going stale.</para>
/// </summary>
public static class SettingsConfigTransfer
{
    /// <summary>Settings that stay on this computer.</summary>
    public static readonly IReadOnlySet<string> Local = new HashSet<string>(StringComparer.Ordinal)
    {
        // Troubleshooting state belongs to the computer being troubleshot.
        nameof(ConfigModel.LogFormat),
        nameof(ConfigModel.EnableLogging),
        nameof(ConfigModel.ConnectionDiagnostics),
        // This computer's consent to export and import (#507): an import must never switch it.
        nameof(ConfigModel.AllowSettingsTransfer),
        // Paths on this computer's disk.
        nameof(ConfigModel.SaveMessageFolder),
        nameof(ConfigModel.LastSaveAsFolder),
        // Belongs with the Windows sign-in entry, which lives in this computer's registry (#770).
        nameof(ConfigModel.StartMinimizedAtSignIn),
        // About this install: its desktop, its update history, its architecture.
        nameof(ConfigModel.DesktopShortcutPrompted),
        nameof(ConfigModel.LastRunVersion),
        nameof(ConfigModel.NativeArmNoticeVersion),
        // A credential. Never moved (Principle 1).
        nameof(ConfigModel.GoogleClientId),
        nameof(ConfigModel.GoogleClientSecret),
    };

    /// <summary>Settings that travel as a file of their own, not through config.ini.</summary>
    public static readonly IReadOnlySet<string> SeparateFile = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ConfigModel.CustomHotkeys),   // hotkeys.json, the Keyboard category
    };

    /// <summary>Settings that travel. <see cref="ConfigModel.Accounts"/> is merged per account, see <see cref="Apply"/>.</summary>
    public static readonly IReadOnlySet<string> Portable = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ConfigModel.PreviewLines),
        nameof(ConfigModel.ReadAsPlainText),
        nameof(ConfigModel.ShowEmbeddedPictures),
        nameof(ConfigModel.LoadWebPictures),
        nameof(ConfigModel.ViewMode),
        nameof(ConfigModel.Sort),
        nameof(ConfigModel.RememberViewPerFolder),
        nameof(ConfigModel.SyncDays),
        nameof(ConfigModel.StartupFolder),
        nameof(ConfigModel.StartupFolderAccount),
        nameof(ConfigModel.StartupFolderLabel),
        nameof(ConfigModel.StartupSyncScope),
        nameof(ConfigModel.MaxImapConnectionsPerAccount),
        nameof(ConfigModel.AutoDiscoverOnline),
        nameof(ConfigModel.InitialSyncCount),
        nameof(ConfigModel.GraphPollSeconds),
        nameof(ConfigModel.MailSyncPollMinutes),
        nameof(ConfigModel.OfflineBodyDays),
        nameof(ConfigModel.OfflineBodyAllFolders),
        nameof(ConfigModel.AppearanceThemeId),
        nameof(ConfigModel.AppearanceTextScale),
        nameof(ConfigModel.AppearanceFontFamily),
        nameof(ConfigModel.AppearanceUnderlineLinks),
        nameof(ConfigModel.AppearanceThickFocus),
        nameof(ConfigModel.AppearanceListDensity),
        nameof(ConfigModel.AppearanceForceMessageTheme),
        nameof(ConfigModel.CustomAnnouncements),
        nameof(ConfigModel.AnnounceHints),
        nameof(ConfigModel.AnnounceStatus),
        nameof(ConfigModel.AnnounceResults),
        nameof(ConfigModel.AnnounceMessageActions),
        nameof(ConfigModel.AnnounceSpellingWhileTyping),
        nameof(ConfigModel.NvdaTypingFix),
        nameof(ConfigModel.AnnounceSpellingWhileNavigating),
        nameof(ConfigModel.AnnounceSpellingSuggestions),
        nameof(ConfigModel.SpellingSuggestionsVerbosity),
        nameof(ConfigModel.AnnounceFormattingWhileNavigating),
        nameof(ConfigModel.AnnounceFlagStatus),
        nameof(ConfigModel.MessageListShowFieldLabels),
        nameof(ConfigModel.MessageListFieldSeparator),
        nameof(ConfigModel.ContactListShowFieldLabels),
        nameof(ConfigModel.CalendarListShowFieldLabels),
        nameof(ConfigModel.RuleListShowFieldLabels),
        nameof(ConfigModel.CalendarReminders),
        nameof(ConfigModel.CalendarReminderMinutes),
        nameof(ConfigModel.DefaultFlagId),
        nameof(ConfigModel.DefaultComposeMode),
        nameof(ConfigModel.AutoSaveDrafts),
        nameof(ConfigModel.AutoSaveIntervalSeconds),
        nameof(ConfigModel.ConfirmEmptyTrash),
        nameof(ConfigModel.SaveMessageFormat),
        nameof(ConfigModel.NotifyOnNewMail),
        nameof(ConfigModel.NotifyOnWatchedConversation),
        nameof(ConfigModel.CloseToTray),
        nameof(ConfigModel.TrayHintShown),
        nameof(ConfigModel.TutorialCompleted),
        nameof(ConfigModel.AutoUpdate),
        nameof(ConfigModel.ShowUpdateInstalledAlerts),
        nameof(ConfigModel.Windowing),
        nameof(ConfigModel.ShowDeclinedEvents),
        nameof(ConfigModel.DefaultCalendarSource),
        nameof(ConfigModel.CalendarAtEndOfFolderList),
        nameof(ConfigModel.CalendarPaneOpen),
        nameof(ConfigModel.Features),
        nameof(ConfigModel.Accounts),
    };

    /// <summary>The settable public properties classification covers.</summary>
    internal static IEnumerable<PropertyInfo> ClassifiedProperties() =>
        typeof(ConfigModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);

    /// <summary>
    /// Copies the travelling settings from <paramref name="incoming"/> onto <paramref name="local"/>,
    /// leaving this computer's own settings alone. Per-account overrides are taken from the file for
    /// the accounts the file carries (<paramref name="incomingAccountIds"/>, already rewritten to this
    /// profile's ids) and kept for this profile's other accounts.
    /// </summary>
    public static void Apply(ConfigModel incoming, ConfigModel local, IReadOnlyCollection<Guid> incomingAccountIds)
    {
        foreach (var prop in ClassifiedProperties())
        {
            if (!Portable.Contains(prop.Name) || prop.Name == nameof(ConfigModel.Accounts)) continue;
            prop.SetValue(local, prop.GetValue(incoming));
        }

        var merged = new Dictionary<Guid, AccountOverrideConfig>();
        foreach (var (id, ovr) in local.Accounts)
            if (!incomingAccountIds.Contains(id)) merged[id] = ovr;
        foreach (var (id, ovr) in incoming.Accounts)
            if (incomingAccountIds.Contains(id)) merged[id] = ovr;
        local.Accounts = merged;
    }
}

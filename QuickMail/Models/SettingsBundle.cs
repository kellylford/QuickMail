using System;
using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>
/// What a settings file (.qmsettings) can carry (issue #507). Each category is a group of profile
/// files that only make sense together: saved views name flags by id, so flags travel with views;
/// a contact group lists its members by contact id, so groups travel with contacts.
/// </summary>
public enum SettingsCategory
{
    Accounts,
    RulesAndViews,
    AddressBook,
    Templates,
    Keyboard,
    Appearance,
    LocalCalendar,
    Preferences,
}

/// <summary>
/// manifest.json inside a settings file: what made it, what it holds, and the identity of every
/// account it refers to — present even when the accounts themselves were not exported, because
/// that identity is what lets an import point rules and views at the right account here.
/// </summary>
public sealed class SettingsBundleManifest
{
    /// <summary>The format this build writes. A file with a higher number is refused.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppVersion { get; set; } = string.Empty;
    public DateTime ExportedUtc { get; set; }
    public string DeviceName { get; set; } = string.Empty;
    public List<SettingsCategory> Categories { get; set; } = [];
    public List<SettingsBundleAccount> Accounts { get; set; } = [];
}

/// <summary>One account as the manifest records it: enough to find the same account elsewhere.</summary>
public sealed class SettingsBundleAccount
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public BackendKind BackendKind { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsShared { get; set; }
}

/// <summary>
/// A category as the export window lists it. <see cref="ToString"/> is the display text, because
/// a screen reader reads a list item's name from it (CLAUDE.md, SelectorItemAccessibilityTests).
/// </summary>
public sealed class SettingsCategoryInfo
{
    public SettingsCategoryInfo(SettingsCategory category, string name)
    {
        Category = category;
        Name = name;
    }

    public SettingsCategory Category { get; }
    public string Name { get; }

    public override string ToString() => Name;

    /// <summary>Every category, in the order the export window lists them.</summary>
    public static IReadOnlyList<SettingsCategoryInfo> All { get; } =
    [
        new(SettingsCategory.Accounts,      "Accounts and signatures"),
        new(SettingsCategory.RulesAndViews, "Rules, saved views, flags and watched conversations"),
        new(SettingsCategory.AddressBook,   "Address book"),
        new(SettingsCategory.Templates,     "Message templates"),
        new(SettingsCategory.Keyboard,      "Keyboard shortcuts"),
        new(SettingsCategory.Appearance,    "Themes and message list layout"),
        new(SettingsCategory.LocalCalendar, "Local Calendar"),
        new(SettingsCategory.Preferences,   "Preferences and custom dictionary"),
    ];

    public static string NameOf(SettingsCategory category)
    {
        foreach (var info in All)
            if (info.Category == category) return info.Name;
        return category.ToString();
    }
}

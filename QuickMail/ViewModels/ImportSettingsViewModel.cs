using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// The Import Settings preview (issue #507): what a settings file will do to this computer, worked
/// out without writing anything. Confirming stages the file for the next start and restarts
/// (spec Revision 2 R1); nothing in the profile changes while this window is open.
/// </summary>
public partial class ImportSettingsViewModel : ObservableObject
{
    private readonly byte[] _bundleBytes;
    private readonly string _profileDir;

    private ImportSettingsViewModel(byte[] bundleBytes, string profileDir, SettingsImportPlan plan)
    {
        _bundleBytes = bundleBytes;
        _profileDir = profileDir;
        Plan = plan;
    }

    /// <summary>Opens and checks a settings file. Throws <see cref="SettingsBundleException"/> with a reason a user can act on.</summary>
    public static ImportSettingsViewModel Load(string bundlePath, string profileDir)
    {
        // Read once: what is staged is exactly what was previewed, even if the file is replaced
        // meanwhile (a sync client updating it, say).
        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(bundlePath); }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            throw new SettingsBundleException("The file could not be opened. " + ex.Message, ex);
        }
        var bundle = SettingsBundle.Open(new System.IO.MemoryStream(bytes));
        return new ImportSettingsViewModel(bytes, profileDir, SettingsImporter.Plan(bundle, profileDir));
    }

    public SettingsImportPlan Plan { get; }

    /// <summary>The first thing read: where the file came from and what importing means.</summary>
    public string Summary => Plan.Summary;

    /// <summary>One item per category in the file.</summary>
    public IReadOnlyList<string> CategoryLines => Plan.CategoryLines;

    /// <summary>One item per account in the file, named by what will happen to it.</summary>
    public IReadOnlyList<ImportAccountRow> Accounts => Plan.Accounts;

    public bool HasAccounts => Plan.Accounts.Count > 0;

    /// <summary>Copies the file into the profile for the next start to apply.</summary>
    public void Stage() => SettingsImporter.Stage(_bundleBytes, _profileDir);
}

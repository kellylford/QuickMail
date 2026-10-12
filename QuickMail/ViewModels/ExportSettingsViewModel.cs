using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>
/// The Export Settings window (issue #507): choose what goes into a settings file, then write it.
/// Every category starts checked — a move that quietly leaves something behind is found out weeks
/// later — and the summary says plainly what personal information the file will hold.
/// </summary>
public partial class ExportSettingsViewModel : ObservableObject
{
    private readonly string _profileDir;
    private readonly Func<Task<IReadOnlyList<CalendarEvent>?>> _loadLocalCalendar;
    private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> _folderPaths;
    private readonly string _appVersion;
    private readonly string _deviceName;
    private readonly Func<DateTime> _now;

    /// <summary>One checkbox. <see cref="ToString"/> is the name a screen reader reads for it.</summary>
    public sealed partial class CategoryOption : ObservableObject
    {
        public CategoryOption(SettingsCategoryInfo info) => Info = info;

        public SettingsCategoryInfo Info { get; }
        public string Name => Info.Name;

        [ObservableProperty]
        private bool _isChecked = true;

        public override string ToString() => Name;
    }

    public ExportSettingsViewModel(
        string profileDir,
        Func<Task<IReadOnlyList<CalendarEvent>?>> loadLocalCalendar,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>> folderPaths,
        string appVersion,
        string deviceName,
        Func<DateTime>? now = null)
    {
        _profileDir = profileDir;
        _loadLocalCalendar = loadLocalCalendar;
        _folderPaths = folderPaths;
        _appVersion = appVersion;
        _deviceName = deviceName;
        _now = now ?? (() => DateTime.Now);

        Categories = new ObservableCollection<CategoryOption>(SettingsCategoryInfo.All.Select(i => new CategoryOption(i)));
        foreach (var option in Categories)
            option.PropertyChanged += OnOptionChanged;
    }

    public ObservableCollection<CategoryOption> Categories { get; }

    public bool CanExport => Categories.Any(c => c.IsChecked);

    /// <summary>True while a file is being written.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportNow))]
    private bool _isExporting;

    /// <summary>What the Export button binds to: something chosen, and no export already under way.</summary>
    public bool CanExportNow => CanExport && !IsExporting;

    public string DefaultFileName => $"QuickMail-settings-{_now():yyyy-MM-dd}{SettingsBundle.FileExtension}";

    /// <summary>What is selected, what personal information that means, and what never travels.</summary>
    public string Summary
    {
        get
        {
            var chosen = Categories.Count(c => c.IsChecked);
            var contents = new List<string>();
            if (IsChecked(SettingsCategory.Accounts)) contents.Add("email addresses, server names and signatures");
            if (IsChecked(SettingsCategory.AddressBook)) contents.Add("your address book");
            if (IsChecked(SettingsCategory.LocalCalendar)) contents.Add("your Local Calendar");
            var holds = contents.Count == 0 ? string.Empty : $" The file will contain {JoinAnd(contents)}.";
            return $"{chosen} of {Categories.Count} selected.{holds} It never contains passwords or sign-ins. "
                 + "Mail is not included: it comes back from your mail servers, apart from POP3 mail that is not left on the server, "
                 + "and unsent mail in the Outbox.";
        }
    }

    private bool IsChecked(SettingsCategory category) => Categories.Any(c => c.Info.Category == category && c.IsChecked);

    private static string JoinAnd(List<string> parts) =>
        parts.Count == 1 ? parts[0] : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CategoryOption.IsChecked)) return;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(CanExportNow));
    }

    /// <summary>
    /// Writes the settings file to <paramref name="path"/> and returns the sentence that reports it.
    /// A failure removes the partial file and throws; the caller says what went wrong.
    /// </summary>
    public async Task<string> ExportAsync(string path)
    {
        IsExporting = true;
        try
        {
            return await ExportCoreAsync(path);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task<string> ExportCoreAsync(string path)
    {
        var chosen = Categories.Where(c => c.IsChecked).Select(c => c.Info.Category).ToList();
        IReadOnlyList<CalendarEvent> calendar = [];
        var calendarMissing = false;
        if (chosen.Contains(SettingsCategory.LocalCalendar))
        {
            // Null when there is no local store to read (--online): say so rather than export an
            // empty calendar as if that were all there is.
            var loaded = await _loadLocalCalendar();
            calendarMissing = loaded is null;
            calendar = loaded ?? [];
        }

        var inputs = new SettingsBundle.ExportInputs
        {
            ProfileDir = _profileDir,
            Categories = chosen,
            AppVersion = _appVersion,
            DeviceName = _deviceName,
            NowUtc = _now().ToUniversalTime(),
            FolderPaths = _folderPaths,
            LocalCalendarEvents = calendar,
        };

        // Written beside the destination, then moved into place: a half-written file never sits
        // under the chosen name, where a sync client could pick it up.
        var temp = path + ".partial";
        try
        {
            await Task.Run(() =>
            {
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    SettingsBundle.Write(inputs, stream);
                File.Move(temp, path, overwrite: true);
            });
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            throw;
        }

        var kb = Math.Max(1, (int)Math.Round(new FileInfo(path).Length / 1024.0));
        var message = $"Settings exported. {chosen.Count} {(chosen.Count == 1 ? "category" : "categories")}, {kb} kilobytes.";
        return calendarMissing ? message + " The Local Calendar could not be read here and was not included." : message;
    }
}

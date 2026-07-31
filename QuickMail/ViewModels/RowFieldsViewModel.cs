using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

/// <summary>One row-kind choice in the chooser's row-type list.</summary>
public sealed record RowKindOption(RowKind Kind, string Name)
{
    /// <summary>
    /// Screen readers read a Selector item's accessible name from ToString(), not from
    /// DisplayMemberPath — without this the list announces the record's synthesized text.
    /// </summary>
    public override string ToString() => Name;
}

/// <summary>
/// One field in the chooser. A thin observable wrapper over <see cref="RowFieldSetting"/> that
/// carries the catalog's display text and reports every change back to the owning view model,
/// which saves immediately.
/// </summary>
public sealed partial class RowFieldRow : ObservableObject
{
    private readonly Action _onChanged;

    public RowFieldRow(RowFieldDef def, RowFieldSetting setting, Action onChanged)
    {
        Id          = def.Id;
        DisplayName = def.DisplayName;
        IsState     = def.IsState;
        HasFalseWord = def.FalseWord is not null;
        _enabled    = setting.Enabled;
        _speakMode  = setting.SpeakMode;
        _onChanged  = onChanged;
    }

    public string Id { get; }
    public string DisplayName { get; }

    /// <summary>True when this field offers a <see cref="SpeakMode"/> choice.</summary>
    public bool IsState { get; }

    /// <summary>
    /// True when "Always speak" has a word to say in the false case. Attachments, for example,
    /// has no "no attachments" wording, so Always would be indistinguishable from Only when true.
    /// </summary>
    public bool HasFalseWord { get; }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private SpeakMode _speakMode;

    partial void OnEnabledChanged(bool value) => _onChanged();
    partial void OnSpeakModeChanged(SpeakMode value) => _onChanged();

    public RowFieldSetting ToSetting() => new() { Id = Id, Enabled = Enabled, SpeakMode = SpeakMode };

    /// <summary>Accessible name for the list row. Checked state is reported by the checkbox itself.</summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// Backs the Message List Fields window: which fields each kind of list row speaks, in what order,
/// and whether field labels are spoken. Every mutation saves immediately (there is no OK/Cancel),
/// which is also what pushes the change out to rows that are already on screen.
/// </summary>
public sealed partial class RowFieldsViewModel : ObservableObject
{
    private readonly IRowLayoutService _rowLayoutService;
    private readonly IConfigService _configService;

    private RowLayouts _layouts;
    private bool _suppressSave;

    /// <summary>Raised when the view should speak something. The View owns the announcement call.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    public RowFieldsViewModel(IRowLayoutService rowLayoutService, IConfigService configService)
    {
        _rowLayoutService = rowLayoutService;
        _configService    = configService;

        _layouts = _rowLayoutService.Load();

        try { _showFieldLabels = _configService.Load().MessageListShowFieldLabels; }
        catch { _showFieldLabels = false; }

        _selectedRowKind = RowKinds[0];
        LoadFieldsForSelectedKind();
    }

    // ── row type ──────────────────────────────────────────────────────────────

    public IReadOnlyList<RowKindOption> RowKinds { get; } =
    [
        new(RowKind.Message,      "Messages"),
        new(RowKind.Conversation, "Conversation groups"),
        new(RowKind.SenderGroup,  "Sender and recipient groups"),
    ];

    [ObservableProperty]
    private RowKindOption _selectedRowKind;

    partial void OnSelectedRowKindChanged(RowKindOption value) => LoadFieldsForSelectedKind();

    // ── fields ────────────────────────────────────────────────────────────────

    /// <summary>The selected row kind's fields, in spoken order.</summary>
    public ObservableCollection<RowFieldRow> Fields { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedIsState))]
    [NotifyPropertyChangedFor(nameof(SpeakWhenTrue))]
    [NotifyPropertyChangedFor(nameof(SpeakAlways))]
    [NotifyPropertyChangedFor(nameof(CanMoveUp))]
    [NotifyPropertyChangedFor(nameof(CanMoveDown))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private RowFieldRow? _selectedField;

    public bool HasSelection    => SelectedField is not null;
    public bool SelectedIsState => SelectedField?.IsState == true;

    public bool CanMoveUp   => SelectedField is not null && Fields.IndexOf(SelectedField) > 0;
    public bool CanMoveDown => SelectedField is not null && Fields.IndexOf(SelectedField) < Fields.Count - 1;

    // Radio proxies for the selected state field's speak mode. Paired bool properties are the
    // repo's standard shape for a radio group bound to a non-bool source.
    public bool SpeakWhenTrue
    {
        get => SelectedField?.SpeakMode == SpeakMode.WhenTrue;
        set { if (value && SelectedField is { } f) { f.SpeakMode = SpeakMode.WhenTrue; RefreshSpeakMode(); } }
    }

    public bool SpeakAlways
    {
        get => SelectedField?.SpeakMode == SpeakMode.Always;
        set { if (value && SelectedField is { } f) { f.SpeakMode = SpeakMode.Always; RefreshSpeakMode(); } }
    }

    private void RefreshSpeakMode()
    {
        OnPropertyChanged(nameof(SpeakWhenTrue));
        OnPropertyChanged(nameof(SpeakAlways));
    }

    // ── labels ────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _showFieldLabels;

    partial void OnShowFieldLabelsChanged(bool value)
    {
        if (_suppressSave) return;
        try
        {
            var cfg = _configService.Load();
            cfg.MessageListShowFieldLabels = value;
            _configService.Save(cfg);
        }
        catch { /* config unwritable — the in-session preview still reflects the choice */ }

        UpdatePreview();
        // Saving the layout is what re-speaks open rows; the labels flag rides along with it.
        SaveLayouts();
    }

    // ── preview ───────────────────────────────────────────────────────────────

    /// <summary>The sample row exactly as it would be spoken with the current settings.</summary>
    [ObservableProperty]
    private string _preview = string.Empty;

    // ── commands ──────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedField is not { } field) return;
        var from = Fields.IndexOf(field);
        var to   = from + delta;
        if (to < 0 || to >= Fields.Count) return;

        // Move, not remove+insert: keeps the selection and the item's UIA container intact.
        Fields.Move(from, to);

        RefreshMoveGuards();
        SaveLayouts();
        UpdatePreview();

        Announce($"Moved {(delta < 0 ? "up" : "down")}. Position {to + 1} of {Fields.Count}.",
                 AnnouncementCategory.Result);
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var kind = SelectedRowKind.Kind;
        _layouts.Set(kind, RowFieldCatalog.DefaultLayout(kind));
        LoadFieldsForSelectedKind();
        SaveLayouts();

        Announce($"{SelectedRowKind.Name} fields reset to defaults.", AnnouncementCategory.Result);
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    private void LoadFieldsForSelectedKind()
    {
        var kind = SelectedRowKind.Kind;

        _suppressSave = true;
        try
        {
            Fields.Clear();
            foreach (var setting in _layouts.For(kind))
            {
                if (RowFieldCatalog.Find(kind, setting.Id) is not { } def) continue;
                Fields.Add(new RowFieldRow(def, setting, OnFieldChanged));
            }
        }
        finally { _suppressSave = false; }

        SelectedField = Fields.FirstOrDefault();
        RefreshMoveGuards();
        UpdatePreview();
    }

    private void OnFieldChanged()
    {
        if (_suppressSave) return;
        RefreshSpeakMode();
        SaveLayouts();
        UpdatePreview();
    }

    private void RefreshMoveGuards()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    private void SaveLayouts()
    {
        if (_suppressSave) return;
        _layouts.Set(SelectedRowKind.Kind, Fields.Select(f => f.ToSetting()).ToList());
        _rowLayoutService.Save(_layouts);
    }

    private void UpdatePreview()
    {
        var kind   = SelectedRowKind.Kind;
        var layout = Fields.Select(f => f.ToSetting()).ToList();
        var text   = RowSpeechBuilder.Build(kind, SampleValues(kind), layout, ShowFieldLabels);

        Preview = string.IsNullOrEmpty(text)
            ? "Nothing selected — this row would be silent."
            : text;
    }

    /// <summary>
    /// A synthetic row that exercises every field, so turning any of them on shows something.
    /// Positional, in <see cref="RowFieldCatalog.For"/> order.
    /// </summary>
    internal static object?[] SampleValues(RowKind kind) => kind switch
    {
        RowKind.Message =>
        [
            "Follow up",        // flag
            "unread",           // status
            true,               // attachments
            "Chris Lee",        // from
            "Budget review",    // subject
            "Lunch tomorrow?",  // preview
            "2:14P",            // date
            true,               // unread
            false,              // replied
            false,              // forwarded
            "Sales Team",       // to
            "Work — Archive",   // folder
            false,              // mailing list
        ],
        RowKind.Conversation =>
        [
            "Budget review", 3, "Chris Lee", "Follow up", true, "Lunch tomorrow?", "2:14P",
        ],
        RowKind.SenderGroup =>
        [
            "Chris Lee", 3, "Follow up", true, "Lunch tomorrow?", "2:14P", "Budget review",
        ],
        _ => [],
    };

    private void Announce(string text, AnnouncementCategory category) =>
        AnnouncementRequested?.Invoke(text, category);
}

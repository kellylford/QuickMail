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

    /// <summary>
    /// The row the user had selected when the window opened, so the preview shows what THAT message
    /// would say. A synthetic sample is flagged, has an attachment and a source folder — none of
    /// which most real rows have — so previewing one made the window look inconsistent with what
    /// the list actually said.
    /// </summary>
    private readonly object? _sampleRow;

    private RowLayouts _layouts;
    private bool _suppressSave;

    /// <summary>Raised when the view should speak something. The View owns the announcement call.</summary>
    public event Action<string, AnnouncementCategory>? AnnouncementRequested;

    /// <param name="sampleRow">
    /// The currently selected message (or group) to preview. Null falls back to a synthetic sample.
    /// </param>
    public RowFieldsViewModel(IRowLayoutService rowLayoutService, IConfigService configService,
        object? sampleRow = null)
    {
        _rowLayoutService = rowLayoutService;
        _configService    = configService;
        _sampleRow        = sampleRow;

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
    [NotifyPropertyChangedFor(nameof(SelectedIsOn))]
    [NotifyPropertyChangedFor(nameof(SpeakWhenTrue))]
    [NotifyPropertyChangedFor(nameof(SpeakAlways))]
    [NotifyPropertyChangedFor(nameof(CanMoveUp))]
    [NotifyPropertyChangedFor(nameof(CanMoveDown))]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    private RowFieldRow? _selectedField;

    partial void OnSelectedFieldChanged(RowFieldRow? value)
    {
        // Recompute under the flag so the note's own change handler stays quiet, then announce
        // once here — whether or not the text differs from the field the user just left.
        _selectionChanging = true;
        try { UpdateSelectedFieldNote(); }
        finally { _selectionChanging = false; }

        AnnounceSelectedFieldNote();
    }

    public bool HasSelection    => SelectedField is not null;
    public bool SelectedIsState => SelectedField?.IsState == true;

    /// <summary>
    /// Whether the selected field is turned on. Gates the speak-mode radios: <c>RowSpeechBuilder</c>
    /// skips a disabled field before it ever looks at <see cref="SpeakMode"/>, so offering the
    /// choice for a field that is off offers a setting that cannot do anything — which is half of
    /// what #558 reported ("uncheck the field, then set speak only when true", and nothing happens).
    /// Move Up/Down already say "Not spoken" for the same reason.
    /// </summary>
    public bool SelectedIsOn => SelectedField?.Enabled == true;

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

    /// <summary>
    /// Guidance about the selected field, shown in the options pane and spoken as a hint when the
    /// selection lands on a field that has one. Two jobs: the overlap between "Read status
    /// (combined)" and the separate Unread/Replied/Forwarded fields, which all produce the word
    /// "unread" and so say it twice when both halves are on; and telling the user why the
    /// speak-mode radios are unavailable on a field that is turned off (#558).
    /// </summary>
    [ObservableProperty]
    private string _selectedFieldNote = string.Empty;

    /// <summary>True while a selection change is recomputing the note, so it announces once.</summary>
    private bool _selectionChanging;

    /// <summary>
    /// Speaks the note when the user toggles the selected field and that changes what the note
    /// says. The selection path announces separately, in <see cref="OnSelectedFieldChanged"/>.
    /// </summary>
    partial void OnSelectedFieldNoteChanged(string value)
    {
        if (!_selectionChanging) AnnounceSelectedFieldNote();
    }

    /// <summary>
    /// Speaks the selected field's note, if it has one. A
    /// <see cref="AnnouncementCategory.Hint"/>, which the View does not interrupt, so it follows
    /// the field name rather than talking over it. Silence is never announced — a field with no
    /// note must not produce "" as an utterance.
    ///
    /// <para>Landing on a field announces unconditionally rather than only when the text changed:
    /// two fields can carry the <em>same</em> note, since "Turn this field on…" applies to every
    /// state field that is off. Keying the announcement off value inequality meant the second of
    /// two such fields said nothing at all — arrowing from Mailing list to Watched explained the
    /// first and left the second looking like it behaved differently.</para>
    /// </summary>
    private void AnnounceSelectedFieldNote()
    {
        if (!string.IsNullOrEmpty(SelectedFieldNote))
            Announce(SelectedFieldNote, AnnouncementCategory.Hint);
    }

    // Fields that say the same thing as "status", and therefore double up with it.
    private static readonly string[] StatusParts = ["unread", "replied", "forwarded"];

    private const string StatusName = "Read status (combined)";

    private void UpdateSelectedFieldNote()
    {
        if (SelectedField is not { } field) { SelectedFieldNote = string.Empty; return; }

        bool StatusOn()  => Fields.Any(f => f.Id == "status" && f.Enabled);
        bool AnyPartOn() => Fields.Any(f => StatusParts.Contains(f.Id) && f.Enabled);

        SelectedFieldNote = field.Id switch
        {
            // Both halves on. Only worth saying when this field is itself spoken — an off field
            // cannot double anything, and its own note (below) is the more useful one.
            "status" when field.Enabled && AnyPartOn() =>
                "Says one word: replied, forwarded, unread, or read. Unread, Replied or Forwarded "
                + "is also on, so that word is said twice — turn this off to use those instead.",

            _ when StatusParts.Contains(field.Id) && field.Enabled && StatusOn() =>
                $"{StatusName} is also on and already says this, so it is said twice. "
                + $"Turn {StatusName} off to use this field on its own.",

            // Why the speak-mode radios are greyed out.
            _ when field.IsState && !field.Enabled =>
                "Turn this field on to choose when it is spoken.",

            "status" =>
                "Says one word: replied, forwarded, unread, or read — whichever applies first, so "
                + "a replied message never says unread. Off by default: Unread, Replied and "
                + "Forwarded say the same things separately and can each be set to speak only "
                + "when true.",

            _ => string.Empty,
        };
    }

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

        // Say when the field being moved is off. Moving a field that is not spoken changes nothing
        // you can hear, and without this the move reads as though it did something.
        var offNote = field.Enabled ? string.Empty : " Not spoken.";
        Announce($"Moved {(delta < 0 ? "up" : "down")}. Position {to + 1} of {Fields.Count}.{offNote}",
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
        // Ticking the selected field's check box is what makes the speak-mode radios available,
        // so the gate has to re-evaluate here and not only when the selection moves.
        OnPropertyChanged(nameof(SelectedIsOn));
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
        var text   = RowSpeechBuilder.Build(kind, ValuesForPreview(kind), layout, ShowFieldLabels);

        Preview = string.IsNullOrEmpty(text)
            ? "No fields turned on — this row would be silent."
            : text;

        UpdateSelectedFieldNote();
    }

    /// <summary>
    /// The real selected row when it matches the row type being edited, otherwise the synthetic
    /// sample. Editing conversation-group fields while a message is selected still needs something
    /// to show, and vice versa.
    /// </summary>
    private object?[] ValuesForPreview(RowKind kind)
    {
        if (_sampleRow is not null && MatchesKind(_sampleRow, kind))
            return RowFieldCatalog.ValuesFor(kind, _sampleRow);
        return SampleValues(kind);
    }

    private static bool MatchesKind(object row, RowKind kind) => kind switch
    {
        RowKind.Message      => row is MailMessageSummary,
        RowKind.Conversation => row is ConversationGroup,
        RowKind.SenderGroup  => row is SenderGroup,
        _ => false,
    };

    /// <summary>
    /// A synthetic row for the preview when the real selection is not of this kind. Positional, in
    /// <see cref="RowFieldCatalog.For"/> order — one entry per catalog field, so appending a field
    /// to the catalog means appending one here too. <c>RowSpeechBuilder.Build</c> tolerates a short
    /// array by treating the tail as absent, which is why a missing entry showed up as a field that
    /// silently never previews rather than as a crash (<c>watched</c> did exactly that).
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
            false,              // watched
            true,               // not on server
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

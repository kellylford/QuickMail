using System;
using System.ComponentModel;
using System.Collections.Generic;

namespace QuickMail.Models;

/// <summary>
/// One field's placement in a row's spoken string. The field's position in the containing
/// list <em>is</em> its spoken order — there is no sort key, because the whole list is
/// rewritten on every change.
/// </summary>
public sealed class RowFieldSetting
{
    /// <summary>Catalog id. Ids not present in <see cref="RowFieldCatalog"/> are dropped on load.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Whether this field is spoken at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>State fields only; ignored for text and count fields.</summary>
    public SpeakMode SpeakMode { get; set; } = SpeakMode.WhenTrue;

    /// <summary>
    /// The word this field says in place of the shipped one (#807): a state's true word, a count's
    /// unit, a text field's label. Null means the shipped word; empty means say nothing extra.
    /// Left out of rowlayout.json while null, so an untouched file looks as it always has.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Word { get; set; }

    /// <summary>A state field's word when false, under <see cref="SpeakMode.Always"/> (#807).</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? FalseWord { get; set; }

    public RowFieldSetting Clone() => new()
    {
        Id = Id, Enabled = Enabled, SpeakMode = SpeakMode, Word = Word, FalseWord = FalseWord,
    };
}

/// <summary>
/// Everything the row renderer needs to compose a spoken string, in one object so each row
/// carries a single extra binding. Replaced wholesale (never mutated) when the user changes the
/// layout, so raising PropertyChanged on the owning property re-speaks every realized row.
/// </summary>
public sealed record RowSpeechSettings(
    RowLayouts Layouts, bool ShowLabels, RowFieldSeparator Separator = RowFieldSeparator.Period)
{
    /// <summary>Shipped defaults, used before the service loads and by tests.</summary>
    public static RowSpeechSettings Default { get; } = new(RowFieldCatalog.DefaultLayouts(), false);
}

/// <summary>
/// Where every list row on this thread gets its <see cref="RowSpeechSettings"/>, so a row's name binding
/// has a source that needs no visual tree (#928). A row with no realized container is named through
/// a stand-in that is never in the window, so a binding that looks up through the tree cannot
/// resolve there: the name stayed empty until the dispatcher turned, and a screen reader asking
/// in that gap heard the fallback, then the default layout instead of the user's.
/// <para>One per thread rather than one per process because rows bind on their UI thread, and the
/// tests run their windows on separate threads in parallel, where one test's layout must not reach
/// another's rows. The app has one UI thread and so one of these.</para>
/// </summary>
public sealed class RowSpeechSource : INotifyPropertyChanged
{
    [ThreadStatic] private static RowSpeechSource? _current;

    /// <summary>This thread's source. Read it on the UI thread.</summary>
    public static RowSpeechSource Current => _current ??= new RowSpeechSource();

    private RowSpeechSettings _settings = RowSpeechSettings.Default;

    public RowSpeechSettings Settings
    {
        get => _settings;
        set
        {
            _settings = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Settings)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The user's spoken field layouts, one per row kind. Persisted as <c>rowlayout.json</c>.
/// </summary>
public sealed class RowLayouts
{
    public List<RowFieldSetting> Message      { get; set; } = [];
    public List<RowFieldSetting> Conversation { get; set; } = [];
    public List<RowFieldSetting> SenderGroup  { get; set; } = [];

    /// <summary>The layout for a row kind.</summary>
    public List<RowFieldSetting> For(RowKind kind) => kind switch
    {
        RowKind.Message      => Message,
        RowKind.Conversation => Conversation,
        RowKind.SenderGroup  => SenderGroup,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Replaces the layout for a row kind.</summary>
    public void Set(RowKind kind, List<RowFieldSetting> fields)
    {
        switch (kind)
        {
            case RowKind.Message:      Message      = fields; break;
            case RowKind.Conversation: Conversation = fields; break;
            case RowKind.SenderGroup:  SenderGroup  = fields; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    /// <summary>A deep copy. Used to hand the renderer an immutable-by-convention snapshot.</summary>
    public RowLayouts Clone() => new()
    {
        Message      = Message.ConvertAll(f => f.Clone()),
        Conversation = Conversation.ConvertAll(f => f.Clone()),
        SenderGroup  = SenderGroup.ConvertAll(f => f.Clone()),
    };

    /// <summary>
    /// Brings a loaded layout back in line with the catalog, in place:
    /// duplicate and unknown ids are dropped (a file written by a newer version), and catalog
    /// fields the file does not mention are appended disabled (a field added since the file was
    /// written). The user's existing order is never disturbed.
    /// </summary>
    public void Reconcile()
    {
        foreach (var kind in new[] { RowKind.Message, RowKind.Conversation, RowKind.SenderGroup })
            Set(kind, Reconcile(kind, For(kind)));
    }

    private static List<RowFieldSetting> Reconcile(RowKind kind, List<RowFieldSetting>? loaded)
    {
        var result = new List<RowFieldSetting>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        foreach (var setting in loaded ?? [])
        {
            if (string.IsNullOrEmpty(setting.Id)) continue;
            if (RowFieldCatalog.Find(kind, setting.Id) is not { } def) continue;   // unknown id
            if (!seen.Add(setting.Id)) continue;                                   // duplicate
            // Words enter here from a file anyone can edit or import (#807): no line breaks or
            // direction overrides in a row's name, nothing longer than a word, nothing stored
            // that only repeats the shipped word.
            setting.Word      = RowFieldWords.Normalize(setting.Word, RowFieldWords.DefaultWord(def));
            setting.FalseWord = RowFieldWords.Normalize(setting.FalseWord, def.FalseWord ?? string.Empty);
            result.Add(setting);
        }

        foreach (var def in RowFieldCatalog.For(kind))
        {
            if (seen.Contains(def.Id)) continue;
            result.Add(new RowFieldSetting { Id = def.Id, Enabled = false, SpeakMode = SpeakMode.WhenTrue });
        }

        return result;
    }
}

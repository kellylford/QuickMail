using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MimeKit;
using QuickMail.Helpers;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class ComposeViewModel : ObservableObject, IDisposable
{
    private readonly ISendMailService _smtp;
    private readonly IAccountService _accountService;
    private readonly ICredentialService _credentials;
    private readonly IMailService _imap;
    private readonly ILocalDraftService _drafts;
    private readonly ITemplateService _templateService;
    private readonly IMarkdownService _markdown;

    [ObservableProperty] private string _to = string.Empty;
    [ObservableProperty] private string _cc = string.Empty;
    [ObservableProperty] private string _bcc = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _subject = string.Empty;

    /// <summary>What kind of composition this is; drives the window title prefix.</summary>
    public ComposeKind ComposeKind { get; private set; } = ComposeKind.NewMessage;

    /// <summary>
    /// Dynamic window title: "{subject or kind} - {mode} - QuickMail".
    /// The subject leads so the taskbar and Alt+Tab identify the message; the
    /// compose mode follows so the editing format is always visible.
    /// </summary>
    public string WindowTitle
    {
        get
        {
            var kindLabel = ComposeKind switch
            {
                ComposeKind.Reply        => "Reply",
                ComposeKind.ReplyAll     => "Reply All",
                ComposeKind.Forward      => "Forward",
                ComposeKind.EditDraft    => "Draft",
                ComposeKind.NewDraft     => "Draft",
                ComposeKind.EditTemplate => "Edit Template",
                _                        => "New Message",
            };
            var lead = string.IsNullOrWhiteSpace(Subject) ? kindLabel : Subject.Trim();
            var mode = CurrentMode switch
            {
                ComposeMode.Markdown => "Markdown",
                ComposeMode.Html     => "HTML",
                _                    => "Plain Text",
            };
            return $"{lead} - {mode} - QuickMail";
        }
    }
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeDisplay))]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyPropertyChangedFor(nameof(IsHtmlMode))]
    [NotifyPropertyChangedFor(nameof(IsMarkdownMode))]
    [NotifyPropertyChangedFor(nameof(IsPreviewAvailable))]
    [NotifyPropertyChangedFor(nameof(IsFormattingAvailable))]
    [NotifyPropertyChangedFor(nameof(IsSpellNavAvailable))]
    private ComposeMode _currentMode = ComposeMode.PlainText;

    /// <summary>True in HTML mode — some formatting (underline) is HTML-only.</summary>
    public bool IsHtmlMode => CurrentMode == ComposeMode.Html;

    /// <summary>True in Markdown mode — drives preview availability.</summary>
    public bool IsMarkdownMode => CurrentMode == ComposeMode.Markdown;

    /// <summary>True in Markdown or HTML mode — the preview window is available in both.</summary>
    public bool IsPreviewAvailable => CurrentMode == ComposeMode.Markdown || CurrentMode == ComposeMode.Html;

    /// <summary>
    /// Formatting commands work in both rich modes: HTML applies real formatting,
    /// Markdown inserts the equivalent syntax. Only Plain Text has none.
    /// </summary>
    public bool IsFormattingAvailable => CurrentMode != ComposeMode.PlainText;

#pragma warning disable CA1822 // [NotifyPropertyChangedFor] raises PropertyChanged for this property on the instance, so it must be an instance member
    public bool IsSpellNavAvailable => true;
#pragma warning restore CA1822
    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>
    /// Which announcement category the View reads the accompanying <see cref="StatusText"/> under.
    ///
    /// Status is right for background progress ("Sending…", "Saving draft…") and wrong for the
    /// outcome of a command the user just invoked. Announcing "Send failed" as Status meant anyone
    /// who had turned background-progress announcements off pressed Send, watched the button come
    /// back enabled, and was told nothing at all — the report in #396.
    ///
    /// One-shot: it returns to Status after every raise, so a message assigned through neither
    /// helper cannot inherit a latched Result and interrupt. Same contract as
    /// <see cref="MainViewModel.StatusAnnouncementCategory"/>.
    /// </summary>
    public AnnouncementCategory StatusCategory { get; private set; } = AnnouncementCategory.Status;

    /// <summary>
    /// Sets <see cref="StatusText"/> as the outcome of a user action, so it is announced as a
    /// Result and survives the background-progress preference. Public because the compose window
    /// reports a few outcomes of its own (address checks, address-book adds) and must classify
    /// them the same way rather than announcing alongside the binding.
    ///
    /// The reset is safe because StatusText's PropertyChanged fires synchronously: the View has
    /// already read the category by the time this returns.
    /// </summary>
    public void SetStatusOutcome(string text) => SetStatus(text, AnnouncementCategory.Result);

    /// <summary>Sets <see cref="StatusText"/> as background progress.</summary>
    private void SetProgress(string text) => SetStatus(text, AnnouncementCategory.Status);

    private void SetStatus(string text, AnnouncementCategory category)
    {
        StatusCategory = category;
        // Cleared first so an identical message repeats. StatusText is an [ObservableProperty] with
        // an equality check, so pressing Send twice with the same empty recipient box would raise no
        // notification the second time — the user presses the button and hears nothing, which is the
        // symptom this change exists to remove. The empty value is never announced; the View skips
        // empty status text.
        StatusText = string.Empty;
        StatusText = text;
        StatusCategory = AnnouncementCategory.Status;
    }

    [ObservableProperty] private bool _isBusy = false;
    [ObservableProperty] private ObservableCollection<AccountModel> _senderAccounts = [];
    [ObservableProperty] private AccountModel? _senderAccount;
    [ObservableProperty] private ObservableCollection<AttachmentModel> _attachments = [];

    private string? _inReplyToMessageId;
    private string? _draftMessageId;

    /// <summary>
    /// True when the locally-stored row this window owns was created BY this window, rather than
    /// opened from Drafts (#637). Only such a row may be discarded when the user declines to save
    /// on the way out — declining your own edits never means deleting the draft you opened.
    /// </summary>
    private bool _draftCreatedByThisWindow;

    /// <summary>
    /// True when Seed handed this window a draft that already existed (#637). Sticky: nothing this
    /// window does afterwards can make it the author of that message.
    /// </summary>
    private bool _seededExistingDraft;

    /// <summary>
    /// The account whose Drafts folder holds this window's stored row (#637). Not the same thing
    /// as <see cref="SenderAccount"/>: the user can change the From account at any time, and the
    /// store keys rows on (id, account, folder), so the row does not move on its own.
    /// </summary>
    private Guid _draftAccountId;

    /// <summary>
    /// Keeps the upload pass off this window's stored draft while it is open (#637). Released in
    /// <see cref="Dispose"/>, and re-taken whenever the row this window owns changes.
    /// </summary>
    private IDisposable? _draftClaim;
    private string? _draftFolderName;

    /// <summary>
    /// The server-side id of this draft, when the server has a copy. Tracked separately from
    /// <see cref="_draftMessageId"/> — which after an offline save is a local id — because it is
    /// what the upload must pass as "replace this one" (#637). Null until a save reaches a server.
    /// </summary>
    private string? _draftServerMessageId;
    private bool _isDirty;
    private bool _isSent;
    private ComposeMode _seededMode = ComposeMode.PlainText;
    private string? _seededHtmlBody;

    public bool IsDirty => _isDirty;
    public bool IsSent  => _isSent;

    /// <summary>The compose mode stored in the draft when it was last saved; PlainText for new composes.</summary>
    public ComposeMode SeededMode => _seededMode;

    public event Action? CloseRequested;

    /// <summary>
    /// Rows this window has changed in the store, for the message list to re-read (#637).
    /// <para>
    /// It carries KEYS, not a description of what happened. Two earlier events described the change
    /// instead -- one said "stored", one said "the row is gone" with a reason -- and every defect
    /// they produced was the same shape: the description and the store disagreed. A row was marked
    /// pending that had just been uploaded; a re-key was announced as an upload; a refusal was put
    /// on a row and never taken off; a discard left a row pointing at nothing. The store is the only
    /// thing that actually knows, so the list asks it.
    /// </para>
    /// <para>
    /// The optional message is the one thing the store cannot answer: WHY a row went. Only this
    /// window knows whether a draft left the list because it uploaded or because its sender changed,
    /// and a row vanishing with nothing said is a defect this branch has already fixed twice.
    /// </para>
    /// </summary>
    public event Action<IReadOnlyList<DraftRowKey>, string?>? DraftRowsChanged;

    /// <summary>
    /// Set by the View to show a Yes/No confirmation dialog.
    /// Parameters: message, title. Returns true when the user confirms.
    /// Mirrors the pattern on MainViewModel — see CLAUDE.md MVVM Rules.
    /// </summary>
    public Func<string, string, bool>? ConfirmationRequested { get; set; }

    /// <summary>
    /// The IAccountService this compose window was built with. Exposed so the address book
    /// opened from here (Ctrl+Shift+B) can name accounts in its account filter — without it
    /// every synced account reads as an indistinguishable "Synced contact".
    /// </summary>
    public IAccountService AccountService => _accountService;

    public ComposeViewModel(ISendMailService smtp, IAccountService accountService, ICredentialService credentials, IMailService imap, ILocalDraftService drafts, ITemplateService templateService, IMarkdownService? markdown = null)
    {
        _smtp = smtp;
        _accountService = accountService;
        _credentials = credentials;
        _imap = imap;
        _drafts = drafts;
        _templateService = templateService;
        _markdown = markdown ?? new MarkdownService();
        _attachments.CollectionChanged += (_, _) =>
        {
            _isDirty = true;
            OnPropertyChanged(nameof(AttachmentSummaryText));
            // Removing the oversized attachment is what the 25 MB refusal asked for.
            RetireNoticeIfConditionResolved();
        };
    }

    // Dirty-marking partial methods — fired by the [ObservableProperty] source generator
    // Editing any of these can be what resolves a refusal, so the notice is re-tested rather than
    // assumed stale -- typing in To used to clear a reason about a missing password (#637).
    partial void OnToChanged(string value)      { _isDirty = true; RetireNoticeIfConditionResolved(); }
    partial void OnCcChanged(string value)      => _isDirty = true;
    partial void OnBccChanged(string value)     => _isDirty = true;
    partial void OnSubjectChanged(string value) => _isDirty = true;
    partial void OnBodyChanged(string value)    => _isDirty = true;

    // Not a dirty-marking hook: choosing a different account is what resolves three of the five
    // send refusals -- no sender, an invalid address, a missing password -- and leaving the
    // sentence standing after the user has done exactly what it asked is the same defect as
    // clearing one that is still true (#637).
    partial void OnSenderAccountChanged(AccountModel? value)
    {
        _passwordCheckedFor = Guid.Empty;   // a different account is a different answer
        RetireNoticeIfConditionResolved();
    }

    public void Seed(ComposeModel model)
    {
        _inReplyToMessageId = model.InReplyToMessageId;
        _draftMessageId     = model.DraftMessageId;
        _draftFolderName    = model.DraftFolderName;
        // Anything Seed arrives with already existed; this window is a visitor to it. ANY id —
        // local or server — counts: saving a server draft offline writes a local row and drops the
        // cached server row, so discarding on "no" would leave the Drafts list showing neither
        // until the next sync brought the server copy back.
        _seededExistingDraft      = model.DraftMessageId != null;
        _draftCreatedByThisWindow = false;
        _draftAccountId           = model.AccountId;

        // Take the row out of the sweep's reach for as long as this window is open. A window that
        // mints its id later re-claims in SaveDraftCoreAsync.
        _draftClaim?.Dispose();
        _draftClaim = null;
        if (LocalMessageId.IsLocal(_draftMessageId))
            ClaimStoredRow(_draftAccountId, _draftFolderName, _draftMessageId!);
        // A local id means the copy on this computer is the newer one and the server's id (if any)
        // is recorded against the stored draft, not here; the next save reads it back from there.
        _draftServerMessageId = LocalMessageId.IsLocal(model.DraftMessageId) ? null : model.DraftMessageId;
        IsDraftPendingUpload  = LocalMessageId.IsLocal(model.DraftMessageId);
        // Why the server would not take this draft, if it said (#637). Shown in this window
        // because this is where Enter on a draft lands — and the guide's instruction, "fix what
        // the server objected to and save again", is unactionable without it.
        // Through SetNotice, not straight at the field: a notice with no condition attached is one
        // only a successful save may clear, and assigning the field directly left this one looking
        // like a stale sentence nobody owned -- so the To assignment a few lines below erased what
        // the server said before the window was ever shown (#637).
        SetNotice(model.DeliveryNotice);
        ComposeKind         = model.Kind;
        OnPropertyChanged(nameof(WindowTitle));

        To      = model.To;
        Cc      = model.Cc;
        Bcc     = model.Bcc;
        Subject = model.Subject;
        Body    = model.Body;

        Attachments.Clear();
        foreach (var att in model.Attachments)
            Attachments.Add(att);

        // Remember the original mode so the View can restore it after wiring up event handlers.
        _seededMode    = model.Mode;
        _seededHtmlBody = model.Mode == ComposeMode.Html ? model.HtmlBody : null;

        // Loading existing data (reply, forward, or re-opened draft) is not itself a dirty edit
        _isDirty = false;

        var accounts = _accountService.LoadAccounts();
        SenderAccounts = new ObservableCollection<AccountModel>(accounts);
        SenderAccount = SenderAccounts.FirstOrDefault(a => a.Id == model.AccountId)
                        ?? SenderAccounts.FirstOrDefault(a => a.IsDefault)
                        ?? SenderAccounts.FirstOrDefault();

        // Auto-append signature if this is a new compose (not a draft re-open) and the
        // account has a signature configured. Drafts already have the signature in the body.
        if (model.DraftMessageId == null && SenderAccount != null && !string.IsNullOrWhiteSpace(SenderAccount.Signature))
        {
            var sig = SenderAccount.Signature;
            // Add separator if body already has content (reply/forward)
            if (!string.IsNullOrWhiteSpace(Body) && !Body.EndsWith('\n'))
                Body += "\n";
            if (!string.IsNullOrWhiteSpace(Body))
                Body += "\n-- \n";
            Body += sig;
            _isDirty = false; // signature insertion is not a user edit
        }
    }

    /// <summary>
    /// Whether the last save left the message somewhere it can be got back from — this computer,
    /// the server, or both (#637).
    /// <para>The window uses this to decide whether closing is safe. It used to decide by looking
    /// for the word "failed" in the status text, which silently stopped working the moment
    /// local-first saving introduced a failure that does not contain it: "No Drafts folder found
    /// on this account." closed the window and destroyed the message.</para>
    /// </summary>
    public bool LastSaveKeptTheMessage { get; private set; }

    /// <summary>
    /// The one durable, focusable place this window explains why a draft is not where the user
    /// expects: what the server said when it refused an upload, or that a save could not be
    /// written to this computer.
    /// <para>Deliberately ONE field carrying both. When a local write fails while a server reason
    /// is on screen, the local failure wins: it is the more urgent of the two -- the user's latest
    /// changes are not saved anywhere -- and the server's reason is not lost, since it lives in
    /// MessageSummary.send_failed_reason and comes back when the draft is reopened. Cleared by any
    /// save that succeeds, on either leg, so it can never outlive what it describes (#637).</para>
    /// </summary>
    [ObservableProperty] private string _deliveryNotice = string.Empty;

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        // FIRST, before any early return. The window consults this to decide whether closing is
        // safe, so a stale `true` from an earlier successful save would let a REFUSED save close
        // the window and lose the message.
        LastSaveKeptTheMessage = false;

        // Both of these refuse the save, which means the window will refuse to close -- so both
        // need the durable field, not only the status line. Without it the user pressed the close
        // key, nothing happened, nothing was said, and pressing it again did the same thing for
        // ever. The 25 MB case is an ordinary mistake, not a corner (#637).
        var account = SenderAccount;
        if (account == null)
        {
            const string noSender = "This message has no sender account selected, so it cannot be "
                                  + "saved. Choose an account in the From field.";
            SetNotice(noSender, () => SenderAccount == null);
            SetStatusOutcome(noSender);
            SaveRefused?.Invoke();
            return;
        }

        if (Attachments.Sum(a => a.FileSize) > 25_000_000)
        {
            const string tooBig = "This message cannot be saved: its attachments add up to more "
                                + "than 25 MB. Remove some and try again.";
            SetNotice(tooBig, () => Attachments.Sum(a => a.FileSize) > 25_000_000);
            SetStatusOutcome(tooBig);
            SaveRefused?.Invoke();
            return;
        }

        IsBusy = true;
        SetProgress("Saving draft…");
        try
        {
            await SaveDraftCoreAsync(account);
            LastSaveKeptTheMessage = true;
            SetStatusOutcome(IsDraftPendingUpload
                ? "Draft saved on this computer. It will go to the server when you are back online."
                : "Draft saved.");
        }
        catch (DraftFolderMissingException)
        {
            // Durable, like the auto-save arm: the window then refuses to close, and until now the
            // only account of why was an announcement and an unfocusable line of text (#637).
            SetNotice("This account has no Drafts folder yet, so this message cannot be saved. "
                    + "Connect the account once so QuickMail can find it.");
            SetStatusOutcome("No Drafts folder found on this account.");
            SaveRefused?.Invoke();
        }
        catch (Exception ex)
        {
            // Both legs failed, so the message is nowhere. Said in QuickMail's own words rather
            // than the exception's: this one is thrown from the LOCAL leg by preference, and in
            // --online mode -- where App deliberately never creates the schema -- that leg always
            // fails, so the durable sentence the user reads and acts on became "SQLite Error 1:
            // no such table: MessageSummary" for what was really an unreachable server. Naming both
            // halves is true whichever of them is the cause; the detail goes to the log (#637).
            LogService.Log("SaveDraftAsync: the draft could not be saved locally or on the server", ex);
            const string nowhere = "This message could not be saved. QuickMail could not write it "
                                 + "to this computer and could not reach the server. Keep this "
                                 + "window open and try again.";
            SetStatusOutcome(nowhere);
            // Durable too. This is the MORE severe half of the case auto-save was given a field
            // for: the user asked for this save, it did not happen, and the window then refuses to
            // close -- with the only account of why in an announcement and an unfocusable line of
            // text (#637).
            SetNotice(nowhere);
            SaveRefused?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// True when this draft is stored on this computer but not yet on the server (#637). Drives the
    /// wording of the save and auto-save outcomes, so that "saved" never overstates where it is.
    /// </summary>
    [ObservableProperty] private bool _isDraftPendingUpload;

    /// <summary>
    /// Saves the current compose state: to this computer first, then to the server's Drafts folder.
    /// <para>The order is the fix for #637. The local write needs no network and is what the user is
    /// promised when they press Save, so it happens first and unconditionally; the upload is a
    /// best-effort second leg whose failure downgrades the wording rather than losing the message.
    /// Only a local-store failure throws out of here.</para>
    /// </summary>
    private async Task SaveDraftCoreAsync(AccountModel account, CancellationToken externalCt = default)
    {
        // The cached folder list first: asking the server where Drafts is, is itself a network call,
        // and needing it before saving would put the network back in front of an offline save.
        // Wrapped because --online mode creates no SQLite schema at all, so this throws there
        // rather than answering — see the runtime-modes table in docs/ARCHITECTURE.md.
        if (_draftFolderName == null)
        {
            try
            {
                _draftFolderName = await _drafts.ResolveDraftsFolderNameAsync(account.Id);
            }
            catch (Exception ex)
            {
                LogService.Log("SaveDraftCoreAsync: no local folder cache to resolve Drafts from", ex);
            }
        }

        if (_draftFolderName == null)
        {
            using var findCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var findCombined = CancellationTokenSource.CreateLinkedTokenSource(findCts.Token, externalCt);
            try
            {
                _draftFolderName = await _imap.FindDraftsFolderNameAsync(account.Id, findCombined.Token);
            }
            catch (OperationCanceledException) when (externalCt.IsCancellationRequested)
            {
                // Only when the CALLER cancelled. Rethrowing every cancellation also swallowed the
                // 30-second lookup timeout on the linked token, and since AutoSaveAsync's
                // cancellation arm is deliberately silent, a folder lookup that stalled produced
                // no notice, no status and no local write -- silence, which is worse than the wrong
                // sentence it replaced. A timeout falls through to the broad catch below and is
                // reported as the missing-folder outcome, which is what it is (#637).
                throw;
            }
            catch (Exception ex)
            {
                // Offline on an account whose folders have never synced. There is nowhere to file
                // the draft even locally, so this is still the missing-folder outcome.
                LogService.Log("SaveDraftCoreAsync: could not resolve the Drafts folder", ex);
            }
        }

        if (_draftFolderName == null)
            throw new DraftFolderMissingException();

        var compose = BuildComposeModel(account.Id);

        // Leg 1 — this computer. Needs no network, so it is what makes the save survive a failing
        // connection. It is NOT guaranteed to be available: --online mode runs with no local store,
        // and there the server leg below is the only one there has ever been.
        string? localId = null;
        Exception? localFailure = null;
        try
        {
            // Whether a row already existed under this window's key BEFORE the save. Read from
            // the field, not from a local that is null on entry to every call — using a local
            // marked the second save of an OPENED draft as this window's own creation.
            var hadStoredRow = LocalMessageId.IsLocal(_draftMessageId);

            // Move the row first if the user changed the sender. The store keys rows on
            // (id, account, folder), so saving under the new account while the old row still
            // exists left TWO — and the old one was still uploaded, into the mailbox of the
            // account the user had just moved away from (#637).
            var orphaned = await RekeyStoredRowIfSenderChangedAsync(account);
            // Recorded BEFORE the save, not after it. The re-key has already taken the old id off
            // this window, so a save that throws on the next line used to lose the key altogether
            // -- and the old account's row stayed queued and went up into the mailbox the user had
            // just moved the draft out of (#637).
            if (orphaned is { } justOrphaned && !_orphanedRows.Contains(justOrphaned))
                _orphanedRows.Add(justOrphaned);
            var announcedRekey = false;

            var saved = await _drafts.SaveAsync(account, compose, _draftFolderName, _draftMessageId, externalCt);

            // Write-then-delete: the replacement row exists by the time the old one goes. An entry
            // is removed only once its row is really gone, so a discard that throws is retried by
            // the next save rather than leaving a queued row under an account the user has moved
            // away from. Iterated over a copy: the list is mutated inside the loop.
            foreach (var old in _orphanedRows.ToList())
            {
                try
                {
                    await _drafts.DiscardAsync(old.Account, old.Folder, old.Id);
                    // BOTH keys: the old row has gone and the new one now exists. Raising only
                    // the old one is why a re-keyed draft vanished from the list and did not
                    // reappear under the account it had moved to until a folder reload.
                    // Only when the sender changed on THIS save. Retrying a discard that failed
                    // earlier moves nothing, and saying so would report a move on an ordinary save
                    // some time later.
                    DraftRowsChanged?.Invoke(
                        [new DraftRowKey(old.Account, old.Folder, old.Id),
                         new DraftRowKey(account.Id, _draftFolderName!, saved.MessageId)],
                        orphaned != null ? "Draft moved to another account." : null);
                    announcedRekey = true;
                    _orphanedRows.Remove(old);
                }
                catch (Exception ex)
                {
                    // A leftover row in the old account is visible and recoverable; losing the
                    // draft is not.
                    // Names both: the Invoke above is inside this try, so a handler that throws
                    // lands here too, after the discard has already succeeded.
                    LogService.Log("SaveDraftCoreAsync: could not drop the re-keyed row, or the list refused it", ex);
                }
            }
            // Minted here rather than opened, so this window may drop it again if the user
            // declines to save on the way out.
            if (!_seededExistingDraft && !hadStoredRow && LocalMessageId.IsLocal(saved.MessageId))
                _draftCreatedByThisWindow = true;
            localId               = saved.MessageId;
            // The save re-arms the upload (the store clears the reason), so the notice must go
            // with it rather than sit there describing a refusal that is no longer true. A SEND
            // refusal is left alone: storing the draft does not give it a recipient.
            ForgetPasswordCheck();
            ClearNoticeIfResolved();
            // Re-claim on the id this save just minted. Seed can only claim a draft that was
            // ALREADY local, which leaves out every new compose, reply and forward — precisely the
            // rows the upload pass skips claimed drafts to protect. Without this the pass uploads
            // the draft mid-edit, deletes the row and its bytes, and the next auto-save re-creates
            // it having lost the supersedes header, filling Drafts with copies (#637).
            ClaimStoredRow(account.Id, _draftFolderName, saved.MessageId);
            // Skipped when the re-key above has already raised BOTH keys for this same save --
            // raising the new key twice is harmless (the second refresh is an idempotent in-place
            // copy) but it doubles the work and makes the event sequence hard to read.
            // Gated on whether the re-key branch actually raised, not on whether there WAS one:
            // its raise sits inside a try whose catch swallows a failed discard, and gating on
            // "there was an orphan" meant a locked database there left the new row unreported.
            if (_draftFolderName != null && !announcedRekey)
                DraftRowsChanged?.Invoke(
                    [new DraftRowKey(account.Id, _draftFolderName, saved.MessageId)], null);
            // Record the owner. Seed knows it only for a message that was already stored, so a new
            // compose had none at all — and the re-key above, which is what stops a sender change
            // leaving a second row behind, never fired for exactly the messages most likely to
            // have their sender changed.
            _draftAccountId       = account.Id;
            _draftMessageId       = saved.MessageId;
            _draftServerMessageId ??= saved.SupersededServerMessageId;
            _isDirty              = false;
            IsDraftPendingUpload  = true;
        }
        catch (Exception ex)
        {
            localFailure = ex;
            LogService.Log("SaveDraftCoreAsync: local draft store unavailable; server-only this time", ex);
        }

        // What leg 1 would have told us, when leg 1 did not run to the end. Both of these read
        // from the store rather than from what THIS call happened to write, because a save that
        // follows a successful one already has a stored row and leg 1 is not what put it there.
        if (localId == null && _draftMessageId is { } storedId && LocalMessageId.IsLocal(storedId))
        {
            // Discarding by a local assigned only inside leg 1 skipped the tidy-up whenever leg 1
            // threw on a later save: the row stayed marked pending while this window reported the
            // draft fully saved, and the next sweep uploaded it a second time. Leg 1 reads
            // _draftMessageId for exactly this reason; leg 2 did not (#637).
            localId = storedId;
            if (_draftFolderName != null)
            {
                try
                {
                    // The supersedes id lives on the stored row, and the only place it was read
                    // back is inside leg 1. Appending with a null replace id leaves the server
                    // holding the old draft AND the new one -- the duplication the replaces header
                    // exists to prevent.
                    _draftServerMessageId ??= await _drafts.GetSupersededServerIdAsync(
                        account.Id, _draftFolderName, storedId);
                }
                catch (Exception ex)
                {
                    LogService.Log("SaveDraftCoreAsync: could not read back the superseded server id", ex);
                }
            }
        }

        // Leg 2 — the server. Best-effort when leg 1 worked; the only hope when it did not.
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, externalCt);

            var serverId = await _imap.AppendDraftAsync(
                account.Id, compose, _draftServerMessageId, combined.Token);

            // Recorded BEFORE the discard, and the discard given its own catch. The upload has
            // happened by this point; letting a failed local delete jump to the outer catch left
            // the id unrecorded and the row still marked pending, so the window reported "it will
            // go to the server when you are back online" about a draft already ON the server — and
            // the next sweep uploaded it again, against a stale id, duplicating it in Drafts.
            _draftMessageId       = serverId;
            _draftServerMessageId = serverId;
            _isDirty              = false;
            IsDraftPendingUpload  = false;
            // Cleared here too, not only in leg 1. A local write that keeps failing while the
            // server leg succeeds left the notice saying the message was saved nowhere -- and
            // telling the user to keep trying Save Draft -- about a message that had just reached
            // the server. The durable channel is the one that must not be stale (#637).
            ForgetPasswordCheck();
            ClearNoticeIfResolved();

            // The server holds it now, so the local copy is redundant — and leaving it would show
            // the draft twice in Drafts once the folder syncs.
            // Folder checked as well as the id: the id may now come from a stored row rather than
            // from leg 1, and the re-key assigns _draftFolderName from a nullable source.
            if (localId != null && _draftFolderName != null)
            {
                var droppedFolder = _draftFolderName;
                try
                {
                    await _drafts.DiscardAsync(account.Id, _draftFolderName, localId);
                    DraftRowsChanged?.Invoke(
                        [new DraftRowKey(account.Id, droppedFolder, localId)], "Draft uploaded.");
                }
                catch (Exception ex)
                {
                    // A leftover row is visible and recoverable; reporting a save that plainly
                    // happened as a failure is not. The event is deliberately inside the try: if
                    // the discard failed the row really is still there, and telling the list it
                    // has gone would hide a draft that still needs uploading.
                    LogService.Log("SaveDraftCoreAsync: uploaded, but the local copy could not be dropped", ex);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Log("SaveDraftCoreAsync: draft kept locally; server upload failed", ex);

            // Both legs failed, so the draft genuinely is nowhere. Anything short of throwing here
            // would report a save that did not happen.
            //
            // The LOCAL failure is thrown, not this one. Rethrowing the server exception put an
            // SMTP or IMAP message into a sentence the user now reads off a durable field -- naming
            // a cause that is not why the message was lost, and pointing at a remedy that would not
            // help. What went wrong here is that this computer would not take the draft (#637).
            if (localFailure != null)
                throw localFailure;
        }
    }

    private sealed class DraftFolderMissingException : Exception
    {
        public DraftFolderMissingException() { }
        public DraftFolderMissingException(string message) : base(message) { }
        public DraftFolderMissingException(string message, Exception innerException) : base(message, innerException) { }
    }

    // ── Auto-save ──────────────────────────────────────────────────────────────

    private CancellationTokenSource _autoSaveCts = new();

    /// <summary>Cancels any in-flight autosave. Called by the window on closing.</summary>
    public void CancelAutoSave() => _autoSaveCts.Cancel();

    /// <summary>
    /// Re-arms auto-save after a close that did not happen (#637).
    /// <para>The window cancels auto-save on the way into its Closing handler, before the user has
    /// answered the save prompt. Answering Cancel leaves the window open with a dead token, so every
    /// later tick failed the local leg and the delivery-notice field -- the durable, focusable one
    /// this feature added precisely because it has to be trustworthy -- told the user their changes
    /// could not be written to this computer. The store was fine; the window had cancelled its own
    /// token.</para>
    /// </summary>
    public void ResumeAutoSave()
    {
        if (!_autoSaveCts.IsCancellationRequested) return;
        _autoSaveCts.Dispose();
        _autoSaveCts = new CancellationTokenSource();

        // The notice is deliberately NOT cleared here any more. It was added alongside the
        // cancellation arm below, and the two cancel out: once a cancelled tick stops writing that
        // sentence, the only thing that can still write it is a store failure that is REALLY
        // happening -- so wiping it on Cancel took away a live warning that the user's changes are
        // unsaved, and collapsed the field with it. Silent loss, which is the one outcome this
        // whole feature exists to prevent (#637).
    }

    public void Dispose()
    {
        // Before the token: releasing the claim makes the draft uploadable again, and the window
        // is going away, so there is nothing left to protect (#637).
        _draftClaim?.Dispose();
        _draftClaim = null;
        _autoSaveCts.Cancel();
        _autoSaveCts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Visual status-row text, e.g. "Auto-saved 3:42 PM". Never announced on success.</summary>
    [ObservableProperty] private string _autoSaveText = string.Empty;

    /// <summary>True once a failed auto-save has been announced; reset by the next success.</summary>
    private bool _autoSaveFailureAnnounced;

    /// <summary>
    /// Raised when an auto-save fails for the first time since the last success,
    /// so the View can announce it once instead of nagging every interval.
    /// <para>On this branch it means the message exists NOWHERE -- a far more severe thing than
    /// the "could not reach the server" it used to mean. The compose window announces it, but an
    /// announcement is gated by a user setting, so the text is also put in the delivery-notice
    /// field, which is durable and focusable (#637).</para>
    /// </summary>
    public event Action<string>? AutoSaveFailed;

    /// <summary>
    /// A save the user asked for did not keep the message, and the reason is in
    /// <see cref="DeliveryNotice"/> (#637). The window puts focus on that field: closing with a
    /// failed save already did, a plain Ctrl+S did not, so a failed save was indistinguishable
    /// from one that worked for anyone who does not hear the announcement.
    /// </summary>
    public event Action? SaveRefused;

    /// <summary>
    /// Periodic background draft save. Quiet by design: success only updates
    /// <see cref="AutoSaveText"/> (visual status), and failures are announced once.
    /// Skips templates (saving a template edit as a mail draft would be wrong),
    /// untouched composes, and composes with no content worth keeping.
    /// </summary>
    public async Task AutoSaveAsync()
    {
        if (!_isDirty || _isSent || IsBusy) return;
        if (ComposeKind == ComposeKind.EditTemplate) return;
        var account = SenderAccount;
        if (account == null) return;
        if (!HasAutoSavableContent()) return;

        IsBusy = true;
        try
        {
            await SaveDraftCoreAsync(account, _autoSaveCts.Token);
            // Being offline is no longer a failure: the draft is on disk either way, and the only
            // difference is where else it is. Saying "failed" for a saved draft was the wrong alarm.
            AutoSaveText = IsDraftPendingUpload
                ? $"Auto-saved on this computer {DateTime.Now:t}"
                : $"Auto-saved {DateTime.Now:t}";
            _autoSaveFailureAnnounced = false;
        }
        catch (OperationCanceledException)
        {
            // Not a failure. The window cancels the auto-save token on its way into the close
            // handler, before the user has answered the prompt, so a tick landing inside that
            // window arrives here -- and reporting it as the local store refusing the write put
            // "your latest changes are not saved" into the durable field while the prompt was
            // still on screen and the store was perfectly healthy (#637).
        }
        catch (DraftFolderMissingException ex)
        {
            // Its own arm: this one never reached the store at all, so "could not save this to your
            // computer" would name the wrong cause and the remedy it gives -- try Save Draft -- is
            // one the user has already been told cannot work on this account (#637).
            LogService.Log("AutoSaveAsync: no Drafts folder for this account", ex);
            AutoSaveText   = "Auto-save failed";
            SetNotice("This account has no Drafts folder yet, so this message cannot be saved. "
                    + "Connect the account once so QuickMail can find it.");
            if (!_autoSaveFailureAnnounced)
            {
                _autoSaveFailureAnnounced = true;
                AutoSaveFailed?.Invoke("Auto-save failed. This account has no Drafts folder.");
            }
        }
        catch (Exception ex)
        {
            // Now means the local store refused the write — the draft really is nowhere.
            LogService.Log("AutoSaveAsync: draft auto-save failed", ex);
            AutoSaveText = "Auto-save failed";
            // Into the delivery-notice field as well as the announcement. On this branch this
            // catch means the LOCAL store refused the write, so the message exists nowhere at
            // all -- and an announcement is gated by a user setting, while AutoSaveText sits in a
            // plain TextBlock with no focus stop. The notice field is durable and reachable, and
            // the next successful save clears it (#637).
            // Deliberately does not say "saved nowhere": an earlier save may well have put an
            // older copy on disk, and overstating that was the same fault as telling the user a
            // draft could not be recovered when the store was merely busy.
            SetNotice("Auto-save could not write this message to your computer, so your "
                    + "latest changes are not saved. Keep this window open and try Save Draft.");
            if (!_autoSaveFailureAnnounced)
            {
                _autoSaveFailureAnnounced = true;
                AutoSaveFailed?.Invoke("Auto-save failed. Your draft is not saved.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Puts a sentence on the durable notice field, remembering the CONDITION that produced it.
    /// </summary>
    /// <param name="why">What the user is told.</param>
    /// <param name="stillHolds">
    /// Re-evaluated before the notice is cleared. Null for a sentence that describes a moment
    /// rather than a state -- a save that failed once -- which any later success may clear.
    /// </param>
    private void SetNotice(string why, Func<bool>? stillHolds = null)
    {
        // Recorded before the field is published: assigning DeliveryNotice raises PropertyChanged,
        // and anything reading the condition from that notification would have seen the PREVIOUS
        // one still attached.
        _noticeStillHolds = stillHolds;
        DeliveryNotice    = why;
    }

    /// <summary>
    /// Clears the notice unless the condition it describes is still true.
    /// <para>This replaced a sticky bool that said only "a send was once refused". A bool cannot
    /// say WHICH condition, so it protected the wrong things and erased the right ones: fixing the
    /// sender account or removing the oversized attachment left the sentence standing, typing in
    /// the To field wiped a refusal that had nothing to do with To, and once set it was never
    /// cleared -- so a later save FAILURE inherited the protection and its sentence outlived the
    /// successful save that disproved it. Asking the condition costs nothing and cannot be one
    /// path behind the code (#637).</para>
    /// </summary>
    private void ClearNoticeIfResolved()
    {
        if (_noticeStillHolds is { } holds && holds()) return;
        DeliveryNotice    = string.Empty;
        _noticeStillHolds = null;
    }

    /// <summary>
    /// Retires the notice if — and only if — the condition it describes has been resolved.
    /// <para>Called when the user edits something that could BE the fix. It differs from
    /// <see cref="ClearNoticeIfResolved"/> in what it does with a notice that carries no condition:
    /// a save that succeeds disproves such a sentence, but an edit disproves nothing, so an edit
    /// must leave it alone. Treating "no condition" as "resolved" here is what made one character
    /// typed into To erase what the server said about a refused draft — the very sentence the user
    /// opened the draft to read, and the one the guide tells him to act on (#637).</para>
    /// </summary>
    private void RetireNoticeIfConditionResolved()
    {
        if (_noticeStillHolds is null) return;
        ClearNoticeIfResolved();
    }

    /// <summary>
    /// A send the user asked for that cannot proceed: says why on the durable, focusable field as
    /// well as the status line, and asks the window to put focus there (#637).
    /// </summary>
    private void RefuseSend(string why, Func<bool> stillHolds)
    {
        // The condition travels with the sentence, so a save cannot clear a refusal that is still
        // true and nothing has to remember to clear one that is not.
        SetNotice(why, stillHolds);
        SetStatusOutcome(why);
        SaveRefused?.Invoke();
    }

    /// <summary>
    /// The condition behind the current <see cref="DeliveryNotice"/>, or null when the sentence
    /// describes a one-off failure rather than a state.
    /// </summary>
    private Func<bool>? _noticeStillHolds;

    /// <summary>
    /// Whether the account has no password stored. Asked from a property setter as well as from
    /// Send — re-testing the refusal reads the Windows credential store, which can throw — so a
    /// store that will not answer must not take the compose window down. It is also not evidence
    /// the password is there, so on failure the refusal stands (#637).
    /// </summary>
    private bool StoredPasswordMissing(AccountModel account)
    {
        // Answered from the last read for the same account. This runs from a property setter, so
        // without it every character typed into To was one synchronous Windows credential-store
        // call on the UI thread -- measured at 24 reads for 24 keystrokes. The account is the only
        // thing the answer depends on, and Send reads the store directly rather than through here,
        // so a password stored while the window is open is still picked up the moment it matters.
        if (_passwordCheckedFor == account.Id) return _passwordMissing;
        try
        {
            _passwordMissing    = string.IsNullOrEmpty(_credentials.GetPassword(account.Id));
            _passwordCheckedFor = account.Id;   // ONLY a real answer is worth keeping
            return _passwordMissing;
        }
        catch (Exception ex)
        {
            // Deliberately not cached. Caching the catch meant one credential-store hiccup pinned
            // "no stored password" for the life of the window, on the durable field, with no way
            // back. The refusal still stands for this call -- an unreadable store is not evidence
            // the password is there -- but the next ask reads again (#637).
            LogService.Log("ComposeViewModel: could not read the stored password while re-testing a refusal", ex);
            return true;
        }
    }

    /// <summary>
    /// Forgets the cached password answer, so the next ask reads the store again.
    /// </summary>
    /// <remarks>
    /// Called when a save succeeds. Signing in again in Manage Accounts changes nothing this window
    /// can observe, so without this the user did exactly what the refusal asked and the sentence
    /// stayed on the field through an edit AND through a successful save -- the failure the
    /// re-evaluated condition was written to remove, re-entering through the cache put in front of
    /// it (#637).
    /// </remarks>
    private void ForgetPasswordCheck() => _passwordCheckedFor = Guid.Empty;

    private Guid _passwordCheckedFor;
    private bool _passwordMissing;

    /// <summary>
    /// Stored rows left behind by sender changes, still waiting to be dropped. Empty once they are.
    /// </summary>
    /// <remarks>
    /// A list, not one slot. A single slot kept the first orphan and silently dropped any later
    /// one, so changing the sender A to B with the discard failing and then B to C left B's row
    /// queued and unreferenced -- and the sweep uploaded the user's draft into the account they had
    /// moved it out of, which is the whole defect the re-key exists to prevent (#637).
    /// </remarks>
    private readonly List<(Guid Account, string Folder, string Id)> _orphanedRows = [];

    /// <summary>Something worth keeping: any recipient, subject, body text, or attachment.</summary>
    private bool HasAutoSavableContent()
    {
        if (!string.IsNullOrWhiteSpace(To) || !string.IsNullOrWhiteSpace(Cc) || !string.IsNullOrWhiteSpace(Bcc))
            return true;
        if (!string.IsNullOrWhiteSpace(Subject)) return true;
        if (Attachments.Count > 0) return true;
        if (CurrentMode == ComposeMode.Html)
            return !(RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).IsEmpty;
        return !string.IsNullOrWhiteSpace(Body);
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        // These refusals get the durable field for the same reason the Save ones do: an
        // unfocusable status line is silence for a user running with custom announcements off, and
        // Send appearing to do nothing is indistinguishable from a key that does not work. Same
        // window, same conditions -- they were fixed on the Save path and left here (#637).
        if (string.IsNullOrWhiteSpace(To))
        {
            RefuseSend("This message has no recipient, so it cannot be sent. Add an address in the To field.",
                       () => string.IsNullOrWhiteSpace(To));
            return;
        }

        var account = SenderAccount;
        if (account == null)
        {
            RefuseSend("This message has no sender account selected, so it cannot be sent. "
                     + "Choose an account in the From field.", () => SenderAccount == null);
            return;
        }

        if (Attachments.Sum(a => a.FileSize) > 25_000_000)
        {
            RefuseSend("This message cannot be sent: its attachments add up to more than 25 MB. "
                     + "Remove some and try again.",
                       () => Attachments.Sum(a => a.FileSize) > 25_000_000);
            return;
        }

        // The From header is built from this address, so an account whose "email address" is not one
        // — a login name typed into the field before it was validated at save time — produces
        // MAIL FROM:<name>, which the server rejects with a message about the recipient or the
        // sender that explains nothing. Say what is actually wrong, and where to fix it. (#396)
        if (!EmailAddressValidator.IsValid(account.Username))
        {
            RefuseSend($"\"{account.Username}\" is not a valid email address, so this message has no "
                     + "valid sender. Fix the email address for this account in Manage Accounts.",
                       () => SenderAccount is { } a && !EmailAddressValidator.IsValid(a.Username));
            return;
        }

        var password = _credentials.GetPassword(account.Id);
        if (string.IsNullOrEmpty(password) && account.AuthType == Models.AuthType.Password)
        {
            RefuseSend("This account has no stored password, so this message cannot be sent. "
                     + "Open Manage Accounts and sign in again.",
                       () => SenderAccount is { } a &&
                             a.AuthType == Models.AuthType.Password &&
                             StoredPasswordMissing(a));
            return;
        }

        IsBusy = true;
        SetProgress("Sending…");
        try
        {
            var compose = BuildComposeModel(account.Id);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _smtp.SendAsync(compose, account, password, cts.Token);
            SetStatusOutcome("Message sent.");
            _isSent = true;

            // Append to Sent folder (best-effort — fire and forget so it doesn't block the UI).
            // Providers that auto-save sent mail (e.g. Gmail) may produce a duplicate; that is
            // harmless and the background sync will eventually deduplicate via UID tracking.
            _ = Task.Run(async () =>
            {
                try
                {
                    using var sentCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await _imap.AppendToSentAsync(account.Id, compose, sentCts.Token);
                }
                catch (Exception ex)
                {
                    LogService.Log("SendAsync: failed to append to Sent folder", ex);
                }
            });

            // Drop the local copy first: it is the one that would otherwise sit in Drafts forever,
            // since nothing on the server will ever reconcile it away (#637).
            if (LocalMessageId.IsLocal(_draftMessageId) && _draftFolderName != null)
            {
                try
                {
                    // The row's OWNER, not whoever is selected as sender now. Changing the From
                    // account and pressing Send before an auto-save re-keys the row meant this
                    // matched nothing, the row survived, and the next sweep uploaded a draft of an
                    // already-sent message into the old account's mailbox (#637).
                    var owner = _draftAccountId != Guid.Empty ? _draftAccountId : account.Id;
                    var sentId = _draftMessageId!;
                    await _drafts.DiscardAsync(owner, _draftFolderName, sentId);
                    // No message: the compose window closing on a successful send is the outcome,
                    // and the send reports itself.
                    DraftRowsChanged?.Invoke([new DraftRowKey(owner, _draftFolderName, sentId)], null);
                }
                catch (Exception ex)
                {
                    LogService.Log("SendAsync: failed to discard the local draft after send", ex);
                }
            }

            // Delete the draft from the server (if one was saved there)
            if (!string.IsNullOrEmpty(_draftServerMessageId) && _draftFolderName != null)
            {
                try
                {
                    using var delCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    // The account that HOLDS that server draft, not whoever is selected as
                    // sender now. Using the sender asked the NEW account's mailbox to trash a
                    // UID the OLD account issued, destroying whatever held that number there.
                    var draftOwner = _draftAccountId != Guid.Empty ? _draftAccountId : account.Id;
                    await _imap.MoveToTrashAsync(draftOwner, _draftFolderName, _draftServerMessageId, delCts.Token);
                }
                catch (Exception ex)
                {
                    LogService.Log("SendAsync: failed to delete draft after send", ex);
                }
            }

            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            SetStatusOutcome($"Send failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Takes (or moves) this window's claim to the row it now owns, so the upload pass leaves it
    /// alone while it is open. Idempotent, and safe to call with an incomplete key.
    /// </summary>
    private void ClaimStoredRow(Guid accountId, string? folderName, string messageId)
    {
        if (accountId == Guid.Empty || folderName == null) return;

        // Take the new claim BEFORE releasing the old one. Releasing first leaves the row
        // unclaimed for the width of one call — on every auto-save — and a sweep landing in that
        // gap uploads the draft and discards the bytes this window is still editing. Claims are
        // reference-counted precisely so the two can overlap.
        var previous = _draftClaim;
        _draftClaim = DraftClaims.Claim(accountId, folderName, messageId);
        previous?.Dispose();
    }

    /// <summary>
    /// Moves this window's stored row to the newly chosen sender account, if it changed (#637).
    /// <para>The row is deleted under the old account and re-created under the new one by the save
    /// that follows. Leaving it behind meant the old row stayed put and was still uploaded — to
    /// the mailbox of the account the user had explicitly moved away from.</para>
    /// </summary>
    /// <returns>
    /// The row the sender change has orphaned, for the caller to drop AFTER the replacement is
    /// written; null when there is nothing to drop.
    /// </returns>
    private async Task<(Guid Account, string Folder, string Id)?> RekeyStoredRowIfSenderChangedAsync(AccountModel account)
    {
        if (_draftAccountId == Guid.Empty || _draftAccountId == account.Id) return null;

        var oldAccountId = _draftAccountId;
        var oldFolder    = _draftFolderName;
        var oldId        = _draftMessageId;

        // Every one of these is wrong the moment the sender changes, whether or not there is a local
        // row to re-key. Gating them on there being one is what left the ONLINE case broken:
        //
        //   The server id belongs to the OLD account's Drafts. Carried across, the next save hands it
        //   to AppendDraftAsync, which resolves the NEW account's Drafts and does AddFlags(Deleted) +
        //   Expunge on that UID -- destroying whatever message happens to hold it there. UIDs are
        //   small per-folder integers, so the collision is ordinary, and an expunge is not a Trash.
        //
        //   The message id is the old account's UID, so leg 1 would pass it as previousMessageId and
        //   drop the NEW account's cached row of that number.
        //
        //   The folder name is the old account's Drafts name, so on any pair that names them
        //   differently ("[Gmail]/Drafts" vs "Drafts") the draft is filed where it cannot be seen.
        //
        // Cleared unconditionally, above the local-row guard (#637).
        _draftServerMessageId = null;
        _draftMessageId       = null;
        // The owner moves FIRST. Resolving the folder reads the local store, which throws outright
        // in --online mode (no schema) and can throw on a locked database. Leaving that between the
        // id reset and the owner update stranded _draftAccountId on the OLD account while the next
        // save advanced the ids to the new one -- and Send then trashed that UID in the old
        // account's Drafts, which is another account's message.
        _draftAccountId       = account.Id;
        try
        {
            _draftFolderName = await _drafts.ResolveDraftsFolderNameAsync(account.Id) ?? _draftFolderName;
        }
        catch (Exception ex)
        {
            // Keep the old name rather than none. The draft still uploads -- the sweep queries by
            // account and the backend resolves the real Drafts itself -- but it may not appear in
            // the new account's Drafts list until a sync.
            LogService.Log("RekeyStoredRowIfSenderChangedAsync: could not resolve the new account's Drafts folder", ex);
        }

        // Only a row minted on this computer can be re-keyed. A server draft has no local row to
        // move, and the copy it has on the old account's server is that account's to keep.
        if (!LocalMessageId.IsLocal(oldId) || oldFolder == null) return null;

        // Handed back rather than dropped here. Deleting the old row before the replacement is
        // written is delete-then-write: if the save that follows fails -- a locked database is the
        // case this branch keeps meeting -- the only stored copy is gone, the row has already been
        // taken out of the list, and the user has been told the draft moved to another account.
        // The caller drops it once the new row exists (#637).
        return (oldAccountId, oldFolder, oldId!);
    }

    /// <summary>
    /// Drops what THIS WINDOW wrote locally, for the user who answers "no" to the save-on-close
    /// prompt (#637).
    /// <para>Needed because the local leg now always succeeds: auto-save has very likely already
    /// persisted the message, and without this the sweep would upload to the user's mailbox a
    /// message they explicitly declined to keep. Before local-first saving, an offline auto-save
    /// simply failed and nothing persisted, so "no" needed no help to mean no.</para>
    /// </summary>
    public async Task DiscardLocalCopyAsync()
    {
        // ONLY a row this window created. A window that merely opened an existing offline draft
        // must leave it exactly as it found it: "no, do not save these changes" is not "delete the
        // draft I opened". Discarding regardless destroyed the draft, its attachments and its
        // stored bytes, with no prompt and no copy in Trash — for a user who answered a question
        // about their edits (#637).
        if (!_draftCreatedByThisWindow) return;
        if (!LocalMessageId.IsLocal(_draftMessageId) || _draftFolderName == null) return;

        var owner = _draftAccountId != Guid.Empty ? _draftAccountId : SenderAccount?.Id ?? Guid.Empty;
        if (owner == Guid.Empty) return;

        try
        {
            var droppedId = _draftMessageId!;
            await _drafts.DiscardAsync(owner, _draftFolderName, droppedId);
            // The row goes with it. Without this, a Drafts list left open while auto-save ran kept
            // a row pointing at a message that no longer exists, and opening it answered that its
            // saved copy was missing -- the ghost row in another costume (#637).
            // No message: the row going IS what the user has just asked for.
            DraftRowsChanged?.Invoke([new DraftRowKey(owner, _draftFolderName, droppedId)], null);
            _draftMessageId = null;
        }
        catch (Exception ex)
        {
            LogService.Log("DiscardLocalCopyAsync", ex);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    // ── Compose modes ──────────────────────────────────────────────────────────

    /// <summary>Status-bar label for the active mode, e.g. "Mode: Markdown".</summary>
    public string ModeDisplay => "Mode: " + CurrentMode switch
    {
        ComposeMode.Markdown => "Markdown",
        ComposeMode.Html     => "HTML",
        _                    => "Plain Text",
    };

    /// <summary>
    /// Set by the View. Returns the rich editor's current content serialized as
    /// HTML, Markdown, and plain text. Null until the View wires it.
    /// </summary>
    public Func<RichBodySnapshot>? RichBodyProvider { get; set; }

    /// <summary>
    /// Raised when content must flow into the rich editor (entering HTML mode).
    /// The View converts the HTML fragment into the editor document.
    /// </summary>
    public event Action<string>? LoadHtmlIntoEditorRequested;

    /// <summary>
    /// Raised to insert plain text (e.g. a template) at the rich editor's caret
    /// while in HTML mode.
    /// </summary>
    public event Action<string>? InsertTextIntoEditorRequested;

    /// <summary>
    /// Switches the editing mode, converting the body content. Switching from a
    /// rich mode to Plain Text asks for confirmation because formatting is lost.
    /// Returns true when the switch happened.
    /// </summary>
    public bool SetMode(ComposeMode newMode)
    {
        if (newMode == CurrentMode) return false;

        // Downgrading to plain text discards formatting — confirm first.
        // An unwired ConfirmationRequested (tests) counts as confirmed so the
        // switch is never silently impossible.
        if (newMode == ComposeMode.PlainText && HasFormattingWorthConfirming())
        {
            var confirmed = ConfirmationRequested?.Invoke(
                "Formatting will be lost when switching to Plain Text. Continue?",
                "Switch to Plain Text") ?? true;
            if (!confirmed) return false;
        }

        switch (CurrentMode, newMode)
        {
            case (ComposeMode.PlainText, ComposeMode.Markdown):
                break; // plain text is valid Markdown source — pass through as-is

            case (ComposeMode.PlainText, ComposeMode.Html):
                var htmlToLoad = _seededHtmlBody ?? _markdown.PlainTextToHtml(Body);
                _seededHtmlBody = null; // consume: only used for the initial draft restore
                LoadHtmlIntoEditorRequested?.Invoke(htmlToLoad);
                break;

            case (ComposeMode.Markdown, ComposeMode.Html):
                LoadHtmlIntoEditorRequested?.Invoke(_markdown.ToHtml(Body));
                break;

            case (ComposeMode.Markdown, ComposeMode.PlainText):
                Body = _markdown.ToPlainText(Body);
                break;

            case (ComposeMode.Html, ComposeMode.Markdown):
                var mdSnap = RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty;
                if (mdSnap.IsEmpty && RichBodyProvider != null)
                    LogService.Debug("SetMode Html→Markdown: provider returned empty snapshot");
                Body = mdSnap.Markdown;
                break;

            case (ComposeMode.Html, ComposeMode.PlainText):
                var ptSnap = RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty;
                if (ptSnap.IsEmpty && RichBodyProvider != null)
                    LogService.Debug("SetMode Html→PlainText: provider returned empty snapshot");
                Body = ptSnap.PlainText;
                break;
        }

        CurrentMode = newMode;
        return true;
    }

    private bool HasFormattingWorthConfirming() => CurrentMode switch
    {
        ComposeMode.Markdown => !string.IsNullOrWhiteSpace(Body) && Body != _markdown.ToPlainText(Body),
        ComposeMode.Html     => !(RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).IsEmpty,
        _                    => false,
    };

    /// <summary>Renders the current Markdown body as a full HTML document for the preview pane.</summary>
    public string RenderPreviewHtml() => _markdown.WrapDocument(_markdown.ToHtml(Body), Subject);

    /// <summary>Returns the rendered HTML body fragment for the preview window, without any wrapper or styles.</summary>
    public string GetBodyHtml() => CurrentMode switch
    {
        ComposeMode.Markdown => _markdown.ToHtml(Body),
        ComposeMode.Html     => (RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).Html,
        _                    => string.Empty,
    };

    /// <summary>Called by the View when the rich editor content changes (RichTextBox has no Body binding).</summary>
    public void MarkBodyDirty() => _isDirty = true;

    /// <summary>
    /// Opens the template picker. The View subscribes to this event to show the dialog.
    /// </summary>
    public event Func<Task<MessageTemplate?>>? InsertTemplateRequested;

    [RelayCommand]
    private async Task InsertTemplateAsync()
    {
        if (InsertTemplateRequested == null) return;
        var template = await InsertTemplateRequested();
        if (template == null) return;

        var displayName = !string.IsNullOrWhiteSpace(SenderAccount?.DisplayName)
            ? SenderAccount!.DisplayName
            : !string.IsNullOrWhiteSpace(SenderAccount?.Username)
                ? SenderAccount!.Username
                : string.Empty;
        var now = DateTime.Now;

        var body = template.Body
            .Replace("{sender}", displayName, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", now.ToString("d"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", now.ToString("t"), StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(template.Subject) && string.IsNullOrWhiteSpace(Subject))
            Subject = template.Subject
                .Replace("{sender}", displayName, StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", now.ToString("d"), StringComparison.OrdinalIgnoreCase)
                .Replace("{time}", now.ToString("t"), StringComparison.OrdinalIgnoreCase);

        if (CurrentMode == ComposeMode.Html)
            InsertTextIntoEditorRequested?.Invoke(body);
        else
            Body += body;
        SetStatusOutcome($"Template '{template.Title}' inserted.");
    }

    [RelayCommand]
    private async Task SaveAsTemplateAsync()
    {
        // Templates are plain-text only — in HTML mode use the editor's text rendering.
        var templateBody = CurrentMode == ComposeMode.Html
            ? (RichBodyProvider?.Invoke() ?? RichBodySnapshot.Empty).PlainText
            : Body;
        if (string.IsNullOrWhiteSpace(templateBody))
        {
            SetStatusOutcome("Nothing to save — body is empty.");
            return;
        }

        var template = new MessageTemplate
        {
            Title = Subject.Trim().Length > 0 ? Subject.Trim() : "Untitled",
            Subject = Subject,
            Body = templateBody
        };

        await _templateService.AddAsync(template);
        SetStatusOutcome($"Template saved as '{template.Title}'.");
    }

    /// <summary>
    /// Set by the View to show a multi-select Open File dialog (CLAUDE.md MVVM rules:
    /// Win32 dialogs are View-layer). Returns the chosen paths, or null when cancelled
    /// or unwired (headless/tests).
    /// </summary>
    public Func<string[]?>? OpenFilePathsRequested { get; set; }

    [RelayCommand]
    private async Task AddAttachmentsAsync()
    {
        var paths = OpenFilePathsRequested?.Invoke();
        if (paths == null) return;
        foreach (var path in paths)
            await AddAttachmentFromPathAsync(path);
    }

    /// <summary>
    /// Adds a file as an attachment (shared by AddAttachmentsCommand and clipboard paste).
    /// Reads asynchronously so large attachments don't freeze the window on slow disks.
    /// </summary>
    public async Task AddAttachmentFromPathAsync(string path)
    {
        if (!File.Exists(path)) return;
        var info  = new FileInfo(path);
        var bytes = await File.ReadAllBytesAsync(path);
        Attachments.Add(new AttachmentModel
        {
            FileName    = info.Name,
            ContentType = AttachmentModel.ContentTypeFromFileName(info.Name),
            FileSize    = info.Length,
            Content     = bytes,
        });
    }

    [RelayCommand]
    private void RemoveAttachment(AttachmentModel? attachment)
    {
        if (attachment != null)
            Attachments.Remove(attachment);
    }

    /// <summary>e.g. "3 files, 1.8 MB of 25 MB limit"</summary>
    public string AttachmentSummaryText
    {
        get
        {
            var count = Attachments.Count;
            if (count == 0) return string.Empty;
            var totalBytes = Attachments.Sum(a => a.FileSize);
            var totalDisplay = totalBytes >= 1_048_576
                ? $"{totalBytes / 1_048_576.0:F1} MB"
                : $"{totalBytes / 1_024.0:F0} KB";
            return $"{count} file{(count == 1 ? "" : "s")}, {totalDisplay} of 25 MB limit";
        }
    }

    [RelayCommand]
    private async Task OpenComposeAttachmentAsync(AttachmentModel? attachment)
    {
        if (attachment?.Content == null) return;

        // Sanitized: forwarded/replied attachments carry server-supplied names, so a
        // crafted name (path separators, absolute path) must not escape the temp folder.
        var safeFileName = AttachmentSafety.SanitizeFileName(attachment.FileName);

        if (AttachmentSafety.IsDangerousExtension(safeFileName))
        {
            // CLAUDE.md MVVM Rules: ViewModels must not call MessageBox directly.
            // If the View hasn't wired a confirmation handler, treat that as deny so
            // we never silently open something potentially dangerous.
            var confirmed = ConfirmationRequested?.Invoke(
                $"'{safeFileName}' is an executable file type. Opening it could be dangerous. Continue?",
                "Security Warning") ?? false;
            if (!confirmed) return;
        }

        // Per-attachment subfolder so two files with the same name (invoice.pdf, invoice.pdf
        // from different messages or sessions) don't overwrite each other in %TEMP%\QuickMail.
        var tempDir = Path.Combine(Path.GetTempPath(), "QuickMail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, safeFileName);
        await File.WriteAllBytesAsync(tempPath, attachment.Content);
        Process.Start(new ProcessStartInfo(tempPath) { UseShellExecute = true });
    }

    internal ComposeModel BuildComposeModel(Guid accountId)
    {
        // Resolve the body parts for the active mode. Markdown mode sends the
        // markdown source as the text/plain part (it reads naturally as text);
        // HTML mode sends the stripped plain text. An effectively empty rich body
        // falls back to text/plain only.
        var body = Body;
        string? htmlBody = null;
        switch (CurrentMode)
        {
            case ComposeMode.Markdown when !string.IsNullOrWhiteSpace(Body):
                htmlBody = _markdown.WrapDocument(_markdown.ToHtml(Body), Subject);
                break;

            case ComposeMode.Html:
                if (RichBodyProvider == null)
                    throw new InvalidOperationException("RichBodyProvider must be set before sending in HTML mode.");
                var snapshot = RichBodyProvider.Invoke();
                if (!snapshot.IsEmpty)
                {
                    body     = snapshot.PlainText;
                    htmlBody = _markdown.WrapDocument(snapshot.Html, Subject);
                }
                break;
        }

        return new ComposeModel
        {
            AccountId           = accountId,
            To                  = To,
            Cc                  = Cc,
            Bcc                 = Bcc,
            Subject             = Subject,
            Body                = body,
            Mode                = CurrentMode,
            HtmlBody            = htmlBody,
            InReplyToMessageId  = _inReplyToMessageId,
            DraftMessageId      = _draftMessageId,
            DraftFolderName     = _draftFolderName,
            Attachments         = Attachments.ToList(),
        };
    }

    // ── Factory helpers ────────────────────────────────────────────────────────

    public static ComposeModel CreateReply(MailMessageDetail detail, Guid accountId)
    {
        var subject = detail.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? detail.Subject
            : $"Re: {detail.Subject}";

        var attribution = $"\n\nOn {detail.Date.ToLocalTime():f}, {detail.From} wrote:\n";

        // Fall back to HTML→text conversion when the message has no plain-text part.
        // HTML-only messages otherwise reply with an empty quote — the attribution
        // line with nothing under it (issue #260).
        var plainBody = string.IsNullOrEmpty(detail.PlainTextBody) && !string.IsNullOrEmpty(detail.HtmlBody)
            ? HtmlStripper.ToPlainText(detail.HtmlBody)
            : detail.PlainTextBody;

        var quoted = string.Join("\n", System.Array.ConvertAll(
            plainBody.Split('\n'),
            line => "> " + line));

        return new ComposeModel
        {
            Kind      = ComposeKind.Reply,
            AccountId = accountId,
            To = string.IsNullOrEmpty(detail.ReplyTo) ? detail.From : detail.ReplyTo,
            Subject = subject,
            Body = attribution + quoted,
            InReplyToMessageId = detail.InternetMessageId
        };
    }

    /// <param name="ownAddress">The sender's own email address; excluded from the Cc list to avoid self-addressing.</param>
    public static ComposeModel CreateReplyAll(MailMessageDetail detail, Guid accountId, string ownAddress = "")
    {
        var model = CreateReply(detail, accountId);

        // Also exclude whichever address landed in model.To (the original From or ReplyTo).
        // Otherwise mailing-list senders who were Cc'd on their own message appear on both
        // the To and Cc lines of the reply-all.
        var toAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (InternetAddressList.TryParse(model.To ?? string.Empty, out var modelToList))
        {
            foreach (var a in modelToList.OfType<MailboxAddress>())
                toAddresses.Add(a.Address);
        }
        if (!string.IsNullOrEmpty(ownAddress))
            toAddresses.Add(ownAddress);

        // Merge original To + Cc, excluding the sender's own address and the To recipient,
        // into the new Cc. Use TryParse so empty/malformed address strings return an empty
        // list rather than throwing MimeKit.ParseException.
        _ = InternetAddressList.TryParse(detail.To ?? string.Empty, out var toList);
        _ = InternetAddressList.TryParse(detail.Cc ?? string.Empty, out var ccList);
        var recipients = (toList ?? [])
            .Concat(ccList ?? [])
            .OfType<MailboxAddress>()
            .Where(a => !toAddresses.Contains(a.Address))
            .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        model.Cc   = string.Join(", ", recipients.Select(a => a.ToString()));
        model.Kind = ComposeKind.ReplyAll;
        return model;
    }

    public static ComposeModel CreateForward(MailMessageDetail detail, Guid accountId)
    {
        var subject = detail.Subject.StartsWith("Fwd:", StringComparison.OrdinalIgnoreCase)
                   || detail.Subject.StartsWith("FW:", StringComparison.OrdinalIgnoreCase)
            ? detail.Subject
            : $"Fwd: {detail.Subject}";

        var header = $"\n\n---------- Forwarded message ----------\n"
                   + $"From: {detail.From}\n"
                   + $"Date: {detail.Date.ToLocalTime():f}\n"
                   + $"Subject: {detail.Subject}\n"
                   + $"To: {detail.To}\n\n";

        // Fall back to HTML→text conversion when the message has no plain-text part.
        var plainBody = string.IsNullOrEmpty(detail.PlainTextBody) && !string.IsNullOrEmpty(detail.HtmlBody)
            ? HtmlStripper.ToPlainText(detail.HtmlBody)
            : detail.PlainTextBody;

        var model = new ComposeModel
        {
            Kind      = ComposeKind.Forward,
            AccountId = accountId,
            Subject   = subject,
            Body      = header + plainBody,
        };

        if (!string.IsNullOrEmpty(detail.HtmlBody))
        {
            model.HtmlBody = BuildForwardedHtmlBlock(detail);
            model.Mode     = ComposeMode.Html;
        }

        return model;
    }

    private static string BuildForwardedHtmlBlock(MailMessageDetail detail)
    {
        // Strip outer html/head/body wrappers so we don't nest them inside the blockquote.
        var body = StripHtmlWrappers(detail.HtmlBody);
        var date = detail.Date.ToLocalTime().ToString("f");
        return $"""
            <p>&#160;</p>
            <div>
              <p>---------- Forwarded message ----------<br />
              From: {WebUtility.HtmlEncode(detail.From)}<br />
              Date: {WebUtility.HtmlEncode(date)}<br />
              Subject: {WebUtility.HtmlEncode(detail.Subject)}<br />
              To: {WebUtility.HtmlEncode(detail.To)}</p>
            </div>
            <blockquote style="border-left: 2px solid #ccc; padding-left: 8px; margin-left: 4px;">
            {body}
            </blockquote>
            """;
    }

    private static string StripHtmlWrappers(string html)
    {
        // Remove leading <!DOCTYPE...> and <html...>...</html> wrapper if present so the
        // fragment can be embedded safely inside a blockquote without double html/body nesting.
        var s = html.Trim();

        // Strip <!DOCTYPE ...>
        if (s.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase))
        {
            var end = s.IndexOf('>');
            if (end >= 0) s = s[(end + 1)..].TrimStart();
        }

        // Extract content of <body>…</body> if present.
        var bodyStart = s.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyStart >= 0)
        {
            // Scan past quoted attribute values to find the true end of the opening tag.
            var bodyTagEnd = IndexOfTagClose(s, bodyStart);
            if (bodyTagEnd >= 0)
            {
                var bodyClose = s.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                s = bodyClose > bodyTagEnd
                    ? s[(bodyTagEnd + 1)..bodyClose]
                    : s[(bodyTagEnd + 1)..];
            }
        }
        else
        {
            // No <body> — try stripping the outer <html>…</html> wrapper.
            var htmlStart  = s.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            var htmlTagEnd = htmlStart >= 0 ? IndexOfTagClose(s, htmlStart) : -1;
            var htmlClose  = s.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
            if (htmlTagEnd >= 0 && htmlClose > htmlTagEnd)
                s = s[(htmlTagEnd + 1)..htmlClose];
            else if (htmlTagEnd >= 0)
                s = s[(htmlTagEnd + 1)..];
        }

        return s.Trim();
    }

    // Scans forward from the start of a tag and returns the index of the closing '>'
    // of that tag's opening sequence, skipping any '>' characters inside quoted attribute values.
    private static int IndexOfTagClose(string s, int tagStart)
    {
        bool inQuote = false;
        char quoteChar = '\0';
        for (int i = tagStart; i < s.Length; i++)
        {
            char c = s[i];
            if (inQuote)
            {
                if (c == quoteChar) inQuote = false;
            }
            else if (c == '"' || c == '\'')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == '>')
            {
                return i;
            }
        }
        return -1;
    }
}

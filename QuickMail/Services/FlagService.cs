using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;

namespace QuickMail.Services;

public class FlagService : IFlagService
{
    private readonly string _flagsFile;
    private readonly IConfigService _configService;
    private readonly ILocalStoreService _localStore;
    private readonly IMailService _mailService;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public event EventHandler? FlagDefinitionsChanged;

    public FlagService(
        ProfileContext profile,
        IConfigService configService,
        ILocalStoreService localStore,
        IMailService mailService)
    {
        _flagsFile     = Path.Combine(profile.ProfileDir, "flags.json");
        _configService = configService;
        _localStore    = localStore;
        _mailService   = mailService;
    }

    public FlagDefinition GetBuiltInFlag() => FlagDefinition.CreateBuiltIn();

    public async Task<List<FlagDefinition>> LoadFlagDefinitionsAsync()
    {
        if (!File.Exists(_flagsFile))
            return new List<FlagDefinition> { FlagDefinition.CreateBuiltIn() };

        try
        {
            var json = await File.ReadAllTextAsync(_flagsFile);
            var list = JsonSerializer.Deserialize<List<FlagDefinition>>(json) ?? new();

            // Ensure the built-in flag is always present and first.
            if (!list.Exists(f => f.Id == FlagDefinition.BuiltInFlagId))
                list.Insert(0, FlagDefinition.CreateBuiltIn());
            else
            {
                // Enforce built-in properties so they cannot be mutated on disk.
                // ColorHex is also reset here to migrate installs that stored the
                // pre-WCAG amber (#FF8C00) before the palette was corrected.
                var bi       = list.Find(f => f.Id == FlagDefinition.BuiltInFlagId)!;
                var builtIn  = FlagDefinition.CreateBuiltIn();
                bi.Name      = builtIn.Name;
                bi.ColorHex  = builtIn.ColorHex;
                bi.IsBuiltIn = true;
            }

            return list;
        }
        catch
        {
            // Corrupt file — back it up and return defaults.
            try
            {
                File.Move(_flagsFile, _flagsFile + $".bak-{DateTime.UtcNow:yyyyMMddHHmmss}",
                    overwrite: false);
            }
            catch { /* ignore backup failure */ }
            return new List<FlagDefinition> { FlagDefinition.CreateBuiltIn() };
        }
    }

    public async Task SaveFlagDefinitionsAsync(List<FlagDefinition> flags)
    {
        var json = JsonSerializer.Serialize(flags, JsonOpts);
        var tmp  = _flagsFile + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, _flagsFile, overwrite: true);
        FlagDefinitionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<FlagDefinition> GetKDefaultFlagAsync()
    {
        var cfg = _configService.Load();
        if (!Guid.TryParse(cfg.DefaultFlagId, out var id))
            return FlagDefinition.CreateBuiltIn();

        var flags = await LoadFlagDefinitionsAsync();
        return flags.Find(f => f.Id == id) ?? FlagDefinition.CreateBuiltIn();
    }

    public Task SetKDefaultFlagAsync(Guid flagId)
    {
        var cfg = _configService.Load();
        cfg.DefaultFlagId = flagId.ToString();
        _configService.Save(cfg);
        return Task.CompletedTask;
    }

    public async Task<FlagDefinition?> SetMessageFlagAsync(
        MailMessageSummary message,
        string? flagId,
        CancellationToken ct = default,
        FlagDefinition? resolvedDef = null)
    {
        // Update local store.
        try
        {
            await _localStore.UpdateFlagIdAsync(
                message.AccountId, message.FolderName, message.MessageId, flagId);
        }
        catch { /* --online mode: local store unavailable */ }

        // Only mirror \Flagged to the server when toggling the built-in flag.
        // Custom flags are local-only and must not pollute the server's \Flagged state.
        bool isBuiltIn  = Guid.TryParse(flagId,         out var newFid) && newFid == FlagDefinition.BuiltInFlagId;
        bool wasBuiltIn = Guid.TryParse(message.FlagId, out var oldFid) && oldFid == FlagDefinition.BuiltInFlagId;
        if (isBuiltIn || wasBuiltIn)
            await _mailService.SetMessageFlaggedAsync(
                message.AccountId, message.FolderName, message.MessageId, isBuiltIn, ct);

        if (flagId == null) return null;
        if (resolvedDef != null) return resolvedDef;

        // Resolve the flag definition so the caller can update the in-memory model.
        if (Guid.TryParse(flagId, out var defId))
        {
            var flags = await LoadFlagDefinitionsAsync();
            return flags.Find(f => f.Id == defId);
        }
        return null;
    }

    public async Task<FlagDefinition?> ToggleDefaultFlagAsync(
        MailMessageSummary message,
        CancellationToken ct = default)
    {
        var kFlag = await GetKDefaultFlagAsync();
        if (message.IsFlagged)
            return await SetMessageFlagAsync(message, null, ct);
        else
            return await SetMessageFlagAsync(message, kFlag.Id.ToString(), ct, resolvedDef: kFlag);
    }
}

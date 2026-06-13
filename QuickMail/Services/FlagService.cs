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
    private readonly string _configFile;
    private readonly IConfigService _configService;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public event EventHandler? FlagDefinitionsChanged;

    public FlagService(ProfileContext profile, IConfigService configService)
    {
        _flagsFile     = Path.Combine(profile.ProfileDir, "flags.json");
        _configFile    = Path.Combine(profile.ProfileDir, "config.ini");
        _configService = configService;
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
                var bi = list.Find(f => f.Id == FlagDefinition.BuiltInFlagId)!;
                bi.Name     = "Flagged";
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
        var cfg   = _configService.Load();
        if (!Guid.TryParse(cfg.DefaultFlagId, out var id))
            return FlagDefinition.CreateBuiltIn();

        var flags = await LoadFlagDefinitionsAsync();
        return flags.Find(f => f.Id == id) ?? FlagDefinition.CreateBuiltIn();
    }

    public async Task SetKDefaultFlagAsync(Guid flagId)
    {
        var cfg = _configService.Load();
        cfg.DefaultFlagId = flagId.ToString();
        _configService.Save(cfg);
        await Task.CompletedTask;
    }

    public async Task SetMessageFlagAsync(
        MailMessageSummary message,
        string? flagId,
        ILocalStoreService localStore,
        IMailService mailService,
        CancellationToken ct = default)
    {
        // Update local store.
        try
        {
            await localStore.UpdateFlagIdAsync(
                message.AccountId, message.FolderName, message.MessageId, flagId);
        }
        catch { /* --online mode: local store unavailable */ }

        // Update IMAP \Flagged flag if this is the built-in flag or a clear.
        bool serverFlagged = flagId != null;
        await mailService.SetMessageFlaggedAsync(
            message.AccountId, message.FolderName, message.MessageId, serverFlagged, ct);

        // Update in-memory model.
        message.FlagId = flagId;

        if (flagId == null)
        {
            message.FlagName     = null;
            message.FlagColorHex = null;
        }
        else
        {
            var flags = await LoadFlagDefinitionsAsync();
            var def   = flags.Find(f => f.Id.ToString() == flagId);
            if (def != null)
            {
                message.FlagName     = def.Name;
                message.FlagColorHex = def.ColorHex;
            }
        }
    }

    public async Task ToggleDefaultFlagAsync(
        MailMessageSummary message,
        ILocalStoreService localStore,
        IMailService mailService,
        CancellationToken ct = default)
    {
        var kFlag = await GetKDefaultFlagAsync();

        // Clear if already flagged with anything; set K-default if unflagged.
        if (message.IsFlagged)
            await SetMessageFlagAsync(message, null, localStore, mailService, ct);
        else
            await SetMessageFlagAsync(message, kFlag.Id.ToString(), localStore, mailService, ct);
    }
}

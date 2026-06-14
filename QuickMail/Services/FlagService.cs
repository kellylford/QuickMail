using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using QuickMail.Models;

namespace QuickMail.Services;

public class FlagService : IFlagService
{
    private readonly string _flagsFile;
    private readonly IConfigService _configService;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public event EventHandler? FlagDefinitionsChanged;

    public FlagService(ProfileContext profile, IConfigService configService)
    {
        _flagsFile     = Path.Combine(profile.ProfileDir, "flags.json");
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
                bi.Name      = "Flagged";
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

    public Task SetMessageFlagAsync(
        MailMessageSummary message,
        string? flagId,
        ILocalStoreService localStore,
        IMailService mailService,
        CancellationToken ct = default)
        => SetMessageFlagCoreAsync(message, flagId, null, localStore, mailService, ct);

    public async Task ToggleDefaultFlagAsync(
        MailMessageSummary message,
        ILocalStoreService localStore,
        IMailService mailService,
        CancellationToken ct = default)
    {
        var kFlag = await GetKDefaultFlagAsync();
        if (message.IsFlagged)
            await SetMessageFlagCoreAsync(message, null, null, localStore, mailService, ct);
        else
            await SetMessageFlagCoreAsync(message, kFlag.Id.ToString(), kFlag, localStore, mailService, ct);
    }

    private async Task SetMessageFlagCoreAsync(
        MailMessageSummary message,
        string? flagId,
        FlagDefinition? resolvedDef,
        ILocalStoreService localStore,
        IMailService mailService,
        CancellationToken ct)
    {
        // Update local store.
        try
        {
            await localStore.UpdateFlagIdAsync(
                message.AccountId, message.FolderName, message.MessageId, flagId);
        }
        catch { /* --online mode: local store unavailable */ }

        // Only mirror \Flagged to the server when toggling the built-in flag.
        // Custom flags are local-only and must not pollute the server's \Flagged state.
        bool isBuiltIn  = Guid.TryParse(flagId,         out var newFid) && newFid == FlagDefinition.BuiltInFlagId;
        bool wasBuiltIn = Guid.TryParse(message.FlagId, out var oldFid) && oldFid == FlagDefinition.BuiltInFlagId;
        if (isBuiltIn || wasBuiltIn)
            await mailService.SetMessageFlaggedAsync(
                message.AccountId, message.FolderName, message.MessageId, isBuiltIn, ct);

        // Resolve flag definition if not already supplied.
        if (flagId != null && resolvedDef == null && Guid.TryParse(flagId, out var defId))
        {
            var flags = await LoadFlagDefinitionsAsync();
            resolvedDef = flags.Find(f => f.Id == defId);
        }

        // Update in-memory model on the UI thread (or directly when running outside a WPF app, e.g. in tests).
        void Apply()
        {
            message.FlagId       = flagId;
            message.FlagName     = resolvedDef?.Name;
            message.FlagColorHex = resolvedDef?.ColorHex;
        }
        if (Application.Current?.Dispatcher is { } disp)
            disp.Invoke(Apply);
        else
            Apply();
    }
}

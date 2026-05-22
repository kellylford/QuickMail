using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QuickMail.Models;

namespace QuickMail.Services;

public class AccountService : IAccountService
{
    private readonly string _dataFolder;
    private readonly string _accountsFile;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public AccountService(ProfileContext profile)
    {
        _dataFolder   = profile.ProfileDir;
        _accountsFile = Path.Combine(profile.ProfileDir, "accounts.json");
    }

    public List<AccountModel> LoadAccounts()
    {
        if (!File.Exists(_accountsFile))
            return [];

        try
        {
            var json = File.ReadAllText(_accountsFile);
            var accounts = JsonSerializer.Deserialize<List<AccountModel?>>(json, JsonOptions) ?? [];
            return accounts.Where(a => a is not null).Cast<AccountModel>().ToList();
        }
        catch
        {
            return [];
        }
    }

    public void SaveAccounts(List<AccountModel> accounts)
    {
        Directory.CreateDirectory(_dataFolder);
        var json = JsonSerializer.Serialize(accounts, JsonOptions);
        File.WriteAllText(_accountsFile, json);
    }

    public void SetDefaultAccount(Guid accountId)
    {
        var accounts = LoadAccounts();
        foreach (var a in accounts)
            a.IsDefault = a.Id == accountId;
        SaveAccounts(accounts);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for ComposeViewModel template commands: InsertTemplate and SaveAsTemplate,
/// including placeholder replacement ({sender}, {date}, {time}).
/// </summary>
public class ComposeViewModelTemplateTests
{
    private static (ComposeViewModel vm, TrackedTemplateService templates) MakeVm(
        List<MessageTemplate>? seedTemplates = null)
    {
        var templates = new TrackedTemplateService();
        if (seedTemplates != null)
        {
            templates.SetTemplates(seedTemplates);
        }
        var vm = new ComposeViewModel(
            new StubSmtpService(),
            new StubAccountService(),
            new StubCredentialService(),
            new StubImapMailService(),
            templates);
        return (vm, templates);
    }

    // ── InsertTemplate ────────────────────────────────────────────────────────

    [Fact]
    public async Task InsertTemplate_AppendsBody()
    {
        var (vm, templates) = MakeVm();
        vm.Body = "Existing body.\n";

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "Greeting",
            Subject = "",
            Body = "Hello, world!"
        };

        // Wire the InsertTemplateRequested event to return our template
        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        // Invoke via the relay command
        await InvokeInsertTemplateAsync(vm);

        Assert.Contains("Existing body.", vm.Body);
        Assert.Contains("Hello, world!", vm.Body);
        Assert.Equal("Template 'Greeting' inserted.", vm.StatusText);
    }

    [Fact]
    public async Task InsertTemplate_DoesNothingWhenNoHandler()
    {
        var (vm, _) = MakeVm();
        vm.Body = "Original";

        // No InsertTemplateRequested handler wired — should be a no-op
        await InvokeInsertTemplateAsync(vm);

        Assert.Equal("Original", vm.Body);
    }

    [Fact]
    public async Task InsertTemplate_DoesNothingWhenHandlerReturnsNull()
    {
        var (vm, _) = MakeVm();
        vm.Body = "Original";

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(null);

        await InvokeInsertTemplateAsync(vm);

        Assert.Equal("Original", vm.Body);
    }

    [Fact]
    public async Task InsertTemplate_PrefillsSubjectWhenEmpty()
    {
        var (vm, _) = MakeVm();
        vm.Subject = "";

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "Follow-up",
            Subject = "Re: Our meeting",
            Body = "Thanks for your time."
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.Equal("Re: Our meeting", vm.Subject);
    }

    [Fact]
    public async Task InsertTemplate_DoesNotOverwriteExistingSubject()
    {
        var (vm, _) = MakeVm();
        vm.Subject = "Already set";

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "Follow-up",
            Subject = "Re: Our meeting",
            Body = "Thanks."
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.Equal("Already set", vm.Subject);
    }

    // ── Placeholder replacement ───────────────────────────────────────────────

    [Fact]
    public async Task InsertTemplate_ReplacesSenderPlaceholder()
    {
        var (vm, _) = MakeVm();
        // Set up a sender account with a display name
        vm.SenderAccounts = new System.Collections.ObjectModel.ObservableCollection<AccountModel>
        {
            new() { Id = Guid.NewGuid(), DisplayName = "Alice Johnson", Username = "alice@example.com" }
        };
        vm.SenderAccount = vm.SenderAccounts[0];

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "Signature",
            Body = "Best regards,\n{sender}"
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.Contains("Alice Johnson", vm.Body);
        Assert.DoesNotContain("{sender}", vm.Body);
    }

    [Fact]
    public async Task InsertTemplate_ReplacesDatePlaceholder()
    {
        var (vm, _) = MakeVm();

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "Dated",
            Body = "Sent on {date}"
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.DoesNotContain("{date}", vm.Body);
        // The date should be replaced with today's date in short format
        var today = DateTime.Now.ToString("d");
        Assert.Contains(today, vm.Body);
    }

    [Fact]
    public async Task InsertTemplate_ReplacesTimePlaceholder()
    {
        var (vm, _) = MakeVm();

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "Timed",
            Body = "Sent at {time}"
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.DoesNotContain("{time}", vm.Body);
    }

    [Fact]
    public async Task InsertTemplate_ReplacesPlaceholdersInSubject()
    {
        var (vm, _) = MakeVm();
        vm.Subject = "";
        vm.SenderAccounts = new System.Collections.ObjectModel.ObservableCollection<AccountModel>
        {
            new() { Id = Guid.NewGuid(), DisplayName = "Bob", Username = "bob@example.com" }
        };
        vm.SenderAccount = vm.SenderAccounts[0];

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "T",
            Subject = "Follow-up from {sender} on {date}",
            Body = "Hello"
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.Contains("Bob", vm.Subject);
        Assert.DoesNotContain("{sender}", vm.Subject);
        Assert.DoesNotContain("{date}", vm.Subject);
    }

    [Fact]
    public async Task InsertTemplate_SenderPlaceholderUsesUsernameWhenNoDisplayName()
    {
        var (vm, _) = MakeVm();
        vm.SenderAccounts = new System.Collections.ObjectModel.ObservableCollection<AccountModel>
        {
            new() { Id = Guid.NewGuid(), DisplayName = "", Username = "noreply@example.com" }
        };
        vm.SenderAccount = vm.SenderAccounts[0];

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "T",
            Body = "{sender}"
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.Contains("noreply@example.com", vm.Body);
        Assert.DoesNotContain("{sender}", vm.Body);
    }

    [Fact]
    public async Task InsertTemplate_SenderPlaceholderEmptyWhenNoAccount()
    {
        var (vm, _) = MakeVm();
        // No sender account set

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "T",
            Body = "[{sender}]"
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.Contains("[]", vm.Body);
    }

    [Fact]
    public async Task InsertTemplate_PlaceholdersAreCaseInsensitive()
    {
        var (vm, _) = MakeVm();
        vm.SenderAccounts = new System.Collections.ObjectModel.ObservableCollection<AccountModel>
        {
            new() { Id = Guid.NewGuid(), DisplayName = "Casey", Username = "casey@example.com" }
        };
        vm.SenderAccount = vm.SenderAccounts[0];

        var template = new MessageTemplate
        {
            Id = 1,
            Title = "T",
            Body = "{SENDER} / {Sender} / {DATE} / {Time}"
        };

        vm.InsertTemplateRequested += () => Task.FromResult<MessageTemplate?>(template);

        await InvokeInsertTemplateAsync(vm);

        Assert.DoesNotContain("{SENDER}", vm.Body);
        Assert.DoesNotContain("{Sender}", vm.Body);
        Assert.DoesNotContain("{DATE}", vm.Body);
        Assert.DoesNotContain("{Time}", vm.Body);
        Assert.Contains("Casey", vm.Body);
    }

    // ── SaveAsTemplate ────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsTemplate_SavesWithSubjectAsTitle()
    {
        var (vm, templates) = MakeVm();
        vm.Subject = "My Response";
        vm.Body = "Thank you for your email.";

        await InvokeSaveAsTemplateAsync(vm);

        Assert.Equal("Template saved as 'My Response'.", vm.StatusText);
        Assert.Single(templates.AddedTemplates);
        Assert.Equal("My Response", templates.AddedTemplates[0].Title);
        Assert.Equal("My Response", templates.AddedTemplates[0].Subject);
        Assert.Equal("Thank you for your email.", templates.AddedTemplates[0].Body);
    }

    [Fact]
    public async Task SaveAsTemplate_UsesUntitledWhenSubjectEmpty()
    {
        var (vm, templates) = MakeVm();
        vm.Subject = "";
        vm.Body = "Some body text";

        await InvokeSaveAsTemplateAsync(vm);

        Assert.Equal("Template saved as 'Untitled'.", vm.StatusText);
        Assert.Single(templates.AddedTemplates);
        Assert.Equal("Untitled", templates.AddedTemplates[0].Title);
    }

    [Fact]
    public async Task SaveAsTemplate_UsesUntitledWhenSubjectWhitespace()
    {
        var (vm, templates) = MakeVm();
        vm.Subject = "   ";
        vm.Body = "Body";

        await InvokeSaveAsTemplateAsync(vm);

        Assert.Equal("Untitled", templates.AddedTemplates[0].Title);
    }

    [Fact]
    public async Task SaveAsTemplate_ShowsErrorWhenBodyEmpty()
    {
        var (vm, templates) = MakeVm();
        vm.Subject = "Title";
        vm.Body = "";

        await InvokeSaveAsTemplateAsync(vm);

        Assert.Equal("Nothing to save — body is empty.", vm.StatusText);
        Assert.Empty(templates.AddedTemplates);
    }

    [Fact]
    public async Task SaveAsTemplate_ShowsErrorWhenBodyWhitespace()
    {
        var (vm, templates) = MakeVm();
        vm.Subject = "Title";
        vm.Body = "   \n  ";

        await InvokeSaveAsTemplateAsync(vm);

        Assert.Equal("Nothing to save — body is empty.", vm.StatusText);
        Assert.Empty(templates.AddedTemplates);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Invokes the private InsertTemplateAsync method via the generated relay command.
    /// </summary>
    private static async Task InvokeInsertTemplateAsync(ComposeViewModel vm)
    {
        // The [RelayCommand] on InsertTemplateAsync generates InsertTemplateCommand
        await ((IAsyncRelayCommand)vm.InsertTemplateCommand).ExecuteAsync(null);
    }

    /// <summary>
    /// Invokes the private SaveAsTemplateAsync method via the generated relay command.
    /// </summary>
    private static async Task InvokeSaveAsTemplateAsync(ComposeViewModel vm)
    {
        await ((IAsyncRelayCommand)vm.SaveAsTemplateCommand).ExecuteAsync(null);
    }
}

/// <summary>
/// Extended stub that tracks added templates for verification.
/// </summary>
sealed class TrackedTemplateService : ITemplateService
{
    private List<MessageTemplate> _templates = [];
    private int _nextId = 1;

    public List<MessageTemplate> AddedTemplates { get; } = [];

    public void SetTemplates(List<MessageTemplate> templates)
    {
        _templates = templates;
        _nextId = templates.Count > 0 ? templates.Max(t => t.Id) + 1 : 1;
    }

    public Task<List<MessageTemplate>> LoadAllAsync() =>
        Task.FromResult(_templates.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList());

    public Task<MessageTemplate> AddAsync(MessageTemplate template)
    {
        template.Id = _nextId++;
        _templates.Add(template);
        AddedTemplates.Add(template);
        return Task.FromResult(template);
    }

    public Task UpdateAsync(MessageTemplate template)
    {
        var existing = _templates.FirstOrDefault(t => t.Id == template.Id);
        if (existing != null)
        {
            existing.Title = template.Title;
            existing.Subject = template.Subject;
            existing.Body = template.Body;
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(int id)
    {
        _templates.RemoveAll(t => t.Id == id);
        return Task.CompletedTask;
    }
}

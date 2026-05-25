using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

/// <summary>
/// Tests for TemplatePickerViewModel: search filtering, selection, empty state,
/// and MatchCountText formatting.
/// </summary>
public class TemplatePickerViewModelTests
{
    private class TestTemplateService : ITemplateService
    {
        private readonly List<MessageTemplate> _templates;

        public TestTemplateService(List<MessageTemplate> templates)
        {
            _templates = templates;
        }

        public Task<List<MessageTemplate>> LoadAllAsync() =>
            Task.FromResult(_templates.OrderBy(t => t.Title).ToList());

        public Task<MessageTemplate> AddAsync(MessageTemplate template) =>
            Task.FromResult(template);

        public Task UpdateAsync(MessageTemplate template) =>
            Task.CompletedTask;

        public Task DeleteAsync(int id) =>
            Task.CompletedTask;
    }

    private static TemplatePickerViewModel MakeVm(List<MessageTemplate>? templates = null)
    {
        var service = new TestTemplateService(templates ?? []);
        return new TemplatePickerViewModel(service);
    }

    // ── LoadAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_PopulatesTemplates()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting Follow-up", Body = "Thanks for meeting." },
            new() { Id = 2, Title = "Out of Office", Body = "I'm away." },
        };
        var vm = MakeVm(templates);

        await vm.LoadAsync();

        Assert.Equal(2, vm.Templates.Count);
        Assert.Equal("Meeting Follow-up", vm.Templates[0].Title);
        Assert.Equal("Out of Office", vm.Templates[1].Title);
    }

    [Fact]
    public async Task LoadAsync_SelectsFirstTemplate()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "First" },
            new() { Id = 2, Title = "Second" },
        };
        var vm = MakeVm(templates);

        await vm.LoadAsync();

        Assert.NotNull(vm.SelectedTemplate);
        Assert.Equal("First", vm.SelectedTemplate!.Title);
    }

    [Fact]
    public async Task LoadAsync_HandlesEmptyList()
    {
        var vm = MakeVm([]);

        await vm.LoadAsync();

        Assert.Empty(vm.Templates);
        Assert.Null(vm.SelectedTemplate);
    }

    // ── Search filtering ──────────────────────────────────────────────────────

    [Fact]
    public async Task Search_FiltersByTitle()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting Follow-up" },
            new() { Id = 2, Title = "Out of Office" },
            new() { Id = 3, Title = "Meeting Agenda" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "meeting";

        Assert.Equal(2, vm.Templates.Count);
        Assert.All(vm.Templates, t => Assert.Contains("meeting", t.Title, System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Search_IsCaseInsensitive()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "MEETING Follow-up" },
            new() { Id = 2, Title = "Out of Office" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "meeting";

        Assert.Single(vm.Templates);
        Assert.Equal("MEETING Follow-up", vm.Templates[0].Title);
    }

    [Fact]
    public async Task Search_NoMatchShowsEmpty()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "xyzzy";

        Assert.Empty(vm.Templates);
        Assert.Null(vm.SelectedTemplate);
    }

    [Fact]
    public async Task Search_ClearingRestoresAll()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting" },
            new() { Id = 2, Title = "Out of Office" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "meeting";
        Assert.Single(vm.Templates);

        vm.SearchText = "";
        Assert.Equal(2, vm.Templates.Count);
    }

    [Fact]
    public async Task Search_WhitespaceOnlyShowsAll()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "   ";

        Assert.Single(vm.Templates);
    }

    [Fact]
    public async Task Search_SelectsFirstMatch()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Apple" },
            new() { Id = 2, Title = "Banana" },
            new() { Id = 3, Title = "Apricot" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "ap";

        Assert.Equal(2, vm.Templates.Count);
        Assert.NotNull(vm.SelectedTemplate);
        Assert.Equal("Apple", vm.SelectedTemplate!.Title);
    }

    // ── MatchCountText ────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchCountText_NoSearch_ShowsTotal()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "A" },
            new() { Id = 2, Title = "B" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        Assert.Equal("2 templates", vm.MatchCountText);
    }

    [Fact]
    public async Task MatchCountText_Singular()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Only" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        Assert.Equal("1 template", vm.MatchCountText);
    }

    [Fact]
    public async Task MatchCountText_WithSearch()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting Follow-up" },
            new() { Id = 2, Title = "Meeting Agenda" },
            new() { Id = 3, Title = "Out of Office" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "meeting";

        Assert.Equal("2 templates matching 'meeting'", vm.MatchCountText);
    }

    [Fact]
    public async Task MatchCountText_SingularWithSearch()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting Follow-up" },
            new() { Id = 2, Title = "Out of Office" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "office";

        Assert.Equal("1 template matching 'office'", vm.MatchCountText);
    }

    [Fact]
    public async Task MatchCountText_ZeroResults()
    {
        var templates = new List<MessageTemplate>
        {
            new() { Id = 1, Title = "Meeting" },
        };
        var vm = MakeVm(templates);
        await vm.LoadAsync();

        vm.SearchText = "nonexistent";

        Assert.Equal("0 templates matching 'nonexistent'", vm.MatchCountText);
    }

    [Fact]
    public async Task MatchCountText_EmptyList()
    {
        var vm = MakeVm([]);
        await vm.LoadAsync();

        Assert.Equal("0 templates", vm.MatchCountText);
    }

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_InitializesWithEmptyState()
    {
        var vm = MakeVm();

        Assert.Empty(vm.Templates);
        Assert.Null(vm.SelectedTemplate);
        Assert.Equal(string.Empty, vm.SearchText);
    }
}

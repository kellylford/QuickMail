using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class FlagPickerViewModelTests
{
    private sealed class TestFlagService : IFlagService
    {
        private readonly List<FlagDefinition> _flags;

        public TestFlagService(List<FlagDefinition>? flags = null)
        {
            _flags = flags ?? [FlagDefinition.CreateBuiltIn()];
        }

#pragma warning disable CS0067
        public event EventHandler? FlagDefinitionsChanged;
#pragma warning restore CS0067
        public FlagDefinition GetBuiltInFlag() => FlagDefinition.CreateBuiltIn();
        public Task<List<FlagDefinition>> LoadFlagDefinitionsAsync() => Task.FromResult(new List<FlagDefinition>(_flags));
        public Task SaveFlagDefinitionsAsync(List<FlagDefinition> flags) => Task.CompletedTask;
        public Task<FlagDefinition> GetKDefaultFlagAsync() => Task.FromResult(FlagDefinition.CreateBuiltIn());
        public Task SetKDefaultFlagAsync(Guid flagId) => Task.CompletedTask;
        public Task<FlagDefinition?> SetMessageFlagAsync(MailMessageSummary message, string? flagId, CancellationToken ct = default, FlagDefinition? resolvedDef = null)
            => Task.FromResult<FlagDefinition?>(resolvedDef ?? (flagId != null ? _flags.Find(f => f.Id.ToString() == flagId) : null));
        public Task<FlagDefinition?> ToggleDefaultFlagAsync(MailMessageSummary message, CancellationToken ct = default)
            => Task.FromResult<FlagDefinition?>(message.IsFlagged ? null : FlagDefinition.CreateBuiltIn());
    }

    private static FlagPickerViewModel MakeVm(List<FlagDefinition>? flags = null, bool currentlyFlagged = false)
    {
        var svc = new TestFlagService(flags);
        return new FlagPickerViewModel(svc, currentlyFlagged);
    }

    [Fact]
    public void Constructor_InitializesEmpty()
    {
        var vm = MakeVm();
        Assert.Empty(vm.Flags);
        Assert.Null(vm.SelectedFlag);
        Assert.False(vm.CurrentlyFlagged);
    }

    [Fact]
    public async Task LoadAsync_PopulatesFlags()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", ColorHex = "#E53E3E" },
        };
        var vm = MakeVm(flags);
        await vm.LoadAsync();
        Assert.Equal(2, vm.Flags.Count);
    }

    [Fact]
    public async Task LoadAsync_SelectsFirstFlag()
    {
        var vm = MakeVm();
        await vm.LoadAsync();
        Assert.NotNull(vm.SelectedFlag);
        Assert.Equal("Flagged", vm.SelectedFlag!.Name);
    }

    [Fact]
    public async Task LoadAsync_EmptyList_SelectedFlagIsNull()
    {
        var vm = MakeVm([]);
        await vm.LoadAsync();
        Assert.Null(vm.SelectedFlag);
    }

    [Fact]
    public async Task ApplyFlagCommand_RaisesFlagSelectedWithSelectedFlag()
    {
        var vm = MakeVm();
        await vm.LoadAsync();

        FlagDefinition? raised = null;
        vm.FlagSelected += f => raised = f;
        vm.ApplyFlagCommand.Execute(null);

        Assert.NotNull(raised);
        Assert.Equal("Flagged", raised!.Name);
    }

    [Fact]
    public async Task ApplyFlagCommand_DisabledWhenNoSelection()
    {
        var vm = MakeVm([]);
        await vm.LoadAsync();
        Assert.False(vm.ApplyFlagCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearFlagCommand_RaisesFlagSelectedWithNull()
    {
        var vm = MakeVm();
        await vm.LoadAsync();

        bool invoked = false;
        FlagDefinition? raised = FlagDefinition.CreateBuiltIn();
        vm.FlagSelected += f => { raised = f; invoked = true; };
        vm.ClearFlagCommand.Execute(null);

        Assert.True(invoked);
        Assert.Null(raised);
    }

    [Fact]
    public void CurrentlyFlagged_ReflectsConstructorArg()
    {
        var vm = MakeVm(currentlyFlagged: true);
        Assert.True(vm.CurrentlyFlagged);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuickMail.Models;
using QuickMail.Services;
using QuickMail.ViewModels;
using Xunit;

namespace QuickMail.Tests;

public class FlagManagerViewModelTests
{
    private sealed class RecordingFlagService : IFlagService
    {
        private List<FlagDefinition> _flags;
        public List<FlagDefinition> Saved { get; private set; } = [];
        public Guid KDefaultId { get; private set; } = FlagDefinition.BuiltInFlagId;

        public RecordingFlagService(List<FlagDefinition>? initial = null)
        {
            _flags = initial ?? [FlagDefinition.CreateBuiltIn()];
        }

#pragma warning disable CS0067
        public event EventHandler? FlagDefinitionsChanged;
#pragma warning restore CS0067
        public FlagDefinition GetBuiltInFlag() => FlagDefinition.CreateBuiltIn();
        public Task<List<FlagDefinition>> LoadFlagDefinitionsAsync() => Task.FromResult(new List<FlagDefinition>(_flags));
        public Task SaveFlagDefinitionsAsync(List<FlagDefinition> flags) { Saved = new List<FlagDefinition>(flags); return Task.CompletedTask; }
        public Task<FlagDefinition> GetKDefaultFlagAsync() => Task.FromResult(_flags.Find(f => f.Id == KDefaultId) ?? FlagDefinition.CreateBuiltIn());
        public Task SetKDefaultFlagAsync(Guid flagId) { KDefaultId = flagId; return Task.CompletedTask; }
        public Task<FlagDefinition?> SetMessageFlagAsync(MailMessageSummary message, string? flagId, CancellationToken ct = default, FlagDefinition? resolvedDef = null)
            => Task.FromResult<FlagDefinition?>(resolvedDef ?? (flagId != null ? _flags.Find(f => f.Id.ToString() == flagId) : null));
        public Task<FlagDefinition?> ToggleDefaultFlagAsync(MailMessageSummary message, CancellationToken ct = default)
            => Task.FromResult<FlagDefinition?>(message.IsFlagged ? null : _flags.Find(f => f.Id == KDefaultId));
    }

    private static async Task<(FlagManagerViewModel vm, RecordingFlagService svc)> MakeVmAsync(
        List<FlagDefinition>? flags = null)
    {
        var svc = new RecordingFlagService(flags);
        var vm  = new FlagManagerViewModel(svc);
        await vm.LoadAsync();
        return (vm, svc);
    }

    // ── LoadAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_PopulatesFlags()
    {
        var (vm, _) = await MakeVmAsync();
        Assert.Single(vm.Flags);
        Assert.Equal("Flagged", vm.Flags[0].Name);
    }

    [Fact]
    public async Task LoadAsync_SelectsFirst()
    {
        var (vm, _) = await MakeVmAsync();
        Assert.NotNull(vm.SelectedFlag);
        Assert.Equal("Flagged", vm.SelectedFlag!.Name);
    }

    [Fact]
    public async Task LoadAsync_OrdersBySortOrder()
    {
        var flags = new List<FlagDefinition>
        {
            new() { Name = "B", SortOrder = 2 },
            new() { Id = FlagDefinition.BuiltInFlagId, Name = "Flagged", SortOrder = 0, IsBuiltIn = true },
            new() { Name = "A", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        Assert.Equal("Flagged", vm.Flags[0].Name);
        Assert.Equal("A",       vm.Flags[1].Name);
        Assert.Equal("B",       vm.Flags[2].Name);
    }

    // ── Add ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddFlag_AppendsAndSelectsNewFlag()
    {
        var (vm, _) = await MakeVmAsync();
        await vm.AddFlagCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Flags.Count);
        Assert.Equal(vm.Flags[1], vm.SelectedFlag);
    }

    [Fact]
    public async Task AddFlag_SavesImmediately()
    {
        var (vm, svc) = await MakeVmAsync();
        await vm.AddFlagCommand.ExecuteAsync(null);
        Assert.Equal(2, svc.Saved.Count);
    }

    [Fact]
    public async Task AddFlag_EntersRenameMode()
    {
        var (vm, _) = await MakeVmAsync();
        await vm.AddFlagCommand.ExecuteAsync(null);
        Assert.True(vm.IsRenaming);
    }

    [Fact]
    public async Task AddFlag_GeneratesUniqueNameWhenNewFlagExists()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "New Flag", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        await vm.AddFlagCommand.ExecuteAsync(null);
        Assert.Equal("New Flag 2", vm.Flags[2].Name);
    }

    [Fact]
    public async Task AddFlag_DisabledAt20UserFlags()
    {
        var flags = new List<FlagDefinition> { FlagDefinition.CreateBuiltIn() };
        for (int i = 0; i < 20; i++)
            flags.Add(new() { Name = $"Flag{i}", SortOrder = i + 1 });
        var (vm, _) = await MakeVmAsync(flags);
        Assert.False(vm.AddFlagCommand.CanExecute(null));
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteFlag_RemovesFlag()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        await vm.DeleteFlagCommand.ExecuteAsync(null);
        Assert.Single(vm.Flags);
        Assert.Equal("Flagged", vm.Flags[0].Name);
    }

    [Fact]
    public async Task DeleteFlag_BuiltIn_NotAllowed()
    {
        var (vm, _) = await MakeVmAsync();
        Assert.False(vm.DeleteFlagCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteFlag_ConfirmFalse_DoesNotDelete()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "ToDelete", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        vm.ConfirmDeleteRequested += (_, _) => Task.FromResult(false);
        await vm.DeleteFlagCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Flags.Count);
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BeginRename_SetsIsRenamingAndEditName()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1]; // select non-built-in flag
        vm.BeginRenameCommand.Execute(null);
        Assert.True(vm.IsRenaming);
        Assert.Equal("Urgent", vm.EditName);
    }

    [Fact]
    public async Task SaveRename_UpdatesFlagNameAndSaves()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", SortOrder = 1 },
        };
        var (vm, svc) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        vm.BeginRenameCommand.Execute(null);
        vm.EditName = "Important";
        await vm.SaveRenameCommand.ExecuteAsync(null);
        Assert.False(vm.IsRenaming);
        Assert.Equal("Important", vm.SelectedFlag!.Name);
        Assert.Equal("Important", svc.Saved[1].Name);
    }

    [Fact]
    public async Task SaveRename_DuplicateName_SetsError()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent",    SortOrder = 1 },
            new() { Name = "Important", SortOrder = 2 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1]; // "Urgent"
        vm.BeginRenameCommand.Execute(null);
        vm.EditName = "Important";     // already exists on a different flag
        await vm.SaveRenameCommand.ExecuteAsync(null);
        Assert.True(vm.IsRenaming);
        Assert.NotEmpty(vm.RenameError);
    }

    [Fact]
    public async Task SaveRename_SameNameAsOwn_Succeeds()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        vm.BeginRenameCommand.Execute(null);
        vm.EditName = "urgent"; // same name, different case — saving own name is fine
        await vm.SaveRenameCommand.ExecuteAsync(null);
        Assert.False(vm.IsRenaming);
        Assert.Empty(vm.RenameError);
    }

    [Fact]
    public async Task SaveRename_EmptyName_SetsError()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        vm.BeginRenameCommand.Execute(null);
        vm.EditName = "   ";
        await vm.SaveRenameCommand.ExecuteAsync(null);
        Assert.True(vm.IsRenaming);
        Assert.NotEmpty(vm.RenameError);
    }

    [Fact]
    public async Task SaveRename_TooLongName_SetsError()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        vm.BeginRenameCommand.Execute(null);
        vm.EditName = new string('A', 33);
        await vm.SaveRenameCommand.ExecuteAsync(null);
        Assert.True(vm.IsRenaming);
        Assert.NotEmpty(vm.RenameError);
    }

    [Fact]
    public async Task CancelRename_ExitsRenameMode()
    {
        var (vm, _) = await MakeVmAsync();
        vm.BeginRenameCommand.Execute(null);
        vm.CancelRenameCommand.Execute(null);
        Assert.False(vm.IsRenaming);
        Assert.Empty(vm.RenameError);
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveDown_ChangesOrder()
    {
        var flags = new List<FlagDefinition>
        {
            new() { Id = FlagDefinition.BuiltInFlagId, Name = "A", SortOrder = 0, IsBuiltIn = true },
            new() { Name = "B", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[0];
        await vm.MoveDownCommand.ExecuteAsync(null);
        Assert.Equal("B", vm.Flags[0].Name);
        Assert.Equal("A", vm.Flags[1].Name);
    }

    [Fact]
    public async Task MoveUp_ChangesOrder()
    {
        var flags = new List<FlagDefinition>
        {
            new() { Id = FlagDefinition.BuiltInFlagId, Name = "A", SortOrder = 0, IsBuiltIn = true },
            new() { Name = "B", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        await vm.MoveUpCommand.ExecuteAsync(null);
        Assert.Equal("B", vm.Flags[0].Name);
        Assert.Equal("A", vm.Flags[1].Name);
    }

    [Fact]
    public async Task CanMoveUp_FalseAtTop()
    {
        var (vm, _) = await MakeVmAsync();
        Assert.False(vm.CanMoveUp);
    }

    [Fact]
    public async Task CanMoveDown_FalseAtBottom()
    {
        var (vm, _) = await MakeVmAsync();
        Assert.False(vm.CanMoveDown);
    }

    // ── SetAsKDefault ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAsKDefault_UpdatesKDefaultId()
    {
        var customId = Guid.NewGuid();
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Id = customId, Name = "Urgent", SortOrder = 1 },
        };
        var (vm, svc) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        await vm.SetAsKDefaultCommand.ExecuteAsync(null);
        Assert.Equal(customId, vm.KDefaultId);
        Assert.Equal(customId, svc.KDefaultId);
    }

    // ── ChangeColor ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeColor_UpdatesSelectedFlagAndSaves()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Urgent", SortOrder = 1, ColorHex = "#3182CE" },
        };
        var (vm, svc) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1]; // select non-built-in flag
        await vm.ChangeColorCommand.ExecuteAsync("#E53E3E");
        Assert.Equal("#E53E3E", vm.SelectedFlag!.ColorHex);
        Assert.Equal("#E53E3E", svc.Saved[1].ColorHex);
    }

    // ── HasSelection / CanDelete ───────────────────────────────────────────────

    [Fact]
    public async Task HasSelection_TrueWhenFlagSelected()
    {
        var (vm, _) = await MakeVmAsync();
        Assert.True(vm.HasSelection);
    }

    [Fact]
    public async Task CanDelete_FalseForBuiltIn()
    {
        var (vm, _) = await MakeVmAsync();
        Assert.False(vm.CanDelete);
    }

    [Fact]
    public async Task CanDelete_TrueForUserFlag()
    {
        var flags = new List<FlagDefinition>
        {
            FlagDefinition.CreateBuiltIn(),
            new() { Name = "Custom", SortOrder = 1 },
        };
        var (vm, _) = await MakeVmAsync(flags);
        vm.SelectedFlag = vm.Flags[1];
        Assert.True(vm.CanDelete);
    }
}

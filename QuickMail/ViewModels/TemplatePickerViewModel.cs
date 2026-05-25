using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using QuickMail.Models;
using QuickMail.Services;

namespace QuickMail.ViewModels;

public partial class TemplatePickerViewModel : ObservableObject
{
    private readonly ITemplateService _templateService;
    private List<MessageTemplate> _allTemplates = [];

    [ObservableProperty] private ObservableCollection<MessageTemplate> _templates = [];
    [ObservableProperty] private MessageTemplate? _selectedTemplate;
    [ObservableProperty] private string _searchText = string.Empty;

    public TemplatePickerViewModel(ITemplateService templateService)
    {
        _templateService = templateService;
    }

    public async Task LoadAsync()
    {
        _allTemplates = await _templateService.LoadAllAsync();
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchText?.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allTemplates
            : _allTemplates.Where(t =>
                t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        Templates = new ObservableCollection<MessageTemplate>(filtered);
        SelectedTemplate = Templates.FirstOrDefault();
    }

    public string MatchCountText
    {
        get
        {
            var count = Templates.Count;
            var q = SearchText?.Trim();
            if (string.IsNullOrEmpty(q))
                return $"{count} template{(count == 1 ? "" : "s")}";
            return $"{count} template{(count == 1 ? "" : "s")} matching '{q}'";
        }
    }
}

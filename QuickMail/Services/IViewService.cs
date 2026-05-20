using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Services;

public interface IViewService
{
    List<SavedView> Load();
    void Save(List<SavedView> views);
}

using QuickMail.Models;

namespace QuickMail.Services;

public interface IConfigService
{
    /// <summary>Returns the current configuration, loading from disk on first call.</summary>
    ConfigModel Load();

    /// <summary>Persists the given configuration to disk.</summary>
    void Save(ConfigModel config);
}

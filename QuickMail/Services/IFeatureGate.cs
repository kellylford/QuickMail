using QuickMail.Models;

namespace QuickMail.Services;

/// <summary>
/// Runtime feature gate. Code paths and UI surfaces check this before exposing
/// experimental functionality. Defaults are baked into ConfigFeatureGate and
/// overridable via the config.ini [features] section or --feature CLI flags.
/// </summary>
public interface IFeatureGate
{
    bool IsEnabled(FeatureFlag flag);
}

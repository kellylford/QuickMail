using QuickMail.Services;

namespace QuickMail.Helpers;

/// <summary>
/// Writes the theme service's currently configured theme id into config.ini.
/// Shared by <c>MainViewModel</c> (Apply / Next / Previous) and
/// <c>ThemeManagerViewModel</c> (Apply / Delete) so the apply-then-persist
/// write-back lives in exactly one place. The theme service itself never writes
/// config — it exposes hex strings only, per the MVVM boundary — so persistence
/// is the ViewModel's job, and this is the single copy of that job.
/// </summary>
internal static class ThemePersistence
{
    public static void PersistConfiguredTheme(IThemeService themeService, IConfigService configService)
    {
        var cfg = configService.Load();
        cfg.AppearanceThemeId = themeService.ConfiguredThemeId;
        configService.Save(cfg);
    }
}

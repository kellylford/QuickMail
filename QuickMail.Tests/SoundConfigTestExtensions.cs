using System.Collections.Generic;
using QuickMail.Models;

namespace QuickMail.Tests;

internal static class SoundConfigTestExtensions
{
    /// <summary>
    /// The stored choices of the sound scheme in use, its section made if need be: what a test means
    /// by "the sound settings" now that each scheme keeps its own (#932).
    /// </summary>
    public static Dictionary<string, string> ActiveSoundEvents(this ConfigModel config)
    {
        var id = config.SoundSchemeId;
        if (!config.SoundSchemes.TryGetValue(id, out var scheme))
        {
            scheme = config.FindSoundScheme(id) ?? new SoundScheme(id, id, isBuiltIn: false);
            config.SoundSchemes[id] = scheme;
        }
        return scheme.Events;
    }
}

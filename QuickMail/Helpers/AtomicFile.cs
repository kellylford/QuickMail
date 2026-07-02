using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace QuickMail.Helpers;

/// <summary>
/// Writes files via a sibling temp file + rename so a crash or power loss mid-write can
/// never leave a truncated file — the old content survives intact until the new content
/// is fully on disk. Every service that persists settings-like state (accounts, views,
/// config, rules, templates, contacts, flags) writes through this helper.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        var tempPath = path + ".tmp";
        if (encoding is null) File.WriteAllText(tempPath, content);
        else File.WriteAllText(tempPath, content, encoding);
        File.Move(tempPath, path, overwrite: true);
    }

    public static async Task WriteAllTextAsync(string path, string content)
    {
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, content).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }
}

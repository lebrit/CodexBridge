using System.Text;

namespace CodexBridge.Core;

public static class ErrorLog
{
    public const string FileName = "CodexBridge-errors.log";
    private static readonly object Gate = new();

    public static string PreferredPath => Path.Combine(AppContext.BaseDirectory, FileName);
    public static string FallbackPath => Path.Combine(AppPaths.DataDirectory, FileName);

    public static string Write(
        string source,
        string message,
        string? details = null,
        string? preferredDirectory = null)
    {
        var entry = new StringBuilder()
            .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {source}")
            .AppendLine(message.Trim());
        if (!string.IsNullOrWhiteSpace(details))
            entry.AppendLine(details.Trim());
        entry.AppendLine(new string('-', 72));

        var directories = preferredDirectory is null
            ? new[] { AppContext.BaseDirectory, AppPaths.DataDirectory }
            : new[] { preferredDirectory };

        lock (Gate)
        {
            foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, FileName);
                    File.AppendAllText(path, entry.ToString(), new UTF8Encoding(false));
                    return path;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Try the per-user fallback when the application directory is read-only.
                }
            }
        }

        return string.Empty;
    }
}

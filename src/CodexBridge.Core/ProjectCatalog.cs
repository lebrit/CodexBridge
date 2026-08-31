namespace CodexBridge.Core;

public enum ProjectListFilter
{
    All,
    Protected,
    Excluded,
    Missing,
    NeedsAttention
}

public static class ProjectCatalogFilter
{
    public static bool Matches(ProjectEntry project, string? query, ProjectListFilter filter)
    {
        var normalizedQuery = query?.Trim() ?? "";
        var matchesText = normalizedQuery.Length == 0
                          || project.Name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                          || project.Path.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
        if (!matchesText)
            return false;

        return filter switch
        {
            ProjectListFilter.Protected => project.IsProtected && project.Status == ProjectStatus.Protected,
            ProjectListFilter.Excluded => !project.IsProtected || project.Status == ProjectStatus.Excluded,
            ProjectListFilter.Missing => project.Status == ProjectStatus.Missing,
            ProjectListFilter.NeedsAttention => project.Status == ProjectStatus.NeedsAttention,
            _ => true
        };
    }
}

public sealed class ProjectDiscoveryService(CatalogStore catalog)
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", ".venv", "bin", "obj", "dist", "build",
        "coverage", ".cache", ".codex-cache", ".codebase-memory", "graphify-out", "$Recycle.Bin",
        "System Volume Information", "Windows", "Program Files", "Program Files (x86)"
    };

    private static readonly string[] MarkerFiles =
    [
        "AGENTS.md", "package.json", "pyproject.toml", "requirements.txt", "Cargo.toml", "go.mod",
        "pom.xml", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "docker-compose.yml",
        "docker-compose.yaml"
    ];

    public async Task<CatalogRefreshResult> RefreshAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        var existing = await catalog.LoadAsync(cancellationToken);
        var byPath = existing.ToDictionary(p => Normalize(p.Path), StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var scanned = 0;

        foreach (var root in settings.ProjectRoots.Where(Directory.Exists))
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((Path.GetFullPath(root), 0));

            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = queue.Dequeue();
                if (++scanned > 20_000)
                {
                    warnings.Add("Поиск остановлен после 20 000 каталогов. Добавьте более точные корни проектов.");
                    queue.Clear();
                    break;
                }

                try
                {
                    if (IsReparsePoint(current.Path))
                        continue;

                    if (LooksLikeProject(current.Path))
                    {
                        var normalized = Normalize(current.Path);
                        found.Add(normalized);
                        if (byPath.TryGetValue(normalized, out var known))
                        {
                            known.Name = Path.GetFileName(current.Path.TrimEnd(Path.DirectorySeparatorChar));
                            known.IsGit = Directory.Exists(Path.Combine(current.Path, ".git"));
                            known.LastSeenUtc = DateTimeOffset.UtcNow;
                            known.Status = known.IsProtected ? ProjectStatus.Protected : ProjectStatus.Excluded;
                        }
                        else
                        {
                            byPath[normalized] = new ProjectEntry
                            {
                                Name = Path.GetFileName(current.Path.TrimEnd(Path.DirectorySeparatorChar)),
                                Path = current.Path,
                                IsGit = Directory.Exists(Path.Combine(current.Path, ".git")),
                                Status = ProjectStatus.Protected
                            };
                        }
                    }

                    if (current.Depth >= Math.Clamp(settings.ScanDepth, 1, 12))
                        continue;

                    foreach (var directory in Directory.EnumerateDirectories(current.Path))
                    {
                        if (!IgnoredDirectories.Contains(Path.GetFileName(directory)))
                            queue.Enqueue((directory, current.Depth + 1));
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    warnings.Add($"Нет доступа: {current.Path}");
                }
                catch (IOException exception)
                {
                    warnings.Add($"Не удалось прочитать {current.Path}: {exception.Message}");
                }
            }
        }

        foreach (var project in byPath.Values.Where(project => !found.Contains(Normalize(project.Path))))
            project.Status = ProjectStatus.Missing;

        var result = byPath.Values.OrderBy(p => p.Name).ThenBy(p => p.Path).ToList();
        await catalog.SaveAsync(result, cancellationToken);
        return new CatalogRefreshResult(result, scanned, warnings);
    }

    public Task SaveAsync(IReadOnlyCollection<ProjectEntry> projects, CancellationToken cancellationToken = default) =>
        catalog.SaveAsync(projects, cancellationToken);

    private static bool LooksLikeProject(string directory)
    {
        if (Directory.Exists(Path.Combine(directory, ".git")))
            return true;

        if (MarkerFiles.Any(marker => File.Exists(Path.Combine(directory, marker))))
            return true;

        try
        {
            return Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly).Any()
                   || Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

public static class PathPolicy
{
    public static IReadOnlyList<string> ReduceNestedRoots(IEnumerable<string> paths)
    {
        var ordered = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length)
            .ToList();

        var result = new List<string>();
        foreach (var path in ordered)
        {
            if (!result.Any(parent => IsInside(path, parent)))
                result.Add(path);
        }

        return result;
    }

    public static bool IsInside(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

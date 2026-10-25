using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexBridge.Core;

public sealed record UpdateInfo(
    string Version,
    string TagName,
    Uri ReleasePage,
    string InstallerName,
    Uri InstallerUrl,
    string ChecksumName,
    Uri ChecksumUrl);

public sealed class UpdateService
{
    private const long MaximumInstallerBytes = 500L * 1024 * 1024;
    private static readonly Regex SupportedVersion = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-build\.(?<run>\d+)\.(?<attempt>\d+))?(?:\+.*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ChecksumLine = new(
        @"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<file>[^\r\n]+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HttpClient client;

    public UpdateService(HttpClient? client = null)
    {
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<UpdateInfo?> GetLatestAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var current = ParseVersion(currentVersion)
                      ?? throw new ArgumentException("Текущая версия имеет неподдерживаемый формат.", nameof(currentVersion));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.github.com/repos/lebrit/CodexBridge/releases?per_page=20");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodexBridge", currentVersion.TrimStart('v')));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(
            responseStream, cancellationToken: cancellationToken) ?? [];

        return releases
            .Where(release => !release.Draft && ParseVersion(release.TagName) is not null)
            .Select(release => (Release: release, Version: ParseVersion(release.TagName)!.Value))
            .Where(item => item.Version.CompareTo(current) > 0)
            .OrderByDescending(item => item.Version)
            .Select(item => CreateUpdateInfo(item.Release))
            .FirstOrDefault(info => info is not null);
    }

    public async Task<string> DownloadVerifiedInstallerAsync(
        UpdateInfo update,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ValidateDownload(update.InstallerUrl, update.InstallerName);
        ValidateDownload(update.ChecksumUrl, update.ChecksumName);
        Directory.CreateDirectory(destinationDirectory);

        var checksumText = await client.GetStringAsync(update.ChecksumUrl, cancellationToken);
        var match = ChecksumLine.Match(checksumText.Trim());
        if (!match.Success || !string.Equals(match.Groups["file"].Value.Trim(), update.InstallerName,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл контрольной суммы релиза имеет неверный формат или имя.");
        var expectedHash = match.Groups["hash"].Value.ToLowerInvariant();

        var destination = Path.Combine(Path.GetFullPath(destinationDirectory), update.InstallerName);
        var temporary = destination + ".partial";
        try
        {
            using var response = await client.GetAsync(
                update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumInstallerBytes)
                throw new InvalidDataException("Установщик превышает допустимый размер.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;
                total += read;
                if (total > MaximumInstallerBytes)
                    throw new InvalidDataException("Установщик превышает допустимый размер.");
                hasher.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            var actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
                throw new InvalidDataException("SHA-256 скачанного установщика не совпадает с релизом.");

            output.Close();
            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // Do not hide the original download or verification error.
            }
            throw;
        }
    }

    public static bool IsNewer(string candidate, string current)
    {
        var candidateVersion = ParseVersion(candidate);
        var currentVersion = ParseVersion(current);
        return candidateVersion is not null && currentVersion is not null
               && candidateVersion.Value.CompareTo(currentVersion.Value) > 0;
    }

    private static UpdateInfo? CreateUpdateInfo(GitHubRelease release)
    {
        var version = release.TagName.TrimStart('v', 'V');
        var installerName = $"CodexBridge-{version}-setup.exe";
        var checksumName = installerName + ".sha256";
        var installer = release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, installerName, StringComparison.OrdinalIgnoreCase));
        var checksum = release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, checksumName, StringComparison.OrdinalIgnoreCase));
        if (installer is null || checksum is null
            || !Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var releasePage)
            || !Uri.TryCreate(installer.DownloadUrl, UriKind.Absolute, out var installerUrl)
            || !Uri.TryCreate(checksum.DownloadUrl, UriKind.Absolute, out var checksumUrl))
            return null;
        if (!IsTrustedGitHubUrl(releasePage) || !IsTrustedGitHubUrl(installerUrl) || !IsTrustedGitHubUrl(checksumUrl))
            return null;
        return new UpdateInfo(version, release.TagName, releasePage,
            installerName, installerUrl, checksumName, checksumUrl);
    }

    private static ReleaseVersion? ParseVersion(string? value)
    {
        var match = SupportedVersion.Match(value?.Trim() ?? string.Empty);
        if (!match.Success)
            return null;
        return new ReleaseVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            !match.Groups["run"].Success,
            match.Groups["run"].Success ? int.Parse(match.Groups["run"].Value) : 0,
            match.Groups["attempt"].Success ? int.Parse(match.Groups["attempt"].Value) : 0);
    }

    private static void ValidateDownload(Uri uri, string fileName)
    {
        if (!IsTrustedGitHubUrl(uri) || Path.GetFileName(fileName) != fileName || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Адрес или имя файла обновления не прошли проверку.");
    }

    private static bool IsTrustedGitHubUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase));

    private readonly record struct ReleaseVersion(
        int Major,
        int Minor,
        int Patch,
        bool Stable,
        int Run,
        int Attempt)
        : IComparable<ReleaseVersion>
    {
        public int CompareTo(ReleaseVersion other)
        {
            var left = new[] { Major, Minor, Patch };
            var right = new[] { other.Major, other.Minor, other.Patch };
            for (var index = 0; index < left.Length; index++)
            {
                var comparison = left[index].CompareTo(right[index]);
                if (comparison != 0)
                    return comparison;
            }
            if (Stable != other.Stable)
                return Stable ? 1 : -1;
            var runComparison = Run.CompareTo(other.Run);
            return runComparison != 0 ? runComparison : Attempt.CompareTo(other.Attempt);
        }
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";
        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("draft")]
        public bool Draft { get; set; }
        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";
    }
}

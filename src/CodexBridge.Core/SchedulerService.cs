using System.Xml.Linq;

namespace CodexBridge.Core;

public sealed class SchedulerService(ProcessRunner processes)
{
    public async Task<OperationResult> InstallAsync(string taskName, string agentExecutable, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(agentExecutable))
            return OperationResult.Fail("Фоновый агент не найден рядом с приложением.", agentExecutable);

        var taskCommand = $"\"{Path.GetFullPath(agentExecutable)}\"";
        var result = await processes.RunAsync("schtasks.exe",
        [
            "/Create", "/TN", taskName, "/TR", taskCommand,
            "/SC", "HOURLY", "/MO", "1", "/F", "/RL", "LIMITED"
        ], cancellationToken: cancellationToken);

        if (!result.Succeeded)
            return OperationResult.Fail("Не удалось установить автоматический бэкап.", result.Combined);

        var firstRun = await RunNowAsync(taskName, cancellationToken);
        return firstRun.Succeeded
            ? OperationResult.Ok("Автоматический бэкап включён: каждый час. Первый фоновый запуск начат.")
            : OperationResult.Fail("Расписание установлено, но первый фоновый запуск не начался.", firstRun.Details);
    }

    public async Task<OperationResult> RemoveAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var result = await processes.RunAsync("schtasks.exe", ["/Delete", "/TN", taskName, "/F"], cancellationToken: cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok("Автоматический бэкап отключён.")
            : OperationResult.Fail("Не удалось отключить автоматический бэкап.", result.Combined);
    }

    public async Task<ScheduledTaskStatus> GetStatusAsync(
        string taskName,
        string currentAgentExecutable,
        CancellationToken cancellationToken = default)
    {
        var result = await processes.RunAsync(
            "schtasks.exe", ["/Query", "/TN", taskName, "/XML"], cancellationToken: cancellationToken);
        if (!result.Succeeded)
            return new ScheduledTaskStatus(false, false);

        var configuredAgent = ReadAgentExecutable(result.Output) ?? "";
        return new ScheduledTaskStatus(true, PathsEqual(configuredAgent, currentAgentExecutable), configuredAgent);
    }

    public async Task<OperationResult> RunNowAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var result = await processes.RunAsync(
            "schtasks.exe", ["/Run", "/TN", taskName], cancellationToken: cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok("Фоновый бэкап запущен. Результат появится в последних операциях.")
            : OperationResult.Fail("Не удалось запустить фоновый бэкап. Сначала включите или обновите расписание.", result.Combined);
    }

    public static string? ReadAgentExecutable(string taskXml)
    {
        if (string.IsNullOrWhiteSpace(taskXml))
            return null;

        try
        {
            return XDocument.Parse(taskXml)
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "Command")?
                .Value.Trim().Trim('"');
        }
        catch
        {
            return null;
        }
    }

    private static bool PathsEqual(string configuredAgent, string currentAgent)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(configuredAgent)
                   && File.Exists(currentAgent)
                   && string.Equals(Path.GetFullPath(configuredAgent), Path.GetFullPath(currentAgent),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public sealed class BackupToolInstaller(ProcessRunner processes)
{
    public async Task<OperationResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        var restic = await InstallPackageAsync("restic.restic", cancellationToken);
        if (!restic.Succeeded)
            return OperationResult.Fail("Не удалось установить restic.", restic.Combined);

        var rclone = await InstallPackageAsync("Rclone.Rclone", cancellationToken);
        return rclone.Succeeded
            ? OperationResult.Ok("Restic и rclone установлены через WinGet.")
            : OperationResult.Fail("Restic установлен, но rclone установить не удалось.", rclone.Combined);
    }

    private Task<ProcessResult> InstallPackageAsync(string packageId, CancellationToken cancellationToken) =>
        processes.RunAsync("winget.exe",
        [
            "install", "--id", packageId, "--exact", "--source", "winget",
            "--accept-package-agreements", "--accept-source-agreements", "--silent", "--disable-interactivity"
        ], cancellationToken: cancellationToken);
}

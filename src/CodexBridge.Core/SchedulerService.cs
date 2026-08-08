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

        return result.Succeeded
            ? OperationResult.Ok("Автоматический бэкап установлен: один раз в час.")
            : OperationResult.Fail("Не удалось установить автоматический бэкап.", result.Combined);
    }

    public async Task<OperationResult> RemoveAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var result = await processes.RunAsync("schtasks.exe", ["/Delete", "/TN", taskName, "/F"], cancellationToken: cancellationToken);
        return result.Succeeded
            ? OperationResult.Ok("Автоматический бэкап отключён.")
            : OperationResult.Fail("Не удалось отключить автоматический бэкап.", result.Combined);
    }

    public async Task<bool> ExistsAsync(string taskName, CancellationToken cancellationToken = default)
    {
        var result = await processes.RunAsync("schtasks.exe", ["/Query", "/TN", taskName], cancellationToken: cancellationToken);
        return result.Succeeded;
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

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using PCL.Plugin.SDK;

namespace PCL.Plugin.ImcProxy;

public sealed class ImcProxyRuntime
{
    public const string DownloadUrl = "https://github.com/HertaTeam/IMCProxy-Free/archive/refs/heads/main.zip";
    public const string ApiUrl = "http://127.0.0.1:8790";

    private readonly IPluginContext _context;
    private readonly ImcProxyApi _api;
    private Process? _process;

    public ImcProxyRuntime(IPluginContext context, ImcProxyApi api)
    {
        _context = context;
        _api = api;
        InstallDirectory = Path.Combine(context.DataDirectory, "imcproxy-free");
    }

    public string InstallDirectory { get; }

    public string NodePath { get; set; } = "";

    public bool HasInstallation()
    {
        return File.Exists(Path.Combine(InstallDirectory, "package.json"));
    }

    public bool IsProxyRunning()
    {
        return _process is { HasExited: false };
    }

    public async Task DownloadFullVersionAsync(IProgress<PluginDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), "imcproxy-free-" + Guid.NewGuid().ToString("n") + ".zip");
        try
        {
            await _context.Launcher.DownloadAsync(new Uri(DownloadUrl), tempZip, progress, cancellationToken);
            await ExtractAsync(tempZip, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    private async Task ExtractAsync(string zipPath, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "imcproxy-free-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir, true);
            var root = Directory.GetDirectories(tempDir).FirstOrDefault() ?? tempDir;

            if (Directory.Exists(InstallDirectory))
                Directory.Delete(InstallDirectory, true);
            Directory.CreateDirectory(InstallDirectory);

            foreach (var file in Directory.GetFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Copy(file, Path.Combine(InstallDirectory, Path.GetFileName(file)), true);
            }
            foreach (var dir in Directory.GetDirectories(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(InstallDirectory, Path.GetFileName(dir));
                CopyDirectory(dir, target);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
        await Task.CompletedTask;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    public async Task InstallDependenciesAsync(CancellationToken cancellationToken)
    {
        await RunHiddenAsync("npm.cmd", "install --no-audit --no-fund", InstallDirectory, cancellationToken);
        await RunHiddenAsync("npm.cmd", "run build", InstallDirectory, cancellationToken);
    }

    public async Task<int> RunHiddenAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _context.Logger.Debug("[imcproxy] " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _context.Logger.Warn("[imcproxy] " + e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsProxyRunning()) return;
        if (!HasInstallation()) throw new InvalidOperationException("IMCProxy-Free 尚未下载，请先点击下载完整版。");

        var entry = Path.Combine(InstallDirectory, "build", "reverse_proxy", "index.js");
        if (!File.Exists(entry)) throw new InvalidOperationException("缺少构建产物，请先点击安装依赖并构建。");

        var node = string.IsNullOrWhiteSpace(NodePath) ? "node.exe" : NodePath;
        var info = new ProcessStartInfo(node, "\"" + entry + "\"")
        {
            WorkingDirectory = InstallDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process = new Process { StartInfo = info, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _context.Logger.Debug("[imcproxy] " + e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _context.Logger.Warn("[imcproxy] " + e.Data);
        };
        _process.Exited += (_, _) => _context.Logger.Info("[imcproxy] proxy process exited");

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        if (process is { HasExited: false })
        {
            try
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _context.Logger.Warn("[imcproxy] cannot stop process", ex);
            }
        }
        process?.Dispose();
    }
}

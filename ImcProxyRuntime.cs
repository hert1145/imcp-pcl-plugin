using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCL.Plugin.SDK;

namespace PCL.Plugin.ImcProxy;

public sealed class ImcProxyRuntime
{
    public const string RepoOwner = "HertaTeam";
    public const string RepoName = "IMCProxy-NextGen-Free";
    public const string ApiUrl = "http://127.0.0.1:8790";

    private readonly IPluginContext _context;
    private readonly ImcProxyApi _api;
    private Process? _process;
    private StreamWriter? _stdin;
    private string _installedTag = "";
    private string _installedAsset = "";
    private string _installedAt = "";

    public ImcProxyRuntime(IPluginContext context, ImcProxyApi api)
    {
        _context = context;
        _api = api;
        InstallDirectory = Path.Combine(context.DataDirectory, "imcproxy-free");
        LoadInstallInfo();
    }

    public string InstallDirectory { get; }

    public string ExecutablePath => Path.Combine(InstallDirectory, "imcproxy.exe");

    public string InstalledTag => _installedTag;

    public string InstalledAsset => _installedAsset;

    public string InstalledAt => _installedAt;

    private string InstallInfoPath => Path.Combine(InstallDirectory, ".install.json");

    public sealed record ReleaseAsset(string Tag, string AssetName, string DownloadUrl, string Notes, bool IsExecutable);

    public bool HasInstallation() => File.Exists(ExecutablePath);

    public bool IsProxyRunning() => _process is { HasExited: false };

    public async Task<ReleaseAsset> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetLatestFromApiAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _context.Logger.Warn("GitHub API unavailable, falling back to the releases page: " + ex.Message);
            return await GetLatestFromHtmlAsync(cancellationToken);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("IMCProxy-PCL-Plugin/2.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static async Task<ReleaseAsset> GetLatestFromApiAsync(CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        var json = await http.GetStringAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var bodyNode) ? bodyNode.GetString() ?? "" : "";
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            ReleaseAsset? zipFallback = null;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var urlNode) ? urlNode.GetString() ?? "" : "";
                if (url.Length == 0) continue;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return new ReleaseAsset(tag, name, url, notes, true);
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && zipFallback is null)
                    zipFallback = new ReleaseAsset(tag, name, url, notes, false);
            }
            if (zipFallback is not null) return zipFallback;
        }
        throw new InvalidOperationException("最新 release 里没有 exe 或 zip 资源");
    }

    private static async Task<ReleaseAsset> GetLatestFromHtmlAsync(CancellationToken cancellationToken)
    {
        using var http = CreateHttpClient();
        using var response = await http.GetAsync($"https://github.com/{RepoOwner}/{RepoName}/releases/latest", cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
        var tag = finalUrl.Length > 0 && finalUrl.Contains("/tag/")
            ? finalUrl[(finalUrl.LastIndexOf('/') + 1)..]
            : "";
        if (tag.Length == 0) throw new InvalidOperationException("无法确定最新 release 的 tag");

        var html = await http.GetStringAsync($"https://github.com/{RepoOwner}/{RepoName}/releases/expanded_assets/{tag}", cancellationToken);
        var match = Regex.Match(html, "/releases/download/[^\"'\\s]+?\\.(exe|zip)", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("最新 release 页面里没有 exe 或 zip 资源");
        var path = match.Value;
        var assetName = path[(path.LastIndexOf('/') + 1)..];
        var url = "https://github.com" + path;
        var executable = assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        return new ReleaseAsset(tag, assetName, url, "", executable);
    }

    public async Task DownloadAsync(IProgress<PluginDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var release = await GetLatestReleaseAsync(cancellationToken);
        await StopAsync(CancellationToken.None);

        var suffix = release.IsExecutable ? ".exe" : ".zip";
        var relative = "downloads/imcproxy-" + Guid.NewGuid().ToString("n") + suffix;
        var local = Path.Combine(_context.DataDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            await _context.Launcher.DownloadAsync(new Uri(release.DownloadUrl), relative, progress, cancellationToken);
            if (release.IsExecutable)
                await InstallExecutableAsync(local, cancellationToken);
            else
                await ExtractAsync(local, cancellationToken);
            _installedTag = release.Tag;
            _installedAsset = release.AssetName;
            _installedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveInstallInfo();
        }
        finally
        {
            if (File.Exists(local)) File.Delete(local);
        }
    }

    private async Task InstallExecutableAsync(string downloadPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(downloadPath);
        if (info.Length < 100_000)
            throw new InvalidDataException("下载的可执行文件体积异常（" + info.Length + " 字节）");
        await using (var probe = File.OpenRead(downloadPath))
        {
            var header = new byte[2];
            if (await probe.ReadAsync(header.AsMemory(0, 2), cancellationToken) != 2 ||
                header[0] != (byte)'M' || header[1] != (byte)'Z')
                throw new InvalidDataException("下载的文件不是有效的 Windows 可执行文件");
        }

        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(Path.Combine(InstallDirectory, "logs"));

        var target = ExecutablePath;
        if (File.Exists(target))
        {
            try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
            File.Delete(target);
        }
        await using var source = File.OpenRead(downloadPath);
        await using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static readonly string[] PreservedFiles =
    {
        "config.json",
        "game-account.txt",
        "game-accounts.txt",
        "bind-code.txt",
        ".imc-session.json",
        ".imc-captoken.json",
    };

    private async Task ExtractAsync(string zipPath, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "imcproxy-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDir);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tempDir, true);
            var root = Directory.GetDirectories(tempDir).FirstOrDefault() ?? tempDir;

            var saved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in PreservedFiles)
            {
                var existing = Path.Combine(InstallDirectory, name);
                if (File.Exists(existing)) saved[name] = await File.ReadAllTextAsync(existing, cancellationToken);
            }

            if (Directory.Exists(InstallDirectory))
                Directory.Delete(InstallDirectory, true);
            Directory.CreateDirectory(InstallDirectory);

            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("logs", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(InstallDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }

            foreach (var (name, content) in saved)
                await File.WriteAllTextAsync(Path.Combine(InstallDirectory, name), content, cancellationToken);

            Directory.CreateDirectory(Path.Combine(InstallDirectory, "logs"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsProxyRunning()) return Task.CompletedTask;
        if (!HasInstallation())
            throw new InvalidOperationException("IMCProxy-Free 尚未下载，请先点击「下载 / 更新完整版」。");

        var info = new ProcessStartInfo(ExecutablePath, "--background")
        {
            WorkingDirectory = InstallDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
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
        _stdin = _process.StandardInput;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        try
        {
            _stdin?.Dispose();
        }
        catch
        {
        }
        _stdin = null;
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

    public void OpenInstallFolder()
    {
        if (!Directory.Exists(InstallDirectory)) return;
        _context.Launcher.OpenPath(InstallDirectory);
    }

    public string ConfigPath => Path.Combine(InstallDirectory, "config.json");

    private void LoadInstallInfo()
    {
        try
        {
            if (!File.Exists(InstallInfoPath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(InstallInfoPath));
            var root = document.RootElement;
            _installedTag = root.TryGetProperty("tag", out var tag) ? tag.GetString() ?? "" : "";
            _installedAsset = root.TryGetProperty("asset", out var asset) ? asset.GetString() ?? "" : "";
            _installedAt = root.TryGetProperty("installedAt", out var at) ? at.GetString() ?? "" : "";
        }
        catch
        {
        }
    }

    private void SaveInstallInfo()
    {
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            var payload = JsonSerializer.Serialize(new
            {
                tag = _installedTag,
                asset = _installedAsset,
                installedAt = _installedAt,
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(InstallInfoPath, payload);
        }
        catch (Exception ex)
        {
            _context.Logger.Warn("[imcproxy] cannot write install info", ex);
        }
    }
}

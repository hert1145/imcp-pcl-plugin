using System.Text.RegularExpressions;
using System.Text;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PCL.Plugin.SDK;

namespace PCL.Plugin.ImcProxy;

public sealed class ControlPage
{
    private readonly IPluginContext _context;
    private readonly ImcProxyRuntime _runtime;
    private readonly ImcProxyApi _api;

    private readonly TextBlock _status = new() { Text = "正在读取状态…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _hint = new() { Text = "", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Opacity = 0.75 };
    private readonly StackPanel _settings = new();
    private readonly TextBox _nodePath = new() { MinWidth = 320, Margin = new Thickness(0, 0, 12, 0) };
    private bool _loaded;
    private TextBox? _configEditor;
    private readonly TextBlock _versionInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8), Opacity = 0.85 };
    private readonly TextBox _changelogBox = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        Height = 180,
        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
        FontSize = 12,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap,
        Margin = new Thickness(0, 4, 0, 0),
    };

    public ControlPage(IPluginContext context, ImcProxyRuntime runtime, ImcProxyApi api)
    {
        _context = context;
        _runtime = runtime;
        _api = api;
    }

    public FrameworkElement CreateView()
    {
        var root = new StackPanel { Margin = new Thickness(24) };

        root.Children.Add(new TextBlock
        {
            Text = "IMCProxy",
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
        });
        root.Children.Add(new TextBlock
        {
            Text = "通过 HTTP API 控制 IMCProxy-Free：下载、启动、停止与配置。代理在后台运行，不显示控制台窗口。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 16),
            Opacity = 0.8,
        });

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("下载 / 更新完整版", DownloadAsync, PluginButtonStyle.Highlight)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("安装依赖并构建", InstallAsync)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("启动代理", StartAsync)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("停止代理", StopAsync, PluginButtonStyle.Danger)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("刷新状态", RefreshAsync)));
        buttons.Children.Add(WithMargin(_context.Ui.CreateButton("打开配置文件夹", OpenConfigFolderAsync)));
        root.Children.Add(buttons);

        var nodeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        nodeRow.Children.Add(new TextBlock { Text = "Node 路径", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        nodeRow.Children.Add(_nodePath);
        nodeRow.Children.Add(_context.Ui.CreateButton("保存", SaveNodePathAsync));
        root.Children.Add(nodeRow);

        root.Children.Add(new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = _status,
        });
        root.Children.Add(_hint);
        root.Children.Add(new TextBlock
        {
            Text = "IMCProxy-Free 版本与更新",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "检测的是被管理的 IMCProxy-Free 版本，与插件自身版本无关。",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        root.Children.Add(_versionInfo);
        var versionRow = new WrapPanel();
        versionRow.Children.Add(WithMargin(_context.Ui.CreateButton("检查更新", CheckUpdatesAsync)));
        versionRow.Children.Add(WithMargin(_context.Ui.CreateButton("加载更新日志", LoadChangelogAsync)));
        root.Children.Add(versionRow);
        root.Children.Add(_changelogBox);

        root.Children.Add(new TextBlock
        {
            Text = "配置文件夹",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 4),
        });
        root.Children.Add(new TextBlock
        {
            Text = "config.json、game-account.txt 与 logs/ 都保存在安装目录。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var folderRow = new WrapPanel();
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("打开文件夹", OpenConfigFolderAsync)));
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("重新载入", ReloadConfigAsync)));
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("保存 config.json", SaveConfigAsync)));
        folderRow.Children.Add(WithMargin(_context.Ui.CreateButton("保存并重启代理", SaveAndRestartAsync, PluginButtonStyle.Highlight)));
        root.Children.Add(folderRow);

        var configEditor = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            Height = 220,
            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(configEditor);
        _configEditor = configEditor;

        root.Children.Add(new TextBlock
        {
            Text = "直接编辑后点保存；端口、上游等改动需要重启代理生效。",
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8),
        });

        root.Children.Add(new TextBlock
        {
            Text = "配置",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 20, 0, 8),
        });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 460,
            Content = _settings,
        };
        root.Children.Add(scroll);

        root.Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadNodePathAsync();
            await LoadConfigTextAsync();
            await RefreshAsync();
        };

        return root;
    }

    private static FrameworkElement WithMargin(FrameworkElement element)
    {
        element.Margin = new Thickness(0, 0, 8, 8);
        return element;
    }

    private async Task LoadNodePathAsync()
    {
        try
        {
            var saved = await _context.Settings.GetAsync<string>("nodePath");
            _nodePath.Text = saved ?? "";
        }
        catch
        {
        }
    }

    private async Task SaveNodePathAsync()
    {
        await PersistNodePathAsync();
        _context.Ui.ShowNotification("已保存 Node 路径", PluginNotificationKind.Success);
    }

    private async Task PersistNodePathAsync()
    {
        var value = _nodePath.Text?.Trim() ?? "";
        _runtime.NodePath = value;
        await _context.Settings.SetAsync("nodePath", value);
    }

    private async Task DownloadAsync()
    {
        var confirmed = await _context.Ui.ConfirmAsync("下载 IMCProxy-Free", "将下载完整仓库（含源码）并覆盖本地目录，继续？");
        if (!confirmed) return;

        var progress = new Progress<PluginDownloadProgress>(p =>
        {
            var text = p.TotalBytes is > 0
                ? $"下载中 {p.BytesReceived / 1024.0 / 1024.0:F1} / {p.TotalBytes.Value / 1024.0 / 1024.0:F1} MB"
                : $"下载中 {p.BytesReceived / 1024.0 / 1024.0:F1} MB";
            _hint.Text = text;
        });

        try
        {
            _hint.Text = "准备下载…";
            await _runtime.DownloadFullVersionAsync(progress, CancellationToken.None);
            _hint.Text = "已下载到 " + _runtime.InstallDirectory;
            _context.Ui.ShowNotification("IMCProxy-Free 下载完成", PluginNotificationKind.Success);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _hint.Text = "下载失败：" + ex.Message;
            _context.Ui.ShowNotification("下载失败", PluginNotificationKind.Error);
            _context.Logger.Error("download failed", ex);
        }
    }

    private async Task InstallAsync()
    {
        if (!_runtime.HasInstallation())
        {
            _context.Ui.ShowNotification("请先下载完整版", PluginNotificationKind.Warning);
            return;
        }
        try
        {
            _hint.Text = "正在安装依赖并构建，可能需要几分钟…";
            await PersistNodePathAsync();
            await _runtime.InstallDependenciesAsync(CancellationToken.None);
            _hint.Text = "构建完成，可以启动代理。";
            _context.Ui.ShowNotification("依赖安装与构建完成", PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _hint.Text = "安装失败：" + ex.Message;
            _context.Ui.ShowNotification("安装失败", PluginNotificationKind.Error);
            _context.Logger.Error("install failed", ex);
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await PersistNodePathAsync();
            await _runtime.StartAsync(CancellationToken.None);
            await Task.Delay(1200);
            await RefreshAsync();
            _context.Ui.ShowNotification("已启动代理", PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _hint.Text = "启动失败：" + ex.Message;
            _context.Ui.ShowNotification("启动失败", PluginNotificationKind.Error);
            _context.Logger.Error("start failed", ex);
        }
    }

    private async Task StopAsync()
    {
        await _runtime.StopAsync(CancellationToken.None);
        await RefreshAsync();
        _context.Ui.ShowNotification("已停止代理", PluginNotificationKind.Info);
    }

    private bool TrySyncApiBaseFromConfig()
    {
        try
        {
            var configFile = Path.Combine(_runtime.InstallDirectory, "config.json");
            if (!File.Exists(configFile)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(configFile));
            if (!document.RootElement.TryGetProperty("api", out var api)) return false;
            var host = api.TryGetProperty("host", out var hostNode) ? hostNode.GetString() ?? "127.0.0.1" : "127.0.0.1";
            if (host == "0.0.0.0") host = "127.0.0.1";
            var port = api.TryGetProperty("port", out var portNode) ? portNode.GetInt32() : 8790;
            var token = api.TryGetProperty("token", out var tokenNode) ? tokenNode.GetString() ?? "" : "";
            _api.BaseUrl = $"http://{host}:{port}/api";
            _api.Token = token;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private Task OpenConfigFolderAsync()
    {
        if (!Directory.Exists(_runtime.InstallDirectory))
        {
            _context.Ui.ShowNotification("尚未下载 IMCProxy-Free，配置文件夹不存在", PluginNotificationKind.Warning);
            return Task.CompletedTask;
        }
        _context.Launcher.OpenPath(_runtime.InstallDirectory);
        return Task.CompletedTask;
    }

    private string ConfigFile => Path.Combine(_runtime.InstallDirectory, "config.json");

    private async Task ReloadConfigAsync()
    {
        await LoadConfigTextAsync();
        _context.Ui.ShowNotification("已重新载入 config.json", PluginNotificationKind.Info);
    }

    private async Task LoadConfigTextAsync()
    {
        if (_configEditor is null) return;
        try
        {
            if (!File.Exists(ConfigFile))
            {
                _configEditor.Text = "{}";
                _context.Ui.ShowNotification("config.json 尚未生成，先启动一次代理", PluginNotificationKind.Info);
                return;
            }
            _configEditor.Text = await File.ReadAllTextAsync(ConfigFile);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("读取失败：" + ex.Message, PluginNotificationKind.Error);
        }
    }

    private async Task SaveConfigAsync()
    {
        if (_configEditor is null) return;
        var content = _configEditor.Text;
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(content);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("JSON 格式错误，未保存：" + ex.Message, PluginNotificationKind.Warning);
            return;
        }
        try
        {
            Directory.CreateDirectory(_runtime.InstallDirectory);
            await File.WriteAllTextAsync(ConfigFile, content);
            _context.Ui.ShowNotification("config.json 已保存", PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("保存失败：" + ex.Message, PluginNotificationKind.Error);
        }
    }

    private async Task SaveAndRestartAsync()
    {
        await SaveConfigAsync();
        try
        {
            await _runtime.StopAsync(CancellationToken.None);
            await _runtime.StartAsync(CancellationToken.None);
            await Task.Delay(1200);
            await RefreshAsync();
            _context.Ui.ShowNotification("已保存并重启代理", PluginNotificationKind.Success);
        }
        catch (Exception ex)
        {
            _context.Ui.ShowNotification("重启失败：" + ex.Message, PluginNotificationKind.Error);
        }
    }

    private const string VersionUrl = "https://raw.githubusercontent.com/HertaTeam/IMCProxy-Free/main/version.json";
    private const string ChangelogUrl = "https://raw.githubusercontent.com/HertaTeam/IMCProxy-Free/main/src/reverse_proxy/Changelog.ts";

    private async Task CheckUpdatesAsync()
    {
        var local = await ReadLocalVersionAsync();
        if (string.IsNullOrWhiteSpace(local))
        {
            try
            {
                var version = await _api.GetAsync("version");
                local = version["editDate"]?.GetValue<string>() ?? version["version"]?.GetValue<string>() ?? "";
            }
            catch
            {
            }
        }

        string remote;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var text = await http.GetStringAsync(VersionUrl);
            using var document = JsonDocument.Parse(text);
            remote = document.RootElement.TryGetProperty("editDate", out var date) ? date.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            _versionInfo.Text = "IMCProxy-Free 检查失败：" + ex.Message;
            _context.Ui.ShowNotification("IMCProxy-Free 更新检查失败", PluginNotificationKind.Warning);
            return;
        }

        var state = string.CompareOrdinal(remote, local) > 0 ? "IMCProxy-Free 有更新，可点「下载 / 更新完整版」" : "IMCProxy-Free 已是最新";
        _versionInfo.Text = "本地 IMCProxy-Free：" + (string.IsNullOrWhiteSpace(local) ? "未知（尚未下载或未运行）" : local) +
            "    远程：" + (string.IsNullOrWhiteSpace(remote) ? "未知" : remote) + Environment.NewLine + state;
        _context.Ui.ShowNotification(state, state.Contains("有更新") ? PluginNotificationKind.Warning : PluginNotificationKind.Success);
    }


    private async Task<string> ReadLocalVersionAsync()
    {
        try
        {
            var file = Path.Combine(_runtime.InstallDirectory, "version.json");
            if (!File.Exists(file)) return "";
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            return document.RootElement.TryGetProperty("editDate", out var date) ? date.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    private async Task LoadChangelogAsync()
    {
        try
        {
            var node = await _api.GetAsync("changelog");
            if (node["entries"] is JsonArray entries)
            {
                var builder = new StringBuilder();
                foreach (var entry in entries)
                {
                    if (entry is not JsonObject item) continue;
                    builder.AppendLine($"v{item["version"]?.GetValue<string>()} ({item["date"]?.GetValue<string>()})");
                    if (item["changes"] is JsonArray changes)
                    {
                        foreach (var change in changes) builder.AppendLine("  - " + change?.GetValue<string>());
                    }
                    builder.AppendLine();
                }
                _changelogBox.Text = builder.ToString().TrimEnd();
                _context.Ui.ShowNotification("已从代理接口读取更新日志", PluginNotificationKind.Success);
                return;
            }
        }
        catch
        {
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var text = await http.GetStringAsync(ChangelogUrl);
            _changelogBox.Text = FormatChangelog(text);
            _context.Ui.ShowNotification("已从仓库读取更新日志", PluginNotificationKind.Info);
        }
        catch (Exception ex)
        {
            _changelogBox.Text = "读取失败：" + ex.Message;
            _context.Ui.ShowNotification("更新日志读取失败", PluginNotificationKind.Warning);
        }
    }

    private static string FormatChangelog(string source)
    {
        var builder = new StringBuilder();
        var entryPattern = new Regex(@"version:\s*""([^""]+)""\s*,\s*date:\s*""([^""]+)""\s*,\s*changes:\s*\[(.*?)\]", RegexOptions.Singleline);
        foreach (Match entry in entryPattern.Matches(source))
        {
            builder.AppendLine("v" + entry.Groups[1].Value + " (" + entry.Groups[2].Value + ")");
            foreach (Match change in Regex.Matches(entry.Groups[3].Value, "\"([^\"]+)\""))
            {
                builder.AppendLine("  - " + change.Groups[1].Value);
            }
            builder.AppendLine();
        }
        return builder.Length == 0 ? "没有解析到更新日志" : builder.ToString().TrimEnd();
    }

    private async Task RefreshAsync()
    {
        TrySyncApiBaseFromConfig();
        try
        {
            var status = await _api.StatusAsync();
            var lines = new List<string>
            {
                "运行状态：" + (status["running"]?.GetValue<bool>() == true ? "运行中" : "未运行"),
                "1.8.9 监听：" + (status["listen"]?["v189"]?.GetValue<string>() ?? "-"),
                "1.12.2 监听：" + (status["listen"]?["v1122"]?.GetValue<string>() ?? "-"),
                "上游服务器：" + (status["upstream"]?.GetValue<string>() ?? "-"),
                "在线玩家：" + (status["playerCount"]?.GetValue<int>().ToString() ?? "0"),
            };
            _status.Text = string.Join("\n", lines);
            _hint.Text = "安装目录：" + _runtime.InstallDirectory;
            await LoadSettingsAsync();
        }
        catch (Exception ex)
        {
            _status.Text = "接口未连接（" + _api.BaseUrl + "）\n" + ex.Message;
            _hint.Text = "代理未运行时部分配置不可读。";
        }
    }

    private async Task LoadSettingsAsync()
    {
        _settings.Children.Clear();
        JsonNode? response;
        try
        {
            response = await _api.ConfigAsync();
        }
        catch
        {
            return;
        }

        if (response?["settings"] is not JsonArray items) return;

        foreach (var item in items)
        {
            if (item is not JsonObject setting) continue;
            var key = setting["key"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key)) continue;
            var type = setting["type"]?.GetValue<string>() ?? "string";
            var value = setting["value"];

            var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 3, 0, 3) };

            var label = new TextBlock
            {
                Text = key,
                Width = 280,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            DockPanel.SetDock(label, Dock.Left);
            row.Children.Add(label);

            FrameworkElement editor;
            Func<string> readValue;
            if (type == "boolean")
            {
                var check = new CheckBox { IsChecked = value?.GetValue<bool>() == true, VerticalAlignment = VerticalAlignment.Center };
                editor = check;
                readValue = () => check.IsChecked == true ? "true" : "false";
            }
            else
            {
                var box = new TextBox
                {
                    Text = value?.ToString() ?? "",
                    Width = 260,
                    Margin = new Thickness(0, 0, 8, 0),
                };
                editor = box;
                readValue = () => box.Text;
            }

            var apply = _context.Ui.CreateButton("应用", async () =>
            {
                try
                {
                    var result = await _api.SetSettingAsync(key, readValue());
                    var updated = result["newValue"]?.ToString() ?? readValue();
                    _context.Ui.ShowNotification($"{key} = {updated}", PluginNotificationKind.Success);
                }
                catch (ImcProxyApiException ex)
                {
                    _context.Ui.ShowNotification($"{key} 未修改：{ex.Message}", PluginNotificationKind.Warning);
                }
                catch (Exception ex)
                {
                    _context.Ui.ShowNotification($"{key} 未修改：{ex.Message}", PluginNotificationKind.Error);
                }
            });

            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(editor);
            right.Children.Add(apply);
            DockPanel.SetDock(right, Dock.Right);
            row.Children.Add(right);

            _settings.Children.Add(row);
        }

        if (_settings.Children.Count == 0)
        {
            _settings.Children.Add(new TextBlock { Text = "没有读取到配置项，请先启动代理。", Opacity = 0.7 });
        }
    }
}

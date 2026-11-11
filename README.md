# IMCProxy · PCL Lite 插件

在 PCL Lite 里管理 IMCProxy-Free：下载完整版、静默启动代理、修改配置。代理在后台运行，不显示控制台窗口；插件页面只保留状态、操作按钮与配置项。

## 目录

```
imcp-pcl-plugin/
├── PCL.Plugin.ImcProxy.csproj
├── Plugin.cs
├── ControlPage.cs
├── ImcProxyApi.cs
├── ImcProxyRuntime.cs
├── plugin.json
└── dist/
    └── imcproxy.control/
        ├── plugin.json
        └── PCL.Plugin.ImcProxy.dll
```

## 安装

1. 关闭 PCL Lite
2. 把 `dist/imcproxy.control` 整个文件夹复制到 PCL Lite 的插件目录：

```
<PCL Lite 数据目录>/Plugins/imcproxy.control/
```

3. 启动 PCL Lite，插件列表里启用「IMCProxy」，页面里会出现同名入口

## 使用

页面包含三块：

- 操作按钮：下载 / 更新完整版、安装依赖并构建、启动代理、停止代理、刷新状态
- Node 路径：默认用 PATH 里的 `node.exe`，可以填绝对路径后保存
- 配置：读取代理的 HTTP API，逐项显示并按类型提供输入框或开关，点「应用」写入

首次使用顺序：

1. 点「下载 / 更新完整版」，从 GitHub 拉取 IMCProxy-Free 完整仓库并解压到插件数据目录
2. 点「安装依赖并构建」，后台执行 `npm install` 与 `npm run build`
3. 点「启动代理」，代理以隐藏窗口方式运行
4. 修改配置项，点「应用」即时生效或按提示重启

代理数据目录：

```
<PCL Lite 数据目录>/Plugins/.plugin-data/imcproxy.control/imcproxy-free/
```

## 依赖

- PCL Lite（插件 API 版本 1）
- Node.js 18 或更高版本
- 首次构建需要网络（npm 依赖）

## 控制方式

插件全部通过 IMCProxy 的 HTTP API 操作，不显示、也不解析控制台输出：

| 功能 | 接口 |
| --- | --- |
| 状态 | `GET /api/status` |
| 配置列表 | `GET /api/config` |
| 修改配置 | `POST /api/config/<key>` |
| 启动 / 停止 | `POST /api/start`、`POST /api/stop` |
| 日志 | `GET /api/logs` |

API 地址默认 `http://127.0.0.1:8790/api`，插件会从被管理目录的 `config.json` 读取 `api.host`、`api.port`、`api.token` 并自动同步。

## 构建

```powershell
dotnet build .\PCL.Plugin.ImcProxy.csproj -c Release -p:PclLiteDir=<pcl-lite 源码目录>\
```

`PclLiteDir` 指向包含 `PCL.Plugin.SDK` 的 pcl-lite 源码根目录。构建产物在 `bin/Release/net10.0-windows/`，把 `PCL.Plugin.ImcProxy.dll` 与 `plugin.json` 一起放进插件目录即可。

不要连同 `PCL.Plugin.SDK.dll` 一起分发，宿主已经提供该程序集。

## 说明

- 插件只管理 IMCProxy-Free；完整版（IMCProxy）不在此仓库分发
- 代理进程由插件启动和结束，PCL Lite 退出时插件会停止代理
- 配置写入失败（例如端口超出范围）会在 PCL Lite 通知里给出原因，`config.json` 不会被改坏

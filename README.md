# DSH Desktop Webview

像桌面应用一样，沉浸式使用[Deepseek Harness](https://github.com/deepseek-ai/deepseek-harness)，保持与浏览器一致的体验。

<img width="1280" height="675" alt="92835d747ea2e55a077e70a8d4c4aa6f" src="https://github.com/user-attachments/assets/cab2a44b-a877-43e2-a459-e96048e037f3" />

## 这软件是什么

基于 WPF (.NET 8) + Microsoft WebView2 , 把本地 Web 服务包装成桌面应用。相当于给你的 dsh 换了个无边框浏览器。自带本地服务生命周期管理，双击即用。跑的还是 `dsh web`，只是把页面放进了 WebView2 而不是你日常用的浏览器。部分 dsh 插件的数据无法同步——这些插件用浏览器的 `localStorage` / `IndexedDB` 存储配置。

- **不会影响你的dsh**：只做两件事，运行`dsh web --no-open`，打开url
- **不是浏览器**：无地址栏、无标签页、无书签历史
- **不是 dsh 专属工具**：dsh 只是默认配置值，理论上适配任何「本地命令 + URL」的服务

## 功能特性

| 特性     | 说明                                                                                    |
| -------- | --------------------------------------------------------------------------------------- |
| 原生窗口 | 保留原生拖动 / 缩放 / 最大化 / Aero 吸附与 DWM 动画；无标题栏，右上角浮层式窗口控制按钮 |
| 服务托管 | 三种策略：永不启动 / 先探测再启动（默认）/ 直接启动                              |
| URL 抓取 | 按正则逐行匹配启动命令 stdout/stderr                                                   |
| 临时 URL | 服务要求带 token 访问时弹出设置窗口；输入的临时 URL 仅用于本次访问                      |
| 外观跟随 | 浅色 / 深色主题启动时跟随系统；窗口控制按钮底色可固定，图标色按底色对比度自动推导       |
| 进程清理 | Job Object 管理整棵进程树，关窗即原子清场，无残留 node / cmd 进程                       |
| 便携软件 | 所有文件全部在 exe 目录，不会往外部写入任何数据                                         |

## 环境要求

- Windows 10 1809（build 17763）或更高
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) —— 默认版需要；`with-net`版自带运行时
- Microsoft Edge WebView2 Runtime（Windows 11 自带；Windows 10 大多已随更新安装）
- [Deepseek Harness](https://github.com/deepseek-ai/deepseek-harness)（本软件没有内置dsh）

## 快速开始

1. 下载并解压软件（解压后得到 `DSH Desktop Webview` 文件夹）
2. 双击文件夹里的 `DSH-Desktop-Webview.exe`

> 本软件没有代码签名，首次运行会被 SmartScreen 拦截。点「更多信息」→「仍要运行」即可。

- 页面顶部有一条透明拖动区域（默认不可见但仍可拖动；会影响交互；右键菜单「拖动层」可开关）
- 右上角最小化 / 最大化还原 / 关闭；默认常显，可改为悬停一段时间后浮现
- 设置入口在页面右键菜单「设置」

## 下载与更新

### 下载

见 Releases 页面。

可以在 Actions 页面直接下载 CI 构建产物。

| 压缩包 | 大小 | 需要 .NET 运行时？ | 说明 |
| --- | --- | --- | --- |
| `DSH-Desktop-Webview-<sha>.zip` | 约 7 MB | 需要 | 默认版。必须安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0) |
| `DSH-Desktop-Webview-with-net-<sha>.zip` | 约 75 MB | 不需要 | 自包含版，运行时已打进包内，下载即用 |

两个包解压后都是一个名为 `DSH Desktop Webview` 的文件夹，双击其中的 `DSH-Desktop-Webview.exe` 即可运行。

### 更新

> **建议备份** 。操作正确不会影响数据，但误操作可能导致配置丢失。更新前把整个文件夹复制一份，至少复制 `config.json` 和 `WebView2Profile` 。

压缩包内没有 `config.json`（设置文件）和 `WebView2Profile`（Cookie / 登录态 / 缓存）等，直接解压覆盖不会影响设置和浏览器数据。

## 从源码构建

需要 .NET 8 SDK。

```powershell
dotnet build DSH-Desktop-Webview.sln -c Debug    # 0 警告 0 错误（TreatWarningsAsErrors）
dotnet test  DSH-Desktop-Webview.sln -c Debug    # 全量单元测试
```

构建便携版（产物在 `artifacts/portable/`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1                 # 默认版（依赖net）
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SelfContained  # 自带net运行时
```

`publish.ps1` 会**清空** `artifacts/portable`。

希望保留运行时数据改用 `publish-keep-data.ps1`。只构建默认版，与 `publish.ps1` 的区别是会将`config.json` / `run.log` / `WebView2Profile` / `temp`备份到`artifacts/.publish-keep` 暂存，构建完成后恢复。

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-keep-data.ps1
```
其他参数和注意事项：

- `-NoPause`：运行结束不 pause，便于自动化调用
- 应用运行中会锁住 exe 导致发布失败，发布前先退出程序
- `publish-keep-data.ps1` 检测到程序正在运行会直接退出（必须如此：否则搬数据中途失败会把数据撕在两处）；若上次残留的暂存目录非空，它会停止并提示手动处理，绝不静默覆盖
- `tools/` 下两个离屏渲染预览工程（picker-preview / settings-preview）是 UI 开发辅助，不在解决方案内
- DSH 工作区不要包含程序目录，详见下方专节

## 故障排查

`run.log` 按标签分类：`[paths]` 数据目录 / `[config]` 配置加载 / `[theme]` 主题 / `[shell]` 窗口与宿主 / `[chrome]` `[strip]` 外观生效值 / `[session]` `[probe]` `[launch]` 会话 / `[nav]` `[nav-fail]` 导航 / `[init-fail]` `[fatal]` 失败。

| 现象 | 处理 |
| --- | --- |
| 报「启动命令异常退出（退出码 N）」 | 检查启动命令 |
| 约 30 秒后报「启动超时」 | 对照控制台实际输出检查 `urlExtractRegex` / `successMarkerRegex` |
| `dsh` 起不来 | 曾强杀外壳会留下 `~/.dsh/.credentials.yaml.lock` 死锁：确认无 `dsh` 进程后删除该文件 |
| 日志反复出现 `GpuProcessExited` | 多为虚拟显示适配器（远程 / 投屏驱动）环境问题，非程序缺陷，功能不受影响 |
| 页面加载不出、日志 `[init-fail]` | 检查 WebView2 Runtime 是否安装 |
| 日志 `[host-fallback]` | 合成宿主不可用已自动降级到 hwnd；也可以在设置中开启（在config.json中`"useHwndHost": true`）。 |
| 首次运行弹「Windows 已保护你的电脑」 | 未签名的正常现象：点「更多信息」→「仍要运行」。不影响功能 |
| 更新后设置没了 | 解压时清空了目标目录。从备份恢复 `config.json` 与 `WebView2Profile` 即可 |
| dsh 内部终端一直错误 | 见下方「DSH 工作区不要包含程序目录」一节 |

### DSH 工作区不要包含程序目录

日常使用不会遇到。正常情况下你的工作区与程序目录无关，可以忽略本节。

处理方式：在外部终端运行 `dsh web` 或移动工作区/程序目录

原因：程序会把 `TMP` / `TEMP` 定向到自身的 `temp\` 目录，再注入给它拉起的服务进程（默认 `dsh web --no-open`）使临时文件不污染系统 `%TEMP%`。

而 dsh 自己的沙箱在启用 `workspace-write` 时有一条硬性校验：临时目录不能位于工作区内。于是当同时满足：

- 由本软件拉起 dsh（临时目录被指向程序目录内的 `temp\`）
- 且程序目录位于工作区之内（如工作区 `L:\dsh-desktop-webview`，程序在 `L:\dsh-desktop-webview\artifacts\portable\`）

两条要求就会冲突，表现为沙箱拒绝启动：

```
Windows ACL temp root must be outside the workspace:
workspace=<你的工作区>; temp=<程序目录>\temp
```
# DSH Desktop Webview

像桌面应用一样，沉浸式使用[Deepseek Harness](https://github.com/deepseek-ai/deepseek-harness)，保持与浏览器一致的体验。

<img width="1280" height="675" alt="92835d747ea2e55a077e70a8d4c4aa6f" src="https://github.com/user-attachments/assets/cab2a44b-a877-43e2-a459-e96048e037f3" />

基于 **WPF (.NET 8) + Microsoft WebView2** , 把本地 Web 服务包装成桌面应用。无边框、自带本地服务生命周期管理，双击即用。

- **不会影响你的dsh**：只做两件事，运行`dsh web --no-open`，打开url
- **不是浏览器**：无地址栏、无标签页、无书签历史
- **不是 dsh 专属工具**：dsh 只是默认配置值，理论上适配任何「本地命令 + URL」的服务


## 功能特性

| 特性 | 说明 |
|---|---|
| 原生窗口 | 保留原生拖动 / 缩放 / 最大化 / Aero 吸附与 DWM 动画；无标题栏，右上角浮层式窗口控制按钮 |
| 服务托管 | 三种策略：永不启动/ 先探测再启动（默认）/ 直接启动（S3） |
| URL 抓取 | 按正则逐行匹配启动命令stdout/stderr  |
| 临时 URL | 服务要求带 token 访问时弹出设置窗口；输入的临时 URL 仅用于本次访问 |
| 外观跟随 | 浅色 / 深色主题启动时跟随系统；窗口控制按钮底色可固定，图标色按底色对比度自动推导 |
| 进程清理 | Job Object 管理整棵进程树，关窗即原子清场，无残留 node / cmd 进程 |
| 便携软件 | 所有文件全部在 exe 目录，不会往外部写入任何数据 |

## 环境要求

- Windows 10 1809（build 17763）或更高
- [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)（可能也会提供自带.NET 8的版本）
- Microsoft Edge WebView2 Runtime（Windows 11 自带；Windows 10 大多已随更新安装）
- `dsh`（仅在使用默认启动命令 `dsh web --no-open` 时需要）

## 快速开始

1. 下载并解压软件
2. 双击 `DSH-Desktop-Webview.exe`

### 主窗口

- **拖动**：页面顶部有一条透明拖动区（默认完全不可见但仍可拖动，会阻挡页面交互；右键菜单「拖动层」可开关）
- **窗口控制按钮**：右上角最小化 / 最大化还原 / 关闭；默认常显，可改为悬停一段时间后浮现
- **设置入口**：页面右键菜单「设置」（追加在 WebView2 右键菜单中）

## 从源码构建

需要 .NET 8 SDK。

```powershell
dotnet build DSH-Desktop-Webview.sln -c Debug    # 0 警告 0 错误（TreatWarningsAsErrors）
dotnet test  DSH-Desktop-Webview.sln -c Debug    # 全量单元测试
```

构建便携版（产物在 `artifacts/portable/`）：

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1                 # 依赖框架，约 26 MB
powershell -ExecutionPolicy Bypass -File .\publish.ps1 -SelfContained  # 自包含，约 180 MB
```

- `-NoPause`：运行结束不pause，便于自动化调用
- 运行会**清空** `artifacts/portable`；应用运行中会锁住 exe 导致发布失败——发布前先退出程序
- `tools/` 下两个离屏渲染预览工程（picker-preview / settings-preview）是 UI 开发辅助，不在解决方案内

## 故障排查

`run.log` 按标签分类：`[paths]` 数据目录 / `[config]` 配置加载 / `[theme]` 主题 / `[shell]` 窗口与宿主 / `[chrome]` `[strip]` 外观生效值 / `[session]` `[probe]` `[launch]` 会话 / `[nav]` `[nav-fail]` 导航 / `[init-fail]` `[fatal]` 失败。

| 现象 | 处理 |
|---|---|
| 报「启动命令异常退出（退出码 N）」 | 启动命令写错（命令名 / 参数不认），在设置窗口修正 |
| 约 30 秒后报「启动超时」 | 服务起得慢或没输出可识别 URL；对照控制台实际输出检查 `urlExtractRegex` / `successMarkerRegex` |
| `dsh` 起不来 | 曾强杀外壳会留下 `~/.dsh/.credentials.yaml.lock` 死锁：确认无 `dsh` 进程后删除该文件 |
| 日志反复出现 `GpuProcessExited` | 多为虚拟显示适配器（远程 / 投屏驱动）环境问题，非程序缺陷，功能不受影响 |
| 窗口尺寸或位置异常 | 低于 500×500 或整体在工作区外会自动恢复 1500×750 居中（日志 `[shell] 窗口尺寸异常`） |
| 手改 config.json 后行为怪异 | 结构非法会整体回退默认（日志 `[config] 加载失败`）；数值异常另有护栏兜底 |
| 页面加载不出、日志 `[init-fail]` | 检查 WebView2 Runtime 是否安装（Evergreen Standalone 可离线安装） |
| 日志 `[host-fallback]` | 合成宿主不可用已自动降级到 hwnd；也可主动设环境变量`DSH_WEBVIEW_HOST=hwnd` |

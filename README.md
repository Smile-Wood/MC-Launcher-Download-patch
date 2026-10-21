# MC Launcher Download patch

> 本仓库是为了能快速应对近期的MC下载禁止事件而作者Vibecoding出来的，由于HMCL暂时无法验证能否使用，现仅能确保PCL2和PCLCE能够正常修补。

一个对 PCL2（原版）/ PCL2-CE / HMCL 启动器进行运行时修补的统一注入器，用于在启动阶段自动为启动器解除近期事件引入的下载限制。提供 CLI 和 GUI（系统托盘常驻）两种形态。

## 支持的启动器

| 启动器 | 修补方式 | 状态 |
|--------|----------|------|
| PCL2-CE (Plain Craft Launcher 2 Community Edition) | `DOTNET_STARTUP_HOOKS` 环境变量 + Harmony 运行时 Prefix 补丁 | ✅ 已验证可用 |
| PCL2 原版 (Plain Craft Launcher 2) | dnlib 生成补丁版临时 exe + `RunAsInvoker` 启动 | ✅ 已验证可用 |
| HMCL (Hello Minecraft! Launcher) | Java Agent + jcmd 运行时 attach | ⚠️ 未能验证 |

## 功能特性

- **三种启动器统一处理**：拖入 exe 自动识别类型（PCL-CE / PCL 原版 / HMCL），选择对应的修补策略
- **启动前旧实例清理**：注入前自动检测并结束已在运行的同一启动器（优雅关闭优先，超时强杀），避免单实例冲突导致"启动后立即退出"
- **托盘常驻 GUI**（WPF + WinForms NotifyIcon）：
  - 单选启动器列表，选中状态持久化
  - 可选"启动时自动注入"+"静默模式"：完全不显示窗口，直接托盘后台注入
  - 关闭窗口 / 最小化自动缩到托盘，后台持续探测运行中的启动器并自动保存路径
  - 注入的启动器全部关闭后，自动清理临时文件并退出自身进程
- **自动降级**：被管理员 shim（`RUNASADMIN`）拦截时自动注入 `__COMPAT_LAYER=RunAsInvoker` 绕过；PCL 原版额外提供 ShellExecute 回退
- **无痕清理**：所有临时文件（补丁 exe、Payload、Harmony 变体）在启动器退出后自动删除

## 使用方法

### CLI

```text
PCL-Injector.exe                 # 监控模式：正常双击启动你的启动器，工具自动接管
PCL-Injector.exe <启动器路径>     # 手动指定：立即打补丁并启动
```

### GUI

```text
PCL-Injector-Gui.exe
```

1. 添加启动器 exe（或点"探测运行中的启动器"自动保存路径）
2. 单选一个启动器，按需勾选"自动注入"与"静默模式"
3. 点"开始注入"，或让托盘在后台自动完成

日志位于 `%LOCALAPPDATA%\PCL-Injector\monitor.log`（注入器侧）与 `inject.log`（补丁侧）。

## 从源码构建

需要 .NET 10 SDK（CLI/GUI/Payload/Core）与 JDK 8+（仅重新打包 hmcl-agent.jar 时需要）。

```powershell
# 一键发布 CLI 单文件 (dist\PCL-Injector.exe)
powershell -ExecutionPolicy Bypass -File publish.ps1

# 发布 GUI 单文件
dotnet publish PCL.Injector.Gui\PCL.Injector.Gui.csproj -c Release -r win-x64 --self-contained `
    /p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true -o dist-gui
```

## 目录结构

```text
├── PCL.Injector.Core/     共享核心：注入引擎、启动器识别(dnlib+字节扫描)、持久化
├── PCL.Injector.Payload/  .NET 启动钩子：Harmony 补丁本体 (注入 PCL-CE)
├── PCL.Injector.Cli/      命令行界面
├── PCL.Injector.Gui/      WPF 图形界面 (托盘常驻)
├── hmcl-agent/            Java Agent 源码 (HMCL 运行时 attach)
├── reference/             分析用补丁脚本
└── publish.ps1            CLI 一键发布脚本
```

## 许可证

本项目以 [PolyForm Noncommercial License 1.0.0](./LICENSE) 发布 —— **仅供个人学习、研究与非商业用途使用，严禁任何形式的商用**（包括但不限于出售、打包进收费服务、用于商业宣传或闭源商业分发）。转载与修改需保留本许可证与出处。

---

## 免责声明

1. **本仓库及其所有发布产物均不包含任何 Minecraft 启动器的源代码或构建版本。** PCL2、PCL2-CE、HMCL 等启动器的著作权归其各自原作者所有；本仓库仅包含作者自行编写的、在运行时对用户本机已合法获取的启动器进行修补的第三方注入器代码。
2. 本软件不会下载、存储、重分发任何启动器本体或 Minecraft 游戏文件；使用前请自行从官方/原作者渠道获取启动器。
3. 本软件按"现状"提供，不附带任何担保。使用本软件产生的一切后果由使用者自行承担，作者不对任何直接或间接损失负责。
4. 请在遵守当地法律法规及启动器原作者许可协议的前提下使用本软件；若原作者要求停止分发，本仓库将配合处理。

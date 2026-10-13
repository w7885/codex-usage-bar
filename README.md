# Codex Usage Bar · Codex 剩余用量栏

一个 Windows 桌面辅助工具：当 Codex 位于前台时，在窗口顶部显示 **5 小时与一周的剩余用量**，省去打开用户菜单的操作。

这是独立的 WPF 悬浮窗口，采用圆角卡片、百分比和进度条；不是官方 Codex 插件，也不是嵌入原生顶栏的组件。本项目与 OpenAI 无隶属关系。

![用量栏界面](docs/images/usage-bar.png)

图片是工具自身的界面渲染，百分比为生成时的快照。

## 功能

- 仅跟随当前前台 Codex 窗口；切换软件、最小化时隐藏。
- 拖动调整并保存位置；双击或通过托盘恢复默认位置。
- 默认每 60 秒在 Codex 前台时刷新，恢复到前台时刷新；快速切换有 5 秒合并限制。
- 悬停或查看托盘详情，获取恢复时间和最后成功更新时间。
- 读取失败保留原数值并标记失败；账号切换时清空旧数据。
- 托盘管理立即刷新、自启动和退出；移除入口清理自身进程与自启动。

## 运行要求

- Windows 10/11 x64、.NET Framework 4.8。
- 已安装并登录 Windows Store/MSIX 版 Codex；当前窗口识别针对该安装方式。
- 使用能返回订阅额度的 ChatGPT 登录；API key 登录不提供这两项额度。
- 可以访问 Codex 用量服务。

## 从源码开始

仓库只包含源码与文档，`dist/` 由构建生成。无需 .NET SDK 或 NuGet 依赖。

下载或克隆仓库后，在该目录的 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\build.ps1
```

也可以双击 **构建.cmd**。`-ExecutionPolicy Bypass` 仅用于该 PowerShell 进程，不修改系统策略。

| 入口 | 用途 |
| --- | --- |
| 启动用量栏.cmd | 启动程序 |
| 安装并启动.cmd | 启动并启用当前用户登录后自启动 |
| 关闭并移除自启动.cmd | 退出并移除自启动，保留源码和位置设置 |

这些入口无需管理员权限。程序位于 `dist/CodexUsageBar.exe`；移动目录后重新运行安装入口以更新路径。程序运行时先退出再重新构建，或使用 `tools/build.ps1 -Staging` 生成备用构建。

## 测试

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\run-tests.ps1
```

自动测试使用本地假 app-server，不需要真实账号，不发送模型推理请求。覆盖解析、超时、EOF、账号切换、通知、子进程清理、设置与布局。输出在本机 `validation/test-output.txt`。

GitHub Actions 已配置 Windows 构建与测试，并设置 10 分钟超时；推送前尚不能宣称远程 CI 已通过。真实跨显示器、多窗口和重登录等操作见 [验证边界](docs/verification.md)。

## 数据与隐私

工具启动自己的 Codex `app-server`，通过 stdio 的 `account/read` 和 `account/rateLimits/read` 获取数据并接收更新通知。Codex 管理凭证；工具只检查凭证文件元数据变化，不解析凭证正文，也不创建推理轮次。

只选择 `codex` 用量桶，按 300／10080 分钟识别窗口；剩余百分比为 `clamp(100 - usedPercent, 0, 100)`。恢复时间到达后仍需重新读取，不自行假定剩余 100%。

`state/` 包含本机位置和过滤后的诊断，可能有匿名账号指纹、用量、进程 ID 和窗口坐标。该目录、`validation/`、编译产物及 `.local/` 实验档案均被 Git 忽略。反馈问题时只附脱敏信息。

第一版面向单个个人账号。切换桌面账号后请对照菜单核实；Codex 的凭证存储或安装布局发生变化时可能需要适配。

## 目录

```text
src/                 C# 源码、应用配置与 DPI manifest
tests/               解析、布局、设置与假服务集成测试
tools/               构建、测试和手动诊断脚本
docs/                架构、验证边界、发布说明与界面示例
.github/workflows/   Windows CI
README.md            使用说明
CONTRIBUTING.md      开发和问题反馈说明
LICENSE              MIT 许可
*.cmd                Windows 双击入口
```

本机生成的 `dist/`、`state/`、`validation/` 和 `.local/` 不属于提交内容。

## 文档

- [架构与数据流](docs/architecture.md)
- [验证记录与边界](docs/verification.md)
- [推送与发布步骤](docs/releasing.md)
- [参与开发](CONTRIBUTING.md)
- [OpenAI App Server 接口](https://learn.chatgpt.com/docs/app-server)

## 许可证

[MIT](LICENSE)。版权署名使用 `Codex Usage Bar contributors`；维护者可在发布前按实际署名调整。
# 架构与数据流

界面使用 WPF、托盘使用 Windows Forms。程序通过 .NET Framework 4.8 的 C# 编译器构建，依赖系统组件与用户安装的 Codex。

## 数据

`QuotaClient` 定位运行时并启动私有 stdio app-server，初始化后只读账号与额度，不创建对话或推理轮次。

`QuotaModels` 优先选择多桶视图的 `codex`，兼容单桶视图；按窗口长度识别两项额度。空值保持未知，失败保留数值与原更新时间。

账号元数据变化或通知使旧缓存失效。单调发布序号与账号代数检查防止在途旧响应回灌。用量通知触发完整读取，避免把局部快照误标为全部最新。

## 窗口与生命周期

`NativeWindows` 按前台顶级窗口、进程路径和窗口类识别已验证的 MSIX Codex，返回物理坐标与 DPI；通过非激活样式和 `SetWindowPos` 跟随。

`OverlayWindow` 管理跟随、定时刷新、拖动、卡片与托盘；`OverlayPlacement` 把 DIP 偏移转换为物理像素并约束在目标窗口内。

`Program` 管理单实例、停止事件和诊断；`PlatformSupport` 保存位置、管理自身的用户级 Run 项。退出时关闭自己的 app-server。

`state/` 是本机状态，`dist/` 是构建产物，`validation/` 是诊断/测试输出，均不提交。自启动引用绝对 EXE 路径，移动目录后应重新运行安装入口。

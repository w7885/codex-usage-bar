# 推送与发布

建议仓库名：`codex-usage-bar`。本目录准备为本地源码仓库；GitHub 远程由维护者创建。

## 首次推送

确认 MIT 许可与署名，检查 `git status`、`git diff --cached`，确保本机数据和二进制不在暂存内容中。

```powershell
git diff --cached --check
git commit -m "Initial release of Codex usage bar"
```

在 GitHub 创建空仓库后，使用它提供的真实远程地址：

```powershell
git remote add origin <GitHub 提供的仓库地址>
git push -u origin main
```

地址占位不可原样执行。当前整理任务不创建远程、提交或推送。

## 发布二进制

先在干净源码副本运行 `tools/run-tests.ps1`，完成真实桌面验收。Release 包可包含正式 EXE、对应 `.config`、三个运行入口、README 和 LICENSE；不要打包测试 EXE、备份版或本机状态。

`dist/` 不提交到 Git 历史，可执行程序放 GitHub Releases。CI 有 10 分钟超时，不运行常驻 GUI、不安装自启动或读取真实账号；只有 GitHub 实际成功后才标记远程 CI 通过。

# 开发说明

## 环境

本项目使用 C#、WPF 和 .NET Framework，直接调用 Windows 上的 `csc.exe`。当前脚本查找 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 以及同目录的 WPF 程序集。

在 Windows PowerShell 5.1 或 PowerShell 7 中进入仓库根目录：

```powershell
.\build.ps1 -OutputDirectory .\dist\app
.\tests\run-tests.ps1
.\package.ps1
```

程序目录下的现有可执行文件运行时会被 Windows 锁定；开发时使用独立输出目录即可继续编译。项目不需要 NuGet 包，不支持在 WSL / Linux 中运行 WPF 界面。

## 代码职责

| 文件 | 职责 |
| --- | --- |
| `App.cs` | 应用入口、窗口事件、日期导航、任务编辑、日记、外观和托盘 |
| `MainWindow.xaml` | 主窗口布局、任务样式和周进度条 |
| `PlannerStore.cs` | 数据模型、校验、原子写入、备份、迁移和日记查询 |
| `tests/StoreTests.cs` | 存储和数据逻辑测试 |
| `tests/UiTests.cs` | 不显示窗口的 WPF 交互与布局测试 |

`PlannerController` 的测试模式会隔离托盘与自启动操作。测试数据在测试程序目录下临时生成，完成后清理，不读取实际使用的记录。

修改持久化格式时保留旧文件兼容和失败回滚；修改界面时检查最小尺寸、长列表、独立滚动和任务栏留白。源码保持与 .NET Framework 自带 C# 编译器兼容。

## 构建产物

`package.ps1` 使用白名单复制程序、自启动脚本和使用文档，再生成 ZIP 与 SHA-256 校验文件。它不遍历或打包用户数据目录。

GitHub 的 Windows 工作流使用相同的测试和打包脚本。首次上传后，应以实际 Actions 运行结果确认云端环境；本机通过不代表云端已经运行。

工作流中的操作及参数参考 [checkout](https://github.com/actions/checkout) 和 [upload-artifact](https://github.com/actions/upload-artifact) 的官方说明，使用已确认的 v7 标签对应提交，避免标签变动影响复现。

发布新版本时更新 `VERSION`、`CHANGELOG.md` 和 `RELEASE_NOTES.md`。提交消息带 `[release]` 并推送到 `main`，测试通过后会创建对应版本的 GitHub Release。也可在 Actions 中手动运行工作流并勾选 `publish_release`。同一版本已存在时会停止发布，应先更新版本号。

## 演示图

运行下列命令重新生成 README 中的图片：

```powershell
.\tools\render-previews.ps1
```

工具只生成虚构计划和小记，不读取 `data/plans.json`，也不截图用户桌面。输出为 `docs/images/widget.png` 和 `docs/images/journal.png`。

## 提交前

检查 `git status` 和暂存文件。`data/`、`output/`、`dist/`、测试临时目录、个人协作文件和可执行文件都应该被忽略。不要用 `git add -f` 绕过这些规则。

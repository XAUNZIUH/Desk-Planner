# DeskPlanner · 桌面计划

[![Windows build](https://github.com/XAUNZIUH/Desk-Planner/actions/workflows/windows.yml/badge.svg)](https://github.com/XAUNZIUH/Desk-Planner/actions/workflows/windows.yml)
[![Download](https://img.shields.io/badge/Windows-Download-355B8F)](https://github.com/XAUNZIUH/Desk-Planner/releases/latest)

一个轻量的 Windows 桌面小组件，把每周目标、每日安排和学习小记放在一起。数据保存在本机，可离线使用。

<img src="docs/images/widget.png" alt="桌面小组件演示：上方周计划，下方日计划" width="310">

## 功能

- **周计划 + 日计划**：上下同时显示，各自切换日期、添加、编辑和回看历史。
- **任务进度**：每个周任务可调整 0%–100% 的进度；每日任务可勾选完成和撤销。
- **每日小记**：一日一篇，支持正文搜索、月份筛选、分页浏览和完整导出。
- **桌面小组件**：隐藏任务栏按钮，保留系统托盘入口，可调整位置、大小、置顶和透明度。
- **外观定制**：修改标题、日期标签、背景文字，或选择本地背景图片。
- **本地保存**：自动保存计划，保留上一版备份；写入失败会提示并保留未保存内容。

<img src="docs/images/journal.png" alt="每日小记演示：左侧检索记录，右侧阅读和编辑正文" width="780">

以上图片使用虚构示例内容生成。

## 快速开始

需要 Windows 10 / 11、Windows PowerShell 5.1 或 PowerShell 7，以及 .NET Framework 4.x 的编译器和 WPF。无需 Python、Node.js、NuGet 或 Visual Studio。

直接使用可以前往 [版本下载](https://github.com/XAUNZIUH/Desk-Planner/releases/latest)，下载 Windows ZIP，解压后双击 `DeskPlanner.exe`。

从源码构建：下载或克隆项目后，在 **Windows PowerShell** 中进入项目目录，运行：

```powershell
.\build.ps1
.\DeskPlanner.exe
```

首次启动为空白计划，登录自启动默认关闭。之后可以在设置里启用。目录需要可写，建议放在个人文件夹中。

如果当前目录的小组件已经在运行，可以编译到另一个目录：

```powershell
.\build.ps1 -OutputDirectory .\dist\app
```

详细操作见 [使用说明](docs/USAGE.md)，迁移和备份见 [数据说明](docs/DATA.md)。

## 构建、测试和打包

在项目根目录运行：

```powershell
.\tests\run-tests.ps1
.\package.ps1
```

打包脚本在 `dist/` 生成一个 ZIP，只包含程序、使用说明和自启动辅助脚本。个人数据不会进入安装包。上传 GitHub 后，Windows 构建工作流会运行测试并生成可下载的构建产物。

当前测试覆盖 JSON 保存与恢复、旧记录兼容、周任务进度、跨天切换、独立日期导航、日记草稿保留、3000 篇记录的检索与分页，以及窗口尺寸和任务栏留白。

## 项目结构

```text
App.cs                 窗口交互、计划操作、日记和托盘入口
MainWindow.xaml        主窗口布局与样式
PlannerStore.cs        数据模型、本地存储和日记检索
app.manifest           Windows 应用清单
build.ps1              编译脚本
package.ps1            无个人数据的程序打包
install-startup.ps1    当前用户登录自启动辅助脚本
assets/                图标和书法资源
docs/                 使用、数据和开发说明
tests/                存储与隐藏 WPF 集成测试
tools/                虚构示例预览生成工具
.github/workflows/     Windows 自动构建与测试
```

开发约定见 [开发说明](docs/DEVELOPMENT.md)，图片资源来源见 [资源说明](assets/README.md)。

版本变化见 [更新记录](CHANGELOG.md)，参与修改见 [贡献说明](CONTRIBUTING.md)。

## 个人数据

实际使用时，程序会在旁边创建 `data/`，保存计划、日记、个人设置和自选背景。该目录以及临时预览、测试产物、可执行文件都已加入 `.gitignore`。本仓库只保存源码、静态资源、文档和虚构演示图。

程序没有联网同步功能。源码仓库用于管理程序版本；个人记录应单独备份。

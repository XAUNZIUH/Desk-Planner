# 参与开发

开发环境、构建与验证方法见 [开发说明](docs/DEVELOPMENT.md)。

提交修改时说明实际解决的问题、变化后的行为和验证结果。保持 Windows 桌面小组件轻量、离线可用，并兼容既有 JSON 记录。

界面问题可以附上虚构数据的演示图；请移除个人计划、日记和本地路径。报告保存问题时说明复现步骤和错误提示即可，无需公开真实记录。

提交前运行：

```powershell
.\tests\run-tests.ps1
.\build.ps1 -OutputDirectory .\dist\app
```

请确认 `git status` 不包含 `data/`、临时截图、生成的可执行文件或个人协作文件。

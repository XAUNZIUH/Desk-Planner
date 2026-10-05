[CmdletBinding()]
param([switch]$Disable)
$ErrorActionPreference = 'Stop'
$plannerExecutable = Join-Path $PSScriptRoot 'DeskPlanner.exe'
if (-not (Test-Path -LiteralPath $plannerExecutable -PathType Leaf)) { throw '请先运行 build.ps1 生成小程序。' }
$startupFolder = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startupFolder '每日计划.lnk'
if ($Disable) {
    if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath }
    Write-Output '已关闭每日计划的登录自启动。'
    return
}
$shortcutShell = New-Object -ComObject WScript.Shell
$plannerShortcut = $shortcutShell.CreateShortcut($shortcutPath)
$plannerShortcut.TargetPath = $plannerExecutable
$plannerShortcut.WorkingDirectory = $PSScriptRoot
$plannerShortcut.Description = '登录后在桌面右上角显示每日计划'
$plannerShortcut.IconLocation = $plannerExecutable + ',0'
$plannerShortcut.WindowStyle = 1
$plannerShortcut.Save()
Write-Output ('已启用登录自启动：' + $shortcutPath)

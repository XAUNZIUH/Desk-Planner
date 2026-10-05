[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $projectDir }
if (-not $env:WINDIR) { throw '请在 Windows 的 PowerShell 中构建。' }
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) { throw '未找到 .NET Framework C# 编译器，请先安装或启用 .NET Framework 4.x。' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path (Get-Location).Path $OutputDirectory }
$buildDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
$buildExecutable = Join-Path $buildDirectory 'DeskPlanner.exe'
$frameworkDir = Split-Path -Parent $compilerPath
$wpfDir = Join-Path $frameworkDir 'WPF'
$references = @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkDir $_) }
$references += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $wpfDir $_) }
$arguments = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', ('/out:' + $buildExecutable), ('/win32icon:' + (Join-Path $projectDir 'assets\planner.ico')), ('/win32manifest:' + (Join-Path $projectDir 'app.manifest')), ('/resource:' + (Join-Path $projectDir 'MainWindow.xaml') + ',DeskPlanner.MainWindow.xaml'))
$arguments += $references
$arguments += '/resource:' + (Join-Path $projectDir 'assets\weekly-self-renewal-v3-spaced.png') + ',DeskPlanner.WeeklyQuote.png'
$arguments += @(Join-Path $projectDir 'App.cs'; Join-Path $projectDir 'PlannerStore.cs')
& $compilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw '编译失败，请检查上面的错误。' }
Write-Output ('已生成：' + $buildExecutable)

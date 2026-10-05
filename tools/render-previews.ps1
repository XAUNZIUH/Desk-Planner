[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$previewProject = Split-Path -Parent $PSScriptRoot
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $previewProject 'docs\images' }
$previewCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$previewFramework = Split-Path -Parent $previewCompiler
$previewWpf = Join-Path $previewFramework 'WPF'
$previewWork = Join-Path $previewProject 'output\docs-preview'
[IO.Directory]::CreateDirectory($previewWork) | Out-Null
$previewExecutable = Join-Path $previewWork 'RenderPreview.exe'
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path (Get-Location).Path $OutputDirectory }
$previewArguments = @('/nologo', '/target:exe', '/codepage:65001', '/main:DeskPlanner.RenderPreview', ('/out:' + $previewExecutable),
    ('/resource:' + (Join-Path $previewProject 'MainWindow.xaml') + ',DeskPlanner.MainWindow.xaml'),
    ('/resource:' + (Join-Path $previewProject 'assets\weekly-self-renewal-v3-spaced.png') + ',DeskPlanner.WeeklyQuote.png'))
$previewArguments += @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $previewFramework $_) }
$previewArguments += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $previewWpf $_) }
$previewArguments += @(Join-Path $previewProject 'App.cs'; Join-Path $previewProject 'PlannerStore.cs'; Join-Path $PSScriptRoot 'RenderPreview.cs')
& $previewCompiler @previewArguments
if ($LASTEXITCODE -ne 0) { throw 'Documentation preview compilation failed.' }
& $previewExecutable ([IO.Path]::GetFullPath($OutputDirectory))
if ($LASTEXITCODE -ne 0) { throw 'Documentation preview rendering failed.' }

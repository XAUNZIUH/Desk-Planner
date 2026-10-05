[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
if ([String]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $PSScriptRoot 'dist' }
if (-not [IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path (Get-Location).Path $OutputDirectory }
$packageOutput = [IO.Path]::GetFullPath($OutputDirectory)
$packageVersion = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
if ($packageVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'VERSION 格式应为 x.y.z。' }
$packageName = 'DeskPlanner-v' + $packageVersion + '-Windows-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$bundleDirectory = Join-Path $packageOutput $packageName
[IO.Directory]::CreateDirectory($bundleDirectory) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $bundleDirectory 'docs')) | Out-Null

& (Join-Path $PSScriptRoot 'build.ps1') -OutputDirectory $bundleDirectory

# Copy only distributable files. Never include an existing data/ directory.
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install-startup.ps1') -Destination $bundleDirectory
foreach ($guide in @('USAGE.md', 'DATA.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('docs\' + $guide)) -Destination (Join-Path $bundleDirectory 'docs')
}
$startHere = "# DeskPlanner · 桌面计划`r`n`r`n解压后双击 DeskPlanner.exe。首次启动将创建空白的本地 data 目录。`r`n`r`n使用说明：docs/USAGE.md；备份与迁移：docs/DATA.md。`r`n"
[IO.File]::WriteAllText((Join-Path $bundleDirectory 'README.md'), $startHere, (New-Object Text.UTF8Encoding $false))

$packageArchive = Join-Path $packageOutput ($packageName + '.zip')
Compress-Archive -LiteralPath $bundleDirectory -DestinationPath $packageArchive
$packageHash = (Get-FileHash -LiteralPath $packageArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($packageArchive + '.sha256'), $packageHash + '  ' + [IO.Path]::GetFileName($packageArchive) + "`r`n", (New-Object Text.UTF8Encoding $false))
Write-Output ('程序包：' + $packageArchive)
Write-Output ('SHA-256：' + $packageHash)

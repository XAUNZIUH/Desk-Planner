$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$frameworkDir = Split-Path -Parent $compilerPath
$wpfDir = Join-Path $frameworkDir 'WPF'

$storeExecutable = Join-Path $PSScriptRoot 'StoreTests.exe'
$storeArguments = @('/nologo', '/target:exe', '/codepage:65001', ('/out:' + $storeExecutable))
$storeArguments += @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkDir $_) }
$storeArguments += @(Join-Path $projectDir 'PlannerStore.cs'; Join-Path $PSScriptRoot 'StoreTests.cs')
& $compilerPath @storeArguments
if ($LASTEXITCODE -ne 0) { throw 'Storage test compilation failed.' }
& $storeExecutable
if ($LASTEXITCODE -ne 0) { throw 'Storage tests failed.' }

$uiExecutable = Join-Path $PSScriptRoot 'UiTests.exe'
$uiArguments = @('/nologo', '/target:exe', '/codepage:65001', '/main:DeskPlanner.UiTests', ('/out:' + $uiExecutable), ('/resource:' + (Join-Path $projectDir 'MainWindow.xaml') + ',DeskPlanner.MainWindow.xaml'))
$uiArguments += @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll', 'System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $frameworkDir $_) }
$uiArguments += @('PresentationCore.dll', 'PresentationFramework.dll', 'WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path $wpfDir $_) }
$uiArguments += @(Join-Path $projectDir 'App.cs'; Join-Path $projectDir 'PlannerStore.cs'; Join-Path $PSScriptRoot 'UiTests.cs')
$uiArguments += '/resource:' + (Join-Path $projectDir 'assets\weekly-self-renewal-v3-spaced.png') + ',DeskPlanner.WeeklyQuote.png'
& $compilerPath @uiArguments
if ($LASTEXITCODE -ne 0) { throw 'WPF integration test compilation failed.' }
& $uiExecutable
if ($LASTEXITCODE -ne 0) { throw 'WPF integration tests failed.' }

Write-Output 'All storage and hidden WPF integration tests passed.'

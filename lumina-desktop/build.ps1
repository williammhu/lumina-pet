param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$workspace = Split-Path $project -Parent
$release = if (Test-Path (Join-Path $workspace 'assets/manifest.json')) { $workspace } else { Join-Path $workspace 'outputs/Lumina-Desktop' }
$assetDirectory = Join-Path $release 'assets'
if ($OutputDirectory) { $release = $OutputDirectory }
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$wpf = Join-Path $framework 'WPF'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$sources = Get-ChildItem (Join-Path $project 'src') -Filter '*.cs' | ForEach-Object FullName
$args = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',('/out:' + (Join-Path $release 'Lumina.exe')),('/win32manifest:' + (Join-Path $project 'app.manifest')),('/win32icon:' + (Join-Path $assetDirectory 'lumina.ico')),('/reference:' + (Join-Path $wpf 'PresentationFramework.dll')),('/reference:' + (Join-Path $wpf 'PresentationCore.dll')),('/reference:' + (Join-Path $wpf 'WindowsBase.dll')),'/reference:System.dll','/reference:System.Core.dll','/reference:System.Xaml.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll')
& $compiler @args @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Copy-Item -LiteralPath (Join-Path $project 'Lumina.exe.config') -Destination $release
Write-Output (Join-Path $release 'Lumina.exe')

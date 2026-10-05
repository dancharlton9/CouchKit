# Builds CouchKit with the C# compiler that ships with Windows (.NET Framework 4.8), so no SDK or
# Visual Studio is needed. Outputs, in bin\:
#   CouchKit.exe                 the background app
#   CouchKit-<version>.pext      the Playnite add-on (with CouchKit.exe inside), installed by double-clicking
# The add-on is compiled against Playnite.SDK.dll from your Playnite install; pass -PlayniteDir if
# Playnite isn't in the default %LOCALAPPDATA%\Playnite.
param([string]$PlayniteDir = (Join-Path $env:LOCALAPPDATA 'Playnite'))
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4 compiler not found at $csc" }
$version = (Get-Content VERSION -Raw).Trim()
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'

New-Item -ItemType Directory -Force bin, obj | Out-Null
Set-Content obj\Version.cs -Encoding UTF8 @"
[assembly: System.Reflection.AssemblyTitle("CouchKit")]
[assembly: System.Reflection.AssemblyProduct("CouchKit")]
[assembly: System.Reflection.AssemblyVersion("$version")]
[assembly: System.Reflection.AssemblyFileVersion("$version")]
"@

& $csc /nologo /target:winexe /platform:x64 /optimize /r:System.Drawing.dll /r:System.Web.Extensions.dll /out:bin\CouchKit.exe (Get-ChildItem src\*.cs).FullName obj\Version.cs
if ($LASTEXITCODE) { throw 'CouchKit.exe build failed' }
"Built bin\CouchKit.exe $version"

$sdk = Join-Path $PlayniteDir 'Playnite.SDK.dll'
if (-not (Test-Path $sdk)) { Write-Warning "Playnite.SDK.dll not found in $PlayniteDir; skipping the Playnite add-on"; return }
& $csc /nologo /target:library /optimize /r:$sdk /r:"$wpf\PresentationFramework.dll" /r:"$wpf\PresentationCore.dll" /r:"$wpf\WindowsBase.dll" /r:System.Xaml.dll `
    /out:obj\CouchKit.Playnite.dll playnite\CouchKitPlugin.cs obj\Version.cs
if ($LASTEXITCODE) { throw 'Playnite add-on build failed' }

$stage = 'obj\pext'
if (Test-Path $stage) { [IO.Directory]::Delete((Resolve-Path $stage).Path, $true) }
New-Item -ItemType Directory $stage | Out-Null
$yaml = (Get-Content playnite\extension.yaml) -replace '^Version: .*', "Version: $version"
[IO.File]::WriteAllLines((Join-Path $PSScriptRoot "$stage\extension.yaml"), $yaml, (New-Object Text.UTF8Encoding $false))   # no BOM
Copy-Item obj\CouchKit.Playnite.dll, playnite\icon.png, bin\CouchKit.exe, couchkit.example.json, LICENSE $stage
$pext = "bin\CouchKit-$version.pext"
if (Test-Path "$pext.zip") { [IO.File]::Delete((Resolve-Path "$pext.zip").Path) }
Compress-Archive "$stage\*" "$pext.zip"
Move-Item "$pext.zip" $pext -Force
"Built $pext"

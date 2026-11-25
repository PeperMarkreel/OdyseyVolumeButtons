param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sdk = dotnet --list-sdks | Select-Object -Last 1
if ($sdk -notmatch '^(\S+) \[(.+)\]$') { throw 'Install a .NET SDK first.' }
$compiler = Join-Path $Matches[2] (Join-Path $Matches[1] 'Roslyn\bincore\csc.dll')
$references = 'mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Web.Extensions' |
    ForEach-Object { '/reference:' + (Join-Path $framework "$_.dll") }
$output = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $root 'outputs' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$icon = Join-Path $PSScriptRoot 'OdysseyVolume.ico'
$headset = Join-Path $root 'assets\odyssey-plus.png'
$sources = 'OdysseyVolume.cs','SteamVrStartup.cs','SteamVrSession.cs','StartupSetup.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ }
dotnet exec $compiler /nologo /noconfig /nostdlib /target:winexe /optimize+ $references "/win32icon:$icon" "/resource:$headset,OdysseyHeadset.png" "/out:$output\OdysseyVolume.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Failed to build OdysseyVolume.exe.' }


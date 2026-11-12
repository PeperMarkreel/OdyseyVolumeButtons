$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$sdk = dotnet --list-sdks | Select-Object -Last 1
if ($sdk -notmatch '^(\S+) \[(.+)\]$') { throw 'Installeer eerst een .NET SDK.' }
$compiler = Join-Path $Matches[2] (Join-Path $Matches[1] 'Roslyn\bincore\csc.dll')
$references = 'mscorlib','System','System.Core','System.Drawing','System.Windows.Forms' |
    ForEach-Object { '/reference:' + (Join-Path $framework "$_.dll") }
$output = Join-Path $root 'outputs'
New-Item -ItemType Directory -Force -Path $output | Out-Null
dotnet exec $compiler /nologo /noconfig /nostdlib /target:winexe /optimize+ $references "/out:$output\OdysseyVolume.exe" (Join-Path $PSScriptRoot 'OdysseyVolume.cs')
if ($LASTEXITCODE -ne 0) { throw 'Het bouwen van OdysseyVolume.exe is mislukt.' }


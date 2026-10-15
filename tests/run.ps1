$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (!$msbuild) { throw 'MSBuild was not found.' }
& $msbuild (Join-Path $projectRoot 'KimNotes.csproj') /t:Build /p:Configuration=Release /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
$runRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('KimNotes-regression-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\KimNotes.exe') -Destination $runRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\Newtonsoft.Json.dll') -Destination $runRoot
$csc = Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
& $csc /nologo /target:exe "/out:$runRoot\Regression.exe" "/reference:$runRoot\KimNotes.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'Regression.cs') (Join-Path $PSScriptRoot 'ScreenshotRegression.cs') (Join-Path $PSScriptRoot 'UiRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Regression runner build failed.' }
& (Join-Path $runRoot 'Regression.exe') (Join-Path $runRoot 'data')
if ($LASTEXITCODE -ne 0) { throw "Regression failed. Artifacts retained at $runRoot" }
Write-Output "Regression artifacts retained at $runRoot"

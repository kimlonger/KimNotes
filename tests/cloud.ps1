param([switch]$UI,[ValidateSet('Cloud','History','Images','Edge','Startup','Policy')][string]$Suite='Cloud')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
& $msbuild (Join-Path $projectRoot 'KimNotes.csproj') /t:Build /p:Configuration=Release /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$runRoot = Join-Path $env:TEMP ('KimNotes-cloud-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\KimNotes.exe'),(Join-Path $projectRoot 'bin\Release\Newtonsoft.Json.dll') -Destination $runRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\KimNotes.exe.config') -Destination $runRoot
$csc = Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
$source=@{Cloud='CloudRegression.cs';History='HistoryRegression.cs';Images='CloudImageRegression.cs';Edge='CloudEdgeRegression.cs';Startup='StartupRegression.cs';Policy='SyncPolicyRegression.cs'}[$Suite]
& $csc /nologo /target:exe "/win32manifest:$projectRoot\app.manifest" "/out:$runRoot\CloudRegression.exe" "/reference:$runRoot\KimNotes.exe" "/reference:$runRoot\Newtonsoft.Json.dll" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll /reference:System.Web.dll /reference:System.Security.dll /reference:System.IO.Compression.dll (Join-Path $PSScriptRoot $source)
if ($LASTEXITCODE -ne 0) { throw 'Cloud runner build failed' }
Copy-Item -LiteralPath (Join-Path $runRoot 'KimNotes.exe.config') -Destination (Join-Path $runRoot 'CloudRegression.exe.config')
if ($UI) { & (Join-Path $runRoot 'CloudRegression.exe') (Join-Path $runRoot 'data') ui }
else { & (Join-Path $runRoot 'CloudRegression.exe') (Join-Path $runRoot 'data') }
if ($LASTEXITCODE -ne 0) { throw "Cloud tests failed: $runRoot" }

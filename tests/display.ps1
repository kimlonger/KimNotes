param([string]$BaselineZip)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild=& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$runRoot=Join-Path $env:TEMP ('KimNotes-display-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
if($BaselineZip){Expand-Archive -LiteralPath $BaselineZip -DestinationPath $runRoot}
else{
  & $msbuild (Join-Path $projectRoot 'KimNotes.csproj') /t:Build /p:Configuration=Release /v:minimal /nologo
  if($LASTEXITCODE -ne 0){throw 'Build failed'}
  Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\KimNotes.exe'),(Join-Path $projectRoot 'bin\Release\Newtonsoft.Json.dll'),(Join-Path $projectRoot 'bin\Release\KimNotes.exe.config') -Destination $runRoot
}
$csc=Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
& $csc /nologo /target:exe "/win32manifest:$projectRoot\app.manifest" "/out:$runRoot\DisplayRegression.exe" "/reference:$runRoot\KimNotes.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'DisplayRegression.cs')
if($LASTEXITCODE -ne 0){throw 'Display runner build failed'}
Copy-Item -LiteralPath (Join-Path $runRoot 'KimNotes.exe.config') -Destination (Join-Path $runRoot 'DisplayRegression.exe.config')
if($BaselineZip){& (Join-Path $runRoot 'DisplayRegression.exe') (Join-Path $runRoot 'data') pictures-only}
else{& (Join-Path $runRoot 'DisplayRegression.exe') (Join-Path $runRoot 'data')}
if($LASTEXITCODE -ne 0){throw "Display regression failed: $runRoot"}

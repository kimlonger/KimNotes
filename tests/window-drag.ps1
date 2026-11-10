param([string]$Preview)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
& $msbuild (Join-Path $projectRoot 'KimNotes.csproj') /t:Build /p:Configuration=Release /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$runRoot = Join-Path $env:TEMP ('KimNotes-window-drag-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\KimNotes.exe'),(Join-Path $projectRoot 'bin\Release\Newtonsoft.Json.dll') -Destination $runRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'bin\Release\KimNotes.exe.config') -Destination (Join-Path $runRoot 'WindowDragRunner.exe.config')
$csc = Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
& $csc /nologo /target:exe "/win32manifest:$projectRoot\app.manifest" "/out:$runRoot\WindowDragRunner.exe" "/reference:$runRoot\KimNotes.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'WindowDragRunner.cs') (Join-Path $PSScriptRoot 'WindowDragRegression.cs') (Join-Path $PSScriptRoot 'NoteMenuRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Runner build failed' }
Write-Output "Drag artifacts: $runRoot"
if ($Preview -eq 'Menu') {
    & (Join-Path $runRoot 'WindowDragRunner.exe') (Join-Path $runRoot 'data') Menu
    if ($LASTEXITCODE -ne 0) { throw "Menu tests failed: $runRoot" }
} elseif ($Preview) {
    Start-Process -FilePath (Join-Path $runRoot 'WindowDragRunner.exe') -ArgumentList @((Join-Path $runRoot 'data'), $Preview) -WindowStyle Hidden
} else {
    & (Join-Path $runRoot 'WindowDragRunner.exe') (Join-Path $runRoot 'data')
    if ($LASTEXITCODE -ne 0) { throw "Drag tests failed: $runRoot" }
}

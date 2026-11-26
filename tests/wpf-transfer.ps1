param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$candidate = Get-Item -LiteralPath $Executable
$work = Join-Path ([IO.Path]::GetTempPath()) ('KimNotes-transfer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
foreach ($name in @($candidate.Name, ($candidate.Name + '.config'), 'Newtonsoft.Json.dll', 'LiteNetLib.dll')) {
    Copy-Item -LiteralPath (Join-Path $candidate.DirectoryName $name) -Destination $work
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
# The desktop assembly grants internal access to this test assembly name.
$runner = Join-Path $work 'CloudRegression.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe "/out:$runner" "/reference:$($candidate.FullName)" `
    "/reference:$framework/WPF/PresentationFramework.dll" "/reference:$framework/WPF/PresentationCore.dll" `
    "/reference:$framework/WPF/WindowsBase.dll" /reference:System.Xaml.dll /reference:System.Net.Http.dll (Join-Path $PSScriptRoot 'TransferRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Transfer regression compilation failed.' }
Copy-Item -LiteralPath ($candidate.FullName + '.config') -Destination ($runner + '.config')
& $runner $work
if ($LASTEXITCODE -ne 0) { throw "Transfer regression failed. Artifacts: $work" }

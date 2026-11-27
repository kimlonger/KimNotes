param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$candidate = Get-Item -LiteralPath $Executable
$work = Join-Path ([IO.Path]::GetTempPath()) ('KimNotes-account-switch-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
foreach ($name in @($candidate.Name, ($candidate.Name + '.config'), 'Newtonsoft.Json.dll', 'LiteNetLib.dll')) {
    Copy-Item -LiteralPath (Join-Path $candidate.DirectoryName $name) -Destination $work
}
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$runner = Join-Path $work 'CloudRegression.exe'
& (Join-Path $framework 'csc.exe') /nologo /target:exe "/out:$runner" "/reference:$($candidate.FullName)" `
    "/reference:$work/Newtonsoft.Json.dll" /reference:System.Net.Http.dll /reference:System.Windows.Forms.dll `
    /reference:System.Xaml.dll "/reference:$framework/WPF/PresentationFramework.dll" `
    "/reference:$framework/WPF/PresentationCore.dll" "/reference:$framework/WPF/WindowsBase.dll" `
    (Join-Path $PSScriptRoot 'WpfAccountSwitchRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Account switch regression compilation failed.' }
Copy-Item -LiteralPath ($candidate.FullName + '.config') -Destination ($runner + '.config')
& $runner (Join-Path $work 'data')
if ($LASTEXITCODE -ne 0) { throw 'Account switch regression failed.' }

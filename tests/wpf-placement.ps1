param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with Windows PowerShell -STA.' }
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PlacementTestDpi {
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    public static uint Read(IntPtr hwnd) {
        try { return GetDpiForWindow(hwnd); }
        catch (EntryPointNotFoundException) { return 0; }
    }
}
'@
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$instance = [Reflection.BindingFlags]'Instance, Public, NonPublic'
$static = [Reflection.BindingFlags]'Static, Public, NonPublic'
$placement = $assembly.GetType('KimNotes.Windows.Notes.NotePlacement', $true)
$capture = $placement.GetMethod('Capture', $static)
$move = $placement.GetMethod('SetWindowPos', $static)
$profile = Join-Path ([IO.Path]::GetTempPath()) ('KimNotes-placement-' + [Guid]::NewGuid().ToString('N'))
$original = $env:KIMNOTES_DATA_DIR
$env:KIMNOTES_DATA_DIR = $profile
$failures = @()

function New-Component([string]$Name, [object[]]$Arguments = @()) {
    $type = $assembly.GetType($Name, $true)
    return $type.GetConstructors($instance).Where({ $_.GetParameters().Count -eq $Arguments.Count })[0].Invoke($Arguments)
}
function New-Note {
    $note = New-Component 'KimNotes.Windows.Notes.NoteWindow'
    $note.Opacity = 0
    $note.ShowInTaskbar = $false
    $note.ShowActivated = $false
    return $note
}
function Pump($Window) {
    $Window.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
}
function Check([bool]$Condition, [string]$Description) {
    if ($Condition) { Write-Host "PASS $Description" }
    else { Write-Host "FAIL $Description"; $script:failures += $Description }
}
try {
    $app = New-Component 'KimNotes.Windows.App'
    $app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
    $assembly.GetType('KimNotes.utils.InitConfig').GetMethod('InitSettings', $static).Invoke($null, @())
    foreach ($screen in [Windows.Forms.Screen]::AllScreens) {
        $note = New-Note
        $reopened = $null
        try {
            $note.Show()
            $hwnd = [Windows.Interop.WindowInteropHelper]::new($note).Handle
            $area = $screen.WorkingArea
            $null = $move.Invoke($null, @($hwnd, [IntPtr]::Zero, [int]($area.Left + 39), [int]($area.Top + 53), 523, 507, [uint32]0x14))
            Pump $note
            $normal = $capture.Invoke($null, @($note, 2))
            $nativeDpi = [PlacementTestDpi]::Read($hwnd)
            if ($nativeDpi) {
                Check ($normal.Dpi -eq $nativeDpi) "Saved DPI matches the native window on $($screen.DeviceName) ($nativeDpi DPI)."
            }
            $note.WindowState = [Windows.WindowState]::Minimized
            Pump $note
            $minimized = $capture.Invoke($null, @($note, 2))
            Check ($minimized.Bounds -eq $normal.Bounds -and $minimized.Dpi -eq $normal.Dpi -and $minimized.Monitor -eq $normal.Monitor) `
                "Minimizing keeps $($normal.Bounds) on $($screen.DeviceName) at $($normal.Dpi) DPI (saved $($minimized.Bounds))."
            $note.Close()
            $reopened = New-Note
            $placement.GetMethod('Restore', $static).Invoke($null, @($reopened, $minimized))
            $reopened.Show()
            Pump $reopened
            $opened = $capture.Invoke($null, @($reopened, 2))
            Check ($opened.Bounds -eq $normal.Bounds) "Reopening uses the same screen pixels and working area (opened $($opened.Bounds))."
        } finally {
            if ($note.IsVisible) { $note.Close() }
            if ($reopened -and $reopened.IsVisible) { $reopened.Close() }
        }
    }
    $workspace = New-Component 'KimNotes.Windows.Notes.NoteWorkspace'
    $catalog = New-Component 'KimNotes.Windows.Notes.NoteCatalogWindow' @(,$workspace)
    try {
        $catalog.Opacity = 0
        $catalog.ShowInTaskbar = $false
        $catalog.ShowActivated = $false
        $catalog.Show()
        $catalog.WindowState = [Windows.WindowState]::Minimized
        Pump $catalog
        $field = $workspace.GetType().GetField('catalog', $instance)
        $field.SetValue($workspace, $catalog)
        $workspace.GetType().GetMethod('ShowCatalog', $instance).Invoke($workspace, @())
        Pump $catalog
        Check ($catalog.WindowState -eq [Windows.WindowState]::Normal -and $field.GetValue($workspace) -eq $catalog) `
            'Opening the minimized catalog restores the same window.'
    } finally { $catalog.Close() }
} finally { $env:KIMNOTES_DATA_DIR = $original }
if ($failures.Count) { throw "$($failures.Count) placement checks failed." }

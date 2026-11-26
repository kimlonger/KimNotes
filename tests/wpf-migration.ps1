param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [string]$PreviewDirectory
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this check using Windows PowerShell with -Sta.'
}

$oldDataDirectory = [Environment]::GetEnvironmentVariable('KIMNOTES_DATA_DIR')
$testDirectory = Join-Path ([IO.Path]::GetTempPath()) ('KimNotes-wpf-migration-' + [Guid]::NewGuid().ToString('N'))
[Environment]::SetEnvironmentVariable('KIMNOTES_DATA_DIR', $testDirectory)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$instanceFlags = [Reflection.BindingFlags]'Instance, Public, NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static, Public, NonPublic'

function New-Component([string]$Name, [object[]]$Arguments = @()) {
    $type = $assembly.GetType($Name, $true)
    $ctor = $type.GetConstructors($instanceFlags) | Where-Object { $_.GetParameters().Count -eq $Arguments.Count } | Select-Object -First 1
    if ($null -eq $ctor) { throw "No constructor for $Name and $($Arguments.Count) arguments." }
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        if ($null -ne $Arguments[$index]) { $Arguments[$index] = $Arguments[$index].PSObject.BaseObject }
    }
    return $ctor.Invoke($Arguments)
}

function Invoke-Component($Target, [string]$Method, [object[]]$Arguments = @()) {
    for ($index = 0; $index -lt $Arguments.Count; $index++) {
        if ($null -ne $Arguments[$index]) { $Arguments[$index] = $Arguments[$index].PSObject.BaseObject }
    }
    if ($Target -is [Type]) { return $Target.GetMethod($Method, $staticFlags).Invoke($null, $Arguments) }
    return $Target.GetType().GetMethod($Method, $instanceFlags).Invoke($Target, $Arguments)
}

function Assert-Migration([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    Write-Output $Description
}

function Find-Visuals($Root, [Type]$Type) {
    if ($Type.IsInstanceOfType($Root)) { $Root }
    for ($index = 0; $index -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($Root); $index++) {
        Find-Visuals ([Windows.Media.VisualTreeHelper]::GetChild($Root, $index)) $Type
    }
}

function Render-Window([Windows.Window]$Window, [string]$Name) {
    Render-Surface $Window.Content ([Windows.Size]::new($Window.Width, $Window.Height)) $Name
}

function Render-Surface([Windows.FrameworkElement]$body, [Windows.Size]$size, [string]$Name) {
    [Windows.Media.VisualTreeHelper]::SetRootDpi($body, [Windows.DpiScale]::new(1, 1))
    $body.Measure($size)
    $body.Arrange([Windows.Rect]::new($size))
    $body.UpdateLayout()
    Assert-Migration ($body.RenderSize.Width -gt 0 -and $body.RenderSize.Height -gt 0) "$Name builds its WPF visual tree."
    if ($PreviewDirectory) {
        [IO.Directory]::CreateDirectory($PreviewDirectory) | Out-Null
        $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int]$size.Width, [int]$size.Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $bitmap.Render($body)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $PreviewDirectory ($Name + '.png')))
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
    }
}

$app = New-Component 'KimNotes.Windows.App'
$config = $assembly.GetType('KimNotes.utils.InitConfig', $true)
$todoStore = $assembly.GetType('KimNotes.utils.TodoStore', $true)
try {
    Invoke-Component $config 'InitSettings'
    $notes = New-Component 'KimNotes.Windows.Notes.NoteWorkspace'
    $directoryPath = Invoke-Component $config 'GetConfigValue' @('notesPath')
    [IO.Directory]::CreateDirectory($directoryPath) | Out-Null
    $old = Join-Path $directoryPath 'legacy.rtf'
    [IO.File]::WriteAllText($old, '{\rtf1\ansi{\fonttbl{\f0 Calibri;}}\pard\f0 \b Legacy text\b0\par}', [Text.Encoding]::ASCII)
    $legacy = New-Component 'KimNotes.Windows.Notes.NoteWindow'
    Invoke-Component $legacy 'Connect' @($notes, $old, $null)
    $legacyEditor = $legacy.FindName('Editor')
    Assert-Migration ([Windows.Documents.TextRange]::new($legacyEditor.Document.ContentStart, $legacyEditor.Document.ContentEnd).Text.Trim() -eq 'Legacy text') 'The live WPF note loads a legacy RTF from the current notes directory.'
    $legacyEditor.AppendText(' edited')
    Assert-Migration ($legacy.SaveCurrentNoteSafe() -and [IO.File]::Exists($old + '.bak') -and [IO.File]::Exists($old + '.meta.json')) 'Editing a legacy note uses atomic storage with backup and metadata.'

    $desktopSession = $assembly.GetType('KimNotes.utils.DesktopSession', $true)
    $noteDirectoryWithSeparator = $directoryPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    try {
        Invoke-Component $config 'SetConfigValue' @('notesPath', $noteDirectoryWithSeparator)
        Invoke-Component $desktopSession 'RememberOpenPaths' (,[string[]]@($old))
        $reopenedPaths = @(Invoke-Component $desktopSession 'Read')
        Assert-Migration ($reopenedPaths.Count -eq 1 -and $reopenedPaths[0] -eq $old) `
            'Session restore keeps an open note when its configured directory ends with a path separator.'
    } finally { Invoke-Component $config 'SetConfigValue' @('notesPath', $directoryPath) }

    $missingNote = Join-Path $directoryPath 'deleted-source-note.rtf'
    [IO.File]::Copy($old, $missingNote + '.bak')
    $missingWindow = New-Component 'KimNotes.Windows.Notes.NoteWindow'
    $missingError = $null
    try { Invoke-Component $missingWindow 'Connect' @($notes, $missingNote, $null) }
    catch {
        $missingError = $_.Exception
        while ($null -ne $missingError.InnerException) { $missingError = $missingError.InnerException }
    }
    Assert-Migration ($missingError -is [IO.FileNotFoundException] -and -not [IO.File]::Exists($missingNote) -and
        [IO.File]::Exists($missingNote + '.bak')) 'Opening a deleted source note does not recreate it from a leftover backup.'

    $damagedNote = Join-Path $testDirectory 'damaged-source-note.rtf'
    [IO.File]::WriteAllBytes($damagedNote, [byte[]]@(0, 255, 0, 255))
    [IO.File]::Copy($old, $damagedNote + '.bak')
    $recoveredWindow = New-Component 'KimNotes.Windows.Notes.NoteWindow'
    Invoke-Component $recoveredWindow 'Connect' @($notes, $damagedNote, $null)
    $recoveredEditor = $recoveredWindow.FindName('Editor')
    $recoveredRange = [Windows.Documents.TextRange]::new($recoveredEditor.Document.ContentStart, $recoveredEditor.Document.ContentEnd)
    $legacyRange = [Windows.Documents.TextRange]::new($legacyEditor.Document.ContentStart, $legacyEditor.Document.ContentEnd)
    Assert-Migration ($recoveredRange.Text -eq $legacyRange.Text -and
        [IO.Directory]::GetFiles($testDirectory, 'damaged-source-note.rtf.damaged-*').Length -eq 1) `
        'Opening an existing damaged note recovers from its backup and preserves the damaged copy.'

    $rtfReader = $assembly.GetType('KimNotes.Windows.Notes.NoteDocumentRtf', $true)
    $prefixedRtf = [IO.MemoryStream]::new([byte[]](@(239, 187, 191, 32, 9, 13, 10) + [IO.File]::ReadAllBytes($old)))
    try {
        $prefixedDocument = Invoke-Component $rtfReader 'Read' @($prefixedRtf)
        $prefixedRange = [Windows.Documents.TextRange]::new($prefixedDocument.ContentStart, $prefixedDocument.ContentEnd)
        Assert-Migration ($prefixedRange.Text -eq $legacyRange.Text) 'An RTF file can start with a UTF-8 BOM and ASCII whitespace.'
    } finally { $prefixedRtf.Dispose() }

    $blank = New-Component 'KimNotes.Windows.Notes.NoteWindow'
    Invoke-Component $blank 'Connect' @($notes, $null, $null)
    $blank.FindName('Editor').AppendText(' ')
    Assert-Migration ($blank.SaveCurrentNoteSafe() -and $null -eq $blank.FilePath -and [IO.Directory]::GetFiles($directoryPath, '*.rtf').Count -eq 1) 'An empty new note does not create a file.'

    $cloud = New-Component 'KimNotes.utils.CloudSync' @($null, $false, $null)
    $catalog = New-Component 'KimNotes.Windows.Notes.NoteCatalogWindow' @($notes)
    Invoke-Component $catalog 'Reload'
    $applySettings = [Func[Collections.Generic.Dictionary[string,string],string]]{ return $null }
    $openAccount = [Action]{ }
    $settings = New-Component 'KimNotes.Windows.Settings.SettingsWindow' @($applySettings, $openAccount, $null)
    $account = New-Component 'KimNotes.Windows.Settings.AccountWindow' @($cloud)
    $dueTime = (Get-Date).AddMinutes(10)

    $desktopSettings = $assembly.GetType('KimNotes.Windows.Runtime.DesktopSettings', $true)
    $sharedProfileName = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)) 'KimNotes'
    $testStartupExe = Join-Path $testDirectory 'example startup folder/KimNotes.WpfPreview.exe'
    $sharedStartupEnabled = Invoke-Component $desktopSettings 'StartupEntries' @($sharedProfileName, $testStartupExe, $true)
    $sharedStartupDisabled = Invoke-Component $desktopSettings 'StartupEntries' @($sharedProfileName, $testStartupExe, $false)
    Assert-Migration ($sharedStartupEnabled.Count -eq 2 -and $sharedStartupEnabled['KimNotes.WpfPreview'] -eq ('"' + $testStartupExe + '"') -and
        $sharedStartupEnabled.ContainsKey('小羊便签') -and $null -eq $sharedStartupEnabled['小羊便签'] -and
        $sharedStartupDisabled.Count -eq 2 -and $null -eq $sharedStartupDisabled['KimNotes.WpfPreview'] -and
        $sharedStartupDisabled.ContainsKey('小羊便签') -and $null -eq $sharedStartupDisabled['小羊便签']) `
        'Shared-profile startup replaces the legacy entry and removes both entries when disabled.'
    $isolatedStartup = Invoke-Component $desktopSettings 'StartupEntries' @($testDirectory, $testStartupExe, $true)
    Assert-Migration ($isolatedStartup.Count -eq 1 -and -not $isolatedStartup.ContainsKey('小羊便签')) `
        'Startup for an isolated profile does not change the legacy startup entry.'

    Render-Window $legacy 'note'
    $noteTools = $legacy.FindName('ToolsChrome').Child
    $defaultToolPositions = @($noteTools.Children | ForEach-Object {
        $_.TranslatePoint([Windows.Point]::new(0, 0), $legacy.Content).X
    })
    Render-Surface $legacy.Content ([Windows.Size]::new(620, $legacy.Height)) 'note-wide'
    $wideToolPositions = @($noteTools.Children | ForEach-Object {
        $_.TranslatePoint([Windows.Point]::new(0, 0), $legacy.Content).X
    })
    Assert-Migration ($defaultToolPositions.Count -eq 9 -and $defaultToolPositions[0] -lt 20 -and
        ($defaultToolPositions -join ',') -eq ($wideToolPositions -join ',')) 'The note toolbar stays left-aligned with fixed button positions as the window widens.'
    $tabType = $assembly.GetType('KimNotes.Windows.Notes.NoteEdgeTab', $true)
    $dockSide = $assembly.GetType('KimNotes.Windows.Notes.NoteDockSide', $true)
    $screen = [Drawing.Rectangle]::new(-1000, 0, 1000, 800)
    $work = [Drawing.Rectangle]::new(-1000, 0, 1000, 760)
    $adjacent = [Drawing.Rectangle]::new(0, 0, 1100, 800)
    $screens = [Drawing.Rectangle[]]@($screen, $adjacent)
    $topPoint = [Drawing.Point]::new(-500, 1)
    $topWindow = [Drawing.Rectangle]::new(-650, -9, 301, 401)
    $up = [Drawing.Point]::new(0, -80)
    $top = Invoke-Component $tabType 'FindDockSide' @($topPoint, $topWindow, $up, $screen, $work, $screens, 18)
    $left = [Drawing.Point]::new(-80, 0)
    $right = [Drawing.Point]::new(80, 0)
    $nextWork = [Drawing.Rectangle]::new(0, 0, 1100, 760)
    $nearLeftWindow = [Drawing.Rectangle]::new(-989, 260, 301, 401)
    $nearLeftCursor = [Drawing.Point]::new(-836, 270)
    $nearLeft = Invoke-Component $tabType 'FindDockSide' @($nearLeftCursor, $nearLeftWindow, $left, $screen, $work, $screens, 18)
    $nearRight = Invoke-Component $tabType 'FindDockSide' @([Drawing.Point]::new(930, 245),
        [Drawing.Rectangle]::new(788, 240, 301, 401), $right, $adjacent, $nextWork, $screens, 18)
    Assert-Migration ($top -eq [Enum]::Parse($dockSide, 'Top') -and $nearLeft -eq [Enum]::Parse($dockSide, 'Left') -and
        $nearRight -eq [Enum]::Parse($dockSide, 'Right')) 'Either outer side docks at the note edge while the cursor is still over the middle of the title.'
    $towardRightSeam = Invoke-Component $tabType 'FindDockSide' @([Drawing.Point]::new(-155, 280),
        [Drawing.Rectangle]::new(-306, 264, 301, 401), $right, $screen, $work, $screens, 18)
    $towardLeftSeam = Invoke-Component $tabType 'FindDockSide' @([Drawing.Point]::new(145, 280),
        [Drawing.Rectangle]::new(7, 264, 301, 401), $left, $adjacent, $nextWork, $screens, 18)
    $above = [Drawing.Rectangle]::new(-1000, -650, 1000, 650)
    $stacked = [Drawing.Rectangle[]]@($screen, $above)
    $towardTopSeam = Invoke-Component $tabType 'FindDockSide' @($topPoint, $topWindow, $up, $screen, $work, $stacked, 18)
    Assert-Migration ($null -eq $towardRightSeam -and $null -eq $towardLeftSeam -and $null -eq $towardTopSeam) `
        'Both directions across a display seam and docking under an upper display stay undocked.'
    $pulledAway = Invoke-Component $tabType 'FindDockSide' @($nearLeftCursor, $nearLeftWindow, $right, $screen, $work, $screens, 18)
    $alongEdge = Invoke-Component $tabType 'FindDockSide' @($nearLeftCursor, $nearLeftWindow, [Drawing.Point]::new(-1, 40), $screen, $work, $screens, 18)
    $tooFar = Invoke-Component $tabType 'FindDockSide' @([Drawing.Point]::new(-810, 280),
        [Drawing.Rectangle]::new(-962, 264, 301, 401), $left, $screen, $work, $screens, 18)
    Assert-Migration ($null -eq $pulledAway -and $null -eq $alongEdge -and $null -eq $tooFar) `
        'Pulling inward, moving along an edge or releasing with space left does not dock.'
    $tabSize = [Drawing.Size]::new(60, 26)
    $firstTab = Invoke-Component $tabType 'FindTabBounds' @($top, $work, $topPoint, $tabSize, 3, [Drawing.Rectangle[]]@())
    $secondTab = Invoke-Component $tabType 'FindTabBounds' @($top, $work, $topPoint, $tabSize, 3, [Drawing.Rectangle[]]@($firstTab))
    Assert-Migration ($null -ne $firstTab -and $null -ne $secondTab -and $work.Contains($secondTab) -and
        -not $firstTab.IntersectsWith($secondTab)) 'Tabs dropped at the same point use distinct visible positions.'
    $heading = Invoke-Component $tabType 'FirstLineCaption' @(('  本周工作计划  ' + [Environment]::NewLine + '会议内容'))
    $emptyHeading = Invoke-Component $tabType 'FirstLineCaption' @(('  ' + [Environment]::NewLine + '这一行不作标签'))
    $horizontalCaption = Invoke-Component $tabType 'FormatCaption' @($heading, $true)
    $verticalCaption = Invoke-Component $tabType 'FormatCaption' @($emptyHeading, $false)
    $sheep = [char]::ConvertFromUtf32(0x1F411)
    $unicodeCaption = Invoke-Component $tabType 'FormatCaption' @(($sheep + '一二三四'), $true)
    Assert-Migration ($heading -eq '本周工作计划' -and $horizontalCaption -eq '本周工作' -and
        $emptyHeading -eq '小羊便签' -and (($verticalCaption -split '\r?\n') -join '') -eq '小羊便签' -and
        ($verticalCaption -split '\r?\n').Count -eq 4 -and $unicodeCaption -eq ($sheep + '一二三')) `
        'Edge captions use up to four complete characters from the first line, with a vertical default for blank notes.'
    Render-Window $legacy 'note'
    $viewportBeforeMenu = $legacyEditor.TranslatePoint([Windows.Point]::new(0, 0), $legacy.Content)
    $moreButton = $legacy.FindName('MoreButton')
    $showMenu = [Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $moreButton)
    $moreButton.RaiseEvent($showMenu)
    Render-Window $legacy 'note-menu'
    $menuHost = $legacy.FindName('MorePanelHost')
    $menu = $menuHost.Child.Child.Children[0]
    $palette = $menu.Children[0]
    $paletteRule = $menu.Children[1]
    $actionRows = $menu.Children[2]
    $rows = @($actionRows.Children)
    $marker = [string][char]0x2713
    $caption = $legacy.FindName('TitleChrome')
    Assert-Migration ($menuHost.Visibility -eq [Windows.Visibility]::Visible -and $moreButton.ContextMenu -eq $null -and
        $caption.Visibility -eq [Windows.Visibility]::Hidden -and
        $menuHost.ActualWidth -eq $caption.ActualWidth -and $menuHost.ActualHeight -eq 98 -and
        ($menuHost.TranslatePoint([Windows.Point]::new(0, 0), $legacy.Content) -eq
            $caption.TranslatePoint([Windows.Point]::new(0, 0), $legacy.Content))) 'The three-dot control opens an in-note full-width overlay at the top edge.'
    Assert-Migration ($palette.ActualHeight -eq 29 -and $palette.Children.Count -eq 12 -and
        @($palette.Children | Where-Object Content -eq $marker).Count -eq 1 -and $rows.Count -eq 2 -and
        @($rows | Where-Object { $_.ActualHeight -eq 30 -and $_.Content.Children[0] -is [Windows.Controls.Viewbox] }).Count -eq 2) `
        'The inline palette now has just the note list and settings rows.'
    $firstActionTop = $rows[0].TranslatePoint([Windows.Point]::new(0, 0), $menu).Y
    Assert-Migration ($paletteRule.ActualHeight -eq 1 -and $paletteRule.ActualWidth -eq $palette.ActualWidth -and
        $paletteRule.Opacity -lt 1 -and -not $paletteRule.IsHitTestVisible -and $firstActionTop -eq $palette.ActualHeight + 5 -and
        $actionRows.Margin.Bottom -eq 4) 'A subtle rule separates the swatches from an inset action area.'
    $paletteLeft = $palette.TranslatePoint([Windows.Point]::new(0, 0), $menuHost).X
    Assert-Migration ([Math]::Abs($palette.ActualWidth - $menuHost.ActualWidth) -le 1 -and
        [Math]::Abs($paletteLeft) -le 1 -and
        @($palette.Children | Where-Object { $_.ActualWidth -lt 20 -or $_.ActualHeight -lt 28 }).Count -eq 0) `
        'The theme strip spans the menu with twelve usable click targets at the default width.'
    Render-Surface $legacy.Content ([Windows.Size]::new($legacy.Width + 100, $legacy.Height)) 'note-menu-wide'
    $columnWidths = $palette.Children.ActualWidth | Measure-Object -Minimum -Maximum
    Assert-Migration ($palette.ActualWidth -eq $menuHost.ActualWidth -and $columnWidths.Maximum - $columnWidths.Minimum -le 1) `
        'The theme strip stretches its equal columns when the note is widened.'
    Render-Window $legacy 'note-menu'
    Assert-Migration (@($palette.Children | Where-Object {
        $_.ToolTip -is [Windows.Controls.ToolTip] -and [Windows.Automation.AutomationProperties]::GetName($_) -eq $_.ToolTip.Content -and
        [Windows.Controls.ToolTipService]::GetInitialShowDelay($_) -le 200 -and
        $_.ToolTip.Placement -eq [Windows.Controls.Primitives.PlacementMode]::Top
    }).Count -eq $palette.Children.Count) 'Each theme shows its accessible name above its swatch on hover.'
    $themeTip = $palette.Children[0].ToolTip
    $themeTip.Measure([Windows.Size]::new([double]::PositiveInfinity, [double]::PositiveInfinity))
    Render-Surface $themeTip $themeTip.DesiredSize 'note-theme-tooltip'
    $tipSurface = $themeTip.Template.FindName('TipSurface', $themeTip)
    Assert-Migration ($tipSurface.CornerRadius.TopLeft -eq 6 -and $tipSurface.Background -eq $menuHost.Child.Background -and
        $tipSurface.BorderBrush -eq $paletteRule.Background -and $themeTip.VerticalOffset -lt 0) `
        'Theme tooltips use matching colors, a rounded border and a gap above their swatches.'
    $filledWidth = 0
    foreach ($swatch in $palette.Children) {
        $fill = $swatch.Template.FindName('SwatchFill', $swatch)
        $origin = $fill.TranslatePoint([Windows.Point]::new(0, 0), $palette)
        Assert-Migration ($fill.Background -eq $swatch.Background -and $origin.X -eq $filledWidth -and $origin.Y -eq 0 -and
            $fill.ActualWidth -eq $swatch.ActualWidth -and $fill.ActualHeight -eq $palette.ActualHeight) `
            "The $($swatch.ToolTip.Content) color meets its neighbors without a gap."
        $filledWidth += $fill.ActualWidth
    }
    Assert-Migration ($filledWidth -eq $palette.ActualWidth) 'The adjacent color blocks cover the full strip width.'
    $themeType = $assembly.GetType('KimNotes.NoteTheme', $true)
    $themeBrushes = $assembly.GetType('KimNotes.Windows.Presentation.NoteThemeBrushes', $true)
    $allThemes = $themeType.GetField('All').GetValue($null)
    $shownNames = @($palette.Children | ForEach-Object { $_.ToolTip.Content })
    $expectedNames = @('天青蓝', '柠檬黄', '薄荷绿', '樱花粉', '晨雾蓝', '蜜桃日落', '薄荷清晨', '薰衣草',
        '远山', '海雾', '云鲸', '暗夜')
    $artworkIds = @($allThemes | Where-Object { Invoke-Component $themeBrushes 'HasArtwork' @($_) } | Select-Object -ExpandProperty Id)
    $pictureSwatches = @($palette.Children | Where-Object { $_.Background -is [Windows.Media.ImageBrush] })
    $classicSwatches = @($palette.Children | Where-Object { $_.Background -is [Windows.Media.SolidColorBrush] })
    Assert-Migration (($shownNames -join ',') -eq ($expectedNames -join ',') -and ($artworkIds -join ',') -eq '4,5,6,7,8,9' -and
        $classicSwatches.Count -eq 4 -and $pictureSwatches.Count -eq 4 -and @($pictureSwatches | Where-Object {
            $source = $_.Background.ImageSource
            if ($source -is [Windows.Media.DrawingImage]) { $source = $source.Drawing.Children[0].ImageSource }
            $source.PixelWidth -eq 896 -and $source.PixelHeight -eq 1200
        }).Count -eq 4) 'The chooser keeps four classics, four gradients and the four remaining illustrations in order.'
    $classicColor = $palette.Children[0]
    $classicColor.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $classicColor))
    Assert-Migration ($legacy.Content.Background -eq (Invoke-Component $themeBrushes 'Body' @($allThemes[10])) -and
        $classicColor.Content -eq $marker -and @($palette.Children | Where-Object Content -eq $marker).Count -eq 1) `
        'Choosing the first displayed color selects the classic ID and applies its check mark.'
    $color = $palette.Children | Where-Object { $_.ToolTip.Content -eq '薄荷清晨' }
    $color.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $color))
    Assert-Migration ($color.Content -eq $marker -and $menuHost.Visibility -eq [Windows.Visibility]::Visible) 'A color can be applied directly and the inline panel stays open.'

    $todo = Invoke-Component $todoStore 'Add' @('Migration test todo', $dueTime, $old, 'Legacy text')
    $menuClosedForAction = [Collections.Generic.List[bool]]::new()
    $openedCardThemes = [Collections.Generic.List[object]]::new()
    $desktopCardTheme = $assembly.GetType('KimNotes.Windows.Presentation.DesktopCardTheme', $true)
    Invoke-Component $desktopCardTheme 'Apply' @(1)
    $fakeAction = [Action]({
        $menuClosedForAction.Add($menuHost.Visibility -eq [Windows.Visibility]::Collapsed)
        $openedCardThemes.Add([Windows.Application]::Current.Resources['CardTheme.Surface'])
    }.GetNewClosure())
    Invoke-Component $legacy 'RunMoreAction' @($fakeAction)
    $legacy.Content.UpdateLayout()
    Assert-Migration ($menuClosedForAction.Count -eq 1 -and $menuClosedForAction[0] -and $caption.Visibility -eq [Windows.Visibility]::Visible -and
        $legacyEditor.TranslatePoint([Windows.Point]::new(0, 0), $legacy.Content) -eq $viewportBeforeMenu) 'A command closes the overlay before dispatch and leaves the editor layout unchanged.'
    Assert-Migration ($openedCardThemes.Count -eq 1 -and $openedCardThemes[0] -eq (Invoke-Component $themeBrushes 'Surface' @($allThemes[2]))) `
        'A command uses its source note theme even when a different palette was last active.'

    $captureState = $notes.GetType().GetProperty('IsCaptureActive', $instanceFlags)
    $captureState.SetValue($notes, [Func[bool]]{ return $true })
    Invoke-Component $legacy 'SetChromeVisible' @($false)
    Invoke-Component $legacy 'UpdateChromeVisibility'
    Assert-Migration ($legacy.FindName('ToolsChrome').Visibility -eq [Windows.Visibility]::Hidden) 'The idle chrome timer preserves the note toolbar state during an active capture.'
    $captureState.SetValue($notes, $null)
    Invoke-Component $legacy 'SetChromeVisible' @($true)

    $toolbarCalls = [Collections.Generic.List[string]]::new()
    $toolsRequested = [Action]({ $toolbarCalls.Add('tools') }.GetNewClosure())
    $todoRequested = [Action]({ $toolbarCalls.Add('todos') }.GetNewClosure())
    $notes.GetType().GetEvent('ToolsRequested', $instanceFlags).GetAddMethod($true).Invoke($notes, @($toolsRequested))
    $notes.GetType().GetEvent('TodoRequested', $instanceFlags).GetAddMethod($true).Invoke($notes, @($todoRequested))
    $toolsButton = $legacy.FindName('DesktopToolsButton')
    $toolsButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $toolsButton))
    $contentStart = $legacyEditor.Document.ContentStart
    $legacyEditor.Selection.Select($contentStart, $contentStart)
    $todoButton = $legacy.FindName('TodoButton')
    $todoButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $todoButton))
    Assert-Migration (($toolbarCalls -join ',') -eq 'tools,todos') 'The bottom buttons open Desktop Tools and open Todos when the note selection is empty.'
    $toolsAvailable = $notes.GetType().GetProperty('ToolsAvailable', $instanceFlags)
    $toolsAvailable.SetValue($notes, [Func[bool]]{ return $true })
    $toolsButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $toolsButton))
    $toolsPopup = Invoke-Component $legacy 'GetToolsPopup'
    $toolsPopup.Child.Measure([Windows.Size]::new(120, 100))
    Render-Surface $toolsPopup.Child $toolsPopup.Child.DesiredSize 'tools-popup'
    $toolTiles = @(Find-Visuals $toolsPopup.Child ([Windows.Controls.Button]))
    $toolSurface = $toolsPopup.Child.Children[0]
    Assert-Migration ($toolsPopup.PlacementTarget -eq $toolsButton -and $toolTiles.Count -eq 1 -and
        $toolSurface.ActualWidth -eq 62 -and $toolSurface.ActualHeight -eq 52 -and
        [Windows.Automation.AutomationProperties]::GetName($toolTiles[0]) -eq '文件互传' -and
        ($toolbarCalls -join ',') -eq 'tools,todos') 'The toolbar has one small anchored tile and only activating it opens file transfer.'
    $toolTiles[0].RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $toolTiles[0]))
    Assert-Migration (($toolbarCalls -join ',') -eq 'tools,todos,tools') 'The file transfer tile runs the existing transfer entry point.'
    $translateTip = $legacy.FindName('TranslateButton').ToolTip
    $translateTip.ApplyTemplate() | Out-Null
    $tooltipSurface = $translateTip.Template.FindName('TipSurface', $translateTip)
    $tooltipPlacement = $translateTip.CustomPopupPlacementCallback.Invoke([Windows.Size]::new(40, 24),
        [Windows.Size]::new(26, 26), [Windows.Point]::new(0, 0))
    Assert-Migration ($translateTip.Content -eq '翻译' -and $tooltipSurface.CornerRadius.TopLeft -eq 6 -and
        $tooltipPlacement[0].Point -eq [Windows.Point]::new(-7, 30) -and $tooltipPlacement[1].Point.Y -lt 0) `
        'The rounded translation tooltip is centered below the control and can flip above it.'

    $legacyEditor.Selection.Select($legacyEditor.Document.ContentStart, $legacyEditor.Document.ContentEnd)
    $selection = Invoke-Component $legacy 'GetTodoSelection'
    Assert-Migration ($selection -eq $legacyEditor.Selection.Text -and $selection.Contains('Legacy text')) 'The add-todo branch takes the actual note selection.'
    $legacyEditor.Selection.Select($contentStart, $contentStart)

    $todoWindow = New-Component 'KimNotes.Windows.Todos.TodoWindow' @($null)
    $noRepeat = [Enum]::ToObject($assembly.GetType('KimNotes.utils.RepeatRule', $true), 0)
    $todoEditor = New-Component 'KimNotes.Windows.Todos.TodoEditor' @('Example', $null, $noRepeat, $false, $false)
    $null = $legacy.SaveCurrentNoteSafe()
    $peachNote = Join-Path $directoryPath 'peach.rtf'
    [IO.File]::WriteAllText($peachNote, '{\rtf1\ansi Peach note\par Preview from the peach note\par}', [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($peachNote + '.meta.json', '{"ThemeId":1}')
    $longNote = Join-Path $directoryPath 'catalog-preview.rtf'
    $longParagraph = ('One long paragraph that needs wrapping at the width of a default note catalog card. ' * 3).Trim()
    [IO.File]::WriteAllText($longNote, ('{\rtf1\ansi Catalog preview fixture\par ' + $longParagraph +
        '\par A separate body paragraph\par A third body paragraph\par A fourth body paragraph\par}'), [Text.Encoding]::ASCII)
    Invoke-Component $catalog 'Reload'
    Render-Window $catalog 'catalog'
    $catalogType = $catalog.GetType()
    $catalogSearch = $catalogType.GetField('search', $instanceFlags).GetValue($catalog)
    $catalogCount = $catalogType.GetField('entryCount', $instanceFlags).GetValue($catalog)
    $catalogResults = $catalogType.GetField('results', $instanceFlags).GetValue($catalog)
    $catalogSearchPill = $catalogSearch.Parent.Parent.Parent
    Assert-Migration ($catalogSearchPill.BorderThickness -eq [Windows.Thickness]::new(1) -and
        $catalogSearchPill.BorderBrush -eq $app.Resources['CardTheme.SearchBorder'] -and
        $catalogSearchPill.Background -eq $app.Resources['CardTheme.SearchSurface'] -and
        $catalogSearchPill.Background.Color -ne $catalogSearchPill.BorderBrush.Color -and
        $catalogCount.Parent -eq $catalogSearchPill.Child -and $catalogSearch.BorderThickness -eq [Windows.Thickness]::new(0) -and
        $null -eq $catalogSearch.FocusVisualStyle) 'Catalog search keeps its theme outline and count inside the field without a focus border.'
    $peachIndex = $catalogResults.Items.IndexOf(($catalogResults.Items | Where-Object Path -eq $peachNote))
    $peachContainer = $catalogResults.ItemContainerGenerator.ContainerFromIndex($peachIndex)
    $peachPresenter = [Windows.Media.VisualTreeHelper]::GetChild($peachContainer, 0)
    $peachCard = [Windows.Media.VisualTreeHelper]::GetChild($peachPresenter, 0)
    $shortPreviewHeight = $peachCard.Child.Children[1].ActualHeight
    $peachFill = $peachCard.Background
    $catalogResults.SelectedIndex = $peachIndex
    $catalog.Content.UpdateLayout()
    Assert-Migration ($peachFill -eq (Invoke-Component $themeBrushes 'Body' @($allThemes[1])) -and
        $peachCard.Background -eq (Invoke-Component $themeBrushes 'SelectedBody' @($allThemes[1])) -and
        $peachCard.Background -ne $peachFill) 'Selecting a peach note uses its own selected palette.'
    $catalogHover = @($peachCard.Style.Triggers | Where-Object {
        $_ -is [Windows.Trigger] -and $_.Property -eq [Windows.UIElement]::IsMouseOverProperty -and $_.Value -eq $true
    })
    Assert-Migration ($catalogHover.Count -eq 1 -and $catalogHover[0].Setters[0].Property -eq [Windows.Controls.Border]::BackgroundProperty -and
        $catalogHover[0].Setters[0].Value.Path.Path -eq 'SelectedFill') 'Hover uses the same deeper shade of the note theme without clicking.'
    $catalogSearch.Text = 'does not exist'
    Assert-Migration ($catalogResults.Items.Count -eq 0 -and $catalogCount.Text -eq '0 条便签') `
        'The search pill shows the number of matched notes.'
    $catalogSearch.Text = 'edited'
    $catalogResults.SelectedIndex = 0
    Render-Window $catalog 'catalog-selected'
    $catalogItem = $catalogResults.ItemContainerGenerator.ContainerFromIndex(0)
    $catalogPresenter = [Windows.Media.VisualTreeHelper]::GetChild($catalogItem, 0)
    $catalogCard = [Windows.Media.VisualTreeHelper]::GetChild($catalogPresenter, 0)
    Assert-Migration ($catalogResults.SelectedItem.Fill -eq (Invoke-Component $themeBrushes 'Body' @($allThemes[2])) -and
        $catalogCard.Background -eq (Invoke-Component $themeBrushes 'SelectedBody' @($allThemes[2]))) `
        'The saved mint theme controls the selected catalog card.'
    $catalogDate = $catalogCard.Child.Children[0].Children[0].Children[0]
    $catalogMore = $catalogCard.Child.Children[0].Children[0].Children[1]
    Assert-Migration ($catalogDate.Text -match '^\d{4}-\d{2}-\d{2}$' -and
        $catalogDate.Visibility -eq [Windows.Visibility]::Collapsed -and $catalogMore.Visibility -eq [Windows.Visibility]::Visible) `
        'A selected card swaps its date for a single ellipsis control.'
    $catalogEntry = $catalogResults.SelectedItem
    $catalogMenuType = $assembly.GetType('KimNotes.Windows.Notes.NoteCatalogMenu', $true)
    $catalogMenu = Invoke-Component $catalogMenuType 'Create' @($catalogMore, $catalogEntry.OpenAction, $catalogEntry.HistoryAction, $catalogEntry.DeleteAction)
    Assert-Migration ($catalogMenu.Items.Count -eq 3 -and @($catalogMenu.Items | Where-Object { $_ -is [Windows.Controls.MenuItem] -and $_.MinHeight -eq 21 }).Count -eq 3 -and
        -not $catalogMenu.StaysOpen -and $catalogMenu.PlacementTarget -eq $catalogMore) `
        'The ellipsis has three compact menu commands, no separators, and dismisses outside the anchor.'
    $catalogSearch.Text = 'Catalog preview fixture'
    Render-Window $catalog 'catalog-four-lines'
    $longContainer = $catalogResults.ItemContainerGenerator.ContainerFromIndex(0)
    $longPresenter = [Windows.Media.VisualTreeHelper]::GetChild($longContainer, 0)
    $longCard = [Windows.Media.VisualTreeHelper]::GetChild($longPresenter, 0)
    $longPreview = $longCard.Child.Children[1]
    Assert-Migration ($longPreview.Text -match '\r?\nA separate body paragraph' -and
        $longPreview.TextWrapping -eq [Windows.TextWrapping]::Wrap -and
        $longPreview.TextTrimming -eq [Windows.TextTrimming]::CharacterEllipsis -and
        [Math]::Abs($shortPreviewHeight - $longPreview.LineHeight) -lt 1 -and
        [Math]::Abs($longPreview.ActualHeight - 3 * $longPreview.LineHeight) -lt 1) `
        'Note previews keep paragraph breaks, fit short bodies to one line, and cap long bodies at three rendered lines.'

    $versionEntry = $catalogResults.Items[0]
    $versionEntry.CanShowHistory = $true
    Invoke-Component $catalog 'ShowVersions' @($versionEntry)
    $history = $catalogType.GetField('history', $instanceFlags).GetValue($catalog)
    $historyType = $history.GetType()
    $versions = $historyType.GetField('versions', $instanceFlags).GetValue($history)
    $versionPreview = $historyType.GetField('preview', $instanceFlags).GetValue($history)
    $versionSurface = $historyType.GetField('previewSurface', $instanceFlags).GetValue($history)
    $versionRow = $historyType.GetNestedType('VersionRow', [Reflection.BindingFlags]::NonPublic).GetConstructors($instanceFlags)[0]
    $current = [Newtonsoft.Json.Linq.JObject]::Parse('{"revision":2,"document":{"text":"Short version"}}')
    $earlier = [Newtonsoft.Json.Linq.JObject]::Parse('{"revision":1,"document":{"text":"Line 1\nLine 2\nLine 3\nLine 4\nLine 5\nLine 6\nLine 7\nLine 8"}}')
    $historyType.GetField('latest', $instanceFlags).SetValue($history, $current)
    $null = $versions.Items.Add($versionRow.Invoke(@($current, $true)))
    $null = $versions.Items.Add($versionRow.Invoke(@($earlier, $false)))
    $versions.SelectedIndex = 0
    Render-Window $catalog 'history-short'
    $shortVersionHeight = $versionSurface.ActualHeight
    $versions.SelectedIndex = 1
    Render-Window $catalog 'history-overflow'
    $back = $historyType.GetField('back', $instanceFlags).GetValue($history)
    $restore = $historyType.GetField('restore', $instanceFlags).GetValue($history)
    $versionScroll = $versionPreview.Template.FindName('PART_ContentHost', $versionPreview)
    Assert-Migration ($catalog.Width -eq 301 -and $catalog.Height -eq 401 -and $catalog.Title -eq '历史版本' -and
        $versionSurface.ActualHeight -eq $shortVersionHeight -and $versionPreview.ActualHeight -eq 4 * $versionPreview.Document.LineHeight -and
        $versionScroll.ScrollableHeight -gt 0 -and $versionScroll.ComputedVerticalScrollBarVisibility -eq [Windows.Visibility]::Collapsed -and
        [Math]::Abs($back.ActualWidth - $restore.ActualWidth) -le 1 -and $back.ActualHeight -eq $restore.ActualHeight) `
        'History keeps the default card size, a fixed four-line preview without a visible scrollbar, and equal footer buttons.'
    $wheel = [Windows.Input.MouseWheelEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, -120)
    $wheel.RoutedEvent = [Windows.Input.Mouse]::MouseWheelEvent
    $versionScroll.RaiseEvent($wheel)
    $versionScroll.UpdateLayout()
    Assert-Migration ($wheel.Handled -and $versionScroll.VerticalOffset -gt 0) 'The history preview can still be scrolled using the mouse wheel.'
    Invoke-Component $catalog 'CloseVersions'

    Render-Window $settings 'settings'
    $pathFields = @(Find-Visuals $settings.Content ([Windows.Controls.TextBox]) | Where-Object {
        $_.Template -and $_.Template.FindName('PathPreview', $_)
    })
    Assert-Migration ($pathFields.Count -eq 2 -and @($pathFields | Where-Object {
        $preview = $_.Template.FindName('PathPreview', $_)
        $preview.TextTrimming -eq 'CharacterEllipsis' -and $preview.Margin.Left -eq 6 -and $preview.Text -eq $_.Text
    }).Count -eq 2) 'Both settings paths use a compact inset and display a trimmed view of the real path.'
    $settingOptions = @(Find-Visuals $settings.Content ([Windows.Controls.CheckBox]))
    Assert-Migration ($settingOptions.Count -eq 4 -and @($settingOptions | Where-Object {
        $_.Template.FindName('Mark', $_).CornerRadius.TopLeft -eq 3
    }).Count -eq 4) 'All four settings options use the same rounded checkbox.'
    Render-Window $account 'account'
    Render-Window $todoWindow 'todos'
    Render-Window $todoEditor 'todo-edit'
    $newTodoLabels = @(Find-Visuals $todoEditor.Content ([Windows.Controls.TextBlock]) | Select-Object -ExpandProperty Text)
    $newTodoButtons = @(Find-Visuals $todoEditor.Content ([Windows.Controls.Button]) | Where-Object Content -in @('取消', '加入待办'))
    Assert-Migration ($newTodoButtons.Count -eq 2 -and [Math]::Abs($newTodoButtons[0].ActualWidth - $newTodoButtons[1].ActualWidth) -le 1 -and
        $newTodoButtons[0].ActualHeight -eq $newTodoButtons[1].ActualHeight -and '备注' -notin $newTodoLabels -and
        '置顶到清单最上方' -notin $newTodoLabels) 'A new todo has matching footer buttons and only the requested fields.'
    $priorityIcons = @(Find-Visuals $todoEditor.Content ([Windows.Controls.RadioButton]) |
        Where-Object GroupName -eq 'TodoPriority' | ForEach-Object {
            @(Find-Visuals $_ ([Windows.Shapes.Path]))[0].Data.ToString()
        } | Sort-Object -Unique)
    Assert-Migration ($priorityIcons.Count -eq 3) 'The three priorities have different icon geometry without relying on color.'
    $presetChoices = $todoEditor.GetType().GetField('presets', $instanceFlags).GetValue($todoEditor)
    $customFields = $todoEditor.GetType().GetField('customFields', $instanceFlags).GetValue($todoEditor)
    Assert-Migration ($presetChoices[0].IsChecked -and $customFields.Visibility -eq 'Visible' -and -not $customFields.IsEnabled) `
        'A new todo starts without a reminder and shows inactive date and time fields.'
    $customFieldHeight = $customFields.ActualHeight
    $presetChoices[3].IsChecked = $true
    Render-Window $todoEditor 'todo-add-custom'
    Assert-Migration ($customFields.IsEnabled -and $customFieldHeight -gt 0 -and $customFields.ActualHeight -eq $customFieldHeight) `
        'Custom mode enables date and time editing without shifting the form.'
    $repeatButton = $todoEditor.GetType().GetField('repeatButton', $instanceFlags).GetValue($todoEditor)
    $formScrollers = @(Find-Visuals $todoEditor.Content ([Windows.Controls.ScrollViewer]) | Where-Object {
        $_.IsAncestorOf($repeatButton)
    })
    $fieldsBottom = $customFields.TranslatePoint([Windows.Point]::new(0, $customFields.ActualHeight), $todoEditor.Content).Y
    $buttonsTop = $newTodoButtons[0].TranslatePoint([Windows.Point]::new(0, 0), $todoEditor.Content).Y
    Assert-Migration ($formScrollers.Count -eq 0 -and $customFields.ActualHeight -gt 0 -and $fieldsBottom -le $buttonsTop -and
        $todoEditor.MinHeight -ge $todoEditor.Height) 'The complete todo form fits at its default height without a scrolling container.'
    $repeatMenu = $repeatButton.ContextMenu
    $repeatItems = @($repeatMenu.Items | Where-Object { $_ -is [Windows.Controls.MenuItem] -and $_.MinHeight -le 22 })
    $repeatLabels = @($repeatItems | ForEach-Object { [Windows.Automation.AutomationProperties]::GetName($_) })
    Assert-Migration ($repeatItems.Count -eq 4 -and $repeatItems[0].IsChecked -and
        ($repeatLabels -join ',') -eq '不重复,每天,工作日,每周') 'Repeat uses a compact menu with a clear current selection.'
    $todoInput = $todoEditor.GetType().GetField('content', $instanceFlags).GetValue($todoEditor)
    $todoInput.Clear()
    Invoke-Component $todoEditor 'Commit'
    Render-Window $todoEditor 'todo-validation'
    $validation = $todoEditor.GetType().GetField('status', $instanceFlags).GetValue($todoEditor)
    $fieldsBottom = $customFields.TranslatePoint([Windows.Point]::new(0, $customFields.ActualHeight), $todoEditor.Content).Y
    $buttonsTop = $newTodoButtons[0].TranslatePoint([Windows.Point]::new(0, 0), $todoEditor.Content).Y
    Assert-Migration ($validation.Visibility -eq 'Visible' -and $validation.Text -ne '' -and $fieldsBottom -le $buttonsTop) `
        'Validation stays visible without crowding the date fields or footer.'
    $todoInput.Text = 'Example'
    $editorPlacement = $assembly.GetType('KimNotes.Windows.Todos.TodoEditorPlacement', $true)
    $workArea = [Windows.Rect]::new(0, 0, 1200, 800)
    $editorSize = [Windows.Size]::new($todoEditor.Width, $todoEditor.Height)
    $rightOfOwner = Invoke-Component $editorPlacement 'GetPosition' @([Windows.Rect]::new(400, 100, 301, 401), $workArea, $editorSize, [double]12, [double]36)
    $leftOfOwner = Invoke-Component $editorPlacement 'GetPosition' @([Windows.Rect]::new(860, 100, 301, 401), $workArea, $editorSize, [double]12, [double]36)
    Assert-Migration ($todoEditor.Width -eq $todoWindow.Width -and $todoEditor.Height -eq $todoWindow.Height -and $rightOfOwner.X -gt 701 -and
        $leftOfOwner.X + $editorSize.Width -lt 860) 'A new todo keeps its default size and opens on whichever side fits.'
    $narrowArea = [Windows.Rect]::new(-620, -40, 560, 420)
    $narrowOwner = [Windows.Rect]::new(-510, -20, 301, 401)
    $offsetEditor = Invoke-Component $editorPlacement 'GetPosition' @($narrowOwner, $narrowArea, $editorSize, [double]12, [double]36)
    Assert-Migration ($offsetEditor -ne $narrowOwner.TopLeft -and
        $narrowArea.Contains([Windows.Rect]::new($offsetEditor, $editorSize))) 'A new todo stays within a narrow secondary display and avoids the exact owner position.'

    $priorityType = $assembly.GetType('KimNotes.utils.TodoPriority', $true)
    $important = [Enum]::Parse($priorityType, 'Important')
    $existingEditor = New-Component 'KimNotes.Windows.Todos.TodoEditor' @('Existing todo', $dueTime, $noRepeat, $true, $false, $important)
    Render-Window $existingEditor 'todo-edit-existing'
    $existingLabels = @(Find-Visuals $existingEditor.Content ([Windows.Controls.TextBlock]) | Select-Object -ExpandProperty Text)
    $existingButtons = @(Find-Visuals $existingEditor.Content ([Windows.Controls.Button]) | Where-Object Content -in @('取消', '保存'))
    Assert-Migration ($existingEditor.Width -eq $todoEditor.Width -and $existingEditor.Height -eq $todoEditor.Height -and
        $existingButtons.Count -eq 2 -and $existingButtons[0].ActualHeight -eq $newTodoButtons[0].ActualHeight -and
        @($existingLabels | Where-Object { $_ -match '备注|置顶' }).Count -eq 0) `
        'Editing a todo uses the same compact form and footer as adding one, with no notes or pin controls.'

    $reception = New-Component 'KimNotes.FileTransferReception' @($cloud, $null)
    $transferSession = New-Component 'KimNotes.Windows.Transfer.TransferSession' @($reception, $null)
    $clipboardFixture = [Func[string]]{ return '  abcd2345  ' }
    $transferWindow = New-Component 'KimNotes.Windows.Transfer.TransferWindow' @($transferSession, $clipboardFixture)
    Assert-Migration ($transferWindow.Width -eq $legacy.Width -and $transferWindow.Height -eq $legacy.Height) 'File transfer uses the same initial dimensions as a note.'
    $transferType = $transferWindow.GetType()
    Render-Window $transferWindow 'transfer-send'
    $sendTab = $transferType.GetField('sendTab', $instanceFlags).GetValue($transferWindow)
    $receiveTab = $transferType.GetField('receiveTab', $instanceFlags).GetValue($transferWindow)
    $modeRow = $sendTab.Parent
    $dropArea = $transferType.GetField('chooseFiles', $instanceFlags).GetValue($transferWindow)
    $transferFooter = $transferType.GetField('footer', $instanceFlags).GetValue($transferWindow)
    $folderAction = $dropArea.Children[1].Children[2].Inlines | Where-Object { $_ -is [Windows.Documents.Hyperlink] }
    $fileActions = $transferFooter.Children[1].Children[0]
    $pickupHint = $transferFooter.Children[1].Children[1].Children[1]
    Assert-Migration ($transferWindow.Title -eq '文件互传' -and $dropArea.Visibility -eq 'Visible') `
        'The file transfer window opens directly on the send page.'
    Assert-Migration ($sendTab.Parent -eq $receiveTab.Parent -and $modeRow.Parent -is [Windows.Controls.DockPanel] -and
        $sendTab.BorderThickness.Left -gt 0 -and $receiveTab.BorderThickness.Left -gt 0 -and
        $sendTab.ActualWidth -eq $receiveTab.ActualWidth -and $sendTab.ActualHeight -eq $receiveTab.ActualHeight -and
        $sendTab.Background.Color -ne $receiveTab.Background.Color -and $sendTab.BorderBrush.Color -ne $receiveTab.BorderBrush.Color) `
        'Transfer modes use two separate buttons with a distinct selection.'
    $activeModeFill = $sendTab.Background.Color
    $inactiveModeFill = $receiveTab.Background.Color
    Assert-Migration ($folderAction -and $folderAction.Inlines.FirstInline.Text -eq '选择文件夹' -and $dropArea.AllowDrop -and
        $dropArea.Children[0].StrokeDashArray.Count -gt 0 -and $fileActions.Children.Count -eq 1 -and
        $fileActions.Children[0].Content -eq '选择文件' -and $pickupHint.Text.Contains('8 位取件码')) `
        'The send layout provides a folder link in its dashed drop area and one full width file action with the real pickup-code length.'
    $surface = $transferWindow.Content
    $fileButton = $fileActions.Children[0]
    $transferSizes = @(
        [Windows.Size]::new($transferWindow.Width, $transferWindow.Height),
        [Windows.Size]::new($transferWindow.MinWidth, $transferWindow.MinHeight),
        [Windows.Size]::new($transferWindow.Width + 60, $transferWindow.Height + 75)
    )
    $sendButtonBounds = @{}
    foreach ($bounds in $transferSizes) {
        $surface.Measure($bounds)
        $surface.Arrange([Windows.Rect]::new($bounds))
        $surface.UpdateLayout()
        $dropTop = $dropArea.TranslatePoint([Windows.Point]::new(0, 0), $surface)
        $tabsBottom = $modeRow.TranslatePoint([Windows.Point]::new(0, $modeRow.ActualHeight), $surface).Y
        $footerTop = $transferFooter.TranslatePoint([Windows.Point]::new(0, 0), $surface).Y
        Assert-Migration ($dropTop.X -eq 14 -and $dropArea.ActualWidth -eq ($bounds.Width - 28) -and
            $dropTop.Y - $tabsBottom -eq 10 -and $footerTop - $dropTop.Y - $dropArea.ActualHeight -eq 10 -and
            $dropArea.ActualHeight -gt 175) "The send area grows vertically and keeps 10 DIP gaps at $bounds."
        $sendButtonBounds[$bounds] = [Windows.Rect]::new($fileButton.TranslatePoint([Windows.Point]::new(0, 0), $surface), $fileButton.RenderSize)
    }
    Invoke-Component $transferWindow 'SetMode' @($true)
    Render-Window $transferWindow 'transfer-receive'
    Assert-Migration ($receiveTab.Background.Color -eq $activeModeFill -and $sendTab.Background.Color -eq $inactiveModeFill) `
        'The selected appearance follows the active transfer mode.'
    $codeInput = $transferType.GetField('codeInput', $instanceFlags).GetValue($transferWindow)
    $pickupButton = $transferType.GetField('takeButton', $instanceFlags).GetValue($transferWindow)
    Assert-Migration ($transferFooter.Children[1].Children[1].Visibility -eq 'Hidden' -and
        $pickupButton.FontWeight -eq $fileButton.FontWeight) 'The receive footer reserves the hint space and matches the send action styling.'
    foreach ($bounds in $transferSizes) {
        $surface.Measure($bounds)
        $surface.Arrange([Windows.Rect]::new($bounds))
        $surface.UpdateLayout()
        $pickupBounds = [Windows.Rect]::new($pickupButton.TranslatePoint([Windows.Point]::new(0, 0), $surface), $pickupButton.RenderSize)
        Assert-Migration ($pickupBounds -eq $sendButtonBounds[$bounds] -and $pickupButton.ActualHeight -eq 36) `
            "Send and receive actions occupy the same fixed-height position at $bounds."
    }
    Render-Window $transferWindow 'transfer-receive'
    Assert-Migration ($pickupButton.IsEnabled -and $pickupButton.Content -eq '粘贴取件码' -and $codeInput.Text.Length -eq 0) `
        'An empty pickup field offers a working paste button.'
    $pickupButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $pickupButton))
    $surface.UpdateLayout()
    Assert-Migration ($codeInput.Text -eq 'ABCD2345' -and $pickupButton.Content -eq '接收文件' -and -not $transferSession.Busy) `
        'Pasting fills and normalizes the pickup code without starting a transfer.'
    $receiveBounds = [Windows.Rect]::new($pickupButton.TranslatePoint([Windows.Point]::new(0, 0), $surface), $pickupButton.RenderSize)
    Assert-Migration ($receiveBounds -eq $sendButtonBounds[$transferSizes[0]]) 'Changing from paste to receive leaves the primary action in place.'
    Assert-Migration ($codeInput.ContextMenu.Template -ne $null -and
        @($codeInput.ContextMenu.Items | Where-Object { [Windows.Automation.AutomationProperties]::GetName($_) -eq '粘贴' }).Count -eq 1 -and
        @($codeInput.ContextMenu.Items | Where-Object { $_.Command -eq [Windows.Input.ApplicationCommands]::Copy -and $_.CommandTarget -eq $codeInput }).Count -eq 1) `
        'The pickup input has a styled menu with paste and a copy command targeted to the input.'

    Invoke-Component $transferWindow 'SetMode' @($false)
    $transferSessionType = $transferSession.GetType()
    $activeTransfer = [Threading.CancellationTokenSource]::new()
    $operationField = $transferSessionType.GetField('operation', $instanceFlags)
    $codeProperty = $transferSessionType.GetProperty('Code', $instanceFlags)
    $renderTransfer = $transferType.GetMethod('Render', ($instanceFlags -bor [Reflection.BindingFlags]::DeclaredOnly))
    try {
        $operationField.SetValue($transferSession, $activeTransfer)
        $codeProperty.SetValue($transferSession, 'ABCD2345')
        $renderTransfer.Invoke($transferWindow, @())
        Render-Window $transferWindow 'transfer-waiting'
        $pendingButtons = @($transferFooter.Children[1].Children)
        $pendingWidths = $pendingButtons.ActualWidth | Measure-Object -Maximum -Minimum
        Assert-Migration (($pendingButtons.Content -join ',') -eq '取消,后台传输,复制取件码' -and
            @($pendingButtons | Where-Object { $_.ActualHeight -ne 36 }).Count -eq 0 -and
            $pendingWidths.Maximum - $pendingWidths.Minimum -le 1 -and
            $transferType.GetProperty('ShouldHideOnClose', $instanceFlags).GetValue($transferWindow)) `
            'The waiting pickup code offers background transfer and closes to the background.'
    } finally {
        $operationField.SetValue($transferSession, $null)
        $codeProperty.SetValue($transferSession, $null)
        $activeTransfer.Dispose()
        $renderTransfer.Invoke($transferWindow, @())
    }

    $legacyFill = $legacy.Content.Background
    Invoke-Component $blank 'SelectTheme' @(3)
    $themeShell = [Windows.Application]::Current.Resources['CardTheme.Surface']
    $themeHeader = [Windows.Application]::Current.Resources['CardTheme.Chrome']
    $sharedCards = @($settings, $account, $catalog, $todoWindow, $todoEditor, $existingEditor)
    Assert-Migration ($blank.Content.Background -eq (Invoke-Component $themeBrushes 'Body' @($allThemes[3])) -and
        $themeShell -eq (Invoke-Component $themeBrushes 'Surface' @($allThemes[3])) -and
        @($sharedCards | Where-Object { $_.Content.Background -ne $themeShell }).Count -eq 0 -and
        $transferWindow.Content.Background -eq $themeShell -and $transferWindow.Content.Children[0].Background -eq $themeHeader) `
        'Switching a note theme immediately refreshes both WPF card families and file transfer.'
    Render-Window $todoWindow 'todos-gradient'
    $artEditor = $blank.FindName('Editor')
    $artEditor.Document.Blocks.Clear()
    $artEditor.Document.Blocks.Add([Windows.Documents.Paragraph]::new([Windows.Documents.Run]::new('本周计划')))
    $artCaption = $blank.FindName('TitleChrome')
    $artTools = $blank.FindName('ToolsChrome')
    $artLogo = $blank.FindName('SheepLogo')
    $artTitleRule = $blank.FindName('TitleRule')
    foreach ($artId in $artworkIds) {
        Invoke-Component $blank 'SelectTheme' @($artId)
        Render-Window $blank ('note-theme-' + $artId)
        $artBody = $blank.Content.Background
        $artBars = Invoke-Component $themeBrushes 'NoteBars' @($allThemes[$artId])
        Assert-Migration ($artBody -eq (Invoke-Component $themeBrushes 'Body' @($allThemes[$artId])) -and
            $artBody.Stretch -eq 'UniformToFill' -and $artBody.AlignmentY -eq 'Bottom' -and
            $artCaption.Background -eq $artBars.Item1 -and $artTools.Background -eq $artBars.Item2 -and
            $artBars.Item1 -is [Windows.Media.LinearGradientBrush] -and $artBars.Item2 -is [Windows.Media.LinearGradientBrush] -and
            $artBars.Item1.GradientStops[-1].Color.A -eq 0 -and $artBars.Item2.GradientStops[-1].Color.A -eq 0 -and
            $artTitleRule.Background -eq $artBars.Item3 -and $artTools.BorderBrush -eq $artBars.Item4 -and
            $artTitleRule.ActualWidth -eq $artCaption.ActualWidth -and $artTitleRule.ActualWidth -eq $artTools.ActualWidth -and
            $artBars.Item3.Color.A -gt 0 -and $artBars.Item3.Color.A -lt 50 -and $artBars.Item4.Color.A -eq $artBars.Item3.Color.A) `
            "Illustration $artId spans the rounded card with subtle full-width chrome dividers."
    }
    $lastArtTheme = $allThemes[$artworkIds[-1]]
    $artShell = [Windows.Application]::Current.Resources['CardTheme.Surface']
    Assert-Migration ($artShell -eq (Invoke-Component $themeBrushes 'Surface' @($lastArtTheme)) -and
        $artShell -is [Windows.Media.SolidColorBrush] -and
        @($sharedCards | Where-Object { $_.Content.Background -ne $artShell }).Count -eq 0 -and
        $transferWindow.Content.Background -eq $artShell -and $legacy.Content.Background -eq $legacyFill) `
        'Illustration palettes refresh shared cards while the other open note retains its own theme.'
    $barPalettes = @($allThemes | Where-Object { $_.Id -notin $artworkIds } |
        ForEach-Object { Invoke-Component $themeBrushes 'NoteBars' @($_) })
    $visibleBarPalettes = @($barPalettes | Where-Object {
        $_.Item1.Color.A -gt 0 -and $_.Item2.Color.A -gt 0 -and
        $_.Item3.Color.A -gt 0 -and $_.Item4.Color.A -gt 0
    })
    Assert-Migration ($visibleBarPalettes.Count -eq $barPalettes.Count) 'Gradient and solid themes retain their bar tints and dividers.'
    $artViewport = $artEditor.TranslatePoint([Windows.Point]::new(0, 0), $blank.Content)
    Assert-Migration ($artLogo.WoolTint -is [Windows.Media.SolidColorBrush] -and
        $artLogo.WoolTint.Color.R -ge 245 -and $artLogo.WoolTint.Color.R -gt $artLogo.WoolTint.Color.B -and
        $artLogo.OutlineTint.Color.R -lt 105 -and $artLogo.OutlineTint -eq $blank.Resources['NoteLogoOutlineBrush'] -and
        $artCaption.ActualHeight -eq $caption.ActualHeight) 'Illustration chrome keeps the original layout and matching logo colors.'
    $nightButtons = @($blank.FindName('BulletButton'), $blank.FindName('BoldButton'), $blank.FindName('StrikeButton'))
    $nightStates = Invoke-Component $themeBrushes 'ToolStates' @($lastArtTheme)
    foreach ($button in $nightButtons) { $button.IsChecked = $true }
    $blank.Topmost = $true
    $nightPin = $blank.FindName('PinButton')
    Render-Window $blank 'note-theme-selected-tools'
    Assert-Migration (@(@($nightPin) + $nightButtons | Where-Object {
        $_.Background -eq $nightStates.Item2 -and $_.BorderBrush.Color.A -eq 0 -and
        $_.Foreground -eq (Invoke-Component $themeBrushes 'Accent' @($lastArtTheme))
    }).Count -eq 4 -and $nightStates.Item1.Color.A -lt $nightStates.Item2.Color.A) `
        'Selected controls share the current theme tint, distinct from hover, with quiet borders.'
    foreach ($button in $nightButtons) { $button.IsChecked = $false }
    $blank.Topmost = $false
    Invoke-Component $blank 'SetChromeVisible' @($false)
    Render-Window $blank 'note-theme-inactive'
    Assert-Migration ($artCaption.Visibility -eq 'Hidden' -and $artTools.Visibility -eq 'Hidden' -and
        $blank.Content.Background -eq $artBody -and
        $artEditor.TranslatePoint([Windows.Point]::new(0, 0), $blank.Content) -eq $artViewport) `
        'Hiding illustration controls exposes the same background and never moves the editor.'
    $cardSize = [Windows.Size]::new($blank.Width, $blank.Height)
    [Windows.Media.VisualTreeHelper]::SetRootDpi($blank.Content, [Windows.DpiScale]::new(1.5, 1.5))
    $blank.Content.Measure($cardSize)
    $blank.Content.Arrange([Windows.Rect]::new($cardSize))
    $blank.Content.UpdateLayout()
    $pixelWidth = [int][Math]::Ceiling($cardSize.Width * 1.5)
    $pixelHeight = [int][Math]::Ceiling($cardSize.Height * 1.5)
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($pixelWidth, $pixelHeight, 144, 144, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($blank.Content)
    $pixels = [byte[]]::new($pixelWidth * $pixelHeight * 4)
    $bitmap.CopyPixels($pixels, $pixelWidth * 4, 0)
    $scanline = [int]($pixelHeight / 2) * $pixelWidth
    Assert-Migration ($pixels[4 * ($scanline + 1) + 3] -eq 255 -and $pixels[4 * ($scanline + $pixelWidth - 2) + 3] -eq 255 -and
        $pixels[3] -eq 0 -and $pixels[7] -eq 0) 'At 150% DPI, the artwork meets both side borders while the corners remain transparent.'
    [Windows.Media.VisualTreeHelper]::SetRootDpi($blank.Content, [Windows.DpiScale]::new(1, 1))
    Invoke-Component $blank 'SetChromeVisible' @($true)
    $codeInput.ContextMenu.ApplyTemplate() | Out-Null
    $codeMenuSurface = @(Find-Visuals $codeInput.ContextMenu ([Windows.Controls.Border]))[0]
    Assert-Migration ($toolSurface.Background -eq $artShell -and $codeMenuSurface.Background -eq $artShell) `
        'Already created tool popups and context menus refresh with the same palette.'
    Render-Window $settings 'settings-illustration-palette'
    foreach ($classicId in 10..13) {
        Invoke-Component $blank 'SelectTheme' @($classicId)
        Render-Window $blank ('note-classic-' + $classicId)
        $classic = $allThemes[$classicId]
        $classicBody = $blank.Content.Background
        $expectedBody = [Windows.Media.Color]::FromRgb($classic.Body.R, $classic.Body.G, $classic.Body.B)
        $expectedChrome = [Windows.Media.Color]::FromRgb($classic.Chrome.R, $classic.Chrome.G, $classic.Chrome.B)
        $classicSurface = Invoke-Component $themeBrushes 'Surface' @($classic)
        Assert-Migration ($classicBody -is [Windows.Media.SolidColorBrush] -and $classicBody.Color -eq $expectedBody -and
            $artCaption.Background.Color -eq $expectedChrome -and $artTools.Background -eq $classicBody -and
            $settings.Content.Background -eq $classicSurface -and $transferWindow.Content.Background -eq $classicSurface) `
            "Classic theme $classicId uses a solid body, matching chrome and matching auxiliary cards."
    }
    Invoke-Component ($assembly.GetType('KimNotes.Windows.Presentation.DesktopCardTheme', $true)) 'Apply' @(2)

    $closeButtonType = $assembly.GetType('KimNotes.Windows.Presentation.TitleCloseButton', $true)
    $titleCloseWindows = @($legacy, $catalog, $settings, $account, $todoWindow, $todoEditor, $transferWindow)
    $glyphs = @()
    foreach ($titleCloseWindow in $titleCloseWindows) {
        $buttons = @(Find-Visuals $titleCloseWindow.Content $closeButtonType)
        if ($buttons.Count -eq 1 -and $buttons[0].Width -eq 26 -and $buttons[0].Height -eq 26) {
            $buttons[0].ApplyTemplate() | Out-Null
            $glyph = $buttons[0].Template.FindName('Glyph', $buttons[0])
            if ($glyph -and $glyph.StrokeThickness -eq 1.7) { $glyphs += $glyph.Data.ToString() }
        }
    }
    Assert-Migration ($glyphs.Count -eq $titleCloseWindows.Count -and @($glyphs | Select-Object -Unique).Count -eq 1) `
        'The WPF card title bars all share the same close glyph and size.'

    $today = @(Invoke-Component $todoStore 'TakeDueAt' @($dueTime))
    Assert-Migration ($today.Count -eq 1 -and $today[0].Id -eq $todo.Id) 'The migrated client reads due reminders from the shared store.'
    $todoFile = Join-Path $testDirectory 'todos.json'
    $beforePrivacy = [IO.File]::ReadAllText($todoFile)
    Invoke-Component $config 'SetConfigValue' @('checkBox3', 'True')
    Invoke-Component $cloud 'ApplyPrivacyMode'
    Invoke-Component $todoStore 'ResetForPrivacyChange'
    Assert-Migration (@(Invoke-Component $todoStore 'Active').Count -eq 0) 'Switching into private mode clears the normal todo cache.'
    $null = Invoke-Component $todoStore 'Add' @('Private todo', $null, $null, $null)
    Assert-Migration ([IO.File]::ReadAllText($todoFile) -eq $beforePrivacy) 'Private-mode tasks stay out of the persisted todo file.'
    Invoke-Component $config 'SetConfigValue' @('checkBox3', 'False')
    Invoke-Component $cloud 'ApplyPrivacyMode'
    Invoke-Component $todoStore 'ResetForPrivacyChange'
    $normalAgain = @(Invoke-Component $todoStore 'Active')
    Assert-Migration ($normalAgain.Count -eq 1 -and $normalAgain[0].Text -eq 'Migration test todo') 'Leaving private mode restores the normal todo data.'

    $cloudStore = $assembly.GetType('KimNotes.utils.CloudStore', $true)
    $ownerField = $cloudStore.GetField('UserId', $staticFlags)
    $owner = [Guid]::NewGuid().ToString()
    $ownerField.SetValue($null, $owner)
    $entry = New-Component 'KimNotes.utils.TodoCloudEntry'
    $entry.Id = [Guid]::NewGuid().ToString()
    $entry.Text = 'First cloud copy'
    $entry.CreatedAt = [DateTime]::UtcNow
    $entries = [Array]::CreateInstance($entry.GetType(), 1)
    $entries.SetValue($entry, 0)
    $merge = $todoStore.GetMethod('MergeDownloaded', $staticFlags)
    $merge.Invoke($null, @($owner, $entries))
    $merge.Invoke($null, @($owner, $entries))
    Invoke-Component $todoStore 'ResetForPrivacyChange'
    $cloudCopies = @(Invoke-Component $todoStore 'Active' | Where-Object Text -eq $entry.Text)
    Assert-Migration ($cloudCopies.Count -eq 1) 'A cloud task from the deployed revisionless response persists locally without duplicating.'
    $ownerField.SetValue($null, [Guid]::NewGuid().ToString())
    Assert-Migration (@(Invoke-Component $todoStore 'Active').Count -eq 1) 'Downloaded tasks belong only to their original account.'
    $ownerField.SetValue($null, $owner)
    Invoke-Component $todoStore 'SetText' @($cloudCopies[0], 'Changed while offline')
    Assert-Migration (Invoke-Component $todoStore 'HasPendingLegacySync' @($owner)) 'A revisionless task keeps edits locally until the server can version them.'
    $entry.Revision = 3
    $merge.Invoke($null, @($owner, $entries))
    Assert-Migration ((Invoke-Component $todoStore 'Find' @($cloudCopies[0].Id)).Text -eq 'Changed while offline' -and
        -not (Invoke-Component $todoStore 'HasPendingLegacySync' @($owner))) 'A versioned response makes the local edit eligible to upload and preserves its text.'
    $entry.Revision = 5
    $entry.DeletedAt = [DateTime]::UtcNow
    $merge.Invoke($null, @($owner, $entries))
    Assert-Migration ($null -eq (Invoke-Component $todoStore 'Find' @($cloudCopies[0].Id)) -and
        [IO.File]::ReadAllText($todoFile).Contains('Changed while offline')) 'A remote tombstone hides the task while preserving its persisted contents.'

    $normal = [Enum]::Parse($priorityType, 'Normal')
    $urgent = [Enum]::Parse($priorityType, 'Urgent')
    $pinnedTask = Invoke-Component $todoStore 'AddDetailed' @('Pinned task', $null, $noRepeat, $normal, 'https://example.test/task', $true, $null, $null)
    $urgentTask = Invoke-Component $todoStore 'AddDetailed' @('Urgent task', $null, $noRepeat, $urgent, $null, $false, $null, $null)
    Invoke-Component $todoStore 'ResetForPrivacyChange'
    $ordered = @(Invoke-Component $todoStore 'Active')
    Assert-Migration ($ordered[0].Id -eq $pinnedTask.Id -and $ordered[1].Id -eq $urgentTask.Id -and
        $ordered[0].Pinned -and $ordered[0].Notes -eq 'https://example.test/task' -and $ordered[1].Priority -eq $urgent) `
        'Pinned tasks, priority and notes persist and control the sort after restarting.'
    $legacyRecords = [Newtonsoft.Json.Linq.JArray]::Parse([IO.File]::ReadAllText($todoFile))
    foreach ($record in $legacyRecords) {
        foreach ($field in @('Priority','Notes','Pinned','CloudDetailsPending','SavedWithDetails')) {
            $null = $record.Remove($field)
        }
    }
    [IO.File]::WriteAllText($todoFile, $legacyRecords.ToString())
    Invoke-Component $todoStore 'ResetForPrivacyChange'
    $ordered = @(Invoke-Component $todoStore 'Active')
    Assert-Migration ($ordered[0].Pinned -and $ordered[0].Notes -eq 'https://example.test/task' -and $ordered[1].Priority -eq $urgent) `
        'New task details survive a rewrite by the released client.'
    $pending = $todoStore.GetMethod('PrepareCloudChanges', $staticFlags).Invoke($null, @($owner, $false))
    $ack = $todoStore.GetMethod('AcknowledgeCloudChanges', $staticFlags)
    $ack.Invoke($null, @($owner, $pending, $false))
    Assert-Migration (-not (Invoke-Component $todoStore 'HasPendingCloudChanges' @($owner, $false)) -and
        (Invoke-Component $todoStore 'HasPendingCloudChanges' @($owner, $true))) `
        'A legacy acknowledgement does not discard details that only a newer server can accept.'
    $pending = $todoStore.GetMethod('PrepareCloudChanges', $staticFlags).Invoke($null, @($owner, $true))
    $ack.Invoke($null, @($owner, $pending, $true))
    Assert-Migration (-not (Invoke-Component $todoStore 'HasPendingCloudChanges' @($owner, $true))) `
        'Detailed acknowledgements settle the pending task metadata.'
    Invoke-Component $todoStore 'Delete' @($ordered[1])
    Assert-Migration ($null -eq (Invoke-Component $todoStore 'Find' @($urgentTask.Id)) -and
        [IO.File]::ReadAllText($todoFile).Contains('Urgent task')) 'Deleting an unfinished task hides it and keeps its data.'

    $todoBody = "Overdue task`nSecond line`nThird line`nFourth line`nFifth line"
    $pastDue = Invoke-Component $todoStore 'AddDetailed' @($todoBody, (Get-Date).AddHours(-2), $noRepeat, $normal, $null, $false, $null, $null)
    $done = Invoke-Component $todoStore 'Add' @('Completed task', $null, $null, $null)
    Invoke-Component $todoStore 'SetDone' @($done, $true)
    $todoCatalog = New-Component 'KimNotes.Windows.Todos.TodoWindow' @($null)
    Render-Window $todoCatalog 'todo-list'
    $todoCatalogType = $todoCatalog.GetType()
    $todoCards = $todoCatalogType.GetField('visibleCards', $instanceFlags).GetValue($todoCatalog)
    $todoCount = $todoCatalogType.GetField('entryCount', $instanceFlags).GetValue($todoCatalog)
    Assert-Migration ($todoCount.Text -eq '3 条待办' -and $todoCards.Count -eq 4) 'The todo search counter counts active items only while excluding completed items from the count.'
    $pastDueCard = $todoCards[$pastDue.Id]
    $overdueCopy = @(Find-Visuals $pastDueCard ([Windows.Controls.TextBlock]) | Where-Object Text -like '*过期*')
    Assert-Migration ($overdueCopy.Count -eq 1 -and $overdueCopy[0].Text -eq '过期') 'The overdue badge displays only the overdue status.'
    $bodyCopy = @(Find-Visuals $pastDueCard ([Windows.Controls.TextBlock]) | Where-Object Text -eq $todoBody)
    Assert-Migration ($bodyCopy.Count -eq 1 -and $bodyCopy[0].TextWrapping -eq 'Wrap' -and
        [Math]::Abs($bodyCopy[0].ActualHeight - 4 * $bodyCopy[0].LineHeight) -le 1) 'A todo displays its text as one body with at most four lines.'
    $todoMore = @(Find-Visuals $pastDueCard ([Windows.Controls.Button]) | Where-Object ContextMenu)
    $todoComplete = @(Find-Visuals $pastDueCard ([Windows.Controls.Button]) | Where-Object {
        [Windows.Automation.AutomationProperties]::GetName($_) -eq '完成待办'
    })
    Assert-Migration ($todoComplete.Count -eq 1 -and [Object]::ReferenceEquals($todoComplete[0].Template, $todoMore[0].Template) -and
        @($todoMore[0].Template.Triggers | Where-Object Property -eq ([Windows.UIElement]::IsMouseOverProperty)).Count -eq 0) `
        'The completion button and ellipsis share quiet chrome without a hover outline.'
    $todoTimestamp = $overdueCopy[0]
    $timestampInk = $todoTimestamp.Foreground
    $bodyWidth = $bodyCopy[0].ActualWidth
    Assert-Migration ($todoMore.Count -eq 1 -and $todoMore[0].Visibility -eq 'Hidden' -and
        $todoTimestamp.Visibility -eq 'Visible' -and
        @(Find-Visuals $pastDueCard ([Windows.Controls.Button]) | Where-Object Content -eq $todoTimestamp).Count -eq 0) `
        'A todo shows a plain timestamp label at rest and keeps the menu button hidden.'
    Invoke-Component $todoCatalog 'OnCardSelected' @($pastDue.Id)
    Assert-Migration ($todoMore[0].Visibility -eq 'Hidden' -and $todoTimestamp.Visibility -eq 'Visible' -and
        $todoTimestamp.Foreground -eq $timestampInk) 'Selecting a todo alone neither reveals its menu nor changes the timestamp color.'
    Invoke-Component $pastDueCard 'SetActionsVisible' @($true)
    Render-Window $todoCatalog 'todo-list-selected'
    Assert-Migration ($todoMore[0].Visibility -eq 'Visible' -and $todoTimestamp.Visibility -eq 'Hidden' -and
        $bodyCopy[0].ActualWidth -eq $bodyWidth) 'Showing actions swaps the timestamp for the menu button without reflowing the todo body.'
    $todoMenu = @(Find-Visuals $pastDueCard ([Windows.Controls.Button]) | Where-Object ContextMenu | Select-Object -ExpandProperty ContextMenu)
    Assert-Migration ($todoMenu.Count -eq 1 -and $todoMenu[0].Items.Count -eq 2 -and
        @($todoMenu[0].Items | Where-Object { $_ -is [Windows.Controls.MenuItem] -and $_.MinHeight -eq 21 }).Count -eq 2) `
        'A selected todo exposes compact edit and delete commands.'
    Invoke-Component $pastDueCard 'SetActionsVisible' @($false)
    Assert-Migration ($todoMore[0].Visibility -eq 'Hidden' -and $todoTimestamp.Visibility -eq 'Visible' -and
        $todoTimestamp.Foreground -eq $timestampInk) 'Hiding actions restores the same timestamp appearance.'
    $doneCard = $todoCards[$done.Id]
    $selectedFill = $pastDueCard.Background
    $doneFill = $doneCard.Background
    $mouseEnter = [Windows.Input.MouseEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0)
    $mouseEnter.RoutedEvent = [Windows.UIElement]::MouseEnterEvent
    $doneCard.RaiseEvent($mouseEnter)
    Assert-Migration ($doneCard.Background -eq $selectedFill -and $pastDueCard.Background -ne $selectedFill) `
        'Hover highlights the current todo without clicking.'
    $mouseLeave = [Windows.Input.MouseEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0)
    $mouseLeave.RoutedEvent = [Windows.UIElement]::MouseLeaveEvent
    $doneCard.RaiseEvent($mouseLeave)
    Assert-Migration ($doneCard.Background -eq $doneFill -and $pastDueCard.Background -eq $selectedFill) `
        'Leaving a todo restores the selection.'
    $todoSearch = $todoCatalogType.GetField('search', $instanceFlags).GetValue($todoCatalog)
    $todoSearchPill = $todoSearch.Parent.Parent.Parent
    Assert-Migration ($todoCount.Parent -eq $todoSearchPill.Child -and $todoSearchPill.Height -eq $catalogSearchPill.Height -and
        $todoSearchPill.BorderBrush -eq $catalogSearchPill.BorderBrush -and $null -eq $todoSearch.FocusVisualStyle) `
        'Todo and catalog search use the same themed surface, with counters inside both fields.'
    $todoSearch.Text = 'Completed task'
    Assert-Migration ($todoCount.Text -eq '0 条待办' -and $todoCards.Count -eq 1 -and $todoCards.ContainsKey($done.Id)) `
        'A completed-only search still shows the matching card while the active count is zero.'
    $todoSearch.Clear()
    $catalogSearch.Clear()
    foreach ($theme in $allThemes) {
        Invoke-Component $desktopCardTheme 'Apply' @($theme.Id)
        $inputFill = $app.Resources['CardTheme.SearchSurface']
        $inputEdge = $app.Resources['CardTheme.SearchBorder']
        Assert-Migration ($catalogSearchPill.Background -eq $inputFill -and $todoSearchPill.Background -eq $inputFill -and
            $catalogSearchPill.BorderBrush -eq $inputEdge -and $todoSearchPill.BorderBrush -eq $inputEdge -and
            $inputFill.Color -ne $inputEdge.Color -and $inputFill -ne $app.Resources['CardTheme.Surface']) `
            "The $($theme.Name) search field updates independently of the window surface."
        if ($theme.Id -in 3, 9) {
            Render-Window $catalog ('catalog-search-' + $theme.Id)
            Render-Window $todoCatalog ('todo-search-' + $theme.Id)
        }
    }
    Invoke-Component $desktopCardTheme 'Apply' @(2)

    Add-Type -TypeDefinition @'
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

public sealed class KimNotesPrivateSyncProbe : HttpMessageHandler
{
    public int Requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
    }
}
'@ -ReferencedAssemblies 'System.Net.Http'
    Invoke-Component $config 'SetConfigValue' @('checkBox3', 'True')
    $probe = [KimNotesPrivateSyncProbe]::new()
    $privateApi = New-Component 'KimNotes.utils.CloudApi' @($probe)
    $privateSync = New-Component 'KimNotes.utils.CloudSync' @($privateApi, $true, $null)
    $privateType = $privateSync.GetType()
    $privateTimer = $privateType.GetField('timer', $instanceFlags).GetValue($privateSync)
    $privateLogin = New-Component 'KimNotes.utils.CloudSession'
    $privateLogin.UserId = [Guid]::NewGuid().ToString()
    $privateType.GetProperty('Session', $instanceFlags).SetValue($privateSync, $privateLogin)
    $privateAccount = New-Component 'KimNotes.Windows.Settings.AccountWindow' @($privateSync)
    $syncButton = $privateAccount.GetType().GetField('synchronize', $instanceFlags).GetValue($privateAccount)
    $null = (Invoke-Component $privateSync 'SyncNow' @($true)).GetAwaiter().GetResult()
    Assert-Migration (-not $privateTimer.IsEnabled -and -not $syncButton.IsEnabled -and $probe.Requests -eq 0) `
        'Private mode disables scheduled and manual sync, even with a saved session, without sending a request.'
    Invoke-Component $config 'SetConfigValue' @('checkBox3', 'False')
    Invoke-Component $privateSync 'ApplyPrivacyMode'
    $privateAccount.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Background)
    Assert-Migration ($privateTimer.IsEnabled -and $syncButton.IsEnabled) 'Leaving private mode restores normal sync controls and scheduling.'
    Assert-Migration ($blank.SaveCurrentNoteSafe() -and $null -ne $blank.FilePath) 'A note using a classic theme saves normally.'
    $classicReopened = New-Component 'KimNotes.Windows.Notes.NoteWindow'
    Invoke-Component $classicReopened 'Connect' @($notes, $blank.FilePath, $null)
    Assert-Migration ($classicReopened.Content.Background -eq (Invoke-Component $themeBrushes 'Body' @($allThemes[13]))) `
        'Reopening a saved note restores the selected classic theme.'
    foreach ($retiredId in 7, 8) {
        Invoke-Component $blank 'SelectTheme' @($retiredId)
        Assert-Migration ($blank.SaveCurrentNoteSafe()) "A note using retired theme $retiredId saves normally."
        $retiredReopened = New-Component 'KimNotes.Windows.Notes.NoteWindow'
        Invoke-Component $retiredReopened 'Connect' @($notes, $blank.FilePath, $null)
        Assert-Migration ($retiredReopened.Content.Background -eq (Invoke-Component $themeBrushes 'Body' @($allThemes[$retiredId]))) `
            "A previously saved note still restores retired theme $retiredId."
    }
}
finally {
    if ($privateSync) { $privateSync.Dispose() }
    if ($transferSession) { $transferSession.Dispose() }
    if ($reception) { $reception.Dispose() }
    if ($cloud) { $cloud.Dispose() }
    [Environment]::SetEnvironmentVariable('KIMNOTES_DATA_DIR', $oldDataDirectory)
    if ([IO.Directory]::Exists($testDirectory) -and
        [IO.Path]::GetFullPath($testDirectory).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($testDirectory).StartsWith('KimNotes-wpf-migration-')) {
        Remove-Item -LiteralPath $testDirectory -Recurse -Force
    }
}

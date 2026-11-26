param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this check using Windows PowerShell with -Sta.'
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$app = [Activator]::CreateInstance($assembly.GetType('KimNotes.Windows.App', $true))
$window = [Activator]::CreateInstance($assembly.GetType('KimNotes.Windows.Notes.NoteWindow', $true), $true)

function Assert-Preview([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    Write-Output $Description
}

$methods = [Reflection.BindingFlags]'Static, NonPublic'
$rtf = $assembly.GetType('KimNotes.Windows.Notes.NoteDocumentRtf', $true)
$read = $rtf.GetMethod('Read', $methods)
$write = $rtf.GetMethod('Write', $methods)
$editor = $window.FindName('Editor')
$body = $window.Content

Assert-Preview ($window.AllowsTransparency -and $window.Width -eq 301 -and $window.Height -eq 401) 'WPF window uses a transparent outline and the baseline dimensions.'

$layoutSize = [Windows.Size]::new($window.Width, $window.Height)
[Windows.Media.VisualTreeHelper]::SetRootDpi($body, [Windows.DpiScale]::new(1, 1))
$body.Measure($layoutSize)
$body.Arrange([Windows.Rect]::new($layoutSize))
$body.UpdateLayout()
$title = $window.FindName('TitleChrome')
$titleRule = $window.FindName('TitleRule')
$tools = $window.FindName('ToolsChrome')
$actions = $tools.Child.Children
$firstAction = $actions[0]
$lastAction = $actions[$actions.Count - 1]
$firstCenter = $firstAction.TranslatePoint([Windows.Point]::new($firstAction.ActualWidth / 2, $firstAction.ActualHeight / 2), $tools)
$lastCenter = $lastAction.TranslatePoint([Windows.Point]::new($lastAction.ActualWidth / 2, $lastAction.ActualHeight / 2), $tools)
Assert-Preview ($actions.Count -eq 9 -and $lastAction -eq $window.FindName('DesktopToolsButton') -and $title.ActualHeight -eq $tools.ActualHeight -and
    [Math]::Abs($firstCenter.X + $lastCenter.X - $tools.ActualWidth) -le 1 -and $firstCenter.Y -eq $lastCenter.Y) `
    'The nine actions fit the note toolbar, with Desktop Tools on the right.'
Assert-Preview ($titleRule.ActualWidth -eq $title.ActualWidth -and $tools.ActualWidth -eq $title.ActualWidth -and
    $titleRule.Margin.Left -eq 0 -and $titleRule.Margin.Right -eq 0 -and
    $tools.Margin.Left -eq 0 -and $tools.Margin.Right -eq 0 -and $tools.BorderThickness.Top -eq 1) `
    'The title and tool dividers meet the inner edges of the outer frame.'

$instanceMethods = [Reflection.BindingFlags]'Instance, NonPublic'
$setChrome = $window.GetType().GetMethod('SetChromeVisible', $instanceMethods)
$viewportOrigin = $editor.TranslatePoint([Windows.Point]::new(0, 0), $body)
$viewportHeight = $editor.ActualHeight
$setChrome.Invoke($window, @($false))
$body.UpdateLayout()
Assert-Preview ($title.Visibility -eq [Windows.Visibility]::Hidden -and $tools.Visibility -eq [Windows.Visibility]::Hidden -and
    $editor.TranslatePoint([Windows.Point]::new(0, 0), $body) -eq $viewportOrigin -and $editor.ActualHeight -eq $viewportHeight) `
    'Idle chrome hides while preserving the editor origin and viewport size.'
$setChrome.Invoke($window, @($true))
$body.UpdateLayout()
Assert-Preview ($title.Visibility -eq [Windows.Visibility]::Visible -and $tools.Visibility -eq [Windows.Visibility]::Visible -and
    $editor.TranslatePoint([Windows.Point]::new(0, 0), $body) -eq $viewportOrigin -and $editor.ActualHeight -eq $viewportHeight) `
    'Revealing both bars leaves the editor in the same place.'

$menuFactory = $assembly.GetType('KimNotes.Windows.Runtime.DesktopTrayMenu', $true)
$menuCalls = [Collections.Generic.List[string]]::new()
$openAction = [Action]({ $menuCalls.Add('first') }.GetNewClosure())
$menuCommands = [ValueTuple[string,Action][]]::new(2)
$menuCommands[0] = [ValueTuple[string,Action]]::new('First action', $openAction)
$menuCommands[1] = [ValueTuple[string,Action]]::new('Second action', [Action]{ })
$trayMenu = $menuFactory.GetMethod('Create', $methods).Invoke($null, (,$menuCommands))
$trayMenu.Measure([Windows.Size]::new(160, 300))
$trayMenu.Arrange([Windows.Rect]::new($trayMenu.DesiredSize))
$trayMenu.UpdateLayout()
$card = [Windows.Media.VisualTreeHelper]::GetChild($trayMenu, 0)
$entry = $trayMenu.Items[0]
$row = [Windows.Media.VisualTreeHelper]::GetChild($entry, 0)
Assert-Preview ($trayMenu.Placement -eq [Windows.Controls.Primitives.PlacementMode]::MousePoint -and
    $card -is [Windows.Controls.Border] -and $card.CornerRadius.TopLeft -gt 0 -and
    $row -is [Windows.Controls.Border] -and $row.Padding.Left -le 10 -and
    $row.Child -is [Windows.Controls.ContentPresenter]) 'The tray stays a rounded, mouse-positioned menu with no icon gutter.'
$entry.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.MenuItem]::ClickEvent, $entry))
Assert-Preview (($menuCalls -join ',') -eq 'first') 'The tray still dispatches its command.'

$trayType = $assembly.GetType('KimNotes.Windows.Runtime.DesktopTray', $true)
$dismiss = $trayType.GetMethod('ShouldDismiss', $methods)
$popup = [IntPtr]::new(12)
$outside = [IntPtr]::new(18)
Assert-Preview ($dismiss.Invoke($null, @(0x0006, [IntPtr]::Zero, $outside, $popup)) -and
    $dismiss.Invoke($null, @(0x001C, [IntPtr]::Zero, $outside, $popup)) -and
    $dismiss.Invoke($null, @(0x001F, [IntPtr]::Zero, [IntPtr]::Zero, $popup)) -and
    -not $dismiss.Invoke($null, @(0x0006, [IntPtr]::Zero, $popup, $popup))) `
    'The tray menu dismisses on outside activation or cancel mode and stays open when focus enters the popup itself.'

$bulletButton = $window.FindName('BulletButton')
$firstParagraph = $editor.Document.Blocks.FirstBlock
$firstParagraph.Inlines.Add([Windows.Documents.Run]::new('1111'))
$editor.CaretPosition = $firstParagraph.ContentEnd.GetInsertionPosition([Windows.Documents.LogicalDirection]::Backward)
$body.UpdateLayout()
$plainPosition = $firstParagraph.ContentStart.GetCharacterRect([Windows.Documents.LogicalDirection]::Forward).X
$bulletClick = [Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $bulletButton)
$bulletButton.RaiseEvent($bulletClick)
$compactList = $editor.Document.Blocks.FirstBlock
Assert-Preview ($bulletButton.IsChecked -and $compactList -is [Windows.Documents.List] -and $compactList.Padding.Left -eq 10 -and $compactList.MarkerOffset -eq 4) 'Clicking bullets applies the compact list style and selects the button.'
$body.UpdateLayout()
$listedParagraph = $compactList.ListItems.FirstListItem.Blocks.FirstBlock
$listPosition = $listedParagraph.ContentStart.GetCharacterRect([Windows.Documents.LogicalDirection]::Forward).X
Assert-Preview ([Math]::Abs($listPosition - $plainPosition - 10) -lt 0.75) 'The bullet text starts ten DIPs after the plain-text position.'
$editor.CaretPosition = $editor.CaretPosition.InsertParagraphBreak()
Assert-Preview ($compactList.ListItems.Count -eq 2 -and $bulletButton.IsChecked) 'A paragraph break continues the compact bullet list.'
[void]$editor.Undo()
$bulletButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $bulletButton))
$body.UpdateLayout()
$restored = $editor.Document.Blocks.FirstBlock
Assert-Preview (-not $bulletButton.IsChecked -and $restored -is [Windows.Documents.Paragraph] -and $restored.Inlines.FirstInline.Text -eq '1111' -and [Math]::Abs($restored.ContentStart.GetCharacterRect([Windows.Documents.LogicalDirection]::Forward).X - $plainPosition) -lt 0.75) 'Turning bullets off restores the original text and placement.'

$editor.Document = [Windows.Documents.FlowDocument]::new([Windows.Documents.Paragraph]::new([Windows.Documents.Run]::new('Source')))
$source = $editor.Document.Blocks.FirstBlock
$sourceEnd = $source.ContentEnd.GetInsertionPosition([Windows.Documents.LogicalDirection]::Backward)
$nextLine = $sourceEnd.InsertParagraphBreak()
$nextLine.InsertTextInRun('Next')
$body.UpdateLayout()
$firstLineRect = $source.ContentStart.GetCharacterRect([Windows.Documents.LogicalDirection]::Forward)
$nextLineRect = $editor.Document.Blocks.LastBlock.ContentStart.GetCharacterRect([Windows.Documents.LogicalDirection]::Forward)
Assert-Preview ([Math]::Abs(($nextLineRect.Y - $firstLineRect.Y) - $firstLineRect.Height) -le 1) 'Enter advances by one line without an extra paragraph gap.'

$sourceEnd = $source.ContentEnd.GetInsertionPosition([Windows.Documents.LogicalDirection]::Backward)
$window.GetType().GetMethod('InsertTranslation', $instanceMethods).Invoke($window, @($sourceEnd, "First`r`nSecond"))
$body.UpdateLayout()
$sourceText = [Windows.Documents.TextRange]::new($source.ContentStart, $source.ContentEnd).Text
$endRect = $editor.CaretPosition.GetCharacterRect([Windows.Documents.LogicalDirection]::Backward)
Assert-Preview ($sourceText -eq "Source`r`nFirst`r`nSecond" -and $editor.Document.Blocks.Count -eq 2 -and
    [Math]::Abs(($endRect.Y - $firstLineRect.Y) - 2 * $firstLineRect.Height) -le 2) 'Translation keeps the original text and inserts one visual break for each translated line.'

$paragraph = [Windows.Documents.Paragraph]::new()
$first = [Windows.Documents.Run]::new('Review ')
$first.FontWeight = [Windows.FontWeights]::Bold
$first.TextDecorations = [Windows.TextDecorations]::Underline
$second = [Windows.Documents.Run]::new('the notes')
$paragraph.Inlines.Add($first)
$paragraph.Inlines.Add($second)
$document = [Windows.Documents.FlowDocument]::new()
$document.Blocks.Add($paragraph)
$editor.Document = $document
$boldButton = $window.FindName('BoldButton')
$strikeButton = $window.FindName('StrikeButton')
$boldClick = [Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $boldButton)
$strikeClick = [Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $strikeButton)
$forward = [Windows.Documents.LogicalDirection]::Forward
$editor.CaretPosition = $first.ContentStart.GetPositionAtOffset(2, $forward)
Assert-Preview ($boldButton.IsChecked -and -not $strikeButton.IsChecked) 'The toolbar follows the formatting at the caret.'
$editor.CaretPosition = $second.ContentStart.GetPositionAtOffset(2, $forward)
Assert-Preview (-not $boldButton.IsChecked) 'Moving the caret to normal text clears the bold state.'
[Windows.Documents.EditingCommands]::ToggleBold.Execute($null, $editor)
Assert-Preview ($boldButton.IsChecked) 'The bold editing command updates the toolbar without moving the caret.'
[Windows.Documents.EditingCommands]::ToggleBold.Execute($null, $editor)
Assert-Preview (-not $boldButton.IsChecked) 'Running the bold command again clears the toolbar state.'
$boldButton.RaiseEvent($boldClick)
Assert-Preview ($boldButton.IsChecked) 'Clicking bold applies the same editing command.'
$boldButton.RaiseEvent($boldClick)
$strikeButton.RaiseEvent($strikeClick)
Assert-Preview ($strikeButton.IsChecked) 'Clicking strikethrough at the caret updates the toolbar immediately.'
$strikeButton.RaiseEvent($strikeClick)
$editor.Selection.Select($first.ContentStart, $second.ContentEnd)
Assert-Preview (-not $boldButton.IsChecked -and -not $strikeButton.IsChecked) 'A mixed bold selection remains unchecked.'

$format = $assembly.GetType('KimNotes.Windows.Notes.NoteFormatting', $true)
$toggle = $format.GetMethod('ToggleStrikethrough', $methods)
$strikeButton.RaiseEvent($strikeClick)
function Get-Decorations($paragraph) {
    @($paragraph.Inlines | ForEach-Object { @($_.TextDecorations | ForEach-Object { $_.Location }) -join ',' })
}
$style = Get-Decorations $paragraph
Assert-Preview (($style -join '|') -eq 'Underline,Strikethrough|Strikethrough') 'Strikethrough preserves an existing underline across a mixed selection.'
Assert-Preview ($strikeButton.IsChecked) 'A selection with mixed underline still shows its common strikethrough.'
$strikeButton.RaiseEvent($strikeClick)
$style = Get-Decorations $paragraph
Assert-Preview (($style -join '|') -eq 'Underline|') 'A second toggle removes only strikethrough.'
Assert-Preview (-not $strikeButton.IsChecked) 'The strikethrough state clears with the formatting.'
$toggle.Invoke($null, @($editor))

$list = [Windows.Documents.List]::new()
$list.MarkerStyle = [Windows.TextMarkerStyle]::Disc
$chinese = -join ([char]0x4fbf, [char]0x7b7e, [char]0x6d4b, [char]0x8bd5)
$list.ListItems.Add([Windows.Documents.ListItem]::new([Windows.Documents.Paragraph]::new([Windows.Documents.Run]::new($chinese))))
$document.Blocks.Add($list)

$visual = [Windows.Media.DrawingVisual]::new()
$draw = $visual.RenderOpen()
$draw.DrawRoundedRectangle([Windows.Media.Brushes]::SteelBlue, $null, [Windows.Rect]::new(0, 0, 72, 30), 5, 5)
$draw.DrawEllipse([Windows.Media.Brushes]::White, $null, [Windows.Point]::new(20, 15), 6, 6)
$draw.Close()
$thumbnail = [Windows.Media.Imaging.RenderTargetBitmap]::new(72, 30, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$thumbnail.Render($visual)
$image = [Windows.Controls.Image]::new()
$image.Source = $thumbnail
$image.Width = $thumbnail.Width
$document.Blocks.Add([Windows.Documents.Paragraph]::new([Windows.Documents.InlineUIContainer]::new($image)))

$stream = [IO.MemoryStream]::new()
try {
    $write.Invoke($null, @($document, $stream))
    $fixture = $stream.ToArray()
    $stream.Position = 0
    $loaded = $read.Invoke($null, @($stream))
    $text = [Windows.Documents.TextRange]::new($loaded.ContentStart, $loaded.ContentEnd).Text
    Assert-Preview ($text.Contains($chinese) -and $text.Contains('Review the notes')) 'RTF round trip retains English and Chinese text.'
    $blocks = @($loaded.Blocks)
    $styled = $blocks[0].Inlines.FirstInline
    $imageParagraph = $blocks[-1]
    $inlineImage = $imageParagraph.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] }
    Assert-Preview ($styled.FontWeight -eq [Windows.FontWeights]::Bold -and $styled.TextDecorations.Count -eq 2 -and $blocks[1] -is [Windows.Documents.List]) 'RTF round trip retains bold, underline, strikethrough and a bullet list.'
    Assert-Preview ($inlineImage.Child -is [Windows.Controls.Image] -and $inlineImage.Child.Source.PixelWidth -eq 72) 'RTF round trip retains an embedded image.'
    $document.PagePadding = [Windows.Thickness]::new(0)
}
finally {
    $stream.Dispose()
}

$logo = $window.FindName('SheepLogo')
$logoFrames = @{ 96 = 24; 120 = 24; 144 = 32; 192 = 48 }
foreach ($dpi in 96, 120, 144, 192) {
    $scale = $dpi / 96
    [Windows.Media.VisualTreeHelper]::SetRootDpi($body, [Windows.DpiScale]::new($scale, $scale))
    $body.Measure($layoutSize)
    $body.Arrange([Windows.Rect]::new($layoutSize))
    $body.UpdateLayout()
    $selectedLogoFrame = $logo.Source.PixelWidth
    Assert-Preview ($selectedLogoFrame -eq $logoFrames[$dpi]) "Title logo frame at $($scale * 100)% is '$selectedLogoFrame' px (expected $($logoFrames[$dpi]) px)."
    $width = [int][Math]::Ceiling($layoutSize.Width * $dpi / 96)
    $height = [int][Math]::Ceiling($layoutSize.Height * $dpi / 96)
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, $dpi, $dpi, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($body)
    $pixels = [byte[]]::new($width * $height * 4)
    $bitmap.CopyPixels($pixels, 4 * $width, 0)
    $allCornersSmooth = $true
    foreach ($corner in @(@(0, 0), @(1, 0), @(0, 1), @(1, 1))) {
        $cornerX = $corner[0] * ($width - 1)
        $cornerY = $corner[1] * ($height - 1)
        $stepX = 1 - 2 * $corner[0]
        $stepY = 1 - 2 * $corner[1]
        $hasAntialiasing = $false
        for ($y = 0; $y -lt 9 * $dpi / 96; $y++) {
            for ($x = 0; $x -lt 9 * $dpi / 96; $x++) {
                $alpha = $pixels[4 * (($cornerY + $stepY * $y) * $width + $cornerX + $stepX * $x) + 3]
                if ($alpha -gt 0 -and $alpha -lt 255) { $hasAntialiasing = $true }
            }
        }
        $tipAlpha = $pixels[4 * ($cornerY * $width + $cornerX) + 3]
        $allCornersSmooth = $allCornersSmooth -and $tipAlpha -eq 0 -and $hasAntialiasing
    }
    Assert-Preview $allCornersSmooth "All four corners have antialiased edges at $($dpi / 96 * 100)%."
    if ($OutputDirectory -and $dpi -eq 144) {
        [IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'KimNotes-Wpf-sample.rtf'), $fixture)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $output = [IO.File]::Create((Join-Path $OutputDirectory 'KimNotes-WpfPreview-stage1.png'))
        try { $encoder.Save($output) }
        finally { $output.Dispose() }
    }
}

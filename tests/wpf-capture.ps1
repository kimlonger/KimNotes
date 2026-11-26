param([Parameter(Mandatory = $true)][string]$Executable)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run this check using Windows PowerShell with -Sta.'
}
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Drawing, System.Net.Http, System.Web
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$instance = [Reflection.BindingFlags]'Instance, NonPublic'
$static = [Reflection.BindingFlags]'Static, NonPublic'

function Assert-Capture([bool]$Condition, [string]$Description) {
    if (-not $Condition) { throw $Description }
    Write-Output $Description
}

function New-Frame([int]$Width, [int]$Height, [byte]$Red, [byte]$Green) {
    $pixels = [byte[]]::new($Width * $Height * 4)
    for ($y = 0; $y -lt $Height; $y++) {
        for ($x = 0; $x -lt $Width; $x++) {
            $pixel = ($y * $Width + $x) * 4
            $pixels[$pixel] = [byte](($y * $Width + $x) % 256)
            $pixels[$pixel + 1] = $Green
            $pixels[$pixel + 2] = $Red
        }
    }
    $frame = [Windows.Media.Imaging.BitmapSource]::Create($Width, $Height, 96, 96,
        [Windows.Media.PixelFormats]::Bgr32, $null, $pixels, $Width * 4)
    $frame.Freeze()
    return $frame
}

function Pixel($Frame, [int]$X, [int]$Y) {
    $values = [byte[]]::new(4)
    $Frame.CopyPixels([Windows.Int32Rect]::new($X, $Y, 1, 1), $values, 4, 0)
    return '{0:X2}{1:X2}{2:X2}' -f $values[2], $values[1], $values[0]
}

function Render-CaptureVisual($Visual, [int]$Width, [int]$Height, [double]$Scale) {
    [Windows.Media.VisualTreeHelper]::SetRootDpi($Visual, [Windows.DpiScale]::new($Scale, $Scale))
    $size = [Windows.Size]::new($Width / $Scale, $Height / $Scale)
    $Visual.Measure($size)
    $Visual.Arrange([Windows.Rect]::new($size))
    $Visual.UpdateLayout()
    $target = [Windows.Media.Imaging.RenderTargetBitmap]::new($Width, $Height, ($Scale * 96), ($Scale * 96),
        [Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($Visual)
    return $target
}

function Has-InsideEdge($Frame, [bool]$Horizontal, [bool]$Far) {
    $limit = if ($Horizontal) { $Frame.PixelHeight } else { $Frame.PixelWidth }
    $middle = if ($Horizontal) { [int]($Frame.PixelWidth / 2) } else { [int]($Frame.PixelHeight / 2) }
    $edge = if ($Far) { $limit - 1 } else { 0 }
    $pixel = [byte[]]::new(4)
    $x = if ($Horizontal) { $middle } else { $edge }
    $y = if ($Horizontal) { $edge } else { $middle }
    $Frame.CopyPixels([Windows.Int32Rect]::new($x, $y, 1, 1), $pixel, 4, 0)
    return $pixel[3] -gt 110
}

$primaryBounds = [Drawing.Rectangle]::new(0, 0, 4, 3)
$secondaryBounds = [Drawing.Rectangle]::new(-3, -2, 3, 4)
$primary = New-Frame 4 3 200 0
$secondary = New-Frame 3 4 0 200
$monitors = [Collections.Generic.Dictionary[Drawing.Rectangle, Windows.Media.Imaging.BitmapSource]]::new()
$monitors.Add($primaryBounds, $primary)
$monitors.Add($secondaryBounds, $secondary)
$snapshotType = $assembly.GetType('KimNotes.Windows.Capture.ScreenSnapshot', $true)
$snapshot = [Activator]::CreateInstance($snapshotType, $instance, $null, @($monitors), $null)
$copy = $snapshotType.GetMethod('Copy', $instance)
$frame = $snapshotType.GetMethod('MonitorImage', $instance).Invoke($snapshot, @($secondaryBounds))
Assert-Capture ([object]::ReferenceEquals($frame, $secondary)) 'The secondary monitor keeps its own unscaled bitmap.'
$fullFrame = $copy.Invoke($snapshot, @($secondaryBounds))
Assert-Capture ([object]::ReferenceEquals($fullFrame, $secondary)) 'A whole-monitor selection reuses the frozen frame without a second full pixel copy.'

$cropped = $copy.Invoke($snapshot, @([Drawing.Rectangle]::new(-2, -1, 2, 2)))
Assert-Capture ($cropped.PixelWidth -eq 2 -and $cropped.PixelHeight -eq 2 -and
    (Pixel $cropped 0 0) -eq '00C804' -and (Pixel $cropped 1 1) -eq '00C808') 'A crop wholly on the negative X/Y monitor uses monitor-relative pixels.'

$spanning = $copy.Invoke($snapshot, @([Drawing.Rectangle]::new(-1, -1, 3, 3)))
Assert-Capture ($spanning.PixelWidth -eq 3 -and $spanning.PixelHeight -eq 3 -and
    (Pixel $spanning 0 1) -eq '00C808' -and (Pixel $spanning 1 1) -eq 'C80000' -and
    (Pixel $spanning 2 1) -eq 'C80001' -and (Pixel $spanning 1 0) -eq '000000') 'A crop spanning both monitors preserves the seam and the uncovered desktop pixel.'

$selection = $assembly.GetType('KimNotes.Windows.Capture.CaptureSelection', $true)
$physicalMonitor = [Drawing.Rectangle]::new(-1600, -300, 1600, 900)
$overlay = $selection.GetMethod('BuildOverlayParameters', $static).Invoke($null, @($physicalMonitor))
Assert-Capture ($overlay.PositionX -eq -1600 -and $overlay.PositionY -eq -300 -and
    $overlay.Width -eq 1600 -and $overlay.Height -eq 900) 'The selection overlay starts at native monitor coordinates without a second DPI transform.'

$surfaceType = $selection.GetNestedType('SelectionSurface', [Reflection.BindingFlags]::NonPublic)
$edgeMonitor = [Drawing.Rectangle]::new(-96, -72, 96, 72)
foreach ($scale in @(1.0, 1.5)) {
    $surface = [Activator]::CreateInstance($surfaceType, $instance, $null, @($edgeMonitor), $null)
    $surfaceType.GetMethod('ShowArea', $instance).Invoke($surface, @($edgeMonitor))
    $rendered = Render-CaptureVisual $surface 96 72 $scale
    Assert-Capture ((Has-InsideEdge $rendered $true $false) -and (Has-InsideEdge $rendered $true $true) -and
        (Has-InsideEdge $rendered $false $false) -and (Has-InsideEdge $rendered $false $true)) `
        "The screenshot selection keeps all four edges visible when it meets the screen bounds at scale $scale."
}

$gestureSurface = [Activator]::CreateInstance($surfaceType, $instance, $null, @($edgeMonitor), $null)
$null = Render-CaptureVisual $gestureSurface 96 72 1.5
$showArea = $surfaceType.GetMethod('ShowArea', $instance)
$renderRequest = $surfaceType.GetField('renderRequest', $instance)
$showArea.Invoke($gestureSurface, @([Drawing.Rectangle]::new(-92, -68, 8, 8)))
$oneFrame = $renderRequest.GetValue($gestureSurface)
for ($i = 1; $i -le 200; $i++) {
    $left = -92 + ($i % 70)
    $showArea.Invoke($gestureSurface, @([Drawing.Rectangle]::new($left, -68, 8, 8)))
}
Assert-Capture ($gestureSurface.IsArrangeValid -and $gestureSurface.IsMeasureValid) 'Two hundred drag updates leave overlay layout valid.'
Assert-Capture ($null -ne $oneFrame -and [object]::ReferenceEquals($oneFrame, $renderRequest.GetValue($gestureSurface))) 'Two hundred drag updates share a single queued visual refresh.'
$null = $oneFrame.Abort()

$viewer = $assembly.GetType('KimNotes.Windows.Capture.PinnedImageWindow', $true)
$overlap = $viewer.GetMethod('LocalToolbarOverlap', $static).Invoke($null, @(
    [Drawing.Rectangle]::new(-100, -80, 100, 80), [Drawing.Rectangle]::new(-30, -20, 48, 35)))
Assert-Capture ($overlap -eq [Drawing.Rectangle]::new(70, 60, 30, 20)) 'The image window leaves the shared toolbar hittable even when it overlaps a negative-origin selection.'

$toolType = $assembly.GetType('KimNotes.Windows.Capture.CaptureTool', $true)
$toolActionType = [Action[int]].GetGenericTypeDefinition().MakeGenericType(@($toolType))
$ignoreTool = [Management.Automation.LanguagePrimitives]::ConvertTo({ param($tool) }, $toolActionType)
$toolbarType = $assembly.GetType('KimNotes.Windows.Capture.CaptureToolbar', $true)
$toolbar = [Activator]::CreateInstance($toolbarType, $instance, $null, @($ignoreTool, $false), $null)
$toolSpace = [Windows.Size]::new(400, 80)
$toolbar.Measure($toolSpace)
$toolbarSize = $toolbar.DesiredSize
foreach ($name in @('Rectangle', 'Arrow', 'Pen', 'Mosaic', 'Text', 'PickColor')) {
    $tool = [Enum]::Parse($toolType, $name)
    $toolbarType.GetMethod('Select', $instance).Invoke($toolbar, @($tool))
    $toolbar.Measure($toolSpace)
    if ($toolbar.DesiredSize -ne $toolbarSize) { throw "Selecting $name changed the capture toolbar dimensions." }
}
Assert-Capture ($toolbarSize.Width -gt 0 -and $toolbarSize.Height -gt 0) 'Switching annotation tools retains the same toolbar dimensions.'
$toolCalls = [Collections.Generic.List[string]]::new()
$recordTool = [Management.Automation.LanguagePrimitives]::ConvertTo(
    ({ param($tool) $toolCalls.Add($tool.ToString()) }.GetNewClosure()), $toolActionType)
$textToolbar = [Activator]::CreateInstance($toolbarType, $instance, $null, @($recordTool, $false), $null)
$actions = $toolbarType.GetField('actions', $instance).GetValue($textToolbar)
$ocrButton = $actions[[Enum]::Parse($toolType, 'Ocr')]
$ocrButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $ocrButton))
Assert-Capture (($toolCalls -join ',') -eq 'Ocr' -and $ocrButton.ToolTip -eq '文字识别' -and
    [Windows.Automation.AutomationProperties]::GetName($ocrButton) -eq '文字识别' -and
    -not $actions.ContainsKey([Enum]::Parse($toolType, 'Translate'))) 'The capture toolbar keeps the renamed OCR action and hides image translation.'

$optionsType = $assembly.GetType('KimNotes.Windows.Capture.CaptureAnnotationOptions', $true)
$styleType = $assembly.GetType('KimNotes.Windows.Capture.CaptureAnnotationStyle', $true)
$options = [Activator]::CreateInstance($optionsType, $true)
$style = $styleType.GetProperty('Default', $static).GetValue($null)
$bindOptions = $optionsType.GetMethod('Bind', $instance)
$supportsOptions = $optionsType.GetMethod('Supports', $static)
foreach ($name in @('Rectangle', 'Arrow', 'Pen', 'Text')) {
    Assert-Capture ($supportsOptions.Invoke($null, @([Enum]::Parse($toolType, $name)))) "$name exposes annotation options."
}
Assert-Capture (-not $supportsOptions.Invoke($null, @([Enum]::Parse($toolType, 'Mosaic')))) 'Mosaic does not expose unrelated annotation options.'
$bindOptions.Invoke($options, @([Enum]::Parse($toolType, 'Rectangle'), $style))
$widthButtons = $optionsType.GetField('widths', $instance).GetValue($options)
$fontButtons = $optionsType.GetField('fontSizes', $instance).GetValue($options)
$colorButtons = $optionsType.GetField('colors', $instance).GetValue($options)
$fillButton = $optionsType.GetField('fill', $instance).GetValue($options)
$optionStyle = $optionsType.GetField('style', $instance)
function Select-Option($Button) {
    $Button.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Primitives.ButtonBase]::ClickEvent, $Button))
}
function Option-Value([string]$Name) {
    return $styleType.GetProperty($Name, $instance).GetValue($optionStyle.GetValue($options))
}
for ($i = 0; $i -lt 3; $i++) {
    Select-Option $widthButtons[$i]
    Assert-Capture ((Option-Value 'StrokeWidth') -eq @(1.5, 2.5, 5.0)[$i] -and $widthButtons[$i].Tag -eq $true) "Line width option $i updates its value and selected feedback."
}
$bindOptions.Invoke($options, @([Enum]::Parse($toolType, 'Text'), $optionStyle.GetValue($options)))
Assert-Capture ($fillButton.Visibility -eq [Windows.Visibility]::Collapsed -and $fontButtons[0].Parent.Visibility -eq [Windows.Visibility]::Visible) 'Text shows font size choices without rectangle fill.'
for ($i = 0; $i -lt 3; $i++) {
    Select-Option $fontButtons[$i]
    Assert-Capture ((Option-Value 'FontSize') -eq @(11.0, 14.0, 22.0)[$i] -and $fontButtons[$i].Tag -eq $true) "Text size option $i updates its value and selected feedback."
}
$colorValues = @('#FF0EAEF3', '#FF8DD500', '#FFFFC000', '#FF464646', '#FFFFFFFF', '#FFDE424A')
for ($i = 0; $i -lt $colorButtons.Length; $i++) {
    Select-Option $colorButtons[$i]
    Assert-Capture (((Option-Value 'Color').ToString()) -eq $colorValues[$i] -and $colorButtons[$i].Tag -eq $true) "Color swatch $i updates the annotation color and selected feedback."
}
$bindOptions.Invoke($options, @([Enum]::Parse($toolType, 'Rectangle'), $optionStyle.GetValue($options)))
Select-Option $fillButton
Assert-Capture ((Option-Value 'Filled') -eq $true -and $fillButton.Tag -eq $true) 'Rectangle fill can be selected.'
Select-Option $fillButton
Assert-Capture ((Option-Value 'Filled') -eq $false -and $fillButton.Tag -eq $false) 'Rectangle fill can be switched off.'

$wheel = $viewer.GetMethod('WheelZoom', $static)
Assert-Capture ([Math]::Abs($wheel.Invoke($null, @(1.0, 30)) - [Math]::Pow(1.1, 0.25)) -lt 0.00001 -and
    [Math]::Abs($wheel.Invoke($null, @(1.0, 240)) - 1.21) -lt 0.00001 -and
    $wheel.Invoke($null, @(4.0, 120)) -eq 4.0 -and $wheel.Invoke($null, @(0.15, -120)) -eq 0.15) 'Precision wheel deltas and zoom limits preserve the expected scale.'

$fittedWheel = $viewer.GetMethod('FittedWheelZoom', $static)
Assert-Capture ([Math]::Abs($fittedWheel.Invoke($null, @(4.0, -120, 0.8)) - 0.8 / 1.1) -lt 0.00001 -and
    $fittedWheel.Invoke($null, @(4.0, 120, 0.8)) -eq 0.8) 'One downward wheel step shrinks immediately after reaching the monitor fit limit.'

$boundsArgs = [object[]]@($physicalMonitor, [Drawing.Point]::new(-100, 520),
    [Drawing.Size]::new(2000, 1000), 4.0)
$sized = $viewer.GetMethod('ScaledBounds', $static).Invoke($null, $boundsArgs)
Assert-Capture ($sized -eq [Drawing.Rectangle]::new(-1600, -200, 1600, 800)) 'Zoom preserves image dimensions without reserving space for the floating toolbar.'

$floatingType = $assembly.GetType('KimNotes.Windows.Capture.FloatingCaptureToolbar', $true)
$place = $floatingType.GetMethod('Place', $static)
$below = $place.Invoke($null, @([Drawing.Rectangle]::new(120, 80, 550, 284), [Drawing.Size]::new(352, 36),
    [Drawing.Rectangle]::new(0, 0, 1920, 1040), 8))
Assert-Capture ($below -eq [Drawing.Rectangle]::new(318, 372, 352, 36)) 'The compact toolbar sits below a wide pinned image and aligns to its right edge.'
$above = $place.Invoke($null, @([Drawing.Rectangle]::new(-1230, 464, 800, 128), [Drawing.Size]::new(528, 54), $physicalMonitor, 12))
Assert-Capture ($above -eq [Drawing.Rectangle]::new(-958, 398, 528, 54)) 'At 150% the toolbar goes above the image when the negative-origin monitor has room there.'

$paletteType = $assembly.GetType('KimNotes.Windows.Capture.CaptureAnnotationPalette', $true)
$placePalette = $paletteType.GetMethod('Place', [Reflection.BindingFlags]'Static,NonPublic')
foreach ($scale in @(1.0, 1.25, 1.5, 2.0)) {
    # All values are logical multiples of four so their scaled coordinates are exact.
    $work = [Drawing.Rectangle]::new([int](-1600 * $scale), [int](-300 * $scale), [int](1600 * $scale), [int](900 * $scale))
    $size = [Drawing.Size]::new([int](200 * $scale), [int](60 * $scale))
    $gap = [int](8 * $scale)
    $bar = [Drawing.Rectangle]::new([int](-900 * $scale), 0, [int](400 * $scale), [int](36 * $scale))
    $anchor = [Drawing.Rectangle]::new([int](-800 * $scale), 0, [int](24 * $scale), [int](24 * $scale))
    $position = $placePalette.Invoke($null, @($anchor, $bar, $size, $work, $gap))
    $expected = [Drawing.Rectangle]::new([int](-888 * $scale), [int](44 * $scale), $size.Width, $size.Height)
    Assert-Capture ($position -eq $expected) "Annotation options are centered under the selected tool at scale $scale."
    $bar = [Drawing.Rectangle]::new([int](-900 * $scale), [int](540 * $scale), [int](400 * $scale), [int](36 * $scale))
    $anchor = [Drawing.Rectangle]::new([int](-800 * $scale), [int](540 * $scale), [int](24 * $scale), [int](24 * $scale))
    $position = $placePalette.Invoke($null, @($anchor, $bar, $size, $work, $gap))
    $expected = [Drawing.Rectangle]::new([int](-888 * $scale), [int](472 * $scale), $size.Width, $size.Height)
    Assert-Capture ($position -eq $expected) "Annotation options flip above the toolbar when below has no room at scale $scale."
    $anchor = [Drawing.Rectangle]::new([int](-1596 * $scale), 0, [int](24 * $scale), [int](24 * $scale))
    $bar = [Drawing.Rectangle]::new([int](-1600 * $scale), 0, [int](400 * $scale), [int](36 * $scale))
    $position = $placePalette.Invoke($null, @($anchor, $bar, $size, $work, $gap))
    Assert-Capture ($position.Left -eq $work.Left -and $position.Right -le $work.Right) "Annotation options stay within the negative-origin monitor at scale $scale."
}

$dropTarget = $viewer.GetMethod('DragTarget', $static).Invoke($null, @(
    [Drawing.Point]::new(80, 90), [Drawing.Point]::new(110, 120), [Drawing.Point]::new(-490, -60)))
Assert-Capture ($dropTarget -eq [Drawing.Point]::new(-520, -90)) 'A drag onto the upper-left monitor keeps the original pixel offset under the pointer.'

$pinPixels = New-Frame 96 72 210 30
$toolbarPin = [Activator]::CreateInstance($viewer, $instance, $null, @($pinPixels, $false, $false, $null), $null)
$pinPanel = $viewer.GetField('imagePanel', $instance).GetValue($toolbarPin)
$pinItems = @($pinPanel.ContextMenu.Items | Where-Object { $_ -is [Windows.Controls.MenuItem] })
Assert-Capture (($pinItems.Header -join ',') -eq '工具栏,插入便签,复制,另存图片,文字识别,重置大小,上一个贴图,下一个贴图,关闭贴图,销毁贴图') `
    'The pinned image menu uses the requested labels and omits image translation.'
$pinItems[0].RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.MenuItem]::ClickEvent, $pinItems[0]))
$floating = $viewer.GetField('toolbar', $instance).GetValue($toolbarPin)
$toolbarCard = $floating.Content
$toolbarCard.Measure([Windows.Size]::new(500, 80))
Assert-Capture ($floating -is [Windows.Window] -and $floating.AllowsTransparency -and -not $floating.ShowActivated -and
    $toolbarCard.DesiredSize -eq [Windows.Size]::new(352, 36) -and $toolbarPin.Content.Children.Count -eq 1) `
    'Show toolbar creates a compact transparent card in its own window and leaves the image layout alone.'
$floatingType.GetMethod('CloseWithOwner', $instance).Invoke($floating, $null)

$confirmationCalls = [Collections.Generic.List[string]]::new()
$viewerActionType = [Action[int,int]].GetGenericTypeDefinition().MakeGenericType(@($viewer, $toolType))
$recordConfirmation = [Management.Automation.LanguagePrimitives]::ConvertTo(
    ({ param($sender, $tool) $confirmationCalls.Add($tool.ToString()) }.GetNewClosure()), $viewerActionType)
$confirmationViewer = [Activator]::CreateInstance($viewer, $instance, $null, @($pinPixels, $false, $false, $recordConfirmation), $null)
$captureEditorFlag = $viewer.GetProperty('IsCaptureEditor', $instance)
$captureEditorFlag.SetValue($confirmationViewer, $true)
$keyHostParameters = [Windows.Interop.HwndSourceParameters]::new('KimNotes capture keyboard test', 1, 1)
$keyHostParameters.WindowStyle = [int]::MinValue # WS_POPUP without WS_VISIBLE.
$keyHost = [Windows.Interop.HwndSource]::new($keyHostParameters)
try {
    function Press-ViewerEnter($Target) {
        $key = [Windows.Input.KeyEventArgs]::new([Windows.Input.Keyboard]::PrimaryDevice, $keyHost, 0, [Windows.Input.Key]::Enter)
        $key.RoutedEvent = [Windows.Input.Keyboard]::PreviewKeyDownEvent
        $Target.RaiseEvent($key)
        return $key.Handled
    }

    Assert-Capture ((Press-ViewerEnter $confirmationViewer) -and ($confirmationCalls -join ',') -eq 'Confirm') `
        'Enter confirms a new screenshot after annotations without requiring a manual copy action.'
    $captureEditorFlag.SetValue($confirmationViewer, $false)
    Assert-Capture (-not (Press-ViewerEnter $confirmationViewer) -and $confirmationCalls.Count -eq 1) `
        'Enter does not confirm a pinned image once capture has completed.'
} finally {
    $keyHost.Dispose()
    $confirmationViewer.Close()
}

foreach ($scale in @(1.0, 1.5)) {
    $pin = [Activator]::CreateInstance($viewer, $instance, $null, @($pinPixels, $false, $false, $null), $null)
    $size = [Drawing.Size]::new(96, 72)
    $dpi = [Windows.DpiScale]::new($scale, $scale)
    $viewer.GetMethod('ApplyImageSize', $instance).Invoke($pin, @($size, $dpi))
    $view = Render-CaptureVisual $pin.Content $size.Width $size.Height $scale
    $unchanged = (Pixel $view 45 35) -eq (Pixel $pinPixels 45 35) -and
        (Pixel $view 93 69) -eq (Pixel $pinPixels 93 69)
    $outline = (Pixel $view 0 36) -ne (Pixel $pinPixels 0 36) -and (Pixel $view 95 36) -ne (Pixel $pinPixels 95 36) -and
        (Pixel $view 48 0) -ne (Pixel $pinPixels 48 0) -and (Pixel $view 48 71) -ne (Pixel $pinPixels 48 71)
    Assert-Capture ($unchanged -and $outline) "The pin paints the complete image and a fine outline on all four edges at scale $scale."
    $export = $viewer.GetMethod('ImageForAction', $instance).Invoke($pin, $null)
    Assert-Capture ([object]::ReferenceEquals($export, $pinPixels)) "The pin outline does not become part of the copied or saved image at scale $scale."
}

$clipboard = $assembly.GetType('KimNotes.Windows.Capture.CaptureClipboard', $true)
$recognized = '测试文字' + [Environment]::NewLine + 'second line'
$ocrType = $assembly.GetType('KimNotes.Windows.Capture.CaptureOcr', $true)
$ocrValues = [Collections.Generic.Dictionary[string, string]]::new()
$ocrValues.Add('image', ('abCD0123+/' * 20000) + 's=')
$ocrValues.Add('test', '中文 English + / = 𝄞 x² ∑ ↔ ©')
$ocrForm = $ocrType.GetMethod('EncodeForm', $static).Invoke($null, (,$ocrValues))
try {
    $encoded = $ocrForm.ReadAsStringAsync().GetAwaiter().GetResult()
    $decoded = [Web.HttpUtility]::ParseQueryString($encoded, [Text.Encoding]::UTF8)
    Assert-Capture ($ocrForm.Headers.ContentType.MediaType -eq 'application/x-www-form-urlencoded' -and
        $encoded.Length -gt 200000 -and $decoded.Get('image') -ceq $ocrValues['image'] -and
        $decoded.Get('test') -ceq $ocrValues['test']) 'A large OCR form preserves Base64, Chinese and symbols through one form decode.'
} finally { $ocrForm.Dispose() }
$expectedOcr = '中文 English + / = 𝄞 x² ∑ ↔ ©'
$ocrJson = '{"words_result":[{"words":"' + $expectedOcr + '"},{"words":"第二行 0123"}]}'
$jsonCorrect = $true
foreach ($charset in @('utf-8', 'iso-8859-1', $null)) {
    $reply = [Net.Http.ByteArrayContent]::new([Text.Encoding]::UTF8.GetBytes($ocrJson))
    $reply.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('application/json')
    $reply.Headers.ContentType.CharSet = $charset
    try {
        $decodedJson = $ocrType.GetMethod('ReadJson', $static).Invoke($null, @($reply)).GetAwaiter().GetResult()
        $words = $decodedJson.Property('words_result').Value
        $jsonCorrect = $jsonCorrect -and $words[0].Property('words').Value.Value -ceq $expectedOcr -and
            $words[1].Property('words').Value.Value -ceq '第二行 0123'
    } finally { $reply.Dispose() }
}
Assert-Capture $jsonCorrect 'OCR JSON preserves UTF-8 text and line order even with a missing or incorrect charset header.'
$textMemory = $clipboard.GetMethod('BuildText', $static).Invoke($null, @($recognized))
try {
    $textBuffer = $clipboard.GetMethod('GlobalLock', $static).Invoke($null, @($textMemory))
    try {
        Assert-Capture ([Runtime.InteropServices.Marshal]::PtrToStringUni($textBuffer) -eq $recognized) `
            'Text clipboard preparation retains Chinese characters and line breaks.'
    } finally { $null = $clipboard.GetMethod('GlobalUnlock', $static).Invoke($null, @($textMemory)) }
} finally { $null = $clipboard.GetMethod('GlobalFree', $static).Invoke($null, @($textMemory)) }
$textCopies = [Collections.Generic.List[string]]::new()
$publishText = [Func[string,Threading.CancellationToken,Threading.Tasks.Task[bool]]]({
    param($value, $token)
    $textCopies.Add($value)
    return [Threading.Tasks.Task[bool]]::FromResult($true)
}.GetNewClosure())
$workspaceType = $assembly.GetType('KimNotes.Windows.Capture.CaptureWorkspace', $true)
$copyOcr = $workspaceType.GetMethod('CopyRecognizedText', $static)
$emptyOcr = $copyOcr.Invoke($null, @(' ', $publishText, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
$copiedOcr = $copyOcr.Invoke($null, @($recognized, $publishText, [Threading.CancellationToken]::None)).GetAwaiter().GetResult()
Assert-Capture ($emptyOcr -eq '图片中未识别到文字。' -and $copiedOcr -eq '识别文字已复制' -and
    $textCopies.Count -eq 1 -and $textCopies[0] -eq $recognized) 'OCR copies a complete result and keeps an empty result out of the clipboard.'

$annotationType = $assembly.GetType('KimNotes.Windows.Capture.CaptureAnnotationDocument', $true)
$annotation = [Activator]::CreateInstance($annotationType, $instance, $null, @($primary), $null)
$replacement = New-Frame 4 3 71 150
$annotationType.GetMethod('AddReplacement', $instance).Invoke($annotation, @($replacement))
$renderAnnotation = $annotationType.GetMethod('Render', $instance)
Assert-Capture ((Pixel ($renderAnnotation.Invoke($annotation, $null)) 1 1) -eq (Pixel $replacement 1 1)) `
    'An in-place translation appears in the screenshot composition used by copy and save.'
$annotationType.GetMethod('Undo', $instance).Invoke($annotation, $null)
Assert-Capture ([object]::ReferenceEquals($renderAnnotation.Invoke($annotation, $null), $primary)) `
    'Undo restores the original screenshot after a translation.'

# Exercise the document on synthetic pixels only. These checks deliberately
# inspect the rendered output rather than merely comparing style properties.
function New-Annotation($Source) {
    return [Activator]::CreateInstance($annotationType, $instance, $null, @($Source), $null)
}
function Colored-Pixels($Frame, [string]$Color) {
    $stride = $Frame.PixelWidth * 4
    $data = [byte[]]::new($stride * $Frame.PixelHeight)
    $Frame.CopyPixels($data, $stride, 0)
    $count = 0
    for ($i = 0; $i -lt $data.Length; $i += 4) {
        if ($Color -eq 'red' -and $data[$i + 2] -gt 200 -and $data[$i + 1] -lt 55 -and $data[$i] -lt 55) { $count++ }
        if ($Color -eq 'blue' -and $data[$i] -gt 200 -and $data[$i + 1] -lt 55 -and $data[$i + 2] -lt 55) { $count++ }
        if ($Color -eq 'white' -and $data[$i] -gt 240 -and $data[$i + 1] -gt 240 -and $data[$i + 2] -gt 240) { $count++ }
    }
    return $count
}
function Cross-Section($Frame, $Source, [int]$X, [int]$Y, [bool]$Horizontal) {
    $count = 0
    for ($delta = -8; $delta -le 8; $delta++) {
        $px = $X; $py = $Y
        if ($Horizontal) { $px += $delta } else { $py += $delta }
        if ((Pixel $Frame $px $py) -ne (Pixel $Source $px $py)) { $count++ }
    }
    return $count
}
$canvas = New-Frame 128 128 100 150
$red = [Windows.Media.Color]::FromRgb(255, 0, 0)
$blue = [Windows.Media.Color]::FromRgb(0, 0, 255)
$penFactory = $annotationType.GetMethod('CreatePen', $static)
$drawGesture = $annotationType.GetMethod('Add', $instance)
$drawText = $annotationType.GetMethod('AddText', $instance)
$drawStroke = $annotationType.GetMethod('AddStroke', $instance)
$undo = $annotationType.GetMethod('Undo', $instance)
foreach ($shape in @('Rectangle', 'Arrow')) {
    $thicknesses = [Collections.Generic.List[int]]::new()
    foreach ($width in @(1.5, 2.5, 5.0)) {
        $doc = New-Annotation $canvas
        $pen = $penFactory.Invoke($null, @($red, [double]$width))
        $start = if ($shape -eq 'Rectangle') { [Windows.Point]::new(20, 20) } else { [Windows.Point]::new(20, 64) }
        $end = if ($shape -eq 'Rectangle') { [Windows.Point]::new(100, 100) } else { [Windows.Point]::new(100, 64) }
        $null = $drawGesture.Invoke($doc, @([Enum]::Parse($toolType, $shape), $start, $end, $pen, $false))
        $result = $renderAnnotation.Invoke($doc, $null)
        $thicknesses.Add((Cross-Section $result $canvas $(if ($shape -eq 'Rectangle') { 20 } else { 50 }) 64 ($shape -eq 'Rectangle')))
        Assert-Capture ((Colored-Pixels $result 'red') -gt 5) "$shape renders the selected red color at width $width."
    }
    Assert-Capture ($thicknesses[0] -lt $thicknesses[1] -and $thicknesses[1] -lt $thicknesses[2]) "$shape renders three visibly different line widths."
    $doc = New-Annotation $canvas
    $pen = $penFactory.Invoke($null, @($blue, [double]2.5))
    $start = if ($shape -eq 'Rectangle') { [Windows.Point]::new(20, 20) } else { [Windows.Point]::new(20, 64) }
    $end = if ($shape -eq 'Rectangle') { [Windows.Point]::new(100, 100) } else { [Windows.Point]::new(100, 64) }
    $null = $drawGesture.Invoke($doc, @([Enum]::Parse($toolType, $shape), $start, $end, $pen, $false))
    Assert-Capture ((Colored-Pixels ($renderAnnotation.Invoke($doc, $null)) 'blue') -gt 5) "$shape renders the selected blue color."
}
$doc = New-Annotation $canvas
$pen = $penFactory.Invoke($null, @($red, [double]2.5))
$null = $drawGesture.Invoke($doc, @([Enum]::Parse($toolType, 'Rectangle'), [Windows.Point]::new(20, 20), [Windows.Point]::new(100, 100), $pen, $false))
Assert-Capture ((Pixel ($renderAnnotation.Invoke($doc, $null)) 60 60) -eq (Pixel $canvas 60 60)) 'An outline rectangle leaves its interior unchanged.'
$doc = New-Annotation $canvas
$null = $drawGesture.Invoke($doc, @([Enum]::Parse($toolType, 'Rectangle'), [Windows.Point]::new(20, 20), [Windows.Point]::new(100, 100), $pen, $true))
Assert-Capture ((Pixel ($renderAnnotation.Invoke($doc, $null)) 60 60) -eq 'FF0000') 'A filled rectangle uses the selected color in its interior.'

$textCounts = [Collections.Generic.List[int]]::new()
foreach ($size in @(11.0, 14.0, 22.0)) {
    $doc = New-Annotation $canvas
    $brush = [Windows.Media.SolidColorBrush]::new($red)
    $drawText.Invoke($doc, @([Windows.Point]::new(8, 8), 'Sample', [double]$size, [double]110, $brush))
    $textCounts.Add((Colored-Pixels ($renderAnnotation.Invoke($doc, $null)) 'red'))
}
Assert-Capture ($textCounts[0] -gt 0 -and $textCounts[0] -lt $textCounts[1] -and $textCounts[1] -lt $textCounts[2]) 'The three text sizes produce progressively larger colored text.'
$doc = New-Annotation $canvas
$drawText.Invoke($doc, @([Windows.Point]::new(8, 8), 'Sample', [double]14, [double]110, [Windows.Media.SolidColorBrush]::new($blue)))
Assert-Capture ((Colored-Pixels ($renderAnnotation.Invoke($doc, $null)) 'blue') -gt 5) 'Text uses the selected color.'

$points = [Collections.Generic.List[Windows.Point]]::new()
$points.Add([Windows.Point]::new(10, 30)); $points.Add([Windows.Point]::new(40, 70)); $points.Add([Windows.Point]::new(90, 30))
$doc = New-Annotation $canvas
$drawStroke.Invoke($doc, @($points, $pen))
$stroke = $renderAnnotation.Invoke($doc, $null)
Assert-Capture ((Colored-Pixels $stroke 'red') -gt 20 -and (Pixel $stroke 10 30) -ne (Pixel $canvas 10 30) -and
    (Pixel $stroke 39 55) -ne (Pixel $canvas 39 55) -and (Pixel $stroke 90 30) -ne (Pixel $canvas 90 30)) 'A freehand curve renders both endpoints and intermediate ink.'
$undo.Invoke($doc, $null)
Assert-Capture ([object]::ReferenceEquals($renderAnnotation.Invoke($doc, $null), $canvas)) 'One undo removes the complete freehand stroke.'
$point = [Collections.Generic.List[Windows.Point]]::new()
$point.Add([Windows.Point]::new(60, 60))
$doc = New-Annotation $canvas
$drawStroke.Invoke($doc, @($point, $pen))
Assert-Capture ((Pixel ($renderAnnotation.Invoke($doc, $null)) 60 60) -ne (Pixel $canvas 60 60)) 'A freehand click produces a visible dot.'
$undo.Invoke($doc, $null)
Assert-Capture ([object]::ReferenceEquals($renderAnnotation.Invoke($doc, $null), $canvas)) 'One undo removes a freehand dot.'

# Exercise the actual pinned-image gesture path without showing a window or
# generating real mouse input. Per-tool settings must survive tool changes.
$pin = [Activator]::CreateInstance($viewer, $instance, $null, @($canvas, $false, $false, $null), $null)
$invokeTool = $viewer.GetMethod('InvokeTool', $instance)
$applyStyle = $viewer.GetMethod('ApplyAnnotationStyle', $instance)
$getStyle = $viewer.GetMethod('AnnotationStyle', $instance)
$styleCtor = $styleType.GetConstructors($instance)[0]
$penTool = [Enum]::Parse($toolType, 'Pen')
$rectTool = [Enum]::Parse($toolType, 'Rectangle')
$penStyle = $styleCtor.Invoke(@($blue, [double]5, [double]14, $false))
$rectStyle = $styleCtor.Invoke(@($red, [double]1.5, [double]22, $true))
$invokeTool.Invoke($pin, @($penTool))
$applyStyle.Invoke($pin, @($penStyle))
$invokeTool.Invoke($pin, @($rectTool))
$applyStyle.Invoke($pin, @($rectStyle))
$remembered = $getStyle.Invoke($pin, @($penTool))
Assert-Capture ($styleType.GetProperty('Color', $instance).GetValue($remembered) -eq $blue -and
    $styleType.GetProperty('StrokeWidth', $instance).GetValue($remembered) -eq 5.0) 'Freehand and rectangle retain independent annotation styles.'
$invokeTool.Invoke($pin, @($penTool))
$viewer.GetMethod('BeginDrawing', $instance).Invoke($pin, @($penTool, [Windows.Point]::new(10, 30)))
$viewer.GetMethod('UpdateDrawing', $instance).Invoke($pin, @($penTool, [Windows.Point]::new(40, 70)))
$viewer.GetMethod('FinishDrawing', $instance).Invoke($pin, @($penTool, [Windows.Point]::new(90, 30)))
$drawn = $viewer.GetMethod('ImageForAction', $instance).Invoke($pin, $null)
Assert-Capture ((Colored-Pixels $drawn 'blue') -gt 20) 'The pinned-image pen gesture applies its selected color to the composed image.'
$invokeTool.Invoke($pin, @([Enum]::Parse($toolType, 'Undo')))
Assert-Capture ([object]::ReferenceEquals($viewer.GetMethod('ImageForAction', $instance).Invoke($pin, $null), $canvas)) 'One toolbar undo removes a complete pinned-image pen gesture.'
$pinToolbar = $viewer.GetField('toolbar', $instance).GetValue($pin)
if ($pinToolbar) { $floatingType.GetMethod('CloseWithOwner', $instance).Invoke($pinToolbar, $null) }

$textCanvas = New-Frame 320 180 100 150
$textTool = [Enum]::Parse($toolType, 'Text')
$outlineType = $assembly.GetType('KimNotes.Windows.Capture.CaptureTextOutline', $true)
$dashPen = $outlineType.GetField('Dashes', $static).GetValue($null)
Assert-Capture ($dashPen.DashStyle.Dashes.Count -gt 0) 'The editor outline uses a dashed pen.'
foreach ($scale in @(1.0, 1.5)) {
    $outline = [Activator]::CreateInstance($outlineType, $true)
    $outlineImage = Render-CaptureVisual $outline 120 60 $scale
    $inset = [int][Math]::Round(3.5 * $scale)
    foreach ($corner in @(@($inset, $inset), @((120 - $inset), $inset), @($inset, (60 - $inset)), @((120 - $inset), (60 - $inset)))) {
        $pixel = [byte[]]::new(4)
        $outlineImage.CopyPixels([Windows.Int32Rect]::new($corner[0], $corner[1], 1, 1), $pixel, 4, 0)
        Assert-Capture ($pixel[3] -gt 100) "The noninteractive editor outline renders each corner handle at scale $scale."
    }
    $pixel = [byte[]]::new(4)
    $outlineImage.CopyPixels([Windows.Int32Rect]::new(60, 30, 1, 1), $pixel, 4, 0)
    Assert-Capture (-not $outline.IsHitTestVisible -and $pixel[3] -eq 0) "The editor outline is noninteractive and transparent inside at scale $scale."
}
foreach ($scale in @(1.0, 1.5)) {
    $textPin = [Activator]::CreateInstance($viewer, $instance, $null, @($textCanvas, $false, $false, $null), $null)
    $viewer.GetMethod('ApplyImageSize', $instance).Invoke($textPin, @([Drawing.Size]::new(320, 180), [Windows.DpiScale]::new($scale, $scale)))
    $null = Render-CaptureVisual $textPin.Content 320 180 $scale
    $imagePanel = $viewer.GetField('imagePanel', $instance).GetValue($textPin)
    $invokeTool.Invoke($textPin, @($textTool))
    $viewer.GetMethod('BeginText', $instance).Invoke($textPin, @([Windows.Point]::new(8, 8)))
    $editor = $viewer.GetField('typing', $instance).GetValue($textPin)
    Assert-Capture ($null -ne $editor) "The annotation text editor can be created inside the image at scale $scale."
    $whiteStyle = $styleCtor.Invoke(@([Windows.Media.Colors]::White, [double]2.5, [double]14, $false))
    $applyStyle.Invoke($textPin, @($whiteStyle))
    $editor.ApplyTemplate() | Out-Null
    function Find-Outline($Visual) {
        if ($outlineType.IsInstanceOfType($Visual)) { return $true }
        for ($childIndex = 0; $childIndex -lt [Windows.Media.VisualTreeHelper]::GetChildrenCount($Visual); $childIndex++) {
            if (Find-Outline ([Windows.Media.VisualTreeHelper]::GetChild($Visual, $childIndex))) { return $true }
        }
        return $false
    }
    Assert-Capture (Find-Outline $editor) "The editor template includes the dashed four-corner outline at scale $scale."
    $backgroundTransparent = $null -eq $editor.Background -or $editor.Background.Opacity -eq 0 -or
        ($editor.Background -is [Windows.Media.SolidColorBrush] -and $editor.Background.Color.A -eq 0)
    Assert-Capture ($editor.Foreground.Color -eq [Windows.Media.Colors]::White -and $editor.CaretBrush.Color -eq [Windows.Media.Colors]::White -and
        $backgroundTransparent) "White annotation text keeps a transparent editor and matching caret at scale $scale."
    $emptyWidth = $editor.Width; $emptyHeight = $editor.Height
    Assert-Capture ($emptyWidth -gt 0 -and $emptyWidth -lt 90 -and $emptyHeight -gt 0 -and $emptyHeight -lt 50) "The empty text editor starts compact at scale $scale."
    $editor.Text = 'Hello world'
    $shortWidth = $editor.Width; $shortHeight = $editor.Height
    Assert-Capture ($shortWidth -gt $emptyWidth + 2 -and $shortWidth -le $imagePanel.RenderSize.Width) "The editor grows with short text at scale $scale."
    $editor.Text = "Hello`nworld"
    Assert-Capture ($editor.Height -gt $shortHeight + 2) "A newline increases the editor height at scale $scale."
    $editor.Text = ('word ' * 35)
    Assert-Capture ($editor.Height -gt $shortHeight + 2 -and $editor.Margin.Left + $editor.Width -le $imagePanel.RenderSize.Width + 0.1) "Automatically wrapped text grows within the image width at scale $scale."
    $editor.Text = '中文测试字'
    $editor.Select($editor.Text.Length, 0)
    $handled = $viewer.GetMethod('HandleTextKey', $instance).Invoke($textPin, @([Windows.Input.Key]::Enter, [Windows.Input.ModifierKeys]::Shift))
    $editor.SelectedText = '第二行文字'
    $null = Render-CaptureVisual $textPin.Content 320 180 $scale
    $editor.UpdateLayout()
    $lineOne = if ($editor.LineCount -gt 0) { $editor.GetLineText(0).TrimEnd([char[]]@("`r", "`n")) } else { '' }
    $lineTwo = if ($editor.LineCount -gt 1) { $editor.GetLineText(1).TrimEnd([char[]]@("`r", "`n")) } else { '' }
    Assert-Capture ($handled -and $editor.LineCount -eq 2 -and $lineOne -eq '中文测试字' -and $lineTwo -eq '第二行文字') `
        "Five Chinese characters remain on each line after Shift+Enter at scale $scale (actual lines: $($editor.LineCount))."
    $editor.Text = 'Hello'
    $editor.UpdateLayout()
    $left = [int][Math]::Round($editor.Margin.Left * $scale)
    $top = [int][Math]::Round($editor.Margin.Top * $scale)
    $right = [int][Math]::Round(($editor.Margin.Left + $editor.Width) * $scale) - 1
    $bottom = [int][Math]::Round(($editor.Margin.Top + $editor.Height) * $scale) - 1
    $textImage = $viewer.GetMethod('ImageForAction', $instance).Invoke($textPin, $null)
    Assert-Capture ((Colored-Pixels $textImage 'white') -gt 0) "White text is included in the exported image at scale $scale."
    foreach ($corner in @(@($left, $top), @($right, $top), @($left, $bottom), @($right, $bottom))) {
        $x = [Math]::Max(0, [Math]::Min(319, $corner[0])); $y = [Math]::Max(0, [Math]::Min(179, $corner[1]))
        Assert-Capture ((Pixel $textImage $x $y) -eq (Pixel $textCanvas $x $y)) "Editor border and corner handles are excluded from export at scale $scale."
    }
    $viewer.GetMethod('BeginText', $instance).Invoke($textPin, @([Windows.Point]::new($imagePanel.RenderSize.Width - 2, $imagePanel.RenderSize.Height - 2)))
    $edgeEditor = $viewer.GetField('typing', $instance).GetValue($textPin)
    $edgeEditor.Text = 'Edge'
    Assert-Capture ($edgeEditor.Margin.Left -ge 0 -and $edgeEditor.Margin.Top -ge 0 -and
        $edgeEditor.Margin.Left + $edgeEditor.Width -le $imagePanel.RenderSize.Width + 0.1 -and
        $edgeEditor.Margin.Top + $edgeEditor.Height -le $imagePanel.RenderSize.Height + 0.1) "Text editor stays fully visible near the lower-right edge at scale $scale."
    $viewer.GetMethod('EndMode', $instance).Invoke($textPin, @($true))
    $pinToolbar = $viewer.GetField('toolbar', $instance).GetValue($textPin)
    if ($pinToolbar) { $floatingType.GetMethod('CloseWithOwner', $instance).Invoke($pinToolbar, $null) }
}

$publicationType = $clipboard.GetNestedType('Publication', [Reflection.BindingFlags]::NonPublic)
$firstCopy = [Activator]::CreateInstance($publicationType, $instance, $null, @([uint32]5, [uint32]7, 1), $null)
$mayReplace = $clipboard.GetMethod('MayReplace', $static)
Assert-Capture ($mayReplace.Invoke($null, @([uint32]5, [uint32]7, 2, $firstCopy)) -and
    -not $mayReplace.Invoke($null, @([uint32]5, [uint32]8, 2, $firstCopy))) 'A later capture can replace our own queued copy while respecting a newer external clipboard change.'
$memory = $clipboard.GetMethod('BuildDib', $static).Invoke($null, @($secondary))
try {
    $buffer = $clipboard.GetMethod('GlobalLock', $static).Invoke($null, @($memory))
    try {
        $dib = [byte[]]::new(124 + 3 * 4 * 4)
        [Runtime.InteropServices.Marshal]::Copy($buffer, $dib, 0, $dib.Length)
        Assert-Capture ([BitConverter]::ToInt32($dib, 0) -eq 124 -and [BitConverter]::ToInt32($dib, 4) -eq 3 -and
            [BitConverter]::ToInt32($dib, 8) -eq -4 -and [BitConverter]::ToUInt16($dib, 14) -eq 32 -and
            [BitConverter]::ToUInt32($dib, 52) -eq 4278190080) 'Clipboard preparation writes a native top-down 32-bit DIB with an alpha mask.'
        Assert-Capture (($dib[124..127] -join ',') -eq '0,200,0,255' -and ($dib[168..171] -join ',') -eq '11,200,0,255') `
            'Clipboard preparation preserves color, row order and opaque alpha in a screenshot.'
    } finally { $null = $clipboard.GetMethod('GlobalUnlock', $static).Invoke($null, @($memory)) }
} finally { $null = $clipboard.GetMethod('GlobalFree', $static).Invoke($null, @($memory)) }

$previousPath = [Environment]::GetEnvironmentVariable('KIMNOTES_DATA_DIR')
$temporaryRoot = [IO.Path]::GetTempPath()
$testPath = Join-Path $temporaryRoot ('KimNotes-capture-test-' + [Guid]::NewGuid().ToString('N'))
[Environment]::SetEnvironmentVariable('KIMNOTES_DATA_DIR', $testPath)
try {
    $writerType = $assembly.GetType('KimNotes.Windows.Capture.CaptureArchiveWrites', $true)
    $writer = [Activator]::CreateInstance($writerType, $true)
    $archive = $assembly.GetType('KimNotes.utils.ScreenshotArchive', $true)
    $id = $archive.GetMethod('NewId', $static).Invoke($null, $null)
    $queue = $writerType.GetMethod('Queue', $instance)
    $queue.Invoke($writer, @($id, $primary))
    $queue.Invoke($writer, @($id, $secondary))
    $failure = $writerType.GetMethod('Wait', $instance).Invoke($writer, @($id))
    Assert-Capture ($null -eq $failure -and [IO.File]::Exists((Join-Path $testPath ('screenshot-history/' + $id + '.bak')))) 'Queued history writes retain a backup and complete in submission order.'
    $result = $archive.GetMethod('Load', $static).Invoke($null, @($id, $false))
    try {
        Assert-Capture ($null -ne $result -and $result.Width -eq 3 -and $result.Height -eq 4 -and
            $result.GetPixel(1, 1).G -eq 200) 'The existing history reader opens the newest PNG from the background writer.'
    } finally { if ($result) { $result.Dispose() } }
} finally {
    if ($writer) { $writerType.GetMethod('Drain', $instance).Invoke($writer, $null) }
    [Environment]::SetEnvironmentVariable('KIMNOTES_DATA_DIR', $previousPath)
    if ([IO.Directory]::Exists($testPath) -and
        [IO.Path]::GetFullPath($testPath).StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($testPath) -match '^KimNotes-capture-test-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $testPath -Recurse -Force
    }
}

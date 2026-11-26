param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with Windows PowerShell -STA.' }
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('KimNotes-image-actions-' + [Guid]::NewGuid().ToString('N'))
$previousData = [Environment]::GetEnvironmentVariable('KIMNOTES_DATA_DIR')
[Environment]::SetEnvironmentVariable('KIMNOTES_DATA_DIR', $tempRoot)
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Executable).Path)
$instance = [Reflection.BindingFlags]'Instance,NonPublic,Public'
$windowType = $assembly.GetType('KimNotes.Windows.Notes.NoteWindow', $true)
$app = [Activator]::CreateInstance($assembly.GetType('KimNotes.Windows.App', $true))
$config = $assembly.GetType('KimNotes.utils.InitConfig', $true)
$config.GetMethod('InitSettings', [Reflection.BindingFlags]'Static,NonPublic,Public').Invoke($null, $null)
$workspace = [Activator]::CreateInstance($assembly.GetType('KimNotes.Windows.Notes.NoteWorkspace', $true), $true)
$window = [Activator]::CreateInstance($windowType, $true)
$windowType.GetMethod('Connect', $instance).Invoke($window, @($workspace, $null, $null))
$editor = $window.FindName('Editor')
$body = $window.Content
function Check([bool]$Passed, [string]$Message) { if (-not $Passed) { throw $Message }; Write-Output $Message }
function Layout {
    $size = [Windows.Size]::new($window.Width, $window.Height)
    $body.Measure($size); $body.Arrange([Windows.Rect]::new($size)); $body.UpdateLayout()
}
function TestBitmap {
    $visual = [Windows.Media.DrawingVisual]::new()
    $draw = $visual.RenderOpen()
    $draw.DrawRectangle([Windows.Media.Brushes]::SteelBlue, $null, [Windows.Rect]::new(0, 0, 120, 80))
    $draw.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(120, 80, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual); $bitmap.Freeze()
    return $bitmap
}
function Text-Only($Paragraph) {
    return (($Paragraph.Inlines | Where-Object { $_ -is [Windows.Documents.Run] } | ForEach-Object { $_.Text }) -join '')
}
function Set-Paragraph([string]$Text) {
    $item = [Windows.Documents.Paragraph]::new([Windows.Documents.Run]::new($Text))
    $editor.Document = [Windows.Documents.FlowDocument]::new($item)
    Layout
    return $item
}
function Select-Text($Paragraph, [string]$Text) {
    $run = $Paragraph.Inlines | Where-Object { $_ -is [Windows.Documents.Run] -and $_.Text.Contains($Text) } | Select-Object -First 1
    if (-not $run) { throw "Missing synthetic text: $Text" }
    $start = $run.ContentStart.GetPositionAtOffset($run.Text.IndexOf($Text), [Windows.Documents.LogicalDirection]::Forward)
    $end = $start.GetPositionAtOffset($Text.Length, [Windows.Documents.LogicalDirection]::Forward)
    $editor.Selection.Select($start, $end)
}
function File-Data([string[]]$Paths) {
    $data = [Windows.DataObject]::new()
    $data.SetData([Windows.DataFormats]::FileDrop, $Paths)
    return $data
}
try {
    $document = [Windows.Documents.FlowDocument]::new()
    $document.PagePadding = [Windows.Thickness]::new(0)
    $imageA = [Windows.Controls.Image]::new(); $imageA.Source = TestBitmap; $imageA.Width = 100
    $imageB = [Windows.Controls.Image]::new(); $imageB.Source = TestBitmap; $imageB.Width = 70
    $a = [Windows.Documents.InlineUIContainer]::new($imageA)
    $b = [Windows.Documents.InlineUIContainer]::new($imageB)
    $paragraph = [Windows.Documents.Paragraph]::new()
    $paragraph.Inlines.Add([Windows.Documents.Run]::new('Before ')); $paragraph.Inlines.Add($a)
    $paragraph.Inlines.Add([Windows.Documents.Run]::new(' Between ')); $paragraph.Inlines.Add($b)
    $paragraph.Inlines.Add([Windows.Documents.Run]::new(' After'))
    $document.Blocks.Add($paragraph)
    $editor.Document = $document
    Layout
    $select = $windowType.GetMethod('SelectImage', $instance)
    $wheelImage = $windowType.GetMethod('TryResizeSelectedImageWithWheel', $instance)
    $insideA = $imageA.TranslatePoint([Windows.Point]::new($imageA.ActualWidth/2, $imageA.ActualHeight/2), $editor)
    $insideB = $imageB.TranslatePoint([Windows.Point]::new($imageB.ActualWidth/2, $imageB.ActualHeight/2), $editor)
    Check (-not $wheelImage.Invoke($window, @($insideA, 120)) -and $imageA.Width -eq 100) 'An unselected image does not intercept wheel input.'
    $leftClick = [Windows.Input.MouseButtonEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, [Windows.Input.MouseButton]::Left)
    $leftClick.RoutedEvent = [Windows.UIElement]::PreviewMouseLeftButtonDownEvent
    $leftClick.Source = $imageA
    $windowType.GetMethod('EditorImageClick', $instance).Invoke($window, @($editor, $leftClick))
    Check ($leftClick.Handled -and [object]::ReferenceEquals($windowType.GetField('selectedImage', $instance).GetValue($window), $imageA)) 'A left-click on an embedded bitmap selects that image.'
    $original = $imageA.Width
    Check ($wheelImage.Invoke($window, @($insideA, 120)) -and $imageA.Width -gt $original) 'Wheel up over the selected image enlarges it.'
    Layout
    $insideA = $imageA.TranslatePoint([Windows.Point]::new($imageA.ActualWidth/2, $imageA.ActualHeight/2), $editor)
    Check ($wheelImage.Invoke($window, @($insideA, -120)) -and [Math]::Abs($imageA.Width - $original) -lt 0.01) 'Wheel down over the selected image shrinks it proportionally.'
    Layout
    $insideB = $imageB.TranslatePoint([Windows.Point]::new($imageB.ActualWidth/2, $imageB.ActualHeight/2), $editor)
    $beforeA = $imageA.Width; $beforeB = $imageB.Width
    Check (-not $wheelImage.Invoke($window, @($insideB, 120)) -and $imageA.Width -eq $beforeA -and $imageB.Width -eq $beforeB) 'Wheel over a different image does not resize either image.'
    Check (-not $wheelImage.Invoke($window, @([Windows.Point]::new($editor.ActualWidth - 2, $editor.ActualHeight - 2), 120)) -and $imageA.Width -eq $beforeA) 'Wheel outside the selected image remains available to the note.'
    Check (-not $wheelImage.Invoke($window, @($insideA, 0))) 'A zero wheel delta does not resize the image.'
    $rightClick = [Windows.Input.MouseButtonEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, [Windows.Input.MouseButton]::Right)
    $rightClick.RoutedEvent = [Windows.UIElement]::PreviewMouseRightButtonDownEvent
    $rightClick.Source = $imageB
    $windowType.GetMethod('PrepareImageMenu', $instance).Invoke($window, @($editor, $rightClick))
    $selected = $windowType.GetField('selectedImage', $instance).GetValue($window)
    Check ([object]::ReferenceEquals($selected, $imageB) -and $rightClick.Handled -and
        [object]::ReferenceEquals($imageB.ContextMenu.PlacementTarget, $imageB) -and
        $imageB.ContextMenu.Placement -eq [Windows.Controls.Primitives.PlacementMode]::MousePoint) 'Right-click selects and targets the image under the pointer.'
    $menu = $imageB.ContextMenu
    $labels = @($menu.Items | ForEach-Object { [Windows.Automation.AutomationProperties]::GetName($_) })
    $shortcuts = @($menu.Items | ForEach-Object { $_.InputGestureText })
    Check ($menu.Items.Count -eq 4 -and ($labels -join ',') -eq '复制,粘贴,剪切,删除' -and
        ($shortcuts -join ',') -eq 'Ctrl+C,Ctrl+V,Ctrl+X,Delete') 'The image menu exposes only copy, paste, cut and delete with clear shortcuts.'
    Check ($select.Invoke($window, @($imageA))) 'The original image can be selected again after opening another image menu.'
    $imageA.Width = 24; Layout
    $insideA = $imageA.TranslatePoint([Windows.Point]::new($imageA.ActualWidth/2, $imageA.ActualHeight/2), $editor)
    Check ($wheelImage.Invoke($window, @($insideA, -120)) -and $imageA.Width -eq 24) 'Image wheel shrink respects the minimum width.'
    $imageA.Width = $editor.ActualWidth - 12; Layout
    $insideA = $imageA.TranslatePoint([Windows.Point]::new($imageA.ActualWidth/2, $imageA.ActualHeight/2), $editor)
    Check ($wheelImage.Invoke($window, @($insideA, 120)) -and $imageA.Width -le $editor.ActualWidth - 12 + 0.01) 'Image wheel enlargement respects the editor width.'

    Check ($select.Invoke($window, @($imageB))) 'The target for image paste can be selected.'
    $originalText = Text-Only $paragraph
    $replacement = TestBitmap
    $data = [Windows.DataObject]::new()
    $data.SetData([Windows.DataFormats]::Bitmap, $replacement)
    $paste = [Windows.DataObjectPastingEventArgs]::new($data, $false, [Windows.DataFormats]::Bitmap)
    $windowType.GetMethod('PasteImage', $instance).Invoke($window, @($editor, $paste))
    $images = @($paragraph.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] })
    $pasted = @($images | Where-Object { [object]::ReferenceEquals($_.Child.Source, $replacement) })
    $newText = Text-Only $paragraph
    Check ($paste.CommandCancelled -and $images.Count -eq 2 -and $pasted.Count -eq 1 -and
        [object]::ReferenceEquals($images[0].Child, $imageA) -and $newText -eq $originalText) "In-memory image paste replaces only the selected image and preserves surrounding text (cancelled=$($paste.CommandCancelled), images=$($images.Count), pasted=$($pasted.Count), text=$($newText -eq $originalText))."
    $deleteItem = $imageA.ContextMenu.Items[3]
    $deleteItem.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.MenuItem]::ClickEvent, $deleteItem))
    $images = @($paragraph.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] })
    Check ($images.Count -eq 1 -and [object]::ReferenceEquals($images[0].Child.Source, $replacement) -and
        ((Text-Only $paragraph) -eq $originalText)) 'The delete menu removes its own image and preserves the other image and text.'

    # Simulate an image reconstructed with a deserialized menu that has no
    # runtime handlers. Selecting it must attach a working menu afresh.
    $restoredImage = [Windows.Controls.Image]::new(); $restoredImage.Source = TestBitmap; $restoredImage.Width = 70
    $staleMenu = [Windows.Controls.ContextMenu]::new(); $restoredImage.ContextMenu = $staleMenu
    $paragraph.Inlines.Add([Windows.Documents.InlineUIContainer]::new($restoredImage)); Layout
    $restoredClick = [Windows.Input.MouseButtonEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, [Windows.Input.MouseButton]::Right)
    $restoredClick.RoutedEvent = [Windows.UIElement]::PreviewMouseRightButtonDownEvent; $restoredClick.Source = $restoredImage
    $windowType.GetMethod('PrepareImageMenu', $instance).Invoke($window, @($editor, $restoredClick))
    Check (-not [object]::ReferenceEquals($restoredImage.ContextMenu, $staleMenu) -and $restoredImage.ContextMenu.Items.Count -eq 4) 'A reconstructed image receives fresh runtime menu handlers.'
    $restoredDelete = $restoredImage.ContextMenu.Items[3]
    $restoredDelete.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.MenuItem]::ClickEvent, $restoredDelete))
    $images = @($paragraph.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] })
    Check ($restoredClick.Handled -and $images.Count -eq 1 -and [object]::ReferenceEquals($images[0].Child.Source, $replacement)) 'A reconstructed image has a working delete command.'

    $retainedImage = $images[0].Child
    Check ($select.Invoke($window, @($retainedImage))) 'The remaining image can be selected before choosing normal text.'
    $textRun = $paragraph.Inlines | Where-Object { $_ -is [Windows.Documents.Run] -and $_.Text.Contains('Between') } | Select-Object -First 1
    $textStart = $textRun.ContentStart.GetPositionAtOffset($textRun.Text.IndexOf('Between'), [Windows.Documents.LogicalDirection]::Forward)
    $textEnd = $textStart.GetPositionAtOffset('Between'.Length, [Windows.Documents.LogicalDirection]::Forward)
    $editor.Selection.Select($textStart, $textEnd)
    $secondBitmap = TestBitmap
    $secondData = [Windows.DataObject]::new(); $secondData.SetData([Windows.DataFormats]::Bitmap, $secondBitmap)
    $secondPaste = [Windows.DataObjectPastingEventArgs]::new($secondData, $false, [Windows.DataFormats]::Bitmap)
    $windowType.GetMethod('PasteImage', $instance).Invoke($window, @($editor, $secondPaste))
    $images = @($paragraph.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] })
    Check ($secondPaste.CommandCancelled -and $images.Count -eq 2 -and
        @($images | Where-Object { [object]::ReferenceEquals($_.Child, $retainedImage) }).Count -eq 1 -and
        @($images | Where-Object { [object]::ReferenceEquals($_.Child.Source, $secondBitmap) }).Count -eq 1 -and
        (Text-Only $paragraph) -eq $originalText.Replace('Between', '')) 'Pasting over text never deletes an image that was previously selected.'

    $plainData = [Windows.DataObject]::new()
    $plainData.SetData([Windows.DataFormats]::UnicodeText, 'plain clipboard sample')
    $plainPaste = [Windows.DataObjectPastingEventArgs]::new($plainData, $false, [Windows.DataFormats]::UnicodeText)
    $beforePlain = [Windows.Documents.TextRange]::new($editor.Document.ContentStart, $editor.Document.ContentEnd).Text
    $windowType.GetMethod('PasteImage', $instance).Invoke($window, @($editor, $plainPaste))
    Check (-not $plainPaste.CommandCancelled -and
        ([Windows.Documents.TextRange]::new($editor.Document.ContentStart, $editor.Document.ContentEnd).Text -eq $beforePlain)) 'Normal text paste is left to the standard RichTextBox path.'

    $textPara = Set-Paragraph 'Before selected After'
    Select-Text $textPara 'selected'
    $textMenu = $editor.ContextMenu
    $textLabels = @($textMenu.Items | ForEach-Object { [Windows.Automation.AutomationProperties]::GetName($_) })
    $textShortcuts = @($textMenu.Items | ForEach-Object { $_.InputGestureText })
    Check ($textMenu.Items.Count -eq 4 -and ($textLabels -join ',') -eq '复制,粘贴,剪切,删除' -and
        ($textShortcuts -join ',') -eq 'Ctrl+C,Ctrl+V,Ctrl+X,Delete') 'The text menu uses the same four commands and shortcuts as the image menu.'
    $textRightClick = [Windows.Input.MouseButtonEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, [Windows.Input.MouseButton]::Right)
    $textRightClick.RoutedEvent = [Windows.UIElement]::PreviewMouseRightButtonDownEvent; $textRightClick.Source = $editor
    $windowType.GetMethod('PrepareImageMenu', $instance).Invoke($window, @($editor, $textRightClick))
    Check ($textRightClick.Handled -and $editor.Selection.Text -eq 'selected' -and
        [object]::ReferenceEquals($textMenu.PlacementTarget, $editor) -and
        $textMenu.Placement -eq [Windows.Controls.Primitives.PlacementMode]::MousePoint) 'Right-clicking selected text keeps the selection and uses the editor menu.'
    $prepared = $windowType.GetMethod('PrepareEditorMenu', $instance).Invoke($window, @($editor, [Windows.Controls.Primitives.PlacementMode]::Bottom))
    Check ([object]::ReferenceEquals($prepared, $textMenu) -and $prepared.Placement -eq [Windows.Controls.Primitives.PlacementMode]::Bottom -and
        $editor.Selection.Text -eq 'selected') 'The keyboard-menu preparation keeps the text selection and positions the same menu at the caret.'
    $mockExecute = [Windows.Input.ExecutedRoutedEventHandler]{ param($sender, $eventArgs) $eventArgs.Handled = $true }
    $mockCanPaste = [Windows.Input.CanExecuteRoutedEventHandler]{ param($sender, $eventArgs) $eventArgs.CanExecute = $true; $eventArgs.Handled = $true }
    $mockBinding = [Windows.Input.CommandBinding]::new([Windows.Input.ApplicationCommands]::Paste, $mockExecute, $mockCanPaste)
    # The production preview handler reads the real clipboard. Detach it while
    # supplying the synthetic CanExecute binding, then restore it immediately.
    $clipboardCanExecute = [Delegate]::CreateDelegate([Windows.Input.CanExecuteRoutedEventHandler], $window,
        $windowType.GetMethod('CanExecuteImageFilePaste', $instance))
    [Windows.Input.CommandManager]::RemovePreviewCanExecuteHandler($editor, $clipboardCanExecute)
    $editor.CommandBindings.Insert(0, $mockBinding)
    try {
        $windowType.GetMethod('RefreshEditorMenu', $instance).Invoke($window, @($textMenu))
        Check ($textMenu.Items[1].IsEnabled -and $textMenu.Items[0].IsEnabled -and $textMenu.Items[2].IsEnabled -and $textMenu.Items[3].IsEnabled) 'Selected text enables editing commands; synthetic paste availability is respected.'
        $editor.Selection.Select($editor.CaretPosition, $editor.CaretPosition)
        $windowType.GetMethod('RefreshEditorMenu', $instance).Invoke($window, @($textMenu))
        Check ($textMenu.Items[1].IsEnabled -and -not $textMenu.Items[0].IsEnabled -and -not $textMenu.Items[2].IsEnabled -and -not $textMenu.Items[3].IsEnabled) 'An empty selection disables copy, cut and delete while paste remains available.'
    } finally {
        $editor.CommandBindings.Remove($mockBinding)
        [Windows.Input.CommandManager]::AddPreviewCanExecuteHandler($editor, $clipboardCanExecute)
    }
    $blankClick = [Windows.Input.MouseButtonEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, [Windows.Input.MouseButton]::Right)
    $blankClick.RoutedEvent = [Windows.UIElement]::PreviewMouseRightButtonDownEvent; $blankClick.Source = $editor
    $windowType.GetMethod('PrepareImageMenu', $instance).Invoke($window, @($editor, $blankClick))
    Check ($blankClick.Handled -and $editor.Selection.IsEmpty -and [object]::ReferenceEquals($textMenu.PlacementTarget, $editor) -and
        $textMenu.Placement -eq [Windows.Controls.Primitives.PlacementMode]::MousePoint) 'Right-clicking blank editor space prepares the same menu without inventing a selection.'

    $null = [IO.Directory]::CreateDirectory($tempRoot)
    $pngPath = Join-Path $tempRoot 'sample.png'
    $pngEncoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $pngEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create((TestBitmap)))
    $pngStream = [IO.File]::Create($pngPath)
    try { $pngEncoder.Save($pngStream) } finally { $pngStream.Dispose() }
    $brokenPath = Join-Path $tempRoot 'broken.png'
    [IO.File]::WriteAllText($brokenPath, 'not an image')
    $textPath = Join-Path $tempRoot 'plain.txt'
    [IO.File]::WriteAllText($textPath, 'plain file')

    $fileData = File-Data @($pngPath)
    $hasImageFiles = $windowType.GetMethod('HasImageFiles', [Reflection.BindingFlags]'Static,NonPublic')
    $otherData = File-Data @($textPath)
    Check ($hasImageFiles.Invoke($null, @($fileData)) -and -not $hasImageFiles.Invoke($null, @($otherData)) -and
        -not $hasImageFiles.Invoke($null, @($plainData))) 'FileDrop format detection distinguishes image files from text and other file types.'
    $filePara = Set-Paragraph 'Before replace-me After'
    Select-Text $filePara 'replace-me'
    $filePaste = [Windows.DataObjectPastingEventArgs]::new($fileData, $false, [Windows.DataFormats]::FileDrop)
    $windowType.GetMethod('PasteImage', $instance).Invoke($window, @($editor, $filePaste))
    $filesInserted = @($filePara.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] })
    Check ($filePaste.CommandCancelled -and $filesInserted.Count -eq 1 -and (Text-Only $filePara) -eq 'Before  After') 'Explorer FileDrop paste inserts an image and replaces only the selected text.'

    $emptyPara = Set-Paragraph ''
    $filePaste = [Windows.DataObjectPastingEventArgs]::new($fileData, $false, [Windows.DataFormats]::FileDrop)
    $windowType.GetMethod('PasteImage', $instance).Invoke($window, @($editor, $filePaste))
    Check ($filePaste.CommandCancelled -and @($emptyPara.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] }).Count -eq 1) 'Explorer FileDrop paste also inserts an image in an empty note.'
    Check ($editor.Undo() -and @($emptyPara.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] }).Count -eq 0) 'Undo removes an imported image without throwing.'

    $badPara = Set-Paragraph 'Before keep-me After'
    Select-Text $badPara 'keep-me'
    $badData = File-Data @($brokenPath)
    $badPaste = [Windows.DataObjectPastingEventArgs]::new($badData, $false, [Windows.DataFormats]::FileDrop)
    $windowType.GetMethod('PasteImage', $instance).Invoke($window, @($editor, $badPaste))
    Check ((Text-Only $badPara) -eq 'Before keep-me After' -and $editor.Selection.Text -eq 'keep-me' -and
        @($badPara.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] }).Count -eq 0) 'A corrupt image file leaves the selected original text untouched.'

    $otherPara = Set-Paragraph 'Do not replace'
    Select-Text $otherPara 'replace'
    $otherPaste = [Windows.DataObjectPastingEventArgs]::new($otherData, $false, [Windows.DataFormats]::FileDrop)
    $windowType.GetMethod('PasteImage', $instance).Invoke($window, @($editor, $otherPaste))
    Check (-not $otherPaste.CommandCancelled -and (Text-Only $otherPara) -eq 'Do not replace' -and
        $editor.Selection.Text -eq 'replace') 'A non-image file is left to the normal paste path without erasing selection.'

    $dropPara = Set-Paragraph 'Begin End'
    $run = $dropPara.Inlines.FirstInline
    $dropPoint = $run.ContentStart.GetPositionAtOffset(5, [Windows.Documents.LogicalDirection]::Forward)
    $import = $windowType.GetMethod('TryInsertImageFiles', $instance)
    Check ($import.Invoke($window, @($fileData, $dropPoint)) -and (Text-Only $dropPara) -eq 'Begin End' -and
        @($dropPara.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] }).Count -eq 1) 'Dropping a local image at a text position preserves neighboring text.'

    $dropPara = Set-Paragraph 'Drag here'
    $dragCtor = [Windows.DragEventArgs].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic,Public') | Select-Object -First 1
    $dragArgs = $dragCtor.Invoke(@($fileData, [Windows.DragDropKeyStates]::None, [Windows.DragDropEffects]::Copy, $editor, [Windows.Point]::new(20, 8)))
    $dragArgs.RoutedEvent = [Windows.DragDrop]::PreviewDropEvent; $dragArgs.Source = $editor
    $windowType.GetMethod('DropImages', $instance).Invoke($window, @($editor, $dragArgs))
    Check ($dragArgs.Handled -and (Text-Only $dropPara) -eq 'Drag here' -and
        @($dropPara.Inlines | Where-Object { $_ -is [Windows.Documents.InlineUIContainer] }).Count -eq 1) 'The actual drop handler accepts synthetic Explorer file data without changing text.'

    $windowType.GetMethod('DismissImageResize', $instance).Invoke($window, $null)
    $longText = [Windows.Documents.Paragraph]::new([Windows.Documents.Run]::new((('A line of note text' + [Environment]::NewLine) * 100)))
    $editor.Document = [Windows.Documents.FlowDocument]::new($longText)
    Layout
    $scrollHost = $editor.Template.FindName('PART_ContentHost', $editor)
    $scroll = $scrollHost.Content
    $scroll.SetVerticalOffset(0); Layout
    $before = $scroll.VerticalOffset
    $wheelArgs = [Windows.Input.MouseWheelEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, -120)
    $wheelArgs.RoutedEvent = [Windows.UIElement]::PreviewMouseWheelEvent; $wheelArgs.Source = $editor
    $windowType.GetMethod('ScrollEditorWithWheel', $instance).Invoke($window, @($editor, $wheelArgs))
    Check ($wheelArgs.Handled -and $scroll.VerticalOffset -gt $before) 'Normal note text still scrolls when no image is selected.'

    # Keep an image selected beyond the viewport and send a normal wheel event
    # through the real editor handler. The image must not steal text scrolling.
    $longImage = [Windows.Controls.Image]::new(); $longImage.Source = TestBitmap; $longImage.Width = 90
    $editor.Document.Blocks.Add([Windows.Documents.Paragraph]::new([Windows.Documents.InlineUIContainer]::new($longImage)))
    Layout
    Check ($select.Invoke($window, @($longImage))) 'An image outside the text viewport can remain selected.'
    $scroll.SetVerticalOffset(0); Layout
    $before = $scroll.VerticalOffset; $oldWidth = $longImage.Width
    $wheelArgs = [Windows.Input.MouseWheelEventArgs]::new([Windows.Input.Mouse]::PrimaryDevice, 0, -120)
    $wheelArgs.RoutedEvent = [Windows.UIElement]::PreviewMouseWheelEvent; $wheelArgs.Source = $editor
    $windowType.GetMethod('ScrollEditorWithWheel', $instance).Invoke($window, @($editor, $wheelArgs))
    Check ($wheelArgs.Handled -and $scroll.VerticalOffset -gt $before -and $longImage.Width -eq $oldWidth) 'Text wheel scrolls while an offscreen image remains selected.'

    $pictureOnly = Set-Paragraph ''
    $picture = [Windows.Controls.Image]::new()
    $picture.Source = TestBitmap
    $pictureOnly.Inlines.Add([Windows.Documents.InlineUIContainer]::new($picture))
    Check ($window.SaveCurrentNoteSafe() -and [IO.File]::Exists($window.FilePath)) 'An image-only note saves normally.'
    $catalogType = $assembly.GetType('KimNotes.Windows.Notes.NoteCatalogWindow', $true)
    $catalog = [Activator]::CreateInstance($catalogType, $instance, $null, @($workspace), $null)
    $indexType = $assembly.GetType('KimNotes.utils.NoteIndex', $true)
    $index = $indexType.GetMethod('Load', [Reflection.BindingFlags]'Static,Public').Invoke($null, $null)
    $readEntry = $catalogType.GetMethod('ReadEntry', $instance)
    $marker = '[' + [char]22270 + [char]29255 + ']'
    $entry = $readEntry.Invoke($catalog, @($index, $window.FilePath))
    Check ($entry.Name.Trim() -eq $marker) 'The saved image-only note has a searchable picture marker.'
    $index.Remove($window.FilePath)
    $entry = $readEntry.Invoke($catalog, @($index, $window.FilePath))
    Check ($entry.Name.Trim() -eq $marker) 'A cold catalog rebuild retains the same picture marker.'
    $index.Set($window.FilePath, [IO.File]::GetLastWriteTime($window.FilePath), 'old preview', '', 'old preview')
    $entry = $readEntry.Invoke($catalog, @($index, $window.FilePath))
    Check ($entry.Name.Trim() -eq $marker) 'A legacy text-only preview with a matching timestamp and length is rebuilt.'
} finally {
    $timer = $windowType.GetField('autoSave', $instance).GetValue($window)
    if ($timer) { $timer.Stop() }
    [Environment]::SetEnvironmentVariable('KIMNOTES_DATA_DIR', $previousData)
    if ([IO.Directory]::Exists($tempRoot) -and [IO.Path]::GetFileName($tempRoot) -match '^KimNotes-image-actions-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}

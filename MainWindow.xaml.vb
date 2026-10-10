Imports System.Collections.ObjectModel
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Windows
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Media.Imaging
Imports System.Windows.Threading

Class MainWindow
    Private ReadOnly items As New ObservableCollection(Of QueueItem)
    Private ReadOnly dependencies As New DependencyManager()
    Private ReadOnly downloader As New Downloader()
    Private settings As AppSettings
    Private paths As ToolPaths
    Private operation As CancellationTokenSource
    Private busy As Boolean
    Private uiReady As Boolean
    Private filling As Boolean
    Private closeRequested As Boolean
    Private ReadOnly saveTimer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(500)}
    Private startupStarted As Boolean
    Private availableHeights As Integer() = DownloadRules.Heights
    Friend ReadOnly MetadataEditors As New Dictionary(Of String, TextBox)

    Public Sub New()
        InitializeComponent()
        UpdateButton.Content = "Update Tools"
        Dim warning = ""
        settings = JsonStore.LoadSettings(warning)
        For Each item In settings.Queue
            items.Add(item)
        Next
        QueueList.ItemsSource = items
        FormatBox.ItemsSource = DownloadRules.Formats.Select(Function(s) s.ToUpperInvariant()).ToList()
        FormatBox.SelectedItem = settings.Format.ToUpperInvariant()
        QualityBox.ItemsSource = DownloadRules.Heights.Select(Function(h) If(h = 0, "Highest available", h & "p maximum")).ToList()
        QualityBox.SelectedIndex = Array.IndexOf(DownloadRules.Heights, settings.MaxHeight)
        NameModeBox.ItemsSource = {"Original title", "Custom name", "Metadata template"}
        InitializeMetadataEditors()
        FolderBox.Text = settings.OutputFolder
        MetadataCheck.IsChecked = settings.EmbedMetadata
        LoadThemes(settings.Theme)
        uiReady = True
        RefreshDownloadOptions()
        UpdateQueueCount()
        If items.Count > 0 Then QueueList.SelectedIndex = 0
        If warning <> "" Then StatusText.Text = warning
        AddHandler saveTimer.Tick, Sub()
                                       saveTimer.Stop()
                                       SaveSettings()
                                   End Sub
        AddHandler Loaded, AddressOf WindowLoaded
        AddHandler Closing, AddressOf WindowClosing
    End Sub

    Private Async Sub WindowLoaded(sender As Object, e As RoutedEventArgs)
        If startupStarted Then Return
        startupStarted = True
        If Not File.Exists(AppPaths.Inside("tools", "manifest.json")) Then
            MessageBox.Show(Me,
                "The application will now download the required dependencies: yt-dlp, Deno and FFmpeg." & Environment.NewLine & Environment.NewLine &
                "This initial setup only happens once. The application will check for updates to these dependencies every time it opens. You can choose to install available updates by clicking the ""Tool Update Available"" button." & Environment.NewLine & Environment.NewLine &
                "Dependencies are saved in %LOCALAPPDATA%\Programs\MultiVid:" & Environment.NewLine & AppPaths.Root,
                "First-run setup", MessageBoxButton.OK, MessageBoxImage.Information)
        End If
        Await PrepareToolsAsync(False)
    End Sub
    Private Function Selected() As QueueItem
        Return TryCast(QueueList.SelectedItem, QueueItem)
    End Function
    Private Sub UpdateQueueCount()
        QueueCount.Text = items.Count & If(items.Count = 1, " item", " items")
        EmptyQueue.Visibility = If(items.Count = 0, Visibility.Visible, Visibility.Collapsed)
    End Sub
    Private Function BeginWork(message As String) As CancellationToken
        busy = True
        operation = New CancellationTokenSource()
        AddPanel.IsEnabled = False
        QueueActions.IsEnabled = False
        OptionsPanel.IsEnabled = False
        NamingPanel.IsEnabled = False
        MetadataExpander.IsEnabled = False
        UpdateButton.IsEnabled = False
        DownloadButton.IsEnabled = False
        CancelButton.IsEnabled = True
        StatusText.Text = message
        Return operation.Token
    End Function
    Private Sub EndWork()
        busy = False
        AddPanel.IsEnabled = True
        QueueActions.IsEnabled = True
        OptionsPanel.IsEnabled = True
        NamingPanel.IsEnabled = Selected() IsNot Nothing
        UpdateButton.IsEnabled = True
        DownloadButton.IsEnabled = True
        CancelButton.IsEnabled = False
        operation.Dispose()
        operation = Nothing
        SaveSettings()
        RefreshInspector()
        If closeRequested Then Close()
    End Sub
    Private Function ToolProgress() As IProgress(Of String)
        Return New Progress(Of String)(Sub(message) ToolStatus.Text = message)
    End Function
    Private Async Function PrepareToolsAsync(force As Boolean) As Task
        If busy Then Return
        Dim token = BeginWork("Preparing download tools…")
        Try
            If Not force Then
                paths = Await dependencies.CheckOnStartupAsync(ToolProgress(), token)
            ElseIf dependencies.UpdateAvailable Then
                paths = Await dependencies.InstallAvailableUpdatesAsync(ToolProgress(), token)
            Else
                paths = Await dependencies.EnsureAsync(True, ToolProgress(), token)
            End If
            StatusText.Text = "Ready. Add videos or download the queued items."
            Dim item = Selected()
            If item IsNot Nothing AndAlso Not item.PreviewLoaded Then Await ReadPreviewAsync(item, token)
        Catch ex As OperationCanceledException
            StatusText.Text = "Tool setup cancelled. Use Update tools to continue."
        Catch ex As Exception
            ToolStatus.Text = "Tools need attention. " & ex.Message
            StatusText.Text = "Tool setup failed. Use Update tools to retry."
        Finally
            UpdateButton.Content = If(dependencies.UpdateAvailable, "Tool Update Available", "Update Tools")
            EndWork()
        End Try
    End Function
    Private Async Sub UpdateClick(sender As Object, e As RoutedEventArgs)
        Await PrepareToolsAsync(True)
    End Sub
    Private Async Sub AddClick(sender As Object, e As RoutedEventArgs)
        Await AddUrlsAsync()
    End Sub
    Private Async Sub UrlKeyDown(sender As Object, e As KeyEventArgs)
        If e.Key = Key.Enter Then
            e.Handled = True
            Await AddUrlsAsync()
        End If
    End Sub
    Private Async Function AddUrlsAsync() As Task
        If busy Then Return
        Dim urls As New List(Of String)
        Dim playlist = PlaylistCheck.IsChecked.GetValueOrDefault()
        Try
            For Each value In Regex.Split(UrlBox.Text.Trim(), "\s+")
                If value <> "" Then urls.Add(DownloadRules.NormalizeUrl(value, playlist))
            Next
            If urls.Count = 0 Then Throw New ArgumentException("Paste a video link first.")
        Catch ex As Exception
            StatusText.Text = ex.Message
            Return
        End Try
        Dim token = BeginWork("Adding videos and reading previews…")
        Dim added As New List(Of QueueItem)
        Try
            For Each url In urls.Distinct()
                Dim existing = items.FirstOrDefault(Function(i) i.Url = url AndAlso i.IsPlaylist = playlist)
                If existing IsNot Nothing Then
                    QueueList.SelectedItem = existing
                    Continue For
                End If
                Dim item As New QueueItem With {.Url = url, .IsPlaylist = playlist, .Title = New Uri(url).Host & If(playlist, " playlist", " video"), .OutputFormat = settings.Format, .MaxHeight = settings.MaxHeight, .Detail = "Waiting for preview"}
                items.Add(item)
                added.Add(item)
            Next
            If added.Count = 0 Then
                StatusText.Text = "These links are already in the queue."
                Return
            End If
            QueueList.SelectedItem = added(0)
            UrlBox.Clear()
            UpdateQueueCount()
            SaveSettings()
            paths = Await dependencies.EnsureAsync(False, ToolProgress(), token)
            For Each item In added
                token.ThrowIfCancellationRequested()
                Await ReadPreviewAsync(item, token)
            Next
            StatusText.Text = added.Count & " item(s) added. Choose your format, then download the queue."
        Catch ex As OperationCanceledException
            StatusText.Text = "Preview loading cancelled. Added items remain queued."
        Catch ex As Exception
            StatusText.Text = "Items were queued, but preparation failed: " & ex.Message
        Finally
            EndWork()
        End Try
    End Function
    Private Async Function ReadPreviewAsync(item As QueueItem, token As CancellationToken) As Task
        Try
            Await downloader.ReadMetadataAsync(paths, item, token)
            MediaOptions.InitializeSelection(item, settings)
            If item.Status = "Queued" Then item.Detail = If(item.IsPlaylist, "Full playlist · all accessible videos", item.Uploader & " · " & item.Duration)
        Catch ex As OperationCanceledException
            If token.IsCancellationRequested Then Throw
            item.Detail = "Preview timed out. You can retry downloading this item."
        Catch ex As Exception
            item.Detail = "Preview unavailable: " & ex.Message
        End Try
        If Selected() Is item Then RefreshInspector()
        SaveSettings()
    End Function
    Private Async Sub QueueSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not uiReady Then Return
        RefreshInspector()
        Dim item = Selected()
        If busy OrElse paths Is Nothing OrElse item Is Nothing OrElse item.PreviewLoaded Then Return
        Dim token = BeginWork("Reading selected video…")
        Try
            Await ReadPreviewAsync(item, token)
        Catch ex As OperationCanceledException
            StatusText.Text = "Preview cancelled."
        Finally
            EndWork()
        End Try
    End Sub
    Private Sub RefreshInspector()
        If Not uiReady Then Return
        filling = True
        Try
            Dim item = Selected()
            RefreshDownloadOptions()
            NamingPanel.IsEnabled = item IsNot Nothing AndAlso Not busy
            RefreshEmbeddedEditor(item)
            PreviewImage.Source = Nothing
            ThumbnailPlaceholder.Visibility = Visibility.Visible
            If item Is Nothing Then
                PreviewTitle.Text = "Nothing selected yet"
                PreviewMeta.Text = "Thumbnail, creator and duration appear here."
                PreviewDetail.Text = ""
                FilenamePreview.Text = ""
                NameBox.Text = ""
                Return
            End If
            PreviewTitle.Text = item.Title
            PreviewMeta.Text = String.Join("  ·  ", {item.Provider, item.Uploader, item.Duration, item.UploadDate}.Where(Function(s) Not String.IsNullOrEmpty(s)))
            PreviewDetail.Text = item.Detail
            LoadThumbnail(item.Thumbnail)
            NameModeBox.SelectedItem = item.NameMode
            NameBox.Text = If(item.NameMode = "Metadata template", item.NameTemplate, item.CustomName)
            NameBox.Visibility = If(item.NameMode = "Original title", Visibility.Collapsed, Visibility.Visible)
            TokenHelp.Visibility = If(item.NameMode = "Metadata template", Visibility.Visible, Visibility.Collapsed)
            RefreshFilename()
        Finally
            filling = False
        End Try
    End Sub
    Private Sub LoadThumbnail(url As String)
        If String.IsNullOrWhiteSpace(url) Then Return
        Try
            Dim uri As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, uri) OrElse Not {"http", "https"}.Contains(uri.Scheme) OrElse uri.UserInfo <> "" Then Return
            Dim bitmap As New BitmapImage()
            bitmap.BeginInit()
            bitmap.DecodePixelWidth = 800
            bitmap.UriSource = uri
            bitmap.EndInit()
            AddHandler bitmap.DownloadFailed, Sub(sender, e)
                                                  If PreviewImage.Source Is bitmap Then
                                                      PreviewImage.Source = Nothing
                                                      ThumbnailPlaceholder.Visibility = Visibility.Visible
                                                  End If
                                              End Sub
            PreviewImage.Source = bitmap
            ThumbnailPlaceholder.Visibility = Visibility.Collapsed
        Catch
            ThumbnailPlaceholder.Visibility = Visibility.Visible
        End Try
    End Sub
    Private Sub NameChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not uiReady OrElse filling OrElse Selected() Is Nothing Then Return
        Selected().NameMode = CStr(NameModeBox.SelectedItem)
        RefreshInspector()
        ScheduleSave()
        If Selected().NameMode = "Metadata template" AndAlso Not settings.MetadataHelpShown Then ShowMetadataHelp()
    End Sub
    Private Sub NameTextChanged(sender As Object, e As TextChangedEventArgs)
        If Not uiReady OrElse filling OrElse Selected() Is Nothing Then Return
        If Selected().NameMode = "Metadata template" Then
            Selected().NameTemplate = NameBox.Text
        Else
            Selected().CustomName = NameBox.Text
        End If
        RefreshFilename()
        ScheduleSave()
    End Sub
    Private Sub InitializeMetadataEditors()
        AudioBitrateBox.ItemsSource = MetadataRules.Bitrates.Select(Function(rate) If(rate = 0, "Best source / quality (automatic)", rate & " kbps")).ToList()
        For Each field In MetadataRules.Fields
            Dim panel As New StackPanel With {.Margin = New Thickness(0, 0, 8, 10)}
            panel.Children.Add(New TextBlock With {.Text = field.Value, .Style = DirectCast(FindResource("Label"), Style), .TextWrapping = TextWrapping.Wrap})
            Dim editor As New TextBox With {.Tag = field.Key, .ToolTip = "Blank keeps the website value. Enter text or {tokens}.", .MaxLength = 12000}
            If {"description", "comment", "lyrics"}.Contains(field.Key) Then
                editor.AcceptsReturn = True
                editor.TextWrapping = TextWrapping.Wrap
                editor.MaxHeight = 120
                editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            End If
            System.Windows.Automation.AutomationProperties.SetName(editor, "Embedded " & field.Value)
            AddHandler editor.TextChanged, AddressOf EmbeddedTagChanged
            MetadataEditors(field.Key) = editor
            panel.Children.Add(editor)
            MetadataFieldsPanel.Children.Add(panel)
        Next
    End Sub
    Private Sub RefreshEmbeddedEditor(item As QueueItem)
        MetadataExpander.IsEnabled = item IsNot Nothing AndAlso Not busy
        MetadataFieldsPanel.IsEnabled = settings.EmbedMetadata
        For Each editor In MetadataEditors
            Dim value = ""
            If item IsNot Nothing Then
                If item.EmbeddedTags Is Nothing Then item.EmbeddedTags = New Dictionary(Of String, String)
                item.EmbeddedTags.TryGetValue(editor.Key, value)
            End If
            editor.Value.Text = If(value, "")
        Next
        RefreshMetadataStatus()
    End Sub
    Private Sub RefreshEncodingOptions()
        Dim item = Selected()
        Dim format = If(item Is Nothing, settings.Format, MediaOptions.FormatFor(item, settings))
        AudioBitrateBox.IsEnabled = item IsNot Nothing AndAlso MetadataRules.SupportsBitrate(format)
        AudioBitrateBox.SelectedIndex = If(item Is Nothing, 0, Math.Max(0, Array.IndexOf(MetadataRules.Bitrates, item.AudioBitrateKbps)))
        RefreshMetadataAvailability()
        BitrateHelp.Text = If(MetadataRules.SupportsBitrate(format), "A bitrate target encodes new audio files, including sources already in this format. MP3 uses a fixed rate; AAC and Opus can vary. Higher targets cannot restore missing source quality.", "Bitrate is read from the media. Video keeps its source bitrate; WAV and FLAC are lossless and do not use a bitrate target.")
    End Sub
    Private Sub AudioBitrateChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not uiReady OrElse filling OrElse Selected() Is Nothing OrElse AudioBitrateBox.SelectedIndex < 0 Then Return
        Selected().AudioBitrateKbps = MetadataRules.Bitrates(AudioBitrateBox.SelectedIndex)
        ScheduleSave()
    End Sub
    Private Sub EmbeddedTagChanged(sender As Object, e As TextChangedEventArgs)
        If Not uiReady OrElse filling OrElse Selected() Is Nothing OrElse busy Then Return
        Dim editor = DirectCast(sender, TextBox)
        Dim item = Selected()
        Dim field = CStr(editor.Tag)
        If item.EmbeddedTags Is Nothing Then item.EmbeddedTags = New Dictionary(Of String, String)
        If String.IsNullOrWhiteSpace(editor.Text) Then
            item.EmbeddedTags.Remove(field)
        Else
            item.EmbeddedTags(field) = editor.Text
        End If
        RefreshMetadataAvailability()
        RefreshMetadataStatus()
        ScheduleSave()
    End Sub
    Private Sub RefreshMetadataAvailability()
        Dim item = Selected()
        Dim format = If(item Is Nothing, settings.Format, MediaOptions.FormatFor(item, settings))
        Dim supported = MetadataRules.SupportedFields(format)
        For Each field In MetadataEditors
            Dim available = supported.Contains(field.Key)
            Dim hasValue = item IsNot Nothing AndAlso item.EmbeddedTags IsNot Nothing AndAlso item.EmbeddedTags.ContainsKey(field.Key)
            field.Value.IsEnabled = available OrElse hasValue
            field.Value.ToolTip = If(available, "Blank keeps the website value. Enter text or {tokens}.", "This tag is not supported in " & format.ToUpperInvariant() & ". Clear an existing value or choose a compatible format.")
            Dim label = DirectCast(DirectCast(field.Value.Parent, StackPanel).Children(0), TextBlock)
            label.Text = MetadataRules.Fields(field.Key) & If(available, "", " (unsupported)")
        Next
    End Sub
    Private Sub RefreshMetadataStatus()
        If Selected() Is Nothing Then
            MetadataValidation.Text = "Select a queued item to edit its tags."
        ElseIf Not settings.EmbedMetadata Then
            MetadataValidation.Text = "Enable the Embed metadata checkbox to write these tags."
        Else
            Try
                MetadataRules.Arguments(Selected(), MediaOptions.FormatFor(Selected(), settings))
                MetadataValidation.Text = "Tags will be embedded in each new file. Empty fields retain available provider metadata."
            Catch ex As ArgumentException
                MetadataValidation.Text = ex.Message
            End Try
        End If
    End Sub
    Private Sub RefreshFilename()
        Dim item = Selected()
        If item Is Nothing Then Return
        Try
            FilenamePreview.Text = "Preview: " & DownloadRules.NamePreview(item, MediaOptions.FormatFor(item, settings)) & If(item.IsPlaylist, "  ·  Each video uses its own metadata.", "")
            If item.NameMode = "Metadata template" Then
                Dim tags = InlineMetadata.Parse(item.NameTemplate).Tags
                If tags.Count > 0 Then FilenamePreview.Text &= Environment.NewLine & If(settings.EmbedMetadata, "Embedded tags: ", "Embedding disabled: ") & String.Join(" · ", tags.Select(Function(field) MetadataRules.Fields(field.Key) & ": " & field.Value))
            End If
        Catch ex As Exception
            FilenamePreview.Text = ex.Message
        End Try
        RefreshMetadataStatus()
    End Sub
    Private Sub RefreshDownloadOptions()
        Dim wasFilling = filling
        filling = True
        Try
            Dim item = Selected()
            If item IsNot Nothing Then MediaOptions.InitializeSelection(item, settings)
            Dim formats = MediaOptions.AvailableFormats(item)
            Dim format = If(item Is Nothing, settings.Format, MediaOptions.FormatFor(item, settings))
            FormatBox.ItemsSource = formats.Select(Function(s) s.ToUpperInvariant()).ToList()
            FormatBox.SelectedItem = format.ToUpperInvariant()
            FormatBox.IsEnabled = formats.Length > 0
            availableHeights = MediaOptions.AvailableHeights(item, format)
            Dim height = If(item Is Nothing, settings.MaxHeight, MediaOptions.HeightFor(item, settings))
            If item Is Nothing AndAlso Not availableHeights.Contains(height) Then availableHeights = {0}.Concat(availableHeights.Skip(1).Concat({height}).Distinct().OrderByDescending(Function(h) h)).ToArray()
            QualityBox.ItemsSource = availableHeights.Select(Function(h) If(h = 0, "Highest available", h & "p maximum")).ToList()
            QualityBox.SelectedIndex = Math.Max(0, Array.IndexOf(availableHeights, height))
            FormatBox.ToolTip = If(item Is Nothing, "Defaults for new queue items. Select a video to choose its own output.", "Output for this queued item. Audio formats use FFmpeg conversion.")
            If item IsNot Nothing AndAlso item.SourceFormatsKnown Then FormatBox.ToolTip = CStr(FormatBox.ToolTip) & Environment.NewLine & "Source containers: " & String.Join(", ", item.SourceFormats.Select(Function(s) s.Extension.ToUpperInvariant()).Where(Function(s) s <> "").Distinct())
            UpdateQualityHint()
            RefreshEncodingOptions()
        Finally
            filling = wasFilling
        End Try
    End Sub
    Private Sub OptionsChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not uiReady OrElse filling OrElse FormatBox.SelectedItem Is Nothing OrElse QualityBox.SelectedIndex < 0 Then Return
        Dim item = Selected()
        Dim format = CStr(FormatBox.SelectedItem).ToLowerInvariant()
        If item Is Nothing Then
            settings.Format = format
            settings.MaxHeight = availableHeights(QualityBox.SelectedIndex)
        Else
            item.OutputFormat = format
            item.MaxHeight = availableHeights(QualityBox.SelectedIndex)
            settings.Format = format
            settings.MaxHeight = item.MaxHeight
        End If
        RefreshDownloadOptions()
        RefreshFilename()
        ScheduleSave()
    End Sub
    Private Sub UpdateQualityHint()
        Dim item = Selected()
        Dim format = If(item Is Nothing, settings.Format, MediaOptions.FormatFor(item, settings))
        Dim audio = DownloadRules.IsAudio(format)
        QualityBox.IsEnabled = Not audio AndAlso availableHeights.Length > 1
        QualityHint.Text = If(audio, "Best source audio; converted to " & format.ToUpperInvariant() & ".", "Best compatible source. No upscaling.")
        If item Is Nothing Then QualityHint.Text = "Defaults for new items. Select a video for its options."
        If item IsNot Nothing AndAlso item.IsPlaylist Then QualityHint.Text = "Formats vary by entry; converted when needed."
        If item IsNot Nothing AndAlso Not item.IsPlaylist AndAlso Not item.SourceFormatsKnown Then QualityHint.Text = "Available formats are checked when the preview loads."
        If item IsNot Nothing AndAlso item.SourceFormatsKnown AndAlso MediaOptions.AvailableFormats(item).Length = 0 Then QualityHint.Text = "No downloadable media streams were found."
        QualityHint.ToolTip = QualityHint.Text
    End Sub
    Private Sub MetadataChanged(sender As Object, e As RoutedEventArgs)
        If Not uiReady Then Return
        settings.EmbedMetadata = MetadataCheck.IsChecked.GetValueOrDefault()
        MetadataFieldsPanel.IsEnabled = settings.EmbedMetadata
        RefreshFilename()
        RefreshMetadataStatus()
        ScheduleSave()
    End Sub
    Private Sub FolderChanged(sender As Object, e As RoutedEventArgs)
        If Not uiReady Then Return
        settings.OutputFolder = FolderBox.Text.Trim()
        ScheduleSave()
    End Sub
    Private Sub ScheduleSave()
        saveTimer.Stop()
        saveTimer.Start()
    End Sub
    Private Sub SaveSettings()
        If Not uiReady Then Return
        Try
            settings.Queue = items.ToList()
            JsonStore.Write(AppPaths.Inside(JsonStore.SettingsFilename), settings)
        Catch ex As Exception
            StatusText.Text = "Could not save your queue: " & ex.Message
        End Try
    End Sub
    Private Sub BrowseClick(sender As Object, e As RoutedEventArgs)
        Using dialog As New System.Windows.Forms.FolderBrowserDialog With {.Description = "Choose the folder for downloaded media", .SelectedPath = settings.OutputFolder, .ShowNewFolderButton = True}
            If dialog.ShowDialog() = System.Windows.Forms.DialogResult.OK Then
                settings.OutputFolder = dialog.SelectedPath
                FolderBox.Text = dialog.SelectedPath
                SaveSettings()
            End If
        End Using
    End Sub
    Private Sub OpenFolderClick(sender As Object, e As RoutedEventArgs)
        OpenFolder(FolderBox.Text.Trim())
    End Sub
    Private Sub OpenFolder(folder As String)
        Try
            If Not Path.IsPathRooted(folder) Then Throw New IOException("Choose an absolute output folder first.")
            If Not Directory.Exists(folder) Then Throw New IOException("This folder does not exist yet. It will be created when downloading.")
            Process.Start(New ProcessStartInfo("explorer.exe", ProcessRunner.Quote(Path.GetFullPath(folder))) With {.UseShellExecute = True})
        Catch ex As Exception
            StatusText.Text = ex.Message
        End Try
    End Sub
    Private Sub SourceClick(sender As Object, e As RoutedEventArgs)
        Dim item = Selected()
        If item Is Nothing Then Return
        Try
            Process.Start(New ProcessStartInfo(DownloadRules.NormalizeUrl(item.Url, item.IsPlaylist)) With {.UseShellExecute = True})
        Catch ex As Exception
            StatusText.Text = ex.Message
        End Try
    End Sub
    Private Sub OpenFileClick(sender As Object, e As RoutedEventArgs)
        Dim item = Selected()
        If item Is Nothing Then Return
        Try
            If Not File.Exists(item.OutputPath) Then Throw New IOException("No saved file is available for this item yet.")
            If Not DownloadRules.Formats.Contains(Path.GetExtension(item.OutputPath).TrimStart("."c).ToLowerInvariant()) Then Throw New IOException("The saved path is not a supported media file.")
            Process.Start(New ProcessStartInfo(item.OutputPath) With {.UseShellExecute = True})
        Catch ex As Exception
            StatusText.Text = ex.Message
        End Try
    End Sub
    Private Sub MetadataHelpClick(sender As Object, e As RoutedEventArgs)
        ShowMetadataHelp()
    End Sub
    Private Sub ShowMetadataHelp()
        MessageBox.Show(Me, InlineMetadata.HelpText, "Filename and embedded metadata help", MessageBoxButton.OK, MessageBoxImage.Information)
        settings.MetadataHelpShown = True
        ScheduleSave()
    End Sub
    Private Sub ViewLogClick(sender As Object, e As RoutedEventArgs)
        Dim item = Selected()
        If item Is Nothing Then Return
        Try
            If Not File.Exists(item.LogPath) Then Throw New IOException("A log is created when this item is downloaded.")
            Process.Start(New ProcessStartInfo("notepad.exe", ProcessRunner.Quote(item.LogPath)) With {.UseShellExecute = True})
        Catch ex As Exception
            StatusText.Text = ex.Message
        End Try
    End Sub
    Private Sub PreviewClick(sender As Object, e As RoutedEventArgs)
        Dim item = Selected()
        If item Is Nothing Then Return
        Dim window As New Window With {.Title = "Preview · " & item.Title, .Width = 780, .Height = 680, .MinWidth = 540, .MinHeight = 420, .Owner = Me, .WindowStartupLocation = WindowStartupLocation.CenterOwner}
        Dim stack As New StackPanel With {.Margin = New Thickness(24)}
        Dim image As New Image With {.Height = 300, .Stretch = System.Windows.Media.Stretch.Uniform, .Source = PreviewImage.Source}
        stack.Children.Add(image)
        stack.Children.Add(New TextBlock With {.Text = item.Title, .FontSize = 23, .FontWeight = FontWeights.SemiBold, .TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 18, 0, 8)})
        stack.Children.Add(New TextBlock With {.Text = PreviewMeta.Text, .TextWrapping = TextWrapping.Wrap})
        Dim watch As New Button With {.Content = "Open video website ↗", .HorizontalAlignment = HorizontalAlignment.Left, .Margin = New Thickness(0, 18, 0, 18)}
        AddHandler watch.Click, Sub()
                                   Try
                                       Process.Start(New ProcessStartInfo(DownloadRules.NormalizeUrl(item.Url, item.IsPlaylist)) With {.UseShellExecute = True})
                                   Catch ex As Exception
                                       StatusText.Text = ex.Message
                                   End Try
                               End Sub
        stack.Children.Add(watch)
        stack.Children.Add(New TextBlock With {.Text = "Playback opens in your default browser. Downloads use public access only.", .TextWrapping = TextWrapping.Wrap, .Margin = New Thickness(0, 0, 0, 14)})
        stack.Children.Add(New TextBlock With {.Text = item.Description, .TextWrapping = TextWrapping.Wrap})
        window.Content = New ScrollViewer With {.Content = stack, .VerticalScrollBarVisibility = ScrollBarVisibility.Auto}
        window.Show()
    End Sub
    Private Sub RemoveClick(sender As Object, e As RoutedEventArgs)
        If busy OrElse Selected() Is Nothing Then Return
        items.Remove(Selected())
        UpdateQueueCount()
        SaveSettings()
    End Sub
    Private Sub ClearClick(sender As Object, e As RoutedEventArgs)
        If busy Then Return
        For Each item In items.Where(Function(i) i.Status = "Completed").ToList()
            items.Remove(item)
        Next
        UpdateQueueCount()
        SaveSettings()
    End Sub
    Private Sub RetryClick(sender As Object, e As RoutedEventArgs)
        If busy OrElse Selected() Is Nothing Then Return
        Selected().Status = "Queued"
        Selected().Detail = "Ready to retry. Existing media files will be kept."
        Selected().Progress = 0
        SaveSettings()
        RefreshInspector()
    End Sub
    Private Async Sub DownloadClick(sender As Object, e As RoutedEventArgs)
        If busy Then Return
        Dim pending = items.Where(Function(i) i.Status <> "Completed").ToList()
        If pending.Count = 0 Then
            StatusText.Text = "There are no pending items to download."
            Return
        End If
        Try
            settings.OutputFolder = FolderBox.Text.Trim()
            If Not Path.IsPathRooted(settings.OutputFolder) Then Throw New IOException("Choose an absolute output folder first.")
            For Each item In pending
                DownloadRules.OutputTemplate(item)
            Next
        Catch ex As Exception
            StatusText.Text = ex.Message
            Return
        End Try
        Dim token = BeginWork("Preparing the download queue…")
        Dim succeeded As Integer
        Dim failed As Integer
        Try
            paths = Await dependencies.EnsureAsync(False, ToolProgress(), token)
            For Each item In pending
                token.ThrowIfCancellationRequested()
                QueueList.SelectedItem = item
                QueueList.ScrollIntoView(item)
                item.Status = "Downloading"
                item.Progress = 0
                item.CompletedFiles = 0
                item.Detail = "Connecting to " & If(item.Provider = "", New Uri(item.Url).Host, item.Provider) & "…"
                SaveSettings()
                Dim progress As New Progress(Of DownloadUpdate)(Sub(update)
                                                                   If update.Percent >= 0 Then item.Progress = update.Percent
                                                                   item.Detail = update.Text
                                                                   If update.FilePath <> "" Then
                                                                       item.OutputPath = update.FilePath
                                                                       item.CompletedFiles += 1
                                                                   End If
                                                                   If Selected() Is item Then PreviewDetail.Text = item.Detail
                                                               End Sub)
                Try
                    StatusText.Text = "Downloading " & (succeeded + failed + 1) & " of " & pending.Count & " · " & item.Title
                    ' Refresh stream IDs before downloading; retain the user's selected output.
                    Await downloader.ReadMetadataAsync(paths, item, token)
                    Dim outcome = Await downloader.DownloadAsync(paths, settings, item, progress, token)
                    Downloader.ApplyOutcome(item, outcome)
                    If outcome.Success Then
                        succeeded += 1
                    Else
                        failed += 1
                    End If
                Catch ex As OperationCanceledException
                    item.Status = "Cancelled"
                    item.Detail = "Cancelled. Partial files remain available for resuming."
                    Throw
                Catch ex As Exception
                    item.Status = "Failed"
                    item.Detail = DownloadRules.ExplainError(ex.Message)
                    failed += 1
                Finally
                    SaveSettings()
                    RefreshInspector()
                End Try
            Next
            StatusText.Text = "Queue finished · " & succeeded & " completed · " & failed & " need attention."
        Catch ex As OperationCanceledException
            StatusText.Text = "Queue cancelled. Completed files are kept; download again to resume."
        Catch ex As Exception
            StatusText.Text = "Could not start downloading: " & ex.Message
        Finally
            EndWork()
        End Try
    End Sub
    Private Sub CancelClick(sender As Object, e As RoutedEventArgs)
        If operation Is Nothing Then Return
        CancelButton.IsEnabled = False
        StatusText.Text = "Cancelling and stopping media processes…"
        operation.Cancel()
    End Sub
    Private Sub WindowClosing(sender As Object, e As CancelEventArgs)
        If busy Then
            e.Cancel = True
            closeRequested = True
            CancelClick(Me, New RoutedEventArgs())
            Return
        End If
        saveTimer.Stop()
        SaveSettings()
    End Sub
    Private Sub LoadThemes(preferred As String)
        If String.Equals(preferred, "Custom ocean", StringComparison.OrdinalIgnoreCase) Then
            preferred = "Ocean"
            settings.Theme = preferred
        End If
        Dim warning = ""
        Dim themes = ThemeManager.Load(warning)
        filling = True
        ThemeBox.ItemsSource = themes
        Dim theme = themes.FirstOrDefault(Function(t) t.Name = preferred)
        If theme Is Nothing Then theme = themes(0)
        ThemeBox.SelectedItem = theme
        ThemeManager.Apply(theme)
        filling = False
        If warning <> "" Then StatusText.Text = warning.Trim()
    End Sub
    Private Sub ThemeChanged(sender As Object, e As SelectionChangedEventArgs)
        If Not uiReady OrElse filling Then Return
        Dim theme = TryCast(ThemeBox.SelectedItem, ThemeDefinition)
        If theme Is Nothing Then Return
        ThemeManager.Apply(theme)
        settings.Theme = theme.Name
        ScheduleSave()
    End Sub
    Private Sub ImportThemeClick(sender As Object, e As RoutedEventArgs)
        Dim dialog As New Microsoft.Win32.OpenFileDialog With {.Filter = "JSON theme (*.json)|*.json", .Title = "Import color theme"}
        If dialog.ShowDialog(Me) <> True Then Return
        Try
            Dim theme = ThemeManager.Read(dialog.FileName)
            If DirectCast(ThemeBox.ItemsSource, List(Of ThemeDefinition)).Any(Function(t) t.Name = theme.Name) Then Throw New IOException("A theme with this name already exists. Change its Name field before importing.")
            settings.Theme = ThemeManager.Import(dialog.FileName)
            LoadThemes(settings.Theme)
            SaveSettings()
        Catch ex As Exception
            StatusText.Text = "Theme could not be imported: " & ex.Message
        End Try
    End Sub

    Friend Sub LoadSmokeItems()
        items.Clear()
        items.Add(New QueueItem With {.Title = "A journey through the mountains", .Url = "https://www.youtube.com/watch?v=BaW_jenozKc", .Uploader = "Creative Studio", .Duration = "12:48", .UploadDate = "20260913", .VideoId = "BaW_jenozKc", .Detail = "Ready to download · highest available", .PreviewLoaded = True})
        items.Add(New QueueItem With {.Title = "Weekend listening / full playlist", .Url = "https://www.youtube.com/playlist?list=PLexample123", .IsPlaylist = True, .Status = "Completed", .Progress = 100, .Detail = "8 files saved", .PreviewLoaded = True})
        items.Add(New QueueItem With {.Title = "Private video", .Url = "https://www.youtube.com/watch?v=abcdefghijk", .Status = "Failed", .Detail = "This video is private. Public videos only.", .PreviewLoaded = True})
        QueueList.SelectedIndex = 0
        UpdateQueueCount()
        ToolStatus.Text = "Tools ready · latest GitHub releases checked"
    End Sub
End Class


Imports System.Collections
Imports System.Globalization
Imports System.IO
Imports System.Threading

Public Class DownloadUpdate
    Public Property Percent As Double = -1
    Public Property Text As String = ""
    Public Property FilePath As String = ""
End Class

Public Class DownloadOutcome
    Public Property Success As Boolean
    Public Property Files As New List(Of String)
    Public Property ErrorMessage As String = ""
End Class

Public Class Downloader
    Public Async Function ReadMetadataAsync(paths As ToolPaths, item As QueueItem, token As CancellationToken) As Task
        Dim args = DownloadRules.BaseArguments(paths)
        args.AddRange({"--dump-single-json", "--skip-download", "-f", "bestvideo+bestaudio/best/bestvideo/bestaudio", If(item.IsPlaylist, "--yes-playlist", "--no-playlist")})
        args.AddRange({"--playlist-end", If(item.IsPlaylist, "20", "1")})
        If item.IsPlaylist Then args.Add("--flat-playlist")
        args.AddRange({"--", DownloadRules.NormalizeUrl(item.Url, item.IsPlaylist)})
        Using timeout = CancellationTokenSource.CreateLinkedTokenSource(token)
            timeout.CancelAfter(TimeSpan.FromMinutes(2))
            Dim result = Await ProcessRunner.RunAsync(paths.Ytdlp, args, timeout.Token, captureLimit:=16 * 1024 * 1024)
            item.LogPath = AppPaths.Inside("logs", "preview-" & Guid.NewGuid().ToString("N") & ".log")
            File.WriteAllText(item.LogPath, item.Url & Environment.NewLine & "Exit code: " & result.ExitCode & Environment.NewLine & result.Errors)
            If result.ExitCode <> 0 Then Throw New IOException(DownloadRules.ExplainError(result.Errors))
            Dim meta = JsonStore.Serializer().Deserialize(Of Dictionary(Of String, Object))(result.Output)
            ApplyMetadata(item, meta)
        End Using
    End Function

    Public Shared Sub ApplyMetadata(item As QueueItem, meta As Dictionary(Of String, Object))
        Dim source = meta
        If Not item.IsPlaylist AndAlso meta.ContainsKey("entries") AndAlso TypeOf meta("entries") Is IEnumerable Then
            source = Nothing
            For Each entry As Object In DirectCast(meta("entries"), IEnumerable)
                Dim candidate = TryCast(entry, Dictionary(Of String, Object))
                If candidate IsNot Nothing Then
                    source = candidate
                    Exit For
                End If
            Next
            If source Is Nothing Then Throw New IOException("No accessible video was found in this collection.")
        End If
        item.Provider = Field(meta, "extractor_key", Field(meta, "extractor", New Uri(item.Url).Host))
        item.Title = Field(source, "title", Field(meta, "title", item.Url))
        item.VideoId = Field(source, "id")
        item.Uploader = Field(source, "uploader", Field(source, "channel", Field(meta, "uploader", "Unknown creator")))
        item.Description = Field(source, "description", Field(meta, "description"))
        If item.Description.Length > 12000 Then item.Description = item.Description.Substring(0, 12000)
        item.UploadDate = Field(source, "upload_date")
        item.Duration = Field(source, "duration_string")
        If item.Duration = "" Then
            Dim seconds As Double
            If Double.TryParse(Field(source, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, seconds) AndAlso seconds >= 0 AndAlso seconds < 100000000 Then
                Dim duration = TimeSpan.FromSeconds(seconds)
                item.Duration = If(duration.TotalHours >= 1, CInt(Math.Floor(duration.TotalHours)) & ":" & duration.ToString("mm\:ss"), duration.ToString("m\:ss"))
            End If
        End If
        item.Thumbnail = Thumbnail(source)
        item.SourceFormats = If(item.IsPlaylist, New List(Of SourceFormat), MediaOptions.ReadFormats(source))
        item.SourceFormatsKnown = Not item.IsPlaylist AndAlso (source.ContainsKey("formats") OrElse source.ContainsKey("url"))
        If item.IsPlaylist Then
            Integer.TryParse(Field(meta, "playlist_count", "0"), item.EntryCount)
            If item.Thumbnail = "" AndAlso meta.ContainsKey("entries") AndAlso TypeOf meta("entries") Is IEnumerable Then
                For Each entry As Object In DirectCast(meta("entries"), IEnumerable)
                    Dim first = TryCast(entry, Dictionary(Of String, Object))
                    If first Is Nothing Then Continue For
                    item.Thumbnail = Thumbnail(first)
                    If item.Thumbnail <> "" Then Exit For
                Next
            End If
            item.Duration = If(item.EntryCount > 0, item.EntryCount & " videos", "Full playlist")
        End If
        item.PreviewLoaded = True
    End Sub

    Private Shared Function Field(meta As Dictionary(Of String, Object), key As String, Optional fallback As String = "") As String
        If meta.ContainsKey(key) AndAlso meta(key) IsNot Nothing Then Return Convert.ToString(meta(key), CultureInfo.InvariantCulture)
        Return fallback
    End Function
    Private Shared Function Thumbnail(meta As Dictionary(Of String, Object)) As String
        Dim url = Field(meta, "thumbnail")
        If url = "" AndAlso meta.ContainsKey("thumbnails") AndAlso TypeOf meta("thumbnails") Is IEnumerable Then
            For Each candidate As Object In DirectCast(meta("thumbnails"), IEnumerable)
                Dim entry = TryCast(candidate, Dictionary(Of String, Object))
                If entry IsNot Nothing Then url = Field(entry, "url", url)
            Next
        End If
        Dim uri As Uri = Nothing
        If Not Uri.TryCreate(url, UriKind.Absolute, uri) OrElse Not {"http", "https"}.Contains(uri.Scheme) OrElse uri.UserInfo <> "" Then Return ""
        Return url
    End Function

    Public Async Function DownloadAsync(paths As ToolPaths, settings As AppSettings, item As QueueItem, progress As IProgress(Of DownloadUpdate), token As CancellationToken) As Task(Of DownloadOutcome)
        Dim args = DownloadRules.Arguments(paths, settings, item)
        Directory.CreateDirectory(settings.OutputFolder)
        Dim log = AppPaths.Inside("logs", DateTime.Now.ToString("yyyyMMdd-HHmmss") & "-" & Guid.NewGuid().ToString("N") & ".log")
        item.LogPath = log
        item.SavedFolder = settings.OutputFolder
        Dim outcome As New DownloadOutcome()
        Dim existingTargets As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
        Dim encodeAudio = item.AudioBitrateKbps > 0 AndAlso MetadataRules.SupportsBitrate(MediaOptions.FormatFor(item, settings))
        Using writer As New StreamWriter(log, False, New System.Text.UTF8Encoding(False)) With {.AutoFlush = True}
            writer.WriteLine("Multi Video Downloader · " & DateTime.Now.ToString("O"))
            writer.WriteLine(item.Url)
            writer.WriteLine("Provider: " & item.Provider & " | Format: " & MediaOptions.FormatFor(item, settings) & " | Height ceiling: " & MediaOptions.HeightFor(item, settings) & " | Output: " & settings.OutputFolder)
            Dim written As Long
            Dim result = Await ProcessRunner.RunAsync(paths.Ytdlp, args, token,
                Sub(line, isError)
                    If written < 10 * 1024 * 1024 Then
                        writer.WriteLine(If(isError, "ERR ", "OUT ") & line)
                        written += line.Length
                        If written >= 10 * 1024 * 1024 Then writer.WriteLine("Log size limit reached; remaining output omitted.")
                    End If
                    If encodeAudio AndAlso line.StartsWith("GUI_DEST|", StringComparison.Ordinal) Then
                        Dim destination = JsonStore.Serializer().Deserialize(Of String)(line.Substring(9))
                        destination = Path.GetFullPath(Path.ChangeExtension(destination, MediaOptions.FormatFor(item, settings)))
                        Dim outputRoot = Path.GetFullPath(settings.OutputFolder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                        If Not destination.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("The audio destination is outside the output folder.")
                        If Not existingTargets.ContainsKey(destination) Then existingTargets(destination) = File.Exists(destination)
                    End If
                    Dim update = ParseProgress(line)
                    If update IsNot Nothing Then
                        If update.FilePath <> "" Then
                            Dim root = Path.GetFullPath(settings.OutputFolder).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                            Dim file = Path.GetFullPath(update.FilePath)
                            If Not file.StartsWith(root, StringComparison.OrdinalIgnoreCase) OrElse Not System.IO.File.Exists(file) Then Throw New IOException("The downloader reported an output file that could not be verified.")
                            outcome.Files.Add(file)
                        End If
                        progress.Report(update)
                    ElseIf line.StartsWith("[Merger]") OrElse line.StartsWith("[ExtractAudio]") OrElse line.StartsWith("[VideoRemuxer]") OrElse line.StartsWith("[VideoConvertor]") OrElse line.StartsWith("[Metadata]") Then
                        progress.Report(New DownloadUpdate With {.Text = "Processing media…"})
                    End If
                End Sub)
            outcome.Success = result.ExitCode = 0 AndAlso outcome.Files.Count > 0
            If Not outcome.Success Then
                outcome.ErrorMessage = If(result.ExitCode = 0, "No media files were produced. The playlist may be empty or its videos unavailable.", DownloadRules.ExplainError(result.Errors))
                If outcome.Files.Count > 0 Then outcome.ErrorMessage = outcome.Files.Count & " file(s) saved; some playlist entries failed. " & outcome.ErrorMessage
            End If
            If encodeAudio Then
                Try
                    For Each file In outcome.Files.Distinct(StringComparer.OrdinalIgnoreCase)
                        If Not existingTargets.ContainsKey(file) Then Throw New IOException("Could not verify whether the audio output already existed; it was left unchanged.")
                        If existingTargets(file) Then Continue For
                        progress.Report(New DownloadUpdate With {.Text = "Encoding audio at " & item.AudioBitrateKbps & " kbps…"})
                        Await AudioEncoding.EncodeAsync(paths, file, item.AudioBitrateKbps, token, writer)
                    Next
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    outcome.Success = False
                    outcome.ErrorMessage = "Audio bitrate conversion failed; the original output was retained. " & ex.Message
                End Try
            End If
            writer.WriteLine("Exit code: " & result.ExitCode)
        End Using
        Return outcome
    End Function

    Public Shared Function ParseProgress(line As String) As DownloadUpdate
        If line.StartsWith("GUI_PROGRESS|", StringComparison.Ordinal) Then
            Dim parts = line.Split("|"c)
            If parts.Length < 4 Then Return Nothing
            Dim percent As Double
            If Not Double.TryParse(parts(1).Trim().TrimEnd("%"c), NumberStyles.Float, CultureInfo.InvariantCulture, percent) OrElse Double.IsNaN(percent) OrElse Double.IsInfinity(percent) Then percent = -1
            Return New DownloadUpdate With {.Percent = Math.Min(100, percent), .Text = parts(1).Trim() & " · " & parts(2).Trim() & " · ETA " & parts(3).Trim()}
        End If
        If line.StartsWith("GUI_ITEM|", StringComparison.Ordinal) Then Return New DownloadUpdate With {.Percent = 0, .Text = "Downloading " & line.Substring(9)}
        If line.StartsWith("GUI_FILE|", StringComparison.Ordinal) Then
            Dim file = JsonStore.Serializer().Deserialize(Of String)(line.Substring(9))
            Return New DownloadUpdate With {.FilePath = file, .Text = "Saved " & Path.GetFileName(file)}
        End If
        Return Nothing
    End Function

    Public Shared Sub ApplyOutcome(item As QueueItem, outcome As DownloadOutcome)
        item.CompletedFiles = outcome.Files.Count
        If outcome.Files.Count > 0 Then item.OutputPath = outcome.Files.Last()
        If outcome.Success Then
            item.Status = "Completed"
            item.Progress = 100
            item.Detail = outcome.Files.Count & " file(s) saved or already present"
        Else
            item.Status = If(outcome.Files.Count > 0, "Partial", "Failed")
            item.Detail = outcome.ErrorMessage
        End If
    End Sub
End Class

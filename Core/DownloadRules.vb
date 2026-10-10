Imports System.Globalization
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Web

Public NotInheritable Class DownloadRules
    Public Shared ReadOnly Formats As String() = {"mp4", "mkv", "webm", "mp3", "m4a", "wav", "flac", "opus"}
    Public Shared ReadOnly Heights As Integer() = {0, 4320, 2160, 1440, 1080, 720, 480, 360, 240}
    Public Shared ReadOnly Tokens As New Dictionary(Of String, String) From {
        {"title", "%(title).120B"}, {"id", "%(id).64B"}, {"uploader", "%(uploader,channel|Unknown).80B"},
        {"channel", "%(channel,uploader|Unknown).80B"}, {"upload_date", "%(upload_date|Unknown)s"},
        {"year", "%(upload_date>%Y|Unknown)s"}, {"playlist", "%(playlist_title|Video).80B"},
        {"index", "%(playlist_index|0)03d"}, {"resolution", "%(resolution|audio)s"}}

    Public Shared Function NormalizeUrl(input As String, playlist As Boolean) As String
        Dim uri As Uri = Nothing
        If String.IsNullOrWhiteSpace(input) OrElse input.Any(Function(c) Char.IsControl(c)) OrElse Not Uri.TryCreate(input.Trim(), UriKind.Absolute, uri) OrElse Not {"http", "https"}.Contains(uri.Scheme) OrElse Not String.IsNullOrEmpty(uri.UserInfo) OrElse String.IsNullOrEmpty(uri.Host) Then Throw New ArgumentException("Paste an HTTP or HTTPS video or playlist link from a yt-dlp supported website.")
        Dim host = uri.Host.ToLowerInvariant()
        If Not {"youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "www.youtu.be"}.Contains(host) OrElse Not uri.IsDefaultPort Then Return input.Trim()
        Dim query = HttpUtility.ParseQueryString(uri.Query)
        Dim list = query("list")
        If playlist Then
            If Not String.IsNullOrEmpty(list) Then
                If Not Regex.IsMatch(list, "^[A-Za-z0-9_-]{2,150}$") Then Throw New ArgumentException("The YouTube playlist ID is invalid.")
                Return "https://www.youtube.com/playlist?list=" & list
            End If
            Return input.Trim()
        End If
        Dim id = query("v")
        If host.EndsWith("youtu.be", StringComparison.Ordinal) Then id = uri.AbsolutePath.Trim("/"c)
        Dim segments = uri.AbsolutePath.Trim("/"c).Split("/"c)
        If segments.Length = 2 AndAlso {"shorts", "live", "embed"}.Contains(segments(0)) Then id = segments(1)
        If String.IsNullOrEmpty(id) OrElse Not Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$") Then
            If uri.AbsolutePath = "/watch" OrElse uri.AbsolutePath = "/playlist" OrElse uri.AbsolutePath.StartsWith("/@", StringComparison.Ordinal) OrElse {"channel", "c", "user"}.Contains(segments(0)) Then Throw New ArgumentException("Paste a YouTube video link, or select Full playlist for a playlist or channel link.")
            ' Clips, attribution links and future extractor URL forms stay intact.
            Return input.Trim()
        End If
        Return "https://www.youtube.com/watch?v=" & id
    End Function
    Public Shared Function IsAudio(format As String) As Boolean
        Return {"mp3", "m4a", "wav", "flac", "opus"}.Contains(format)
    End Function
    Public Shared Function OutputTemplate(item As QueueItem) As String
        Dim stem As String
        Select Case item.NameMode
            Case "Custom name"
                stem = LiteralName(item.CustomName)
                If item.IsPlaylist Then stem &= " - %(playlist_index|0)03d"
            Case "Metadata template"
                stem = CompileTemplate(InlineMetadata.Parse(item.NameTemplate).FilenameTemplate)
            Case Else
                stem = "%(title).120B"
        End Select
        If Not stem.Contains("%(id)") Then stem &= " [%(id).64B]"
        Return If(item.IsPlaylist, "%(playlist_title|Playlist).80B/", "") & stem & ".%(ext)s"
    End Function
    Public Shared Function LiteralName(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then Throw New ArgumentException("Enter a custom filename, or choose Original title.")
        If value.Length > 140 Then Throw New ArgumentException("Keep the custom filename under 141 characters.")
        If value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse value.Any(Function(c) Char.IsControl(c)) Then Throw New ArgumentException("Filename cannot contain slashes, quotes, colons or other Windows reserved characters.")
        If value.Trim().Trim("."c).Length = 0 OrElse value.EndsWith(".") Then Throw New ArgumentException("Filename cannot be only dots or end with a dot.")
        Return value.Trim().Replace("%", "%%")
    End Function
    Public Shared Function CompileTemplate(value As String) As String
        If String.IsNullOrWhiteSpace(value) OrElse value.Length > 240 Then Throw New ArgumentException("Enter a metadata template of up to 240 characters.")
        Dim literal = Regex.Replace(value, "\{([a-z_]+)\}", "")
        If literal.Contains("{") OrElse literal.Contains("}") OrElse literal.Contains("%") OrElse literal.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 OrElse literal.Any(Function(c) Char.IsControl(c)) Then Throw New ArgumentException("Use the supported {tokens} and plain filename text. Paths and raw yt-dlp expressions are not allowed.")
        Dim matches = Regex.Matches(value, "\{([a-z_]+)\}").Cast(Of Match)().ToList()
        Dim fixedBytes = System.Text.Encoding.UTF8.GetByteCount(literal) + If(value.Contains("{id}"), 4, 68)
        Dim variableCount As Integer
        For Each match In matches
            Dim token = match.Groups(1).Value
            If Not Tokens.ContainsKey(token) Then Throw New ArgumentException("Unknown token {" & token & "}. Available: " & String.Join(", ", Tokens.Keys))
            If Tokens(token).Contains("B") Then
                variableCount += 1
            Else
                fixedBytes += If(token = "id", 11, If(token = "year", 4, If(token = "upload_date", 8, 24)))
            End If
        Next
        Dim fieldBudget = If(variableCount = 0, 140, Math.Min(140, (190 - fixedBytes) \ Math.Max(1, variableCount)))
        If fixedBytes > 190 OrElse fieldBudget < 16 Then Throw New ArgumentException("This filename template is too long. Shorten its text or use fewer metadata fields.")
        Return Regex.Replace(value, "\{([a-z_]+)\}", Function(m)
                                                             Dim token = m.Groups(1).Value
                                                             Return Regex.Replace(Tokens(token), "\.\d+B", "." & fieldBudget.ToString(CultureInfo.InvariantCulture) & "B")
                                                         End Function)
    End Function
    Public Shared Function NamePreview(item As QueueItem, format As String) As String
        OutputTemplate(item)
        Dim text As String = item.Title
        If item.NameMode = "Custom name" Then text = item.CustomName
        If item.NameMode = "Metadata template" Then
            Dim fields As New Dictionary(Of String, String) From {{"title", item.Title}, {"id", If(item.VideoId, "VIDEO_ID")}, {"uploader", item.Uploader}, {"channel", item.Uploader}, {"upload_date", item.UploadDate}, {"year", If(item.UploadDate.Length >= 4, item.UploadDate.Substring(0, 4), "Unknown")}, {"playlist", item.Title}, {"index", "001"}, {"resolution", "source"}}
            text = Regex.Replace(InlineMetadata.Parse(item.NameTemplate).FilenameTemplate, "\{([a-z_]+)\}", Function(m) fields(m.Groups(1).Value))
        End If
        If item.NameMode <> "Metadata template" OrElse Not InlineMetadata.Parse(item.NameTemplate).FilenameTemplate.Contains("{id}") Then text &= " [" & If(String.IsNullOrEmpty(item.VideoId), "VIDEO_ID", item.VideoId) & "]"
        Return text & "." & format
    End Function
    Public Shared Function BaseArguments(paths As ToolPaths) As List(Of String)
        Return New List(Of String) From {"--ignore-config", "--no-plugin-dirs", "--no-cache-dir", "--no-colors", "--encoding", "utf-8", "--socket-timeout", "30", "--retries", "3", "--ffmpeg-location", Path.GetDirectoryName(paths.Ffmpeg), "--no-js-runtimes", "--js-runtimes", "deno:" & paths.Deno}
    End Function
    Public Shared Function Arguments(paths As ToolPaths, settings As AppSettings, item As QueueItem) As List(Of String)
        Dim format = MediaOptions.FormatFor(item, settings)
        Dim height = MediaOptions.HeightFor(item, settings)
        If Not Formats.Contains(format) OrElse height < 0 OrElse height > 32768 Then Throw New ArgumentException("Invalid format or resolution.")
        If Not MediaOptions.AvailableFormats(item).Contains(format) Then Throw New ArgumentException("This video has no compatible " & format.ToUpperInvariant() & " source. Select an available format for this item.")
        If Not Path.IsPathRooted(settings.OutputFolder) Then Throw New ArgumentException("Choose an absolute output folder path.")
        Dim args = BaseArguments(paths)
        args.AddRange({"--newline", "--progress", "--progress-delta", "0.4", "--windows-filenames", "--trim-filenames", "220", "--no-overwrites", "--continue", "--no-simulate", "--no-abort-on-error", "-P", Path.GetFullPath(settings.OutputFolder), "-o", OutputTemplate(item), If(item.IsPlaylist, "--yes-playlist", "--no-playlist"), "--progress-template", "download:GUI_PROGRESS|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s", "--print", "before_dl:GUI_ITEM|%(title)s", "--print", "after_move:GUI_FILE|%(filepath)j"})
        If Not item.IsPlaylist Then args.AddRange({"--playlist-end", "1"})
        If settings.EmbedMetadata Then
            args.AddRange({"--embed-metadata", "--no-embed-info-json"})
            args.AddRange(MetadataRules.Arguments(item, format))
            If format = "mp3" Then args.AddRange({"--postprocessor-args", "Metadata+ffmpeg_o:-id3v2_version 3"})
        End If
        If Not MetadataRules.Bitrates.Contains(item.AudioBitrateKbps) Then Throw New ArgumentException("Choose a valid audio bitrate.")
        If MetadataRules.SupportsBitrate(format) AndAlso item.AudioBitrateKbps > 0 Then args.AddRange({"--print", "before_dl:GUI_DEST|%(filename)j"})
        If IsAudio(format) Then
            args.AddRange({"-f", "bestaudio/best", "--extract-audio", "--audio-format", format, "--audio-quality", If(format = "mp3" AndAlso item.AudioBitrateKbps > 0, item.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture) & "K", "0")})
        Else
            Dim convert = (item.IsPlaylist OrElse Not item.SourceFormatsKnown OrElse item.SourceFormats.Any(Function(s) s.Id = "")) AndAlso format <> "mkv"
            args.AddRange({"-f", MediaOptions.VideoSelector(item, format, height), "--merge-output-format", If(convert, format & "/mkv", format), If(convert, "--recode-video", "--remux-video"), format})
        End If
        args.AddRange({"--", NormalizeUrl(item.Url, item.IsPlaylist)})
        If args.Sum(Function(a) ProcessRunner.Quote(a).Length + 1) > 30000 Then Throw New ArgumentException("The combined filename and metadata is too long. Shorten the text fields.")
        Return args
    End Function
    Public Shared Function ExplainError(raw As String) As String
        Dim lower = raw.ToLowerInvariant()
        If lower.Contains("private video") Then Return "This video is private. This version supports public videos and cannot sign in."
        If lower.Contains("sign in") OrElse lower.Contains("login") OrElse lower.Contains("age-restricted") OrElse lower.Contains("members-only") OrElse lower.Contains("cookies") Then Return "This website requires authentication, age verification or a bot check for this request. Public-only mode cannot complete it; browser login is not imported."
        If lower.Contains("not available in your country") OrElse lower.Contains("geo-restricted") Then Return "This website restricts the video in your region."
        If lower.Contains("unsupported url") OrElse lower.Contains("no video formats") OrElse lower.Contains("no video could be found") Then Return "yt-dlp could not find a downloadable video at this link. Use a direct video or post link and check for tool updates."
        If lower.Contains("cannot parse data") OrElse lower.Contains("unable to extract") Then Return "yt-dlp could not read the video data from this website. Check the link and tool updates; website changes or access restrictions may prevent downloading."
        If lower.Contains("drm") Then Return "The media is protected by DRM and cannot be downloaded by this application."
        If lower.Contains("removed") OrElse lower.Contains("unavailable") Then Return "The video was removed, is unavailable, or is restricted by its owner."
        If lower.Contains("requested format") Then Return "No compatible source matches this format and resolution. Try Highest available or MKV."
        If lower.Contains("403") OrElse lower.Contains("429") Then Return "The website refused or rate-limited the media request. Update tools, wait, and retry."
        If lower.Contains("disk full") OrElse lower.Contains("no space") Then Return "The output drive has insufficient free space. Choose another folder or free disk space."
        If lower.Contains("permission") OrElse lower.Contains("access") Then Return "A file or network request was denied. Check the output folder, security software and network connection."
        If lower.Contains("timed out") OrElse lower.Contains("resolve") OrElse lower.Contains("connection") Then Return "The connection failed or timed out. Check your network and retry."
        Return "The download failed. " & If(raw.Length > 800, raw.Substring(raw.Length - 800), raw).Trim()
    End Function
End Class

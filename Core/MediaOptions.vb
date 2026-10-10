Imports System.Collections
Imports System.Globalization
Imports System.Text.RegularExpressions

' Capabilities come from the video, rather than assumptions about its website.
Public NotInheritable Class MediaOptions
    Public Shared Function Field(meta As Dictionary(Of String, Object), key As String, Optional fallback As String = "") As String
        If meta.ContainsKey(key) AndAlso meta(key) IsNot Nothing Then Return Convert.ToString(meta(key), CultureInfo.InvariantCulture)
        Return fallback
    End Function

    Public Shared Function ReadFormats(meta As Dictionary(Of String, Object)) As List(Of SourceFormat)
        Dim sources As New List(Of SourceFormat)
        If meta.ContainsKey("formats") AndAlso TypeOf meta("formats") Is IEnumerable Then
            For Each value As Object In DirectCast(meta("formats"), IEnumerable)
                Dim entry = TryCast(value, Dictionary(Of String, Object))
                If entry IsNot Nothing Then AddFormat(sources, entry)
            Next
        ElseIf meta.ContainsKey("url") Then
            AddFormat(sources, meta)
        End If
        Return sources
    End Function

    Private Shared Sub AddFormat(sources As List(Of SourceFormat), meta As Dictionary(Of String, Object))
        If Field(meta, "has_drm").ToLowerInvariant() = "true" Then Return
        If Field(meta, "protocol").ToLowerInvariant().Contains("mhtml") Then Return
        Dim format As New SourceFormat With {
            .Id = Field(meta, "format_id"), .Extension = Field(meta, "ext").ToLowerInvariant(),
            .VideoCodec = Field(meta, "vcodec").ToLowerInvariant(), .AudioCodec = Field(meta, "acodec").ToLowerInvariant()}
        Dim height As Double
        If Double.TryParse(Field(meta, "height"), NumberStyles.Float, CultureInfo.InvariantCulture, height) AndAlso height > 0 AndAlso height <= 32768 Then format.Height = CInt(height)
        If format.HasVideo OrElse format.HasAudio Then sources.Add(format)
    End Sub

    Public Shared Function AvailableFormats(item As QueueItem) As String()
        If item Is Nothing OrElse item.IsPlaylist OrElse Not item.SourceFormatsKnown Then Return DownloadRules.Formats
        Return DownloadRules.Formats.Where(Function(format) If(DownloadRules.IsAudio(format), item.SourceFormats.Any(Function(s) s.HasAudio), VideoSources(item, format).Any())).ToArray()
    End Function

    Public Shared Function AvailableHeights(item As QueueItem, format As String) As Integer()
        If item Is Nothing OrElse item.IsPlaylist OrElse Not item.SourceFormatsKnown Then Return DownloadRules.Heights
        Return {0}.Concat(VideoSources(item, format).Where(Function(s) s.Height > 0).Select(Function(s) s.Height).Distinct().OrderByDescending(Function(h) h)).ToArray()
    End Function

    Public Shared Function FormatFor(item As QueueItem, settings As AppSettings) As String
        Return If(String.IsNullOrEmpty(item.OutputFormat), settings.Format, item.OutputFormat)
    End Function

    Public Shared Function HeightFor(item As QueueItem, settings As AppSettings) As Integer
        Return If(item.MaxHeight < 0, settings.MaxHeight, item.MaxHeight)
    End Function

    Public Shared Sub InitializeSelection(item As QueueItem, settings As AppSettings)
        Dim available = AvailableFormats(item)
        Dim format = FormatFor(item, settings)
        If Not available.Contains(format) Then format = If(available.Contains("mkv"), "mkv", If(available.Contains("mp3"), "mp3", available.FirstOrDefault()))
        item.OutputFormat = If(format, settings.Format)
        Dim height = HeightFor(item, settings)
        Dim heights = AvailableHeights(item, item.OutputFormat)
        If Not heights.Contains(height) Then height = heights.Where(Function(h) h <= height).DefaultIfEmpty(0).Max()
        item.MaxHeight = height
    End Sub

    Private Shared Function VideoCompatible(source As SourceFormat, format As String) As Boolean
        If Not source.HasVideo Then Return False
        If format = "mkv" Then Return True
        If format = "webm" Then Return source.Extension = "webm" OrElse Regex.IsMatch(source.VideoCodec, "^(vp0?[89]|av0?1)")
        Return {"mp4", "m4v", "mov"}.Contains(source.Extension) OrElse Regex.IsMatch(source.VideoCodec, "^(avc|h26[45]|hev|av0?1|mpeg4)")
    End Function

    Private Shared Function AudioCompatible(source As SourceFormat, format As String) As Boolean
        If Not source.HasAudio OrElse format = "mkv" Then Return True
        If format = "webm" Then Return Regex.IsMatch(source.AudioCodec, "^(opus|vorbis)") OrElse ({"webm", "weba", "opus", "ogg"}.Contains(source.Extension) AndAlso {"", "unknown"}.Contains(source.AudioCodec))
        Return Regex.IsMatch(source.AudioCodec, "^(aac|mp4a|mp3|alac|ac-?3|ec-?3|eac3|opus)") OrElse ({"mp4", "m4a", "m4v", "mov", "aac", "mp3"}.Contains(source.Extension) AndAlso {"", "unknown"}.Contains(source.AudioCodec))
    End Function

    Private Shared Function VideoSources(item As QueueItem, format As String) As IEnumerable(Of SourceFormat)
        Dim hasAudio = item.SourceFormats.Any(Function(s) s.HasAudio)
        Dim separateAudio = item.SourceFormats.Any(Function(s) Not s.HasVideo AndAlso s.HasAudio AndAlso AudioCompatible(s, format))
        Return item.SourceFormats.Where(Function(s) VideoCompatible(s, format) AndAlso If(s.HasAudio, AudioCompatible(s, format), Not hasAudio OrElse separateAudio))
    End Function

    Private Shared Function IdFilter(sources As IEnumerable(Of SourceFormat)) As String
        ' Escape the regular expression and yt-dlp's quoted filter syntax.
        Dim ids = sources.Select(Function(s) s.Id).Distinct().Select(Function(id) Regex.Escape(id).Replace("'", "\x27").Replace("""", "\x22").Replace("\]", "\x5d").Replace("]", "\x5d").Replace("\[", "\x5b"))
        Return "[format_id~='^(?:" & String.Join("|", ids) & ")$']"
    End Function

    Public Shared Function VideoSelector(item As QueueItem, format As String, height As Integer) As String
        Dim ceiling = If(height = 0, "", "[height<=" & height.ToString(CultureInfo.InvariantCulture) & "]")
        If Not item.IsPlaylist AndAlso item.SourceFormatsKnown AndAlso item.SourceFormats.All(Function(s) s.Id <> "") Then
            Dim video = VideoSources(item, format).ToList()
            Dim combined = video.Where(Function(s) s.HasAudio).ToList()
            Dim separate = video.Where(Function(s) Not s.HasAudio).ToList()
            Dim audio = item.SourceFormats.Where(Function(s) Not s.HasVideo AndAlso s.HasAudio AndAlso AudioCompatible(s, format)).ToList()
            Dim choices As New List(Of String)
            If separate.Count > 0 AndAlso audio.Count > 0 Then choices.Add("bestvideo" & IdFilter(separate) & ceiling & "+bestaudio" & IdFilter(audio))
            If combined.Count > 0 Then choices.Add("best" & IdFilter(combined) & ceiling)
            If separate.Count > 0 AndAlso Not item.SourceFormats.Any(Function(s) s.HasAudio) Then choices.Add("bestvideo" & IdFilter(separate) & ceiling)
            Dim selector = String.Join("/", choices)
            If selector.Length > 0 AndAlso selector.Length < 16000 Then Return selector
        End If
        Dim fallback = "bestvideo" & ceiling & "+bestaudio/best" & ceiling & "/bestvideo" & ceiling
        If format = "mp4" Then Return "bestvideo[ext=mp4]" & ceiling & "+bestaudio[ext=m4a]/best[ext=mp4]" & ceiling & "/" & fallback
        If format = "webm" Then Return "bestvideo[ext=webm]" & ceiling & "+bestaudio[ext=webm]/best[ext=webm]" & ceiling & "/" & fallback
        Return fallback
    End Function
End Class

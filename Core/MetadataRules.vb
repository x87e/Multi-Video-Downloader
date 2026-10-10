Imports System.Globalization
Imports System.Text.RegularExpressions

Public NotInheritable Class MetadataRules
    Public Shared ReadOnly Fields As New Dictionary(Of String, String) From {
        {"title", "Title"}, {"artist", "Artist / creator"}, {"album", "Album title"},
        {"album_artist", "Album artist"}, {"composer", "Composer"}, {"genre", "Genre"},
        {"date", "Release date / year"}, {"track", "Track number (e.g. 1/12)"}, {"disc", "Disc number (e.g. 1/2)"},
        {"comment", "Comment"}, {"description", "Description / synopsis"}, {"copyright", "Copyright"},
        {"publisher", "Publisher"}, {"language", "Language (e.g. eng)"}, {"grouping", "Grouping"},
        {"show", "Series / show"}, {"season_number", "Season number"}, {"episode_id", "Episode title / ID"},
        {"episode_sort", "Episode number"}, {"director", "Director"}, {"performer", "Performer"},
        {"producer", "Producer"}, {"lyrics", "Lyrics"}}
    Public Shared ReadOnly Bitrates As Integer() = {0, 96, 128, 160, 192, 256, 320}
    Private Shared ReadOnly TokenTemplates As New Dictionary(Of String, String) From {
        {"title", "%(title|)s"}, {"id", "%(id|)s"}, {"uploader", "%(uploader,channel|Unknown)s"},
        {"channel", "%(channel,uploader|Unknown)s"}, {"upload_date", "%(upload_date|Unknown)s"},
        {"year", "%(upload_date>%Y|Unknown)s"}, {"playlist", "%(playlist_title|Video)s"},
        {"index", "%(playlist_index|0)03d"}, {"resolution", "%(resolution|audio)s"}}

    Public Shared Function SupportedFields(format As String) As String()
        If format = "wav" Then Return {"title", "artist", "album", "genre", "date", "track", "comment", "copyright", "language"}
        If {"mp4", "m4a"}.Contains(format) Then Return Fields.Keys.Where(Function(field) Not {"publisher", "director", "performer", "producer"}.Contains(field)).ToArray()
        Return Fields.Keys.ToArray()
    End Function

    Public Shared Function SupportsBitrate(format As String) As Boolean
        Return {"mp3", "m4a", "opus"}.Contains(format)
    End Function

    Public Shared Function Compile(value As String, Optional expandTokens As Boolean = True) As String
        If value Is Nothing Then Return ""
        If value.Length > 12000 OrElse value.Any(Function(c) Char.IsControl(c) AndAlso c <> Microsoft.VisualBasic.ControlChars.Lf AndAlso c <> Microsoft.VisualBasic.ControlChars.Cr AndAlso c <> Microsoft.VisualBasic.ControlChars.Tab) Then Throw New ArgumentException("Metadata fields must be under 12,001 characters and cannot contain control characters.")
        If Not expandTokens Then Return value.Replace("%", "%%")
        Dim literal = Regex.Replace(value, "\{([a-z_]+)\}", "")
        If literal.Contains("{") OrElse literal.Contains("}") Then Throw New ArgumentException("Use complete supported {tokens} in metadata fields.")
        Return Regex.Replace(value.Replace("%", "%%"), "\{([a-z_]+)\}", Function(m)
            Dim token = m.Groups(1).Value
            If Not TokenTemplates.ContainsKey(token) Then Throw New ArgumentException("Unknown metadata token {" & token & "}.")
            Return TokenTemplates(token)
        End Function)
    End Function

    Public Shared Function Arguments(item As QueueItem, Optional format As String = Nothing) As List(Of String)
        If format Is Nothing Then format = item.OutputFormat
        Dim supported = SupportedFields(format)
        Dim values As New Dictionary(Of String, String)
        If item.EmbeddedTags IsNot Nothing Then
            For Each field In item.EmbeddedTags
                If Not Fields.ContainsKey(field.Key) Then Throw New ArgumentException("Unsupported metadata field: " & field.Key)
                If Not String.IsNullOrWhiteSpace(field.Value) Then
                    If Not supported.Contains(field.Key) Then Throw New ArgumentException(Fields(field.Key) & " is not supported in " & format.ToUpperInvariant() & ". Clear that field or choose a compatible format.")
                    values(field.Key) = field.Value
                End If
            Next
        End If
        Dim filenameTemplate = item.NameTemplate
        If item.NameMode = "Metadata template" Then
            Dim parsed = InlineMetadata.Parse(item.NameTemplate)
            filenameTemplate = parsed.FilenameTemplate
            For Each field In parsed.Tags
                If Not supported.Contains(field.Key) Then Throw New ArgumentException(Fields(field.Key) & " is not supported in " & format.ToUpperInvariant() & ". Remove that named tag or choose a compatible format.")
                values(field.Key) = field.Value
            Next
        End If
        Dim literalTitle = Not values.ContainsKey("title") AndAlso item.NameMode = "Custom name"
        If Not values.ContainsKey("title") Then
            If item.NameMode = "Metadata template" Then values("title") = filenameTemplate
            If item.NameMode = "Custom name" Then values("title") = item.CustomName
        End If
        Dim args As New List(Of String)
        For Each field In values
            ' A literal prefix prevents plain words being mistaken for source field names.
            ' Escaped colons keep arbitrary user text inside FROM, never the TO regex.
            Dim template = ("=" & Compile(field.Value, Not (field.Key = "title" AndAlso literalTitle)) & "=").Replace(":", "\:")
            If field.Key = "description" AndAlso {"flac", "opus"}.Contains(format) Then
                ' Vorbis comments map description and comment to the same key.
                ' Preserve the editable Comment and store Description as Synopsis.
                args.AddRange({"--parse-metadata", "==:(?s)^=(?P<meta_description>.*)=$"})
            Else
                args.AddRange({"--parse-metadata", template & ":(?s)^=(?P<meta_" & field.Key & ">.*)=$"})
            End If
            If field.Key = "description" Then args.AddRange({"--parse-metadata", template & ":(?s)^=(?P<meta_synopsis>.*)=$"})
            If field.Key = "language" Then
                args.AddRange({"--parse-metadata", template & ":(?s)^=(?P<meta0_language>.*)=$", "--parse-metadata", template & ":(?s)^=(?P<meta1_language>.*)=$"})
            End If
        Next
        If args.Sum(Function(a) a.Length) > 24000 Then Throw New ArgumentException("The combined metadata is too long. Shorten the descriptions or lyrics.")
        Return args
    End Function
End Class

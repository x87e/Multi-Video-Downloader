Imports System.Text.RegularExpressions

Public Class ParsedMetadataTemplate
    Public Property FilenameTemplate As String
    Public ReadOnly Tags As New Dictionary(Of String, String)
End Class

Public NotInheritable Class InlineMetadata
    Private Shared ReadOnly Pattern As New Regex("\{(?<field>[A-Za-z][A-Za-z0-9_ ]*)\s*:(?<value>(?:[^{}]|\{[a-z_]+\})*)\}")
    Private Shared ReadOnly Aliases As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
        {"albumtitle", "album"}, {"albumname", "album"}, {"tracknumber", "track"}, {"discnumber", "disc"},
        {"year", "date"}, {"releasedate", "date"}, {"creator", "artist"}, {"series", "show"},
        {"season", "season_number"}, {"episode", "episode_id"}, {"episodenumber", "episode_sort"}}

    Public Shared Function Parse(value As String) As ParsedMetadataTemplate
        If value Is Nothing Then value = ""
        If value.Length > 12000 Then Throw New ArgumentException("Keep the complete metadata template under 12,001 characters.")
        Dim result As New ParsedMetadataTemplate()
        Dim filename = Pattern.Replace(value, Function(match)
            Dim name = match.Groups("field").Value.Trim().Replace(" ", "").Replace("_", "")
            Dim field = MetadataRules.Fields.Keys.FirstOrDefault(Function(key) String.Equals(key.Replace("_", ""), name, StringComparison.OrdinalIgnoreCase))
            If field Is Nothing AndAlso Not Aliases.TryGetValue(name, field) Then Throw New ArgumentException("Unknown metadata field {" & match.Groups("field").Value.Trim() & ":...}. Try {Album:My Album}, {Genre:Rock} or {Track:1}.")
            Dim text = match.Groups("value").Value.Trim()
            If text = "" Then Throw New ArgumentException("Enter a value after " & match.Groups("field").Value.Trim() & ": inside the braces.")
            If result.Tags.ContainsKey(field) Then Throw New ArgumentException("Enter only one named override for " & MetadataRules.Fields(field) & ".")
            MetadataRules.Compile(text)
            result.Tags(field) = text
            Return ""
        End Function)
        Dim literal = Regex.Replace(filename, "\{[a-z_]+\}", "")
        If literal.Contains("{") OrElse literal.Contains("}") Then Throw New ArgumentException("Use complete {tokens} or {Field:Value} tags, for example {Album:My Album}. A value may contain a simple token such as {index}.")
        If result.Tags.Count > 0 Then
            filename = Regex.Replace(filename, "[ \t]{2,}", " ").Trim()
            If filename = "" Then filename = "{title}"
        End If
        result.FilenameTemplate = filename
        Return result
    End Function

    Public Shared ReadOnly HelpText As String = String.Join(Environment.NewLine, {
        "Choose Metadata template and append named tags, for example:", "",
        "{title} {Album:My Album} {Genre:Rock} {Track:1}", "",
        "{title} sets the filename. The named Album, Genre and Track tags are embedded inside the MP3 and stay out of its filename and Title.", "",
        "YouTube often has no album information, so enter it yourself. Spaces and colons are allowed in values. Tag names ignore letter case; each tag needs a value and closing brace.", "",
        "Other examples: {Artist:My Artist} {AlbumArtist:My Band} {Year:2026} {Disc:1/2}. For a playlist use {Track:{index}}; each entry gets its own track number.", "",
        "Named tags take priority over the corresponding fields in the expanded editor. Tags alone keep the original title as the filename. Existing {uploader}, {title} and other tokens still work.", "",
        "Keep Embed source metadata and edited file tags enabled. These tags also work for other formats where supported. Bitrate is an actual encoding setting; use the audio bitrate control for that."})
End Class

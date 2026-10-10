Imports System.IO
Imports System.Text
Imports System.Web.Script.Serialization

Public NotInheritable Class AppPaths
    Public Shared Property Root As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MultiVid")
    Public Shared Function Inside(ParamArray parts As String()) As String
        Dim result = Path.GetFullPath(parts.Aggregate(Root, Function(a, b) Path.Combine(a, b)))
        Dim prefix = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        If Not result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then Throw New IOException("The application data path escaped its private folder.")
        RejectLinks(result)
        Return result
    End Function
    Public Shared Sub RejectLinks(target As String)
        Dim current = Path.GetFullPath(target)
        While Not String.IsNullOrEmpty(current)
            If (Directory.Exists(current) OrElse File.Exists(current)) AndAlso (File.GetAttributes(current) And FileAttributes.ReparsePoint) <> 0 Then
                Throw New IOException("A symbolic link or junction is not allowed in the application data path: " & current)
            End If
            current = Path.GetDirectoryName(current)
        End While
    End Sub
    Public Shared Sub Initialize()
        RejectLinks(Root)
        Directory.CreateDirectory(Root)
        For Each name In {"tools", "themes", "logs"}
            Directory.CreateDirectory(Inside(name))
        Next
        Environment.SetEnvironmentVariable("DENO_DIR", Inside("tools", "deno-cache"), EnvironmentVariableTarget.Process)
    End Sub
End Class

Public NotInheritable Class JsonStore
    Public Const SettingsFilename As String = "multi-video-settings.json"
    Public Shared Function Serializer() As JavaScriptSerializer
        Return New JavaScriptSerializer With {.MaxJsonLength = 16 * 1024 * 1024, .RecursionLimit = 100}
    End Function
    Public Shared Function Read(Of T)(file As String) As T
        If New FileInfo(file).Length > 16 * 1024 * 1024 Then Throw New IOException("JSON file is too large: " & file)
        Return Serializer().Deserialize(Of T)(System.IO.File.ReadAllText(file, Encoding.UTF8))
    End Function
    Public Shared Sub Write(file As String, value As Object)
        Dim temporary = file & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            Using stream As New FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                Dim bytes = New UTF8Encoding(False).GetBytes(Serializer().Serialize(value))
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(True)
            End Using
            If System.IO.File.Exists(file) Then
                System.IO.File.Replace(temporary, file, file & ".bak", True)
            Else
                System.IO.File.Move(temporary, file)
            End If
        Finally
            If System.IO.File.Exists(temporary) Then System.IO.File.Delete(temporary)
        End Try
    End Sub
    Public Shared Function LoadSettings(ByRef warning As String) As AppSettings
        Dim file = AppPaths.Inside(SettingsFilename)
        If Not System.IO.File.Exists(file) Then
            ' Import the original app's preferences without changing its queue file.
            file = AppPaths.Inside("settings.json")
            If Not System.IO.File.Exists(file) Then Return New AppSettings()
        End If
        Dim settings As AppSettings
        Try
            settings = Read(Of AppSettings)(file)
            Validate(settings)
        Catch ex As Exception
            Dim backup = file & ".bak"
            If System.IO.File.Exists(backup) Then
                Try
                    settings = Read(Of AppSettings)(backup)
                    Validate(settings)
                    System.IO.File.Copy(file, file & ".invalid-" & DateTime.UtcNow.ToString("yyyyMMddHHmmss"), False)
                    warning = "Recovered settings from the previous save. The damaged file was preserved."
                    Return Recover(settings)
                Catch
                    ' Preserve the original and report the failure; never silently overwrite a damaged queue.
                End Try
            End If
            Throw New IOException("Settings could not be read. Your queue is preserved at " & file & ". " & ex.Message, ex)
        End Try
        Return Recover(settings)
    End Function
    Public Shared Function Recover(settings As AppSettings) As AppSettings
        For Each item In settings.Queue
            If {"Downloading", "Preparing", "Reading"}.Contains(item.Status) Then
                item.Status = "Queued"
                item.Detail = "Interrupted last time. Ready to resume."
                item.Progress = 0
            End If
        Next
        Return settings
    End Function
    Private Shared Sub Validate(settings As AppSettings)
        If settings Is Nothing OrElse settings.Queue Is Nothing Then Throw New IOException("Invalid settings structure.")
        If Not DownloadRules.Formats.Contains(settings.Format) Then settings.Format = "mp4"
        If settings.MaxHeight < 0 OrElse settings.MaxHeight > 32768 Then settings.MaxHeight = 0
        If String.IsNullOrWhiteSpace(settings.OutputFolder) Then settings.OutputFolder = New AppSettings().OutputFolder
        For Each item In settings.Queue
            If item Is Nothing Then Throw New IOException("Invalid queue entry.")
            item.Url = DownloadRules.NormalizeUrl(item.Url, item.IsPlaylist)
            If Not DownloadRules.Formats.Contains(item.OutputFormat) Then item.OutputFormat = settings.Format
            If item.MaxHeight < 0 OrElse item.MaxHeight > 32768 Then item.MaxHeight = settings.MaxHeight
            If item.SourceFormats Is Nothing OrElse item.SourceFormats.Any(Function(s) s Is Nothing) Then
                item.SourceFormats = New List(Of SourceFormat)
                item.SourceFormatsKnown = False
            End If
            If Not item.IsPlaylist AndAlso Not item.SourceFormatsKnown Then item.PreviewLoaded = False
            If item.EmbeddedTags Is Nothing Then item.EmbeddedTags = New Dictionary(Of String, String)
            If Not MetadataRules.Bitrates.Contains(item.AudioBitrateKbps) Then item.AudioBitrateKbps = 0
            If Not {"Original title", "Custom name", "Metadata template"}.Contains(item.NameMode) Then item.NameMode = "Original title"
        Next
    End Sub
End Class

Imports System.ComponentModel
Imports System.Runtime.CompilerServices
Imports System.Web.Script.Serialization

Public Class QueueItem
    Implements INotifyPropertyChanged
    Public Property Key As String = Guid.NewGuid().ToString("N")
    Public Property Url As String = ""
    Public Property IsPlaylist As Boolean
    Public Property VideoId As String = ""
    Public Property Thumbnail As String = ""
    Public Property Description As String = ""
    Public Property Uploader As String = ""
    Public Property UploadDate As String = ""
    Public Property Duration As String = ""
    Public Property EntryCount As Integer
    Public Property NameMode As String = "Original title"
    Public Property CustomName As String = ""
    Public Property NameTemplate As String = "{uploader} - {title} [{id}]"
    Public Property OutputPath As String = ""
    Public Property SavedFolder As String = ""
    Public Property LogPath As String = ""
    Public Property CompletedFiles As Integer
    Public Property PreviewLoaded As Boolean
    Public Property Provider As String = ""
    Public Property SourceFormats As New List(Of SourceFormat)
    Public Property SourceFormatsKnown As Boolean
    Public Property EmbeddedTags As New Dictionary(Of String, String)
    Public Property AudioBitrateKbps As Integer
    Public Property OutputFormat As String = ""
    Public Property MaxHeight As Integer = -1
    Private _title As String = "Reading video…"
    Private _status As String = "Queued"
    Private _detail As String = "Ready to download"
    Private _progress As Double
    Public Property Title As String
        Get
            Return _title
        End Get
        Set(value As String)
            _title = value
            Notify()
        End Set
    End Property
    Public Property Status As String
        Get
            Return _status
        End Get
        Set(value As String)
            _status = value
            Notify()
        End Set
    End Property
    Public Property Detail As String
        Get
            Return _detail
        End Get
        Set(value As String)
            _detail = value
            Notify()
        End Set
    End Property
    Public Property Progress As Double
        Get
            Return _progress
        End Get
        Set(value As Double)
            _progress = value
            Notify()
        End Set
    End Property
    <ScriptIgnore>
    Public ReadOnly Property Kind As String
        Get
            Return If(IsPlaylist, "PLAYLIST", "VIDEO")
        End Get
    End Property
    Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    Public Sub Notify(<CallerMemberName> Optional name As String = Nothing)
        RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
    End Sub
End Class

Public Class AppSettings
    Public Property Format As String = "mp4"
    Public Property MaxHeight As Integer = 0
    Public Property OutputFolder As String = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Videos")
    Public Property Theme As String = "Dark"
    Public Property EmbedMetadata As Boolean = True
    Public Property MetadataHelpShown As Boolean
    Public Property Queue As New List(Of QueueItem)
End Class

Public Class ToolRecord
    Public Property AssetId As String = ""
    Public Property Version As String = ""
    Public Property DirectoryName As String = ""
    Public Property Hashes As New Dictionary(Of String, String)
End Class

Public Class ToolManifest
    Public Property LastCheckedUtc As DateTime = DateTime.MinValue
    Public Property Tools As New Dictionary(Of String, ToolRecord)
End Class

Public Class ToolPaths
    Public Property Ytdlp As String
    Public Property Ffmpeg As String
    Public Property Ffprobe As String
    Public Property Deno As String
End Class

Public Class SourceFormat
    Public Property Id As String = ""
    Public Property Extension As String = ""
    Public Property VideoCodec As String = ""
    Public Property AudioCodec As String = ""
    Public Property Height As Integer
    <ScriptIgnore>
    Public ReadOnly Property HasVideo As Boolean
        Get
            Return VideoCodec <> "none" AndAlso (VideoCodec <> "" OrElse Height > 0 OrElse {"mp4", "m4v", "mov", "webm", "mkv", "flv", "avi", "ts", "mpeg", "ogv"}.Contains(Extension))
        End Get
    End Property
    <ScriptIgnore>
    Public ReadOnly Property HasAudio As Boolean
        Get
            Return AudioCodec <> "none" AndAlso (AudioCodec <> "" OrElse HasVideo OrElse {"mp3", "m4a", "aac", "wav", "flac", "opus", "ogg", "weba"}.Contains(Extension))
        End Get
    End Property
End Class

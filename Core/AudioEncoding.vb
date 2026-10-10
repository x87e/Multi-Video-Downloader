Imports System.Globalization
Imports System.IO
Imports System.Threading

Public NotInheritable Class AudioEncoding
    Public Shared Async Function EncodeAsync(paths As ToolPaths, file As String, bitrate As Integer, token As CancellationToken, log As TextWriter) As Task
        Dim format = Path.GetExtension(file).TrimStart("."c).ToLowerInvariant()
        If Not MetadataRules.SupportsBitrate(format) OrElse Not MetadataRules.Bitrates.Contains(bitrate) OrElse bitrate = 0 Then Throw New ArgumentException("Invalid audio bitrate or format.")
        If format = "mp3" Then
            Dim probeArgs As String() = {"-v", "error", "-select_streams", "a:0", "-show_entries", "stream=bit_rate", "-of", "default=noprint_wrappers=1:nokey=1", file}
            Dim probe = Await ProcessRunner.RunAsync(paths.Ffprobe, probeArgs, token)
            Dim actualRate As Integer
            If probe.ExitCode = 0 AndAlso Integer.TryParse(probe.Output.Trim(), actualRate) AndAlso actualRate = bitrate * 1000 Then
                log.WriteLine("MP3 audio already matches " & bitrate & " kbps; no additional encoding needed.")
                Return
            End If
        End If
        Dim temporary = Path.Combine(Path.GetDirectoryName(file), ".bitrate-" & Guid.NewGuid().ToString("N") & "." & format)
        Dim encoder = If(format = "mp3", "libmp3lame", If(format = "m4a", "aac", "libopus"))
        Dim args As New List(Of String) From {"-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", file, "-map", "0", "-map_metadata", "0", "-c", "copy", "-c:a", encoder, "-b:a", bitrate.ToString(CultureInfo.InvariantCulture) & "k"}
        If format = "mp3" Then args.AddRange({"-id3v2_version", "3"})
        args.Add(temporary)
        Try
            log.WriteLine("Encoding new " & format.ToUpperInvariant() & " output at " & bitrate & " kbps")
            Dim result = Await ProcessRunner.RunAsync(paths.Ffmpeg, args, token)
            log.WriteLine(result.Errors)
            If result.ExitCode <> 0 OrElse Not System.IO.File.Exists(temporary) Then Throw New IOException("FFmpeg could not encode the selected bitrate. " & result.Errors)
            token.ThrowIfCancellationRequested()
            System.IO.File.Replace(temporary, file, Nothing)
        Finally
            If System.IO.File.Exists(temporary) Then System.IO.File.Delete(temporary)
        End Try
    End Function
End Class

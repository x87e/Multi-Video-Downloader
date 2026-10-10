Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text.RegularExpressions
Imports System.Threading

Public Class DependencyManager
    Private ReadOnly gate As New SemaphoreSlim(1, 1)
    Private cached As ToolPaths
    Private ReadOnly availableUpdates As New HashSet(Of String)
    Private Enum ToolOperation
        Existing
        Startup
        InstallUpdates
        Repair
    End Enum
    Public ReadOnly Property UpdateAvailable As Boolean
        Get
            Return availableUpdates.Count > 0
        End Get
    End Property
    Public Property LastNotice As String = "Tools have not been checked yet."
    Private Shared ReadOnly client As HttpClient = CreateClient()
    Private Shared ReadOnly groups As New Dictionary(Of String, String()) From {{"yt-dlp", {"yt-dlp.exe"}}, {"ffmpeg", {"ffmpeg.exe", "ffprobe.exe"}}, {"deno", {"deno.exe"}}}

    Private Shared Function CreateClient() As HttpClient
        Dim value As New HttpClient With {.Timeout = TimeSpan.FromMinutes(20)}
        value.DefaultRequestHeaders.UserAgent.ParseAdd("YtdlpGui/1.0")
        Return value
    End Function
    Public Function EnsureAsync(force As Boolean, progress As IProgress(Of String), token As CancellationToken) As Task(Of ToolPaths)
        Return PrepareAsync(If(force, ToolOperation.Repair, ToolOperation.Existing), progress, token)
    End Function
    Public Function CheckOnStartupAsync(progress As IProgress(Of String), token As CancellationToken) As Task(Of ToolPaths)
        Return PrepareAsync(ToolOperation.Startup, progress, token)
    End Function
    Public Function InstallAvailableUpdatesAsync(progress As IProgress(Of String), token As CancellationToken) As Task(Of ToolPaths)
        Return PrepareAsync(ToolOperation.InstallUpdates, progress, token)
    End Function
    Private Async Function PrepareAsync(operation As ToolOperation, progress As IProgress(Of String), token As CancellationToken) As Task(Of ToolPaths)
        Await gate.WaitAsync(token)
        Try
            If operation = ToolOperation.Existing AndAlso cached IsNot Nothing AndAlso {cached.Ytdlp, cached.Ffmpeg, cached.Ffprobe, cached.Deno}.All(Function(p) System.IO.File.Exists(p)) Then Return cached
            cached = Nothing
            progress.Report("Verifying installed tools…")
            Dim manifest As New ToolManifest()
            Dim file = AppPaths.Inside("tools", "manifest.json")
            Dim initialSetup = Not System.IO.File.Exists(file)
            If System.IO.File.Exists(file) Then
                Try
                    manifest = JsonStore.Read(Of ToolManifest)(file)
                    If manifest Is Nothing OrElse manifest.Tools Is Nothing Then manifest = New ToolManifest()
                Catch
                    manifest = New ToolManifest()
                End Try
            End If
            Dim healthy As New Dictionary(Of String, Boolean)
            For Each group In groups.Keys
                healthy(group) = Await Task.Run(Function() IsHealthy(manifest, group), token)
            Next
            Dim cleanupWarnings = CleanupUnusedTools(manifest, healthy)
            Dim allHealthy = healthy.Values.All(Function(b) b)
            If operation = ToolOperation.Existing Then
                If Not allHealthy Then Throw New IOException("Required tools are missing or damaged. Click Update Tools to reinstall them.")
                cached = Resolve(manifest)
                LastNotice = "Installed tools ready"
                If cleanupWarnings.Count > 0 Then LastNotice &= " · Cleanup pending: " & String.Join(" | ", cleanupWarnings.Distinct())
                progress.Report(LastNotice)
                Return cached
            End If
            Dim warnings As New List(Of String)
            For Each group In groups.Keys
                token.ThrowIfCancellationRequested()
                Try
                    progress.Report("Checking " & group & " on GitHub…")
                    Dim repo = If(group = "yt-dlp", "yt-dlp/yt-dlp", If(group = "ffmpeg", "yt-dlp/FFmpeg-Builds", "denoland/deno"))
                    Dim releaseUrl = "https://api.github.com/repos/" & repo & "/releases/" & If(group = "ffmpeg", "tags/latest", "latest")
                    Dim raw As String
                    Using timeout = CancellationTokenSource.CreateLinkedTokenSource(token)
                        timeout.CancelAfter(TimeSpan.FromSeconds(35))
                        raw = Await TextAsync(releaseUrl, timeout.Token)
                    End Using
                    Dim release = JsonStore.Serializer().Deserialize(Of Dictionary(Of String, Object))(raw)
                    Dim assetName = If(group = "yt-dlp", "yt-dlp.exe", If(group = "ffmpeg", "ffmpeg-master-latest-win64-gpl.zip", "deno-x86_64-pc-windows-msvc.zip"))
                    Dim assets = DirectCast(release("assets"), System.Collections.IEnumerable).Cast(Of Dictionary(Of String, Object))().ToList()
                    Dim asset = assets.Single(Function(a) CStr(a("name")) = assetName)
                    Dim assetId = Convert.ToString(asset("id"), Globalization.CultureInfo.InvariantCulture)
                    If healthy(group) AndAlso manifest.Tools(group).AssetId = assetId Then
                        availableUpdates.Remove(group)
                        If operation <> ToolOperation.Repair Then Continue For
                    ElseIf healthy(group) Then
                        availableUpdates.Add(group)
                    End If
                    ' Only initial setup installs automatically; later openings just check for updates.
                    If operation = ToolOperation.Startup AndAlso (healthy(group) OrElse Not initialSetup) Then Continue For
                    Dim record = Await InstallAsync(group, CStr(release("tag_name")), assetId, asset, assets, progress, token)
                    manifest.Tools(group) = record
                    JsonStore.Write(file, manifest)
                    healthy(group) = True
                    availableUpdates.Remove(group)
                    cleanupWarnings = CleanupUnusedTools(manifest, healthy)
                Catch ex As Exception
                    token.ThrowIfCancellationRequested()
                    If Not healthy(group) Then Throw New IOException("Cannot prepare " & group & ". " & ex.Message & " Check your connection and use Update tools to retry.", ex)
                    warnings.Add(group & ": " & ex.Message)
                End Try
            Next
            If warnings.Count = 0 Then manifest.LastCheckedUtc = DateTime.UtcNow
            JsonStore.Write(file, manifest)
            If Not healthy.Values.All(Function(b) b) Then Throw New IOException("Required tools are missing or damaged. Click Update Tools to reinstall them.")
            cached = Resolve(manifest)
            LastNotice = If(warnings.Count = 0, "Tools ready · latest GitHub releases checked", "Installed tools ready; tool check or installation failed. " & String.Join(" | ", warnings))
            If UpdateAvailable Then LastNotice &= " · Updates available. Click Tool Update Available to install."
            If cleanupWarnings.Count > 0 Then LastNotice &= " · Cleanup pending: " & String.Join(" | ", cleanupWarnings.Distinct())
            progress.Report(LastNotice)
            Return cached
        Finally
            gate.Release()
        End Try
    End Function

    Private Shared Function CleanupUnusedTools(manifest As ToolManifest, healthy As Dictionary(Of String, Boolean)) As List(Of String)
        Dim warnings As New List(Of String)
        Try
            For Each directory In System.IO.Directory.GetDirectories(AppPaths.Inside("tools"))
                Dim name = Path.GetFileName(directory)
                Dim version = Regex.Match(name, "^(yt-dlp|ffmpeg|deno)-[a-f0-9]{32}$")
                Dim isStage = Regex.IsMatch(name, "^\.stage-[a-f0-9]{32}$")
                If Not version.Success AndAlso Not isStage Then Continue For
                If version.Success Then
                    Dim group = version.Groups(1).Value
                    ' Only a verified, committed replacement permits removal of this tool's old versions.
                    If Not healthy.ContainsKey(group) OrElse Not healthy(group) OrElse Not manifest.Tools.ContainsKey(group) Then Continue For
                    If String.Equals(name, manifest.Tools(group).DirectoryName, StringComparison.OrdinalIgnoreCase) Then Continue For
                End If
                Try
                    DeleteUnusedToolDirectory(directory)
                Catch ex As Exception
                    warnings.Add(name & ": " & ex.Message & " Cleanup will be retried on a later tool check.")
                End Try
            Next
        Catch ex As Exception
            warnings.Add("Could not check unused tool folders: " & ex.Message)
        End Try
        Return warnings
    End Function

    Private Shared Sub DeleteUnusedToolDirectory(directory As String)
        Dim name = Path.GetFileName(directory)
        Dim checkedPath = AppPaths.Inside("tools", name)
        If Not String.Equals(directory, checkedPath, StringComparison.OrdinalIgnoreCase) OrElse Not Regex.IsMatch(name, "^((yt-dlp|ffmpeg|deno)-|\.stage-)[a-f0-9]{32}$") Then
            Throw New IOException("Refusing to remove a folder outside managed tool storage.")
        End If
        ' Our installer creates flat folders. Refuse unexpected subfolders and links rather than traverse them.
        If System.IO.Directory.GetDirectories(checkedPath).Length <> 0 Then Throw New IOException("The unused tool folder contains unexpected subfolders.")
        Dim files = System.IO.Directory.GetFiles(checkedPath)
        For Each file In files
            AppPaths.RejectLinks(file)
        Next
        For Each file In files
            System.IO.File.Delete(file)
        Next
        System.IO.Directory.Delete(checkedPath, False)
    End Sub

    Public Shared Function HashFile(file As String) As String
        Using stream = System.IO.File.OpenRead(file), hash = SHA256.Create()
            Return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant()
        End Using
    End Function
    Private Shared Function IsHealthy(manifest As ToolManifest, group As String) As Boolean
        Try
            If Not manifest.Tools.ContainsKey(group) Then Return False
            Dim record = manifest.Tools(group)
            If record Is Nothing OrElse Not Regex.IsMatch(record.DirectoryName, "^(yt-dlp|ffmpeg|deno)-[a-f0-9]{32}$") Then Return False
            For Each name In groups(group)
                Dim file = AppPaths.Inside("tools", record.DirectoryName, name)
                If Not System.IO.File.Exists(file) OrElse Not record.Hashes.ContainsKey(name) OrElse HashFile(file) <> record.Hashes(name) Then Return False
            Next
            Return True
        Catch
            Return False
        End Try
    End Function
    Private Shared Function Resolve(manifest As ToolManifest) As ToolPaths
        Return New ToolPaths With {.Ytdlp = AppPaths.Inside("tools", manifest.Tools("yt-dlp").DirectoryName, "yt-dlp.exe"), .Ffmpeg = AppPaths.Inside("tools", manifest.Tools("ffmpeg").DirectoryName, "ffmpeg.exe"), .Ffprobe = AppPaths.Inside("tools", manifest.Tools("ffmpeg").DirectoryName, "ffprobe.exe"), .Deno = AppPaths.Inside("tools", manifest.Tools("deno").DirectoryName, "deno.exe")}
    End Function

    Private Shared Async Function InstallAsync(group As String, version As String, assetId As String, asset As Dictionary(Of String, Object), assets As List(Of Dictionary(Of String, Object)), progress As IProgress(Of String), token As CancellationToken) As Task(Of ToolRecord)
        Dim directoryName = group & "-" & Guid.NewGuid().ToString("N")
        Dim stage = AppPaths.Inside("tools", ".stage-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(stage)
        Try
            Dim assetName = CStr(asset("name"))
            Dim archive = Path.Combine(stage, "payload")
            progress.Report("Installing " & group & " " & version & "…")
            Await FetchAsync(CStr(asset("browser_download_url")), archive, progress, group, token)
            Dim digest = If(asset.ContainsKey("digest"), TryCast(asset("digest"), String), Nothing)
            Dim expected As String = Nothing
            If digest IsNot Nothing AndAlso Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") Then expected = digest.Substring(7).ToLowerInvariant()
            If expected Is Nothing Then
                Dim checksum = assets.FirstOrDefault(Function(a) CStr(a("name")) = "SHA2-256SUMS" OrElse CStr(a("name")) = "checksums.sha256" OrElse CStr(a("name")) = assetName & ".sha256sum" OrElse CStr(a("name")) = assetName & ".sha256")
                If checksum IsNot Nothing Then
                    Dim sums = Await TextAsync(CStr(checksum("browser_download_url")), token)
                    For Each line In sums.Split(Microsoft.VisualBasic.ControlChars.Lf)
                        Dim match = Regex.Match(line.Trim(), "^([a-fA-F0-9]{64})\s+\*?(.+)$")
                        If match.Success AndAlso match.Groups(2).Value = assetName Then expected = match.Groups(1).Value.ToLowerInvariant()
                    Next
                    If expected Is Nothing AndAlso Regex.IsMatch(sums.Trim(), "^[a-fA-F0-9]{64}$") Then expected = sums.Trim().ToLowerInvariant()
                End If
            End If
            If expected Is Nothing Then Throw New IOException("The release has no usable SHA-256 digest; installation was not activated.")
            If Await Task.Run(Function() HashFile(archive), token) <> expected Then Throw New IOException("SHA-256 verification failed. The downloaded file was rejected.")
            progress.Report("Checking and unpacking " & group & "…")
            If group = "yt-dlp" Then
                File.Move(archive, Path.Combine(stage, "yt-dlp.exe"))
            Else
                Await Task.Run(Sub() ExtractExecutables(archive, stage, groups(group)), token)
                File.Delete(archive)
            End If
            Dim record As New ToolRecord With {.AssetId = assetId, .Version = version, .DirectoryName = directoryName}
            For Each name In groups(group)
                token.ThrowIfCancellationRequested()
                Dim executable = Path.Combine(stage, name)
                Using timeout = CancellationTokenSource.CreateLinkedTokenSource(token)
                    timeout.CancelAfter(TimeSpan.FromSeconds(25))
                    Dim result = Await ProcessRunner.RunAsync(executable, {If(name.StartsWith("ff"), "-version", "--version")}, timeout.Token)
                    If result.ExitCode <> 0 Then Throw New IOException(name & " failed its startup check: " & result.Errors)
                End Using
                record.Hashes(name) = Await Task.Run(Function() HashFile(executable), token)
            Next
            Directory.Move(stage, AppPaths.Inside("tools", directoryName))
            Return record
        Finally
            DeleteStage(stage)
        End Try
    End Function

    Public Shared Sub ExtractExecutables(archive As String, target As String, names As String())
        Using zip = ZipFile.OpenRead(archive)
            For Each name In names
                Dim matches = zip.Entries.Where(Function(e) e.FullName.Replace("\", "/").Split("/"c).Last() = name).ToList()
                If matches.Count <> 1 Then Throw New IOException("Archive must contain exactly one " & name)
                Dim entry = matches(0)
                If entry.Length < 1024 OrElse entry.Length > 512L * 1024 * 1024 Then Throw New IOException("Unexpected executable size in archive.")
                ' Never use archive paths: only explicit expected basenames are written.
                Using source = entry.Open(), output = New FileStream(Path.Combine(target, name), FileMode.CreateNew)
                    source.CopyTo(output)
                End Using
            Next
        End Using
    End Sub
    Private Shared Sub DeleteStage(stage As String)
        Dim checkedPath = AppPaths.Inside("tools", Path.GetFileName(stage))
        If Not String.Equals(stage, checkedPath, StringComparison.OrdinalIgnoreCase) OrElse Not Path.GetFileName(stage).StartsWith(".stage-") Then Return
        If Not Directory.Exists(stage) Then Return
        ' Stage contains only our known flat files. Do not follow or recursively delete directory entries.
        For Each file In Directory.GetFiles(stage)
            AppPaths.RejectLinks(file)
            System.IO.File.Delete(file)
        Next
        Directory.Delete(stage, False)
    End Sub
    Private Shared Sub ValidateGithub(url As String)
        Dim uri As New Uri(url)
        If uri.Scheme <> "https" OrElse Not {"api.github.com", "github.com"}.Contains(uri.Host) Then Throw New IOException("Unexpected dependency download host.")
    End Sub
    Private Shared Async Function TextAsync(url As String, token As CancellationToken) As Task(Of String)
        ValidateGithub(url)
        Using response = Await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
            response.EnsureSuccessStatusCode()
            Using stream = Await response.Content.ReadAsStreamAsync(), output As New MemoryStream()
                Dim buffer(8191) As Byte
                Do
                    Dim count = Await stream.ReadAsync(buffer, 0, buffer.Length, token)
                    If count = 0 Then Exit Do
                    If output.Length + count > 8 * 1024 * 1024 Then Throw New IOException("Release metadata exceeds the size limit.")
                    output.Write(buffer, 0, count)
                Loop
                Return System.Text.Encoding.UTF8.GetString(output.ToArray())
            End Using
        End Using
    End Function
    Private Shared Async Function FetchAsync(url As String, file As String, progress As IProgress(Of String), name As String, token As CancellationToken) As Task
        ValidateGithub(url)
        Using timeout = CancellationTokenSource.CreateLinkedTokenSource(token)
            timeout.CancelAfter(TimeSpan.FromMinutes(20))
            Using response = Await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                response.EnsureSuccessStatusCode()
                Using source = Await response.Content.ReadAsStreamAsync(), target As New FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, True)
                    Dim buffer(81919) As Byte
                    Dim total As Long
                    Dim last = DateTime.MinValue
                    Do
                        Dim count = Await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token)
                        If count = 0 Then Exit Do
                        total += count
                        If total > 1024L * 1024 * 1024 Then Throw New IOException("Dependency download exceeds the 1 GB limit.")
                        Await target.WriteAsync(buffer, 0, count, timeout.Token)
                        If DateTime.UtcNow - last > TimeSpan.FromMilliseconds(400) Then
                            Dim size = response.Content.Headers.ContentLength
                            progress.Report("Downloading " & name & " · " & (total / 1048576).ToString("0.0") & " MB" & If(size.HasValue, " / " & (size.GetValueOrDefault() / 1048576).ToString("0.0") & " MB", ""))
                            last = DateTime.UtcNow
                        End If
                    Loop
                    If response.Content.Headers.ContentLength.HasValue AndAlso response.Content.Headers.ContentLength.Value <> total Then Throw New IOException("Dependency download was incomplete.")
                End Using
            End Using
        End Using
    End Function
End Class


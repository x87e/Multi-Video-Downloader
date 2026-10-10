Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports Microsoft.Win32.SafeHandles

Public Class ProcessResult
    Public Property ExitCode As Integer
    Public Property Output As String
    Public Property Errors As String
End Class

Public NotInheritable Class ProcessRunner
    ' CommandLineToArgvW/CRT escaping; no cmd.exe, shell expansion, or command interpolation.
    Public Shared Function Quote(value As String) As String
        Dim result As New StringBuilder("""")
        Dim slashes As Integer
        For Each c In value
            If c = "\"c Then
                slashes += 1
            ElseIf c = """"c Then
                result.Append("\"c, slashes * 2 + 1).Append(c)
                slashes = 0
            Else
                result.Append("\"c, slashes).Append(c)
                slashes = 0
            End If
        Next
        Return result.Append("\"c, slashes * 2).Append(""""c).ToString()
    End Function

    Public Shared Function RunAsync(executable As String, arguments As IEnumerable(Of String), token As CancellationToken, Optional onLine As Action(Of String, Boolean) = Nothing, Optional captureLimit As Integer = 1024 * 1024) As Task(Of ProcessResult)
        Return Task.Run(Function()
                            token.ThrowIfCancellationRequested()
                            Dim stdout As New StringBuilder()
                            Dim stderr As New StringBuilder()
                            Dim sync As New Object()
                            Dim callbackError As Exception = Nothing
                            Using job As New ProcessJob(), process As New Process()
                                process.StartInfo = New ProcessStartInfo(executable, String.Join(" ", arguments.Select(Function(a) Quote(a)))) With {
                                    .UseShellExecute = False, .CreateNoWindow = True, .WindowStyle = ProcessWindowStyle.Hidden,
                                    .RedirectStandardOutput = True, .RedirectStandardError = True,
                                    .StandardOutputEncoding = Encoding.UTF8, .StandardErrorEncoding = Encoding.UTF8,
                                    .WorkingDirectory = AppPaths.Root}
                                Dim receive As Action(Of String, Boolean) = Sub(line, isError)
                                                                               If line Is Nothing Then Return
                                                                               SyncLock sync
                                                                                   Dim builder = If(isError, stderr, stdout)
                                                                                   If builder.Length + line.Length > captureLimit Then builder.Remove(0, Math.Min(builder.Length, Math.Max(line.Length, builder.Length \ 2)))
                                                                                   builder.AppendLine(If(line.Length > captureLimit, line.Substring(0, captureLimit), line))
                                                                                   Try
                                                                                       If onLine IsNot Nothing Then onLine(line, isError)
                                                                                   Catch ex As Exception
                                                                                       callbackError = ex
                                                                                       job.Kill()
                                                                                   End Try
                                                                               End SyncLock
                                                                           End Sub
                                AddHandler process.OutputDataReceived, Sub(sender, e) receive(e.Data, False)
                                AddHandler process.ErrorDataReceived, Sub(sender, e) receive(e.Data, True)
                                process.Start()
                                Try
                                    job.Attach(process)
                                    process.BeginOutputReadLine()
                                    process.BeginErrorReadLine()
                                    Using registration = token.Register(Sub() job.Kill())
                                        process.WaitForExit()
                                    End Using
                                    token.ThrowIfCancellationRequested()
                                    If callbackError IsNot Nothing Then Throw New IOException("Could not process tool output: " & callbackError.Message, callbackError)
                                    Return New ProcessResult With {.ExitCode = process.ExitCode, .Output = stdout.ToString(), .Errors = stderr.ToString()}
                                Finally
                                    job.Kill()
                                    If Not process.HasExited Then
                                        process.Kill()
                                        process.WaitForExit()
                                    End If
                                End Try
                            End Using
                        End Function)
    End Function
End Class

Friend NotInheritable Class ProcessJob
    Implements IDisposable
    Private ReadOnly handle As SafeFileHandle
    <StructLayout(LayoutKind.Sequential)>
    Private Structure BasicLimits
        Public PerProcessTime As Long
        Public PerJobTime As Long
        Public Flags As UInteger
        Public MinimumWorkingSet As UIntPtr
        Public MaximumWorkingSet As UIntPtr
        Public ActiveProcessLimit As UInteger
        Public Affinity As UIntPtr
        Public PriorityClass As UInteger
        Public SchedulingClass As UInteger
    End Structure
    <StructLayout(LayoutKind.Sequential)>
    Private Structure IoCounters
        Public ReadOperations As ULong
        Public WriteOperations As ULong
        Public OtherOperations As ULong
        Public ReadBytes As ULong
        Public WriteBytes As ULong
        Public OtherBytes As ULong
    End Structure
    <StructLayout(LayoutKind.Sequential)>
    Private Structure ExtendedLimits
        Public Basic As BasicLimits
        Public Io As IoCounters
        Public ProcessMemoryLimit As UIntPtr
        Public JobMemoryLimit As UIntPtr
        Public PeakProcessMemory As UIntPtr
        Public PeakJobMemory As UIntPtr
    End Structure
    <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function CreateJobObject(attributes As IntPtr, name As String) As SafeFileHandle
    End Function
    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function SetInformationJobObject(job As SafeFileHandle, kind As Integer, ByRef info As ExtendedLimits, size As UInteger) As Boolean
    End Function
    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function AssignProcessToJobObject(job As SafeFileHandle, process As IntPtr) As Boolean
    End Function
    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function TerminateJobObject(job As SafeFileHandle, code As UInteger) As Boolean
    End Function
    Public Sub New()
        handle = CreateJobObject(IntPtr.Zero, Nothing)
        If handle.IsInvalid Then Throw New ComponentModel.Win32Exception(Marshal.GetLastWin32Error())
        Dim limits As New ExtendedLimits With {.Basic = New BasicLimits With {.Flags = &H2000UI}}
        If Not SetInformationJobObject(handle, 9, limits, CUInt(Marshal.SizeOf(limits))) Then Throw New ComponentModel.Win32Exception(Marshal.GetLastWin32Error())
    End Sub
    Public Sub Attach(process As Process)
        If Not AssignProcessToJobObject(handle, process.Handle) AndAlso Not process.HasExited Then Throw New ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot manage the downloader process tree.")
    End Sub
    Public Sub Kill()
        If Not handle.IsClosed AndAlso Not handle.IsInvalid Then TerminateJobObject(handle, 1UI)
    End Sub
    Public Sub Dispose() Implements IDisposable.Dispose
        handle.Dispose()
    End Sub
End Class

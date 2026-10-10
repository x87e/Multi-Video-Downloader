Imports System.IO
Imports System.Windows

Class Application
    Private instanceLock As FileStream
    Protected Overrides Sub OnStartup(e As StartupEventArgs)
        MyBase.OnStartup(e)
        Try
            AppPaths.Initialize()
            Try
                instanceLock = New FileStream(AppPaths.Inside("instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
            Catch ex As IOException
                MessageBox.Show("Multi Video Downloader is already running, or its application data folder is locked.", "Multi Video Downloader", MessageBoxButton.OK, MessageBoxImage.Information)
                Shutdown()
                Return
            End Try
            Dim window As New MainWindow()
            MainWindow = window
            window.Show()
        Catch ex As Exception
            MessageBox.Show(ex.Message, "Multi Video Downloader could not start", MessageBoxButton.OK, MessageBoxImage.Error)
            Shutdown(1)
        End Try
    End Sub
    Protected Overrides Sub OnExit(e As ExitEventArgs)
        If instanceLock IsNot Nothing Then instanceLock.Dispose()
        MyBase.OnExit(e)
    End Sub
End Class

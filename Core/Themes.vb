Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Windows
Imports System.Windows.Media

Public Class ThemeDefinition
    Public Property Name As String = "My theme"
    Public Property Background As String = "#0D131D"
    Public Property Panel As String = "#151E2C"
    Public Property Input As String = "#101824"
    Public Property Border As String = "#2B394C"
    Public Property Text As String = "#EEF3FA"
    Public Property Muted As String = "#A4B2C6"
    Public Property Accent As String = "#68E0CC"
    Public Property AccentText As String = "#082820"
    Public Property Selection As String = "#233F47"
    Public Overrides Function ToString() As String
        Return Name
    End Function
End Class

Public NotInheritable Class ThemeManager
    Public Shared Function Load(ByRef warning As String) As List(Of ThemeDefinition)
        Dim dark As New ThemeDefinition With {.Name = "Midnight"}
        Dim light As New ThemeDefinition With {.Name = "Daylight", .Background = "#EFF3F7", .Panel = "#FFFFFF", .Input = "#F5F7FA", .Border = "#CAD4DF", .Text = "#172437", .Muted = "#506176", .Accent = "#147A6D", .AccentText = "#FFFFFF", .Selection = "#DDF0EB"}
        Dim slate As New ThemeDefinition With {.Name = "Slate", .Background = "#171820", .Panel = "#21232E", .Input = "#191B24", .Border = "#3A3D4E", .Text = "#F1F0F8", .Muted = "#B4B1C8", .Accent = "#B8A8F7", .AccentText = "#25203C", .Selection = "#39334F"}
        Dim monochrome As New ThemeDefinition With {.Name = "Dark", .Background = "#121212", .Panel = "#1C1C1C", .Input = "#171717", .Border = "#3A3A3A", .Text = "#F0F0F0", .Muted = "#B0B0B0", .Accent = "#D8D8D8", .AccentText = "#121212", .Selection = "#333333"}
        Dim ocean As New ThemeDefinition With {.Name = "Ocean", .Background = "#16384A", .Panel = "#205066", .Input = "#153B50", .Border = "#5799B5", .Text = "#E8FAFF", .Muted = "#B7E5F4", .Accent = "#77E6F2", .AccentText = "#082635", .Selection = "#2D7087"}
        Dim result As New List(Of ThemeDefinition) From {dark, light, slate, monochrome}
        Dim example = AppPaths.Inside("themes", "example.theme.json")
        If Not File.Exists(example) Then JsonStore.Write(example, ocean)
        For Each file In Directory.GetFiles(AppPaths.Inside("themes"), "*.json")
            Try
                AppPaths.RejectLinks(file)
                Dim theme = Read(file)
                If String.Equals(Path.GetFileName(file), "example.theme.json", StringComparison.OrdinalIgnoreCase) AndAlso String.Equals(theme.Name, "Custom ocean", StringComparison.OrdinalIgnoreCase) Then
                    theme = ocean
                    JsonStore.Write(file, theme)
                End If
                If result.Any(Function(t) t.Name = theme.Name) Then Throw New IOException("Duplicate theme name.")
                result.Add(theme)
            Catch ex As Exception
                warning &= " Theme skipped: " & Path.GetFileName(file) & " (" & ex.Message & ")."
            End Try
        Next
        Return result
    End Function
    Public Shared Function Read(file As String) As ThemeDefinition
        If New FileInfo(file).Length > 65536 Then Throw New IOException("Theme file exceeds 64 KB.")
        Dim theme = JsonStore.Read(Of ThemeDefinition)(file)
        If theme Is Nothing OrElse String.IsNullOrWhiteSpace(theme.Name) OrElse theme.Name.Length > 40 Then Throw New IOException("Theme needs a name of up to 40 characters.")
        For Each propertyInfo In GetType(ThemeDefinition).GetProperties().Where(Function(p) p.Name <> "Name")
            If Not Regex.IsMatch(CStr(propertyInfo.GetValue(theme)), "^#[A-Fa-f0-9]{6}$") Then Throw New IOException(propertyInfo.Name & " must be a six-digit hex color.")
        Next
        Return theme
    End Function
    Public Shared Sub Apply(theme As ThemeDefinition)
        For Each propertyInfo In GetType(ThemeDefinition).GetProperties().Where(Function(p) p.Name <> "Name")
            Dim color = CType(ColorConverter.ConvertFromString(CStr(propertyInfo.GetValue(theme))), Color)
            Dim brush As New SolidColorBrush(color)
            brush.Freeze()
            System.Windows.Application.Current.Resources(propertyInfo.Name & "Brush") = brush
        Next
    End Sub
    Public Shared Function Import(file As String) As String
        Dim theme = Read(file)
        Dim destination = AppPaths.Inside("themes", Guid.NewGuid().ToString("N") & ".theme.json")
        JsonStore.Write(destination, theme)
        Return theme.Name
    End Function
End Class

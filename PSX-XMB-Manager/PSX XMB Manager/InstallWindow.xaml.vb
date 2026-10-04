Imports PSX_XMB_Manager.Structs
Imports PSX_XMB_Manager.Utils
Imports System.ComponentModel
Imports System.IO
Imports System.Text.RegularExpressions
Imports System.Threading

Public Class InstallWindow

    Public MountedDrive As MountedPSXDrive = Nothing
    Public StorageBackend As IPSXStorageBackend

    Private HDLGameID As String = ""

    Public ProjectToInstall As ComboBoxProjectItem = Nothing
    Public CurrentProjectDirectory As String = ""

    Public InstallStatus As String
    Public InstallForPS1 As Boolean = False
    Public InstallForPS2 As Boolean = False

    Private InstallRunning As Boolean = False
    Private InjectCancellation As CancellationTokenSource

    Private Sub InstallWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected OrElse String.IsNullOrEmpty(MountedDrive.DriveID) Then
            MsgBox("No HDD connected, installation will be aborted.", MsgBoxStyle.Critical, "Error while trying to install")
            Close()
        Else
            If ProjectToInstall IsNot Nothing Then
                If File.Exists(ProjectToInstall.ProjectFile) Then

                    Dim GameAppTitle As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(0).Split("="c)(1)
                    Dim GameAppID As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(1).Split("="c)(1)
                    Dim GameAppDirectory As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(2).Split("="c)(1)

                    'Set cover
                    If File.Exists(GameAppDirectory + "\res\jkt_001.png") Then
                        Dispatcher.BeginInvoke(Sub()
                                                   Dim TempBitmapImage = New BitmapImage()
                                                   TempBitmapImage.BeginInit()
                                                   TempBitmapImage.CacheOption = BitmapCacheOption.OnLoad
                                                   TempBitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache
                                                   TempBitmapImage.UriSource = New Uri(GameAppDirectory + "\res\jkt_001.png", UriKind.RelativeOrAbsolute)
                                                   TempBitmapImage.EndInit()
                                                   InstallImage.Source = TempBitmapImage
                                               End Sub)
                    Else
                        If IsURLValid("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS1/" + GameAppID + ".jpg") Then
                            Dispatcher.BeginInvoke(Sub()
                                                       Dim TempBitmapImage = New BitmapImage()
                                                       TempBitmapImage.BeginInit()
                                                       TempBitmapImage.CacheOption = BitmapCacheOption.OnLoad
                                                       TempBitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache
                                                       TempBitmapImage.UriSource = New Uri("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS1/" + GameAppID + ".jpg", UriKind.RelativeOrAbsolute)
                                                       TempBitmapImage.EndInit()
                                                       InstallImage.Source = TempBitmapImage
                                                   End Sub)
                        ElseIf IsURLValid("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS2/" + GameAppID + ".jpg") Then
                            Dispatcher.BeginInvoke(Sub()
                                                       Dim TempBitmapImage = New BitmapImage()
                                                       TempBitmapImage.BeginInit()
                                                       TempBitmapImage.CacheOption = BitmapCacheOption.OnLoad
                                                       TempBitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache
                                                       TempBitmapImage.UriSource = New Uri("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS2/" + GameAppID + ".jpg", UriKind.RelativeOrAbsolute)
                                                       TempBitmapImage.EndInit()
                                                       InstallImage.Source = TempBitmapImage
                                                   End Sub)
                        End If
                    End If
                End If

                'Set current status
                If Not String.IsNullOrEmpty(InstallStatus) Then
                    InstallationStatusTextBlock.Text = InstallStatus
                End If
            Else
                MsgBox("Could not load the selected project to install.", MsgBoxStyle.Critical, "Error")
                Close()
            End If
        End If
    End Sub

    Private Async Sub InstallWindow_ContentRendered(sender As Object, e As EventArgs) Handles Me.ContentRendered
        If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected OrElse ProjectToInstall Is Nothing Then Return
        Await Task.Delay(200)

        InstallRunning = True
        Try
            If InstallForPS2 Then
                Await InstallPS2GameAsync()
            ElseIf InstallForPS1 Then
                Await InstallPS1GameAsync()
            Else
                Await InstallAppAsync()
            End If
        Catch ex As Exception
            Dim BackendError As StorageBackendException = TryCast(ex, StorageBackendException)
            MsgBox(If(BackendError IsNot Nothing, BackendErrorCodes.FormatForUser(BackendError), ex.Message), MsgBoxStyle.Critical, "Error while installing")
            SetStatus("")
        Finally
            InstallRunning = False
            Mouse.SetCursor(Cursors.Arrow)
        End Try
    End Sub

    Private Sub InstallWindow_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        If Not InstallRunning Then Return

        e.Cancel = True
        If InjectCancellation IsNot Nothing Then
            If MsgBox("Cancel the game injection?" + vbCrLf + "hdl_dump stops safely and the game will not be added.", MsgBoxStyle.YesNo Or MsgBoxStyle.Question, "Cancel installation") = MsgBoxResult.Yes Then
                InjectCancellation.Cancel()
            End If
        Else
            MsgBox("The installation is writing to the PSX HDD." + vbCrLf + "Please wait until it has finished.", MsgBoxStyle.Exclamation, "Please wait")
        End If
    End Sub

    ''' <summary>Closes the window from inside the installation flow (the flow returns right after).</summary>
    Private Sub CloseWindow()
        InstallRunning = False
        Close()
    End Sub

    Private Sub SetStatus(Text As String)
        InstallationStatusTextBlock.Text = Text
    End Sub

    ''' <summary>hdl_dump's device name for the connected HDD (hdd#: locally, the NBD raw file in WSL).</summary>
    Private ReadOnly Property HDLDriveName As String
        Get
            Return MountedDrive.HDLDriveName
        End Get
    End Property

    Private Shared Function DescribeFailure(Tool As String, Result As ProcessResult) As String
        Dim Reason As String = If(Result.TimedOut, " timed out.", If(Result.Cancelled, " was cancelled.", " failed with exit code " + Result.ExitCode.ToString() + "."))
        Return Tool + Reason + vbCrLf + vbCrLf + Result.CombinedOutput.Trim().Replace(vbLf, vbCrLf)
    End Function

    Public Async Function InstallAppAsync() As Task
        If String.IsNullOrEmpty(HDLDriveName) Then
            MsgBox("Could not determine the HDD drive name of the connected PSX HDD.", MsgBoxStyle.Critical, "Error while installing homebrew")
            Return
        End If

        If ProjectToInstall IsNot Nothing Then
            'Proceed to installation on HDD

            'Get homebrew properties
            Dim HomebrewTitle As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(0).Split("="c)(1)
            Dim HomebrewELF As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(3).Split("="c)(1)
            Dim HomebrewPartition As String

            CurrentProjectDirectory = File.ReadAllLines(ProjectToInstall.ProjectFile)(2).Split("="c)(1)

            'Set a PP partition name on known homebrew
            If HomebrewTitle.Contains("Open PS2 Loader") Or HomebrewTitle.Contains("OPL") Then
                HomebrewPartition = "PP.APPS-00001..OPL"
            ElseIf HomebrewTitle.Contains("LaunchELF") Or HomebrewTitle.Contains("uLE") Or HomebrewTitle.Contains("wLE") Then
                HomebrewPartition = "PP.APPS-00002..WLE"
            ElseIf HomebrewTitle.Contains("hdl_srv") Or HomebrewTitle.Contains("hdl_server") Or HomebrewTitle.Contains("hdl server") Then
                HomebrewPartition = "PP.APPS-00003..HDL"
            ElseIf HomebrewTitle.Contains("SMS") Or HomebrewTitle.Contains("Simple Media System") Then
                HomebrewPartition = "PP.APPS-00004..SMS"
            ElseIf HomebrewTitle.Contains("GSM") Then
                HomebrewPartition = "PP.APPS-00005..GSM"
            Else
                'Set own PP partition name
                HomebrewPartition = InputBox("Please enter a valid partition name:", "Could not determine partition for this homebrew.", "PP.APPS-00001..TITLE")
            End If

            'Update UI
            SetStatus("Creating partition, please wait...")

            If Not String.IsNullOrEmpty(HomebrewPartition) Then
                Await CreateHomebrewPartitionAsync(HomebrewPartition)
            Else
                MsgBox("Partition name cannot be empty! Please try again.", MsgBoxStyle.Exclamation, "Error")
                Return
            End If
        End If
    End Function

    Public Async Function InstallPS2GameAsync() As Task
        If ProjectToInstall IsNot Nothing Then
            'Proceed to installation on HDD
            'Get game properties
            Dim GameTitle As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(0).Split("="c)(1)
            Dim GameID As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(1).Split("="c)(1)
            Dim GameISO As String = File.ReadAllLines(ProjectToInstall.ProjectFile)(3).Split("="c)(1)

            HDLGameID = File.ReadAllLines(ProjectToInstall.ProjectFile)(1).Split("="c)(1).Replace("_", "-").Replace(".", "").Trim()
            CurrentProjectDirectory = File.ReadAllLines(ProjectToInstall.ProjectFile)(2).Split("="c)(1)

            'The ISO path as the backend's hdl_dump sees it (unchanged locally, /mnt/... inside WSL); it must exist there
            Dim BackendISOPath As String = Await StorageBackend.ConvertWindowsPathAsync(GameISO, CancellationToken.None, mustExist:=True)

            'Check if it's a CD or DVD and start injecting the game
            Dim InjectCommand As String = If(GetDiscType(GameISO) = DiscType.DVD, "inject_dvd", "inject_cd")
            Dim InjectProgress As New Progress(Of String)(AddressOf ShowInjectProgress)
            Dim Result As ProcessResult

            InjectCancellation = New CancellationTokenSource()
            Try
                Result = Await StorageBackend.RunHdlDumpAsync({InjectCommand, HDLDriveName, GameTitle, BackendISOPath, GameID, "*u4", "-hide"},
                                                              Nothing, InjectCancellation.Token, InjectProgress)
            Finally
                InjectCancellation.Dispose()
                InjectCancellation = Nothing
            End Try

            If Result.Cancelled Then
                MsgBox("The installation was cancelled. hdl_dump stopped before adding the game.", MsgBoxStyle.Information, "Installation cancelled")
                CloseWindow()
                Return
            ElseIf Not Result.Succeeded Then
                MsgBox(DescribeFailure("hdl_dump " + InjectCommand, Result), MsgBoxStyle.Exclamation, "Error installing game")
                SetStatus("")
                Return
            End If

            'Proceed to the creation of the game's PP partition
            SetStatus("Creating game PP partition ...")
            Await CreateGamePartitionAsync()
        Else
            MsgBox("Could not load the project to install.", MsgBoxStyle.Critical, "Error")
        End If
    End Function

    Public Async Function InstallPS1GameAsync() As Task
        If ProjectToInstall IsNot Nothing Then
            'Proceed to installation on HDD
            'Get game properties
            Dim ProjectInfos As String() = File.ReadAllLines(ProjectToInstall.ProjectFile)
            Dim GameTitle As String = ProjectInfos(0).Split("="c)(1)
            Dim GameID As String = ProjectInfos(1).Split("="c)(1)
            CurrentProjectDirectory = ProjectInfos(2).Split("="c)(1)
            Dim GameVCD As String = ProjectInfos(3).Split("="c)(1)

            'Ask for a partition name
            Dim PPPartitionName As String = InputBox("Enter a valid partition name starting with PP. including the dot." + vbCrLf + vbCrLf + "WARNING: No + sign in the partition name and no whitespaces !", "Creating the game PP partition", "PP.SHORT_GAME_TITLE")
            If Not String.IsNullOrEmpty(PPPartitionName) Then
                If PPPartitionName.StartsWith("PP.") Then
                    If PPPartitionName.Length < 50 Then

                        If PPPartitionName.Contains("+") Then
                            MsgBox("A + sign has been detected in the partition name and will be replaced with ""_"".", MsgBoxStyle.Information, "Unallowed character detected")
                            PPPartitionName = PPPartitionName.Replace("+", "_")
                        End If

                        'Trim the final PPPartitionName
                        PPPartitionName = PPPartitionName.Trim()
                    Else
                        MsgBox("Partition name is too long. Please retry the installation with a shorter name.", MsgBoxStyle.Critical, "Partition name invalid")
                        CloseWindow()
                        Return
                    End If
                Else
                    MsgBox("Partition name needs to start with ""PP."". Please retry the installation.", MsgBoxStyle.Critical, "Partition name invalid")
                    CloseWindow()
                    Return
                End If
            Else
                MsgBox("No partition name entered. Exiting installation.", MsgBoxStyle.Critical, "Partition name invalid")
                CloseWindow()
                Return
            End If

            'Calculate the required partition size
            Dim TotalGameSize As Long = New FileInfo(GameVCD).Length
            For Each ProjectFileLine As String In ProjectInfos
                If ProjectFileLine.StartsWith("IMAGE1=") Then TotalGameSize += New FileInfo(ProjectFileLine.Split("="c)(1)).Length
                If ProjectFileLine.StartsWith("IMAGE2=") Then TotalGameSize += New FileInfo(ProjectFileLine.Split("="c)(1)).Length
                If ProjectFileLine.StartsWith("IMAGE3=") Then TotalGameSize += New FileInfo(ProjectFileLine.Split("="c)(1)).Length
            Next

            Dim GameTotalSizeInMB As Double = TotalGameSize / 1024 / 1024
            Dim GameTotalSizeRoundedValue As Double = Math.Round(GameTotalSizeInMB, 0, MidpointRounding.AwayFromZero)
            Dim GameRequiredPartitionSizeInMB As Double

            'Final partition size should be a multiple of 128MiB
            If GameTotalSizeRoundedValue >= 1000 Then
                GameRequiredPartitionSizeInMB = 128 * Integer.Parse(GameTotalSizeRoundedValue.ToString().Substring(0, 2))
            Else
                GameRequiredPartitionSizeInMB = 128 * Integer.Parse(GameTotalSizeRoundedValue.ToString().Substring(0, 1))
            End If

            If MsgBox("A new partition " + PPPartitionName + " with " + GameRequiredPartitionSizeInMB.ToString() + "M will be created." + vbCrLf + "Do you want to proceed with the installation ?", MsgBoxStyle.YesNo, "Please confirm") = MsgBoxResult.Yes Then

                '1. mkpart commands for the PP partition
                Dim Commands As New List(Of String) From {
                    "device " + MountedDrive.DriveID,
                    "mkpart " + PPPartitionName + " " + GameRequiredPartitionSizeInMB.ToString() + "M PFS",
                    "exit"
                }

                'Update UI
                SetStatus("Creating game partition...")

                '2. Proceed to partition creation
                Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, Nothing, CancellationToken.None)
                Dim PFSShellOutput As String = Result.CombinedOutput

                '3. Read partition creation output
                If PFSShellOutput.Contains("created.") Then
                    SetStatus(PPPartitionName + " created. Now adding files ...")

                    '4. Add files to the partition
                    Await PS1AddFilesToPartitionAsync(PPPartitionName)
                Else
                    MsgBox("There was an error in creating the game's PP partition, please check if the name doesn't already exists and if you have enough space.", MsgBoxStyle.Exclamation, "Error installing game")
                    SetStatus("")
                    Return
                End If
            Else
                MsgBox("Exiting game installation.", MsgBoxStyle.Critical, "Installation aborted")
                CloseWindow()
            End If
        Else
            MsgBox("Could not load the project to install.", MsgBoxStyle.Critical, "Error")
        End If
    End Function

    Private Async Function CreateGamePartitionAsync() As Task
        Dim CreatedGamePartition As String = ""

        'Get the created partition
        '1. List partitions
        Dim TocResult As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"toc", HDLDriveName}, Nothing, CancellationToken.None)
        Dim QueryOutput As String() = OutputText.SplitLines(TocResult.StandardOutput)

        '2. Search for the created hidden partition
        For Each HDDPartition As String In QueryOutput
            If Not String.IsNullOrEmpty(HDDPartition) Then
                If HDDPartition.Split(New String() {" "}, StringSplitOptions.RemoveEmptyEntries).Count >= 5 Then
                    HDDPartition = HDDPartition.Split(New String() {" "}, StringSplitOptions.RemoveEmptyEntries)(4)
                    If HDDPartition.Trim().StartsWith("__." + HDLGameID) Then 'The created hidden partition
                        CreatedGamePartition = HDDPartition.Trim()
                        Exit For
                    End If
                End If
            End If
        Next

        If String.IsNullOrEmpty(CreatedGamePartition) Then
            MsgBox("Could not find the game's hidden partition __." + HDLGameID + " on the HDD after the injection." + vbCrLf + vbCrLf +
                   TocResult.CombinedOutput.Trim().Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation, "Error installing game")
            SetStatus("")
            Return
        End If

        '3. mkpart commands for the PP partition
        Dim Commands As New List(Of String) From {
            "device " + MountedDrive.DriveID,
            "mkpart " + CreatedGamePartition.Replace("__.", "PP.") + " 128M PFS",
            "exit"
        }

        '4. Proceed to partition creation
        Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, Nothing, CancellationToken.None)
        Dim PFSShellOutput As String = Result.CombinedOutput

        '5. Read partition creation output
        If PFSShellOutput.Contains("Main partition of 128M created.") Then
            SetStatus("Partition created, modifying header...")

            '6. Modify the created partition
            Await ModifyPartitionHeaderAsync(CreatedGamePartition.Replace("__.", "PP."), False)
        Else
            MsgBox("There was an error in creating the game's PP partition, please check if the name doesn't already exists and if you have enough space.", MsgBoxStyle.Exclamation, "Error installing game")
            SetStatus("")
            Return
        End If
    End Function

    Public Async Function CreateHomebrewPartitionAsync(PartitionName As String) As Task
        If ProjectToInstall IsNot Nothing Then
            '1. mkpart commands for the PP partition
            Dim Commands As New List(Of String) From {
                "device " + MountedDrive.DriveID,
                "mkpart " + PartitionName + " 128M PFS",
                "exit"
            }

            '2. Proceed to partition creation
            Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, Nothing, CancellationToken.None)
            Dim PFSShellOutput As String = Result.CombinedOutput

            '3. Read partition creation output
            If PFSShellOutput.Contains("Main partition of 128M created.") Then
                SetStatus("Partition created, modifying header...")

                '4. Modify the created partition
                Await ModifyPartitionHeaderAsync(PartitionName, False)
            Else
                MsgBox("There was an error in creating the homebrew's PP partition." + vbCrLf + "Please check if the partition name '" + PartitionName + "' does not already exists of if HDD space is sufficient.", MsgBoxStyle.Exclamation, "Error while installing homebrew")
                CloseWindow()
            End If
        End If
    End Function

    Public Async Function ModifyPartitionHeaderAsync(PartitionName As String, FinalizePS1 As Boolean) As Task
        '1./2. hdl_dump modify_header reads system.cnf, icon.sys, list.ico, ... from its working directory,
        'so it runs with the project directory as its own working directory (no copy of hdl_dump, no global directory change)
        Dim Result As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"modify_header", HDLDriveName, PartitionName}, CurrentProjectDirectory, CancellationToken.None)
        Dim HDLDumpOutput As String = Result.CombinedOutput

        '3. Read hdl_dump output
        If Not HDLDumpOutput.Contains("partition not found:") AndAlso Result.Succeeded Then
            If FinalizePS1 Then
                SetStatus("Partition header modified. Installation is done!")

                If MsgBox("Installation completed with success!", MsgBoxStyle.OkOnly, "Success") = MsgBoxResult.Ok Then
                    CloseWindow()
                End If
            Else
                SetStatus("Partition header modified, adding files...")

                '4. Add files to the partition
                Await PS2AddFilesToPartitionAsync(PartitionName)
            End If
        Else
            MsgBox("There was an error while modifying the partition, please check if you have enough space and report the next error." + vbCrLf + HDLDumpOutput.Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation, "Error installing game")
            Return
        End If
    End Function

    ''' <summary>pfsshell commands that upload the project's res folder (and res\image) into the current PFS directory.</summary>
    Private Function ResFolderCommands() As List(Of String)
        Dim Kind As StorageBackendKind = StorageBackend.Kind
        Dim Commands As New List(Of String) From {"mkdir res", "cd res"}

        For Each ResFile As String In {"info.sys", "jkt_001.png", "jkt_002.png", "jkt_cp.png", "man.xml", "notice.jpg"}
            If File.Exists(Path.Combine(CurrentProjectDirectory, "res", ResFile)) Then
                Commands.AddRange(PfsShellCommands.PutFile(Kind, "res\" + ResFile))
            End If
        Next

        If Directory.Exists(Path.Combine(CurrentProjectDirectory, "res", "image")) Then
            Commands.Add("mkdir image")
            Commands.Add("cd image")

            For Each ImageFile As String In {"0.png", "1.png", "2.png"}
                If File.Exists(Path.Combine(CurrentProjectDirectory, "res", "image", ImageFile)) Then
                    Commands.AddRange(PfsShellCommands.PutFile(Kind, "res\image\" + ImageFile))
                End If
            Next
        End If

        Return Commands
    End Function

    Public Async Function PS2AddFilesToPartitionAsync(PartitionName As String) As Task
        'Now put the "res" folder and EXECUTE.KELF file into the partition
        Dim Commands As New List(Of String) From {
            "device " + MountedDrive.DriveID,
            "mount " + PartitionName,
            "put EXECUTE.KELF"
        }
        Commands.AddRange(ResFolderCommands())
        Commands.Add("umount")
        Commands.Add("exit")

        'Put all detected files to the partition using pfsshell, from the project directory
        Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, CurrentProjectDirectory, CancellationToken.None)

        'Update UI when finished
        SetStatus("")

        If Result.TimedOut OrElse Result.ExitCode <> 0 Then
            MsgBox(DescribeFailure("pfsshell", Result), MsgBoxStyle.Exclamation, "Error adding files")
            Return
        End If

        If MsgBox("Installation completed with success!", MsgBoxStyle.OkOnly, "Success") = MsgBoxResult.Ok Then
            CloseWindow()
        End If
    End Function

    Public Async Function PS1AddFilesToPartitionAsync(PartitionName As String) As Task
        'Now put the game VCD(s), (DISCS.TXT) the "res" folder and EXECUTE.KELF file into the partition
        Dim Commands As New List(Of String) From {
            "device " + MountedDrive.DriveID,
            "mount " + PartitionName,
            "put EXECUTE.KELF"
        }

        For Each GameFile As String In {"DISCS.TXT", "IMAGE0.VCD", "IMAGE1.VCD", "IMAGE2.VCD", "IMAGE3.VCD"}
            If File.Exists(Path.Combine(CurrentProjectDirectory, GameFile)) Then
                Commands.Add("put " + GameFile)
            End If
        Next

        Commands.AddRange(ResFolderCommands())
        Commands.Add("umount")
        Commands.Add("exit")

        SetStatus("Adding files... This can take some time.")
        Mouse.SetCursor(Cursors.Wait)

        'Put all detected files to the partition using pfsshell, from the project directory
        Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, CurrentProjectDirectory, CancellationToken.None)

        Mouse.SetCursor(Cursors.Arrow)

        If Result.TimedOut OrElse Result.ExitCode <> 0 Then
            MsgBox(DescribeFailure("pfsshell", Result), MsgBoxStyle.Exclamation, "Error adding files")
            SetStatus("")
            Return
        End If

        'Update UI when finished
        SetStatus("Files added to the game partition. Finalizing...")

        '5. Modify the partition header
        Await ModifyPartitionHeaderAsync(PartitionName, True)
    End Function

    ''' <summary>Shows hdl_dump's injection progress lines and percentage.</summary>
    Private Sub ShowInjectProgress(Line As String)
        If String.IsNullOrWhiteSpace(Line) Then Return

        'Progress status
        InstallationStatusTextBlock.Text = Line.Trim()

        'Progress percentage
        Dim ProgressPercentage As Double = 0
        If Regex.Match(Line, "\d\d[%]+").Success Then
            If Double.TryParse(Regex.Match(Line, "\d\d[%]+").Value.Replace("%", ""), ProgressPercentage) = True Then
                InstallationProgressBar.Value = ProgressPercentage
            End If
        End If
    End Sub

End Class

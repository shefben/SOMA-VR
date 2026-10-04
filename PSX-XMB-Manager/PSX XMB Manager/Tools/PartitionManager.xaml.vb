Imports System.Threading
Imports PSX_XMB_Manager.Structs

Public Class PartitionManager

    Public MountedDrive As MountedPSXDrive
    Public StorageBackend As IPSXStorageBackend

    Dim WithEvents PartitionsContextMenu As New ContextMenu()
    Dim WithEvents GamePartitionContextMenu As New ContextMenu()

    Dim WithEvents ModifyItem As New MenuItem With {.Header = "Modify game"}
    Dim WithEvents ModifyNameItem As New MenuItem With {.Header = "Change game title"}
    Dim WithEvents ModifyFlagsItem As New MenuItem With {.Header = "Change flags"}
    Dim WithEvents ModifyDMAItem As New MenuItem With {.Header = "Change DMA"}
    Dim WithEvents ModifyVisibilityItem As New MenuItem With {.Header = "Change partition visibility"}

    Dim WithEvents MountItem As New MenuItem With {.Header = "Mount partition as network folder"}
    Dim WithEvents DumpItem As New MenuItem With {.Header = "Dump partition header"}
    Dim WithEvents RemoveItem As New MenuItem With {.Header = "Remove partition (destructive)"}

    Dim ListOfMountedPartitions As New List(Of Tuple(Of Partition, PfsMountHandle))() 'Keeps partition info and its mount

    ''' <summary>The device name hdl_dump expects for the current connection (hdd#: locally, the NBD raw file in WSL).</summary>
    Private ReadOnly Property HDLDriveName As String
        Get
            Return StorageBackend.MountedDrive.HDLDriveName
        End Get
    End Property

    ''' <summary>The device pfsshell opens for the current connection (\\.\PHYSICALDRIVE# locally, the NBD raw file in WSL).</summary>
    Private ReadOnly Property DriveID As String
        Get
            Return StorageBackend.MountedDrive.DriveID
        End Get
    End Property

    Private Shared Sub ShowError(ex As Exception, title As String)
        Dim backendError As StorageBackendException = TryCast(ex, StorageBackendException)
        MsgBox(If(backendError IsNot Nothing, BackendErrorCodes.FormatForUser(backendError), ex.Message), MsgBoxStyle.Critical, title)
    End Sub

    Private Shared Sub ShowToolFailure(tool As String, result As ProcessResult, title As String)
        Dim reason As String = If(result.TimedOut, " timed out.", If(result.Cancelled, " was cancelled.", " failed with exit code " + result.ExitCode.ToString() + "."))
        MsgBox(tool + reason + vbCrLf + vbCrLf + result.CombinedOutput.Trim().Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation, title)
    End Sub

    Private Async Function LoadParititonsAsync() As Task
        PartitionsListView.Items.Clear()

        Dim Result As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"toc", HDLDriveName}, Nothing, CancellationToken.None)
        If Not Result.Succeeded Then
            ShowToolFailure("hdl_dump toc", Result, "Could not read the partition table")
            Return
        End If
        Dim QueryOutput As String() = OutputText.SplitLines(Result.StandardOutput).Where(Function(Line) Line <> "").ToArray()

        For Each HDDPartition As String In QueryOutput.Skip(1)
            If HDDPartition.StartsWith("0") Then

                Dim Part As New Partition() With {.Type = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(0),
                    .Start = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(1),
                    .Parts = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(2),
                    .Size = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(3),
                    .Name = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(4)}

                PartitionsListView.Items.Add(Part)
            ElseIf HDDPartition.StartsWith("Total") Then
                Dim HDDSizes As String() = HDDPartition.Split({","}, StringSplitOptions.RemoveEmptyEntries)
                Dim TotalSpaceInGB = Utils.GetIntOnly(HDDSizes(0)) / 1024
                Dim UsedSpaceInGB = Utils.GetIntOnly(HDDSizes(1)) / 1024
                Dim AvailableSpaceInGB = Utils.GetIntOnly(HDDSizes(2)) / 1024

                HDDSpaceTextBlock.Text = "Total Space : " + FormatNumber(TotalSpaceInGB, 2) + " GB - Used : " + FormatNumber(UsedSpaceInGB, 2) + " GB - Available : " + FormatNumber(AvailableSpaceInGB, 2) + " GB"

            End If
        Next
    End Function

    ''' <summary>Converts one "hdl_dump hdl_toc" game line into the list view item.</summary>
    Public Shared Function ParseGamePartition(HDDPartition As String) As GamePartition
        Dim Game As HdlTocGame = Nothing
        If HdlTocParser.TryParseGameLine(HDDPartition, Game) Then
            Return New GamePartition() With {.Type = Game.Type,
                .Size = FormatNumber(Game.SizeInKB / 1024, 2) + " MB",
                .Flags = Game.Flags,
                .DMA = Game.DMA,
                .Startup = Game.Startup,
                .Name = Game.Name}
        End If

        'Unrecognised layout: the original column split
        Dim GameSize = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(1).Trim().Replace("KB", "")
        Dim GameSizeInMB = CInt(GameSize) / 1024

        Return New GamePartition() With {.Type = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(0),
            .Size = FormatNumber(GameSizeInMB, 2) + " MB",
            .Flags = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(2),
            .DMA = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(3),
            .Startup = HDDPartition.Split({" "}, StringSplitOptions.RemoveEmptyEntries)(4),
            .Name = HDDPartition.Split({"  "}, StringSplitOptions.RemoveEmptyEntries)(2)}
    End Function

    Private Async Function LoadGamePartitionsAsync() As Task
        GamesPartitionsListView.Items.Clear()

        Dim Result As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"hdl_toc", HDLDriveName}, Nothing, CancellationToken.None)
        If Not Result.Succeeded Then
            ShowToolFailure("hdl_dump hdl_toc", Result, "Could not read the games list")
            Return
        End If
        Dim QueryOutput As String() = OutputText.SplitLines(Result.StandardOutput).Where(Function(Line) Line <> "").ToArray()

        For Each HDDPartition As String In QueryOutput.Skip(1)
            If HDDPartition.StartsWith("DVD") Or HDDPartition.StartsWith("CD") Then
                GamesPartitionsListView.Items.Add(ParseGamePartition(HDDPartition))
            End If
        Next
    End Function

    Public Async Sub ReloadPartitions()
        If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected Then
            MsgBox("The PSX HDD is not connected anymore.", MsgBoxStyle.Exclamation)
            Return
        End If
        LoadPartitionsButton.IsEnabled = False
        Try
            Await LoadParititonsAsync()
            Await LoadGamePartitionsAsync()
        Catch ex As Exception
            ShowError(ex, "Could not read the PSX HDD")
        Finally
            LoadPartitionsButton.IsEnabled = True
        End Try
    End Sub

    Private Sub LoadContextMenus()
        PartitionsContextMenu.Items.Add(MountItem)
        PartitionsContextMenu.Items.Add(RemoveItem)
        PartitionsContextMenu.Items.Add(ModifyVisibilityItem)

        GamePartitionContextMenu.Items.Add(ModifyItem)
        GamePartitionContextMenu.Items.Add(DumpItem)

        ModifyItem.Items.Add(ModifyNameItem)
        ModifyItem.Items.Add(ModifyFlagsItem)
        ModifyItem.Items.Add(ModifyDMAItem)

        PartitionsListView.ContextMenu = PartitionsContextMenu
        GamesPartitionsListView.ContextMenu = GamePartitionContextMenu
    End Sub

    ''' <summary>Runs "hdl_dump modify DEVICE NAME ..." and reports the result; True when hdl_dump succeeded.</summary>
    Private Async Function RunModifyAsync(GameName As String, Options As IEnumerable(Of String), Title As String) As Task(Of Boolean)
        Dim Arguments As New List(Of String) From {"modify", HDLDriveName, GameName}
        Arguments.AddRange(Options)
        Try
            Dim Result As ProcessResult = Await StorageBackend.RunHdlDumpAsync(Arguments, Nothing, CancellationToken.None)
            If Not Result.Succeeded Then
                ShowToolFailure("hdl_dump modify", Result, Title)
                Return False
            End If
            Return True
        Catch ex As Exception
            ShowError(ex, Title)
            Return False
        End Try
    End Function

    Private Async Sub ModifyNameItem_Click(sender As Object, e As RoutedEventArgs) Handles ModifyNameItem.Click
        If GamesPartitionsListView.SelectedItem IsNot Nothing Then
            Dim SelectedPartition As GamePartition = CType(GamesPartitionsListView.SelectedItem, GamePartition)
            Dim NewGameTitle As String = InputBox("Please enter a new name for " + SelectedPartition.Name + " : ", "Change game title")

            If Not NewGameTitle = "" Then
                If Await RunModifyAsync(SelectedPartition.Name, {NewGameTitle}, "Change game title") Then
                    MsgBox("Game Title renamed.", MsgBoxStyle.Information)
                    ReloadPartitions()
                End If
            End If
        End If
    End Sub

    Private Async Sub ModifyFlagsItem_Click(sender As Object, e As RoutedEventArgs) Handles ModifyFlagsItem.Click
        If GamesPartitionsListView.SelectedItem IsNot Nothing Then
            Dim SelectedPartition As GamePartition = CType(GamesPartitionsListView.SelectedItem, GamePartition)
            Dim NewGameFlags As String = InputBox("Please enter the new flags for " + SelectedPartition.Name + vbCrLf + vbCrLf +
                                                  "Format +1 or combined +1+2+3... : ", "Change game flags")

            If Not NewGameFlags = "" Then
                'The flags were passed unquoted before, so each word stays a separate argument
                If Await RunModifyAsync(SelectedPartition.Name, NewGameFlags.Split({" "c}, StringSplitOptions.RemoveEmptyEntries), "Change game flags") Then
                    MsgBox("Game Flags changed.", MsgBoxStyle.Information)
                    ReloadPartitions()
                End If
            End If
        End If
    End Sub

    Private Async Sub ModifyDMAItem_Click(sender As Object, e As RoutedEventArgs) Handles ModifyDMAItem.Click
        If GamesPartitionsListView.SelectedItem IsNot Nothing Then
            Dim SelectedPartition As GamePartition = CType(GamesPartitionsListView.SelectedItem, GamePartition)
            Dim NewGameFlags As String = InputBox("Please enter the new flags for " + SelectedPartition.Name + vbCrLf + vbCrLf +
                                                  "Format *u4 ... : ", "Change game DMA ")

            If Not NewGameFlags = "" Then
                If Await RunModifyAsync(SelectedPartition.Name, NewGameFlags.Split({" "c}, StringSplitOptions.RemoveEmptyEntries), "Change game DMA") Then
                    MsgBox("Game DMA changed.", MsgBoxStyle.Information)
                End If
            End If
        End If
    End Sub

    Private Async Sub ModifyVisibilityItem_Click(sender As Object, e As RoutedEventArgs) Handles ModifyVisibilityItem.Click
        If PartitionsListView.SelectedItem IsNot Nothing Then
            Dim SelectedPartition As Partition = CType(PartitionsListView.SelectedItem, Partition)

            Try
                If SelectedPartition.Name.StartsWith("__.") Then
                    'Change to visible
                    If MsgBox("Do you really want to make the partition " + SelectedPartition.Name + " visible ?" + vbCrLf + "This won't work if the PP partition already exists.", MsgBoxStyle.YesNo, "Change partition visibility") = MsgBoxResult.Yes Then
                        Dim Result As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"modify", HDLDriveName, SelectedPartition.Name, "-unhide"}, Nothing, CancellationToken.None)

                        If Result.CombinedOutput.Contains("partition with such name already exists:") Then
                            MsgBox("A visible partition with such name already exists.", MsgBoxStyle.Information)
                        ElseIf Not Result.Succeeded Then
                            ShowToolFailure("hdl_dump modify -unhide", Result, "Change partition visibility")
                        Else
                            MsgBox("Partition is now visible : " + SelectedPartition.Name.Replace("__.", "PP."), MsgBoxStyle.Information)
                            ReloadPartitions()
                        End If
                    End If
                ElseIf SelectedPartition.Name.StartsWith("PP.") Then
                    'Hide partition
                    If MsgBox("Do you really want to hide the partition " + SelectedPartition.Name + " ?" + vbCrLf + "This won't work if the hidden __. partition already exists.", MsgBoxStyle.YesNo, "Change partition visibility") = MsgBoxResult.Yes Then
                        Dim Result As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"modify", HDLDriveName, SelectedPartition.Name, "-hide"}, Nothing, CancellationToken.None)

                        If Result.CombinedOutput.Contains("partition with such name already exists:") Then
                            MsgBox("A hidden partition with such name already exists.", MsgBoxStyle.Information)
                        ElseIf Not Result.Succeeded Then
                            ShowToolFailure("hdl_dump modify -hide", Result, "Change partition visibility")
                        Else
                            MsgBox("Partition is now hidden : " + SelectedPartition.Name.Replace("PP.", "__."), MsgBoxStyle.Information)
                            ReloadPartitions()
                        End If
                    End If
                End If
            Catch ex As Exception
                ShowError(ex, "Change partition visibility")
            End Try

        End If
    End Sub

    Private Sub LoadPartitionsButton_Click(sender As Object, e As RoutedEventArgs) Handles LoadPartitionsButton.Click
        ReloadPartitions()
    End Sub

    Private Sub PartitionManager_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        LoadContextMenus()
        If StorageBackend IsNot Nothing Then AddHandler StorageBackend.MountRemoved, AddressOf StorageBackend_MountRemoved
    End Sub

    Private Sub PartitionManager_Closed(sender As Object, e As EventArgs) Handles Me.Closed
        If StorageBackend IsNot Nothing Then RemoveHandler StorageBackend.MountRemoved, AddressOf StorageBackend_MountRemoved
    End Sub

    ''' <summary>A partition was unmounted elsewhere (for example by Disconnect); forget it here as well.</summary>
    Private Sub StorageBackend_MountRemoved(sender As Object, Mount As PfsMountHandle)
        Dispatcher.BeginInvoke(Sub()
                                   ListOfMountedPartitions.RemoveAll(Function(Entry) Entry.Item2.MountId = Mount.MountId)
                                   UpdateMountItemHeader()
                               End Sub)
    End Sub

    Private Async Sub RemoveItem_Click(sender As Object, e As RoutedEventArgs) Handles RemoveItem.Click
        If PartitionsListView.SelectedItem IsNot Nothing Then
            Dim SelectedPartition As Partition = CType(PartitionsListView.SelectedItem, Partition)

            If MsgBox("Do you really want to delete the partition " + SelectedPartition.Name + " ?" + vbCrLf + "This operation can be destructive !", MsgBoxStyle.YesNo, "Please confirm") = MsgBoxResult.Yes Then

                'rmpart commands, sent straight to pfsshell
                Dim Commands As New List(Of String) From {
                    "device " + DriveID,
                    "rmpart " + SelectedPartition.Name,
                    "exit"
                }

                Try
                    Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, Nothing, CancellationToken.None)
                    Dim PFSShellOutput As String = Result.CombinedOutput

                    If PFSShellOutput.Contains("No such file or directory") OrElse Result.TimedOut Then
                        MsgBox("There was an error while deleting the partition. More details :" + vbCrLf + PFSShellOutput.Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation, "Error")
                    Else
                        MsgBox("Partition " + SelectedPartition.Name + " deleted !", MsgBoxStyle.Information)
                        ReloadPartitions()
                    End If
                Catch ex As Exception
                    ShowError(ex, "Error")
                End Try

            End If

        End If
    End Sub

    Private Sub CreateNewPartitionButton_Click(sender As Object, e As RoutedEventArgs) Handles CreateNewPartitionButton.Click
        Dim NewPartitionWindow As New NewPartition() With {.ShowActivated = True, .MountedDrive = StorageBackend.MountedDrive, .StorageBackend = StorageBackend}
        NewPartitionWindow.Show()
    End Sub

    Private Async Sub MountItem_Click(sender As Object, e As RoutedEventArgs) Handles MountItem.Click
        If PartitionsListView.SelectedItem IsNot Nothing Then
            Dim SelectedPartition As Partition = CType(PartitionsListView.SelectedItem, Partition)
            Dim MountedEntry As Tuple(Of Partition, PfsMountHandle) = ListOfMountedPartitions.FirstOrDefault(Function(Entry) Entry.Item1.Name = SelectedPartition.Name)

            Try
                If MountedEntry Is Nothing Then
                    'Mount selected partition
                    Dim NewMount As PfsMountHandle = Await StorageBackend.MountPfsPartitionAsync(SelectedPartition.Name, SelectedPartition.Name, CancellationToken.None)
                    ListOfMountedPartitions.Add(New Tuple(Of Partition, PfsMountHandle)(SelectedPartition, NewMount))

                    MsgBox($"{SelectedPartition.Name} mounted to {NewMount.WindowsPath} !", MsgBoxStyle.Information)
                Else
                    'Unmount
                    Await StorageBackend.UnmountPfsPartitionAsync(MountedEntry.Item2, CancellationToken.None)
                    ListOfMountedPartitions.Remove(MountedEntry)
                    MsgBox($"{SelectedPartition.Name} unmounted from {MountedEntry.Item2.WindowsPath} !", MsgBoxStyle.Information)
                End If
            Catch ex As Exception
                ShowError(ex, If(MountedEntry Is Nothing, "Could not mount the partition", "Could not unmount the partition"))
            End Try
            UpdateMountItemHeader()
        End If
    End Sub

    Private Sub UpdateMountItemHeader()
        If PartitionsListView.SelectedItem IsNot Nothing Then
            Dim SelectedPartition As Partition = CType(PartitionsListView.SelectedItem, Partition)
            If ListOfMountedPartitions.Any(Function(Entry) Entry.Item1.Name = SelectedPartition.Name) Then
                MountItem.Header = "Unmount partition"
            Else
                MountItem.Header = "Mount partition as network folder"
            End If
        End If
    End Sub

    Private Sub PartitionsListView_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles PartitionsListView.SelectionChanged
        UpdateMountItemHeader()
    End Sub

End Class

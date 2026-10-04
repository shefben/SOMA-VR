Imports System.ComponentModel
Imports System.IO
Imports System.Management
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Text
Imports System.Windows.Interop
Imports System.Windows.Threading
Imports PSX_XMB_Manager.Structs

Public Class Utilities

    Public MountedDrive As MountedPSXDrive = Nothing
    Public StorageBackend As IPSXStorageBackend

    Private WithEvents BChunk As New Process()
    Private WithEvents BIOSUnpacker As New Process()
    Private WithEvents CUE2POPS As New Process()
    Private WithEvents DD As New Process()
    Private WithEvents KELFTool As New Process()
    Private WithEvents PAKer As New Process()
    Private WithEvents PSXDec As New Process()
    Private WithEvents STARGazer As New Process()

    Dim ListOfFoundDrives As New List(Of ComboBoxHDDDrive)()

    Private NewBaseName As String = ""

    'Backup & restore of the PSX connected through WSL2
    Private ConnectedPsxSize As Long = 0
    Private RawCopyRunning As Boolean = False
    Private RawCopyIsRestore As Boolean = False
    Private RawCopyCancellation As CancellationTokenSource

    Private Async Sub Utilities_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        GetHDDrives()

        'List the connected PSX as backup & restore target once its size is known
        Await RefreshConnectedPsxSizeAsync()
        GetHDDrives()
    End Sub

    Private Sub Utilities_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        If RawCopyRunning Then
            e.Cancel = True
            If RawCopyIsRestore Then
                MsgBox("The restore of the PSX HDD is running and cannot be interrupted. Please wait until it is finished.", MsgBoxStyle.Exclamation, "Restore running")
            ElseIf MsgBox("A backup of the PSX HDD is running. Do you want to cancel it ?", MsgBoxStyle.YesNo, "Backup running") = MsgBoxResult.Yes Then
                RawCopyCancellation?.Cancel()
            End If
            Return
        End If

        UnregisterNotification()
    End Sub

    Private Shared Sub ShowError(ex As Exception, title As String)
        Dim backendError As StorageBackendException = TryCast(ex, StorageBackendException)
        MsgBox(If(backendError IsNot Nothing, BackendErrorCodes.FormatForUser(backendError), ex.Message), MsgBoxStyle.Critical, title)
    End Sub

    Private ReadOnly Property IsWslPsxConnected As Boolean
        Get
            Return StorageBackend IsNot Nothing AndAlso StorageBackend.Kind = StorageBackendKind.WSL2NBD AndAlso StorageBackend.IsConnected
        End Get
    End Property

    Private Async Function RefreshConnectedPsxSizeAsync() As Task
        ConnectedPsxSize = 0
        If Not IsWslPsxConnected Then Return
        Try
            ConnectedPsxSize = Await StorageBackend.GetRawDeviceSizeAsync(CancellationToken.None)
        Catch ex As Exception
            'Not listed then; the main window shows the connection problem
            BackendLog.Write(StorageBackendKind.WSL2NBD, "", "raw-size", "Utilities: " + ex.Message, -1, TimeSpan.Zero, "", "", "")
        End Try
    End Function

#Region "HDD Utils"

    Private NotifyDeviceBroadcast As IntPtr

    Private Sub RegisterNotification(ByRef GUIDInfo As Guid)
        Dim NewDeviceInterface As New DEV_BROADCAST_DEVICEINTERFACE
        Dim NewDeviceInterfaceBuffer As IntPtr

        'Set the GUID
        NewDeviceInterface.dbcc_size = Marshal.SizeOf(NewDeviceInterface)
        NewDeviceInterface.dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE
        NewDeviceInterface.dbcc_reserved = 0
        NewDeviceInterface.dbcc_classguid = GUIDInfo

        'Allocate a buffer for the DLL call
        NewDeviceInterfaceBuffer = Marshal.AllocHGlobal(NewDeviceInterface.dbcc_size)

        'Copy NewDeviceInterfaceBuffer to buffer
        Marshal.StructureToPtr(NewDeviceInterface, NewDeviceInterfaceBuffer, True)

        'Register the device notification
        Dim MainWindowSource As HwndSource = HwndSource.FromHwnd(New WindowInteropHelper(Me).Handle)
        NotifyDeviceBroadcast = RegisterDeviceNotification(MainWindowSource.Handle, NewDeviceInterfaceBuffer, DEVICE_NOTIFY_WINDOW_HANDLE)
        MainWindowSource.AddHook(AddressOf HwndHandler)

        'Copy buffer to NewDeviceInterfaceBuffer
        Marshal.PtrToStructure(NewDeviceInterfaceBuffer, NewDeviceInterface)

        'Free buffer
        Marshal.FreeHGlobal(NewDeviceInterfaceBuffer)
    End Sub

    Private Sub UnregisterNotification()
        Dim ret As UInteger = UnregisterDeviceNotification(NotifyDeviceBroadcast)
    End Sub

    Protected Overrides Sub OnSourceInitialized(e As EventArgs)
        MyBase.OnSourceInitialized(e)
        Try
            Dim NewGUID As New Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED")
            RegisterNotification(NewGUID)
        Catch ex As Exception
            MsgBox(ex.Message)
        End Try
    End Sub

    Private Function HwndHandler(hwnd As IntPtr, msg As Integer, wparam As IntPtr, lparam As IntPtr, ByRef handled As Boolean) As IntPtr
        If msg = WM_DEVICECHANGE Then
            Dim NotificationEventType = wparam.ToInt32()
            If NotificationEventType = DBT_DEVICEARRIVAL Then
                Dim NewHDR As New DEV_BROADCAST_HDR
                Marshal.PtrToStructure(lparam, NewHDR)
                If NewHDR.dbch_devicetype = DBT_DEVTYP_DEVICEINTERFACE Then
                    Dispatcher.BeginInvoke(Sub() GetHDDrives())
                End If
            ElseIf NotificationEventType = DBT_DEVICEREMOVECOMPLETE Then
                Dim NewHDR As New DEV_BROADCAST_HDR
                Marshal.PtrToStructure(lparam, NewHDR)
                If NewHDR.dbch_devicetype = DBT_DEVTYP_DEVICEINTERFACE Then
                    Dispatcher.BeginInvoke(Sub() GetHDDrives())
                End If
            End If
        End If

        handled = False
        Return IntPtr.Zero
    End Function

    Private Sub GetHDDrives()
        ConnectedDrivesComboBox.SelectedIndex = -1
        ConnectedDrivesComboBox2.SelectedIndex = -1
        ConnectedDrivesComboBox.ItemsSource = Nothing
        ConnectedDrivesComboBox2.ItemsSource = Nothing
        ListOfFoundDrives.Clear()

        Dim DiskDriveQuery As String = "SELECT Caption, InterfaceType, Name, MediaType, Size FROM Win32_DiskDrive"
        Dim NewManagementObjectSearcher As New ManagementObjectSearcher(DiskDriveQuery)

        For Each FoundDrive As ManagementObject In NewManagementObjectSearcher.Get()
            If CStr(FoundDrive("InterfaceType")) = "USB" AndAlso CStr(FoundDrive("MediaType")) = "External hard disk media" Then
                If CULng(FoundDrive("Size")) <= 250000000000 Then 'Do not add other connected drives bigger than PSX HDDs

                    Dim NewHDDDrive As New ComboBoxHDDDrive() With {
                        .DeviceCaption = CStr(FoundDrive("Caption")),
                        .DeviceInterfaceType = CStr(FoundDrive("InterfaceType")),
                        .DeviceMediaType = CStr(FoundDrive("MediaType")),
                        .DevicePath = CStr(FoundDrive("Name")),
                        .DeviceSize = CULng(FoundDrive("Size")),
                        .ComboBoxDisplayText = CStr(FoundDrive("Caption")) & " | " & FormatNumber(CULng(FoundDrive("Size")) / 1073741824, 2) + " GB" & " | " & CStr(FoundDrive("Name"))}

                    ListOfFoundDrives.Add(NewHDDDrive)
                End If
            End If
        Next

        'Also allow VHDs for testing
        For Each VirtualDrive As ManagementObject In NewManagementObjectSearcher.Get()
            If CStr(VirtualDrive("InterfaceType")) = "SCSI" AndAlso CStr(VirtualDrive("MediaType")) = "Fixed hard disk media" Then
                If CULng(VirtualDrive("Size")) <= 250000000000 Then
                    If CStr(VirtualDrive("Caption")).Contains("Virtual") Then 'Only add virtual drives
                        Dim NewHDDDrive As New ComboBoxHDDDrive() With {
                        .DeviceCaption = CStr(VirtualDrive("Caption")),
                        .DeviceInterfaceType = CStr(VirtualDrive("InterfaceType")),
                        .DeviceMediaType = CStr(VirtualDrive("MediaType")),
                        .DevicePath = CStr(VirtualDrive("Name")),
                        .DeviceSize = CULng(VirtualDrive("Size")),
                        .ComboBoxDisplayText = CStr(VirtualDrive("Caption")) & " | " & FormatNumber(CULng(VirtualDrive("Size")) / 1073741824, 2) + " GB" & " | " & CStr(VirtualDrive("Name"))}

                        ListOfFoundDrives.Add(NewHDDDrive)
                    End If
                End If
            End If
        Next

        'The PSX connected through WSL2 is not a Windows physical drive, so it is listed as its own target
        If IsWslPsxConnected AndAlso ConnectedPsxSize > 0 Then
            ListOfFoundDrives.Add(New ComboBoxHDDDrive() With {
                .IsConnectedPsx = True,
                .DeviceCaption = "Connected PSX via WSL2",
                .DevicePath = StorageBackend.MountedDrive.NativeDevicePath,
                .DeviceSize = CULng(ConnectedPsxSize),
                .ComboBoxDisplayText = "Connected PSX via WSL2 — " + StorageBackend.MountedDrive.ConnectedOnIP + " — " + StorageValidation.FormatSize(ConnectedPsxSize)})
        End If

        ConnectedDrivesComboBox.ItemsSource = ListOfFoundDrives
        ConnectedDrivesComboBox.DisplayMemberPath = "ComboBoxDisplayText"
        ConnectedDrivesComboBox2.ItemsSource = ListOfFoundDrives
        ConnectedDrivesComboBox2.DisplayMemberPath = "ComboBoxDisplayText"
    End Sub

    Private Sub BrowseBackupSavePathButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseBackupSavePathButton.Click
        Dim SFD As New Forms.SaveFileDialog() With {.Filter = "IMG files (*.img)|*.img", .Title = "Select a save path for the backup file"}
        If SFD.ShowDialog() = Forms.DialogResult.OK Then
            BackupSavePathTextBox.Text = SFD.FileName
        End If
    End Sub

    Private Async Sub StartBackupButton_Click(sender As Object, e As RoutedEventArgs) Handles StartBackupButton.Click
        If ConnectedDrivesComboBox.SelectedItem IsNot Nothing AndAlso Not String.IsNullOrEmpty(BackupSavePathTextBox.Text) Then
            Dim SavePath As String = BackupSavePathTextBox.Text
            Dim SelectedHDD As ComboBoxHDDDrive = CType(ConnectedDrivesComboBox.SelectedItem, ComboBoxHDDDrive)
            Dim HDDPath As String = SelectedHDD.DevicePath

            If SelectedHDD.IsConnectedPsx Then
                Await RunConnectedPsxBackupAsync(SelectedHDD, SavePath)
                Return
            End If

            If MsgBox($"Do you really want to create a backup of {SelectedHDD.ComboBoxDisplayText} ?" & vbCrLf &
                      "This process will take some hours and start in a separate window so you can continue working with PSX XMB Manager.",
                      MsgBoxStyle.YesNo,
                      "Start Backup") = MsgBoxResult.Yes Then
                If IncreaseBlockSizeCheckBox.IsChecked Then
                    RunBackupProcess(HDDPath, SavePath, True)
                Else
                    RunBackupProcess(HDDPath, SavePath)
                End If
            End If
        End If
    End Sub

    Private Sub RunBackupProcess(HDDPath As String, SavePath As String, Optional Increase As Boolean = False)
        Try
            DD = New Process()
            DD.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\dd.exe"
            If Increase Then
                DD.StartInfo.Arguments = $"if={HDDPath} of=""{SavePath}"" bs=4M --size --progress"
            Else
                DD.StartInfo.Arguments = $"if={HDDPath} of=""{SavePath}"" bs=1M --size --progress"
            End If
            DD.Start()
        Catch ex As Exception
            MsgBox(ex.ToString)
        End Try
    End Sub

    Private Sub BrowseBackupSavePathButton_Copy_Click(sender As Object, e As RoutedEventArgs) Handles BrowseBackupSavePathButton_Copy.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "IMG files (*.img)|*.img", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            BackupRestorePathTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Async Sub StartRestoreButton_Click(sender As Object, e As RoutedEventArgs) Handles StartRestoreButton.Click
        If ConnectedDrivesComboBox2.SelectedItem IsNot Nothing AndAlso Not String.IsNullOrEmpty(BackupRestorePathTextBox.Text) Then
            Try
                Dim RestorePath As String = BackupRestorePathTextBox.Text
                Dim SelectedHDD As ComboBoxHDDDrive = CType(ConnectedDrivesComboBox2.SelectedItem, ComboBoxHDDDrive)
                Dim HDDPath As String = SelectedHDD.DevicePath

                If SelectedHDD.IsConnectedPsx Then
                    Await RunConnectedPsxRestoreAsync(RestorePath)
                    Return
                End If

                If MsgBox($"Do you really want to restore the selected backup '{Path.GetFileName(BackupRestorePathTextBox.Text)}' to {SelectedHDD.ComboBoxDisplayText} ?" & vbCrLf &
                          "This process will delete all data on the HDD and takes some hours to complete." & vbCrLf &
                          "The restore process will start in a separate window so you can continue working with PSX XMB Manager.",
                          MsgBoxStyle.YesNo,
                          "Start Backup") = MsgBoxResult.Yes Then
                    If IncreaseBlockSizeCheckBox.IsChecked Then
                        RunRestoreProcess(RestorePath, HDDPath, True)
                    Else
                        RunRestoreProcess(RestorePath, HDDPath)
                    End If
                End If
            Catch ex As Exception
                MsgBox(ex.ToString)
            End Try
        End If
    End Sub

    Private Sub RunRestoreProcess(RestorePath As String, HDDPath As String, Optional Increase As Boolean = False)
        Try
            DD = New Process()
            DD.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\dd.exe"
            If Increase Then
                DD.StartInfo.Arguments = $"if={RestorePath} of=""{HDDPath}"" bs=4M --progress"
            Else
                DD.StartInfo.Arguments = $"if={RestorePath} of=""{HDDPath}"" bs=1M --progress"
            End If
            DD.Start()
        Catch ex As Exception
            MsgBox(ex.ToString)
        End Try
    End Sub

    Private Function SelectedBlockSize() As String
        Return If(IncreaseBlockSizeCheckBox.IsChecked, "4M", "1M")
    End Function

    Private Sub SetRawCopyRunning(Running As Boolean, IsRestore As Boolean)
        RawCopyRunning = Running
        RawCopyIsRestore = IsRestore
        StartBackupButton.IsEnabled = Not Running
        StartRestoreButton.IsEnabled = Not Running
        IncreaseBlockSizeCheckBox.IsEnabled = Not Running
        InstallPOPStarterButton.IsEnabled = Not Running
        CancelRawCopyButton.IsEnabled = Running AndAlso Not IsRestore
        Cursor = If(Running, Cursors.AppStarting, Cursors.Arrow)
    End Sub

    Private Sub CancelRawCopyButton_Click(sender As Object, e As RoutedEventArgs) Handles CancelRawCopyButton.Click
        If RawCopyRunning AndAlso Not RawCopyIsRestore Then
            CancelRawCopyButton.IsEnabled = False
            RawCopyProgressTextBlock.Text = "Cancelling the backup..."
            RawCopyCancellation?.Cancel()
        End If
    End Sub

    ''' <summary>Backs the PSX connected through WSL2 up with Linux dd into a Windows file.</summary>
    Private Async Function RunConnectedPsxBackupAsync(SelectedHDD As ComboBoxHDDDrive, SavePath As String) As Task
        If Not IsWslPsxConnected Then
            MsgBox("The PSX is not connected through WSL2 anymore. Please connect it again.", MsgBoxStyle.Exclamation, "Start Backup")
            Return
        End If

        If MsgBox($"Do you really want to create a backup of {SelectedHDD.ComboBoxDisplayText} ?" & vbCrLf & vbCrLf &
                  $"Destination: {SavePath}" & vbCrLf & vbCrLf &
                  "The backup runs with dd inside WSL and can take some hours. The progress is shown in this window and the backup can be cancelled; a cancelled backup file is incomplete.",
                  MsgBoxStyle.YesNo,
                  "Start Backup") <> MsgBoxResult.Yes Then Return

        RawCopyCancellation = New CancellationTokenSource()
        SetRawCopyRunning(True, False)
        RawCopyProgressTextBlock.Text = "Starting the backup..."
        Dim BackupProgress As New Progress(Of String)(Sub(Line) If Not String.IsNullOrWhiteSpace(Line) Then RawCopyProgressTextBlock.Text = Line.Trim())

        Try
            Dim Result As ProcessResult = Await StorageBackend.BackupRawDeviceAsync(SavePath, SelectedBlockSize(), BackupProgress, RawCopyCancellation.Token)

            If Result.Cancelled OrElse Result.ErrorCode = BackendErrorCodes.OperationCancelled Then
                RawCopyProgressTextBlock.Text = "Backup cancelled."
                MsgBox("The backup was cancelled. The file " + SavePath + " is incomplete and cannot be used for a restore.", MsgBoxStyle.Exclamation, "Backup cancelled")
            ElseIf Result.Succeeded Then
                RawCopyProgressTextBlock.Text = "Backup done."
                MsgBox("Backup completed with success!" & vbCrLf & SavePath, MsgBoxStyle.Information, "Backup done")
            Else
                RawCopyProgressTextBlock.Text = "Backup failed."
                MsgBox("The backup failed. The file " + SavePath + " may be incomplete." & vbCrLf & vbCrLf & Result.CombinedOutput.Trim().Replace(vbLf, vbCrLf), MsgBoxStyle.Critical, "Backup failed")
            End If
        Catch ex As Exception
            RawCopyProgressTextBlock.Text = "Backup failed."
            ShowError(ex, "Backup failed")
        Finally
            SetRawCopyRunning(False, False)
            RawCopyCancellation.Dispose()
            RawCopyCancellation = Nothing
        End Try
    End Function

    ''' <summary>Overwrites the PSX connected through WSL2 with a backup file. The sizes must match exactly.</summary>
    Private Async Function RunConnectedPsxRestoreAsync(RestorePath As String) As Task
        If Not IsWslPsxConnected Then
            MsgBox("The PSX is not connected through WSL2 anymore. Please connect it again.", MsgBoxStyle.Exclamation, "Restore Backup")
            Return
        End If

        If Not File.Exists(RestorePath) Then
            MsgBox("The backup file " + RestorePath + " does not exist.", MsgBoxStyle.Exclamation, "Restore Backup")
            Return
        End If

        Dim SourceSize As Long = New FileInfo(RestorePath).Length
        Dim TargetSize As Long
        Try
            TargetSize = Await StorageBackend.GetRawDeviceSizeAsync(CancellationToken.None)
        Catch ex As Exception
            ShowError(ex, "Restore Backup")
            Return
        End Try

        'A backup of another HDD size is never written
        If Not StorageValidation.IsExactSizeMatch(SourceSize, TargetSize) Then
            MsgBox("The backup cannot be restored because its size does not match the connected PSX HDD." & vbCrLf & vbCrLf &
                   $"Backup file: {RestorePath}" & vbCrLf &
                   $"Backup size: {StorageValidation.FormatSize(SourceSize)} ({SourceSize:N0} bytes)" & vbCrLf &
                   $"PSX HDD size: {StorageValidation.FormatSize(TargetSize)} ({TargetSize:N0} bytes)",
                   MsgBoxStyle.Critical, "Size mismatch")
            Return
        End If

        If MsgBox("WARNING: This will overwrite ALL data on the PSX HDD." & vbCrLf & vbCrLf &
                  $"Source file: {RestorePath}" & vbCrLf &
                  $"Source size: {StorageValidation.FormatSize(SourceSize)} ({SourceSize:N0} bytes)" & vbCrLf &
                  $"PSX IP: {StorageBackend.MountedDrive.ConnectedOnIP}" & vbCrLf &
                  $"Target size: {StorageValidation.FormatSize(TargetSize)} ({TargetSize:N0} bytes)" & vbCrLf & vbCrLf &
                  "The restore runs with dd inside WSL, can take some hours and cannot be cancelled once it has started." & vbCrLf &
                  "Do you really want to restore this backup ?",
                  MsgBoxStyle.YesNo Or MsgBoxStyle.Exclamation Or MsgBoxStyle.DefaultButton2,
                  "Restore Backup") <> MsgBoxResult.Yes Then Return

        SetRawCopyRunning(True, True)
        RawCopyProgressTextBlock.Text = "Starting the restore..."
        Dim RestoreProgress As New Progress(Of String)(Sub(Line) If Not String.IsNullOrWhiteSpace(Line) Then RawCopyProgressTextBlock.Text = Line.Trim())

        Try
            Dim Result As ProcessResult = Await StorageBackend.RestoreRawDeviceAsync(RestorePath, SelectedBlockSize(), RestoreProgress, CancellationToken.None)

            If Not Result.Succeeded Then
                RawCopyProgressTextBlock.Text = "Restore failed."
                MsgBox("The restore failed. The PSX HDD may now be in an inconsistent state." & vbCrLf & vbCrLf & Result.CombinedOutput.Trim().Replace(vbLf, vbCrLf), MsgBoxStyle.Critical, "Restore failed")
                Return
            End If

            'Read the partition table again to check the restored HDD
            RawCopyProgressTextBlock.Text = "Restore written, checking the partition table..."
            Dim TocResult As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"toc", StorageBackend.MountedDrive.HDLDriveName}, Nothing, CancellationToken.None)
            If TocResult.Succeeded Then
                RawCopyProgressTextBlock.Text = "Restore done."
                MsgBox("Restore completed with success! The partition table of the PSX HDD was read successfully.", MsgBoxStyle.Information, "Restore done")
            Else
                RawCopyProgressTextBlock.Text = "Restore written, partition table check failed."
                MsgBox("The backup was written, but the partition table of the PSX HDD could not be read afterwards." & vbCrLf & vbCrLf & TocResult.CombinedOutput.Trim().Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation, "Restore check failed")
            End If
            Utils.ReloadPartitions()
        Catch ex As Exception
            RawCopyProgressTextBlock.Text = "Restore failed."
            ShowError(ex, "Restore failed")
        Finally
            SetRawCopyRunning(False, True)
        End Try
    End Function

    Private Async Sub InstallPOPStarterButton_Click(sender As Object, e As RoutedEventArgs) Handles InstallPOPStarterButton.Click
        If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected OrElse String.IsNullOrEmpty(StorageBackend.MountedDrive.DriveID) Then
            MsgBox("No HDD connected, installation will be aborted.", MsgBoxStyle.Critical, "Error while trying to install")
        Else
            If MsgBox($"Please confirm to install POPStarter on your PSX HDD connected on {StorageBackend.MountedDrive.ConnectedOnIP}" & vbCrLf, MsgBoxStyle.OkCancel, "Start installation") = MsgBoxResult.Ok Then
                InstallPOPStarterButton.IsEnabled = False
                Cursor = Cursors.Wait
                POPSInstallProgressTextBox.AppendText("Preparing PFS commands" & vbCrLf)

                'mkdir & put commands, sent straight to pfsshell; the files are put from the Tools directory
                Dim Commands As New List(Of String) From {
                    "device " + StorageBackend.MountedDrive.DriveID,
                    "mount __common",
                    "mkdir POPS",
                    "cd POPS",
                    "put IOPRP252.IMG",
                    "put POPS.ELF",
                    "put POPSTARTER.ELF",
                    "umount",
                    "exit"
                }

                POPSInstallProgressTextBox.AppendText("Starting installation using pfsshell..." & vbCrLf)
                POPSInstallProgressTextBox.ScrollToEnd()

                Try
                    Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools"), CancellationToken.None)
                    Dim PFSShellOutput As String = Result.CombinedOutput

                    If Result.TimedOut OrElse Result.Cancelled OrElse Result.ExitCode <> 0 OrElse PFSShellOutput.Contains("No such file or directory") Then
                        POPSInstallProgressTextBox.AppendText("Installation failed:" & vbCrLf & PFSShellOutput.Trim().Replace(vbLf, vbCrLf) & vbCrLf)
                        POPSInstallProgressTextBox.ScrollToEnd()
                        MsgBox("There was an error while installing POPStarter, please check the installation log.", MsgBoxStyle.Exclamation, "Error while trying to install")
                    Else
                        POPSInstallProgressTextBox.AppendText("Installation done!" & vbCrLf)
                        POPSInstallProgressTextBox.ScrollToEnd()
                        MsgBox("Installation completed with success!", MsgBoxStyle.OkOnly, "Success")
                    End If
                Catch ex As Exception
                    POPSInstallProgressTextBox.AppendText("Installation failed: " & ex.Message & vbCrLf)
                    ShowError(ex, "Error while trying to install")
                Finally
                    Cursor = Cursors.Arrow
                    InstallPOPStarterButton.IsEnabled = Not RawCopyRunning
                End Try
            End If
        End If
    End Sub

#End Region

#Region "Converters"

    Private Sub BrowseCUEFileButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseCUEFileButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "CUE files (*.cue)|*.cue", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedCueTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub BrowseOutputFolderButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseOutputFolderButton.Click
        Dim FBD As New Forms.FolderBrowserDialog() With {.RootFolder = Environment.SpecialFolder.Desktop, .Description = "Select a folder where you want to save the VCD file"}
        If FBD.ShowDialog() = Forms.DialogResult.OK Then
            POPSOutputFolderTextBox.Text = FBD.SelectedPath
        End If
    End Sub

    Private Sub ConvertToPOPSButton_Click(sender As Object, e As RoutedEventArgs) Handles ConvertToPOPSButton.Click
        If Not String.IsNullOrEmpty(SelectedCueTextBox.Text) Then
            If Not String.IsNullOrEmpty(POPSOutputFolderTextBox.Text) Then

                Cursor = Cursors.Wait

                If LogTextBox.Dispatcher.CheckAccess() = False Then
                    LogTextBox.Dispatcher.BeginInvoke(Sub() LogTextBox.Clear())
                Else
                    LogTextBox.Clear()
                End If

                'Set CUE2POPS process properties
                CUE2POPS = New Process()
                CUE2POPS.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\cue2pops.exe"

                'Build the arguments string
                Dim NewStringBuilder As New StringBuilder()
                NewStringBuilder.Append("""" + SelectedCueTextBox.Text + """")

                If Add2SecToAllTrackIndexesCheckBox.IsChecked Then
                    NewStringBuilder.Append(" gap++")
                ElseIf Sub2SecToAllTrackIndexesCheckBox.IsChecked Then
                    NewStringBuilder.Append(" gap--")
                End If
                If PatchVideoModeCheckBox.IsChecked Then
                    NewStringBuilder.Append(" vmode")
                End If
                If EnableCheatsCheckBox.IsChecked Then
                    NewStringBuilder.Append(" trainer")
                End If

                NewStringBuilder.Append(" """ + POPSOutputFolderTextBox.Text + "\IMAGE0.VCD""")

                CUE2POPS.StartInfo.Arguments = NewStringBuilder.ToString()
                CUE2POPS.StartInfo.RedirectStandardOutput = True
                CUE2POPS.StartInfo.RedirectStandardError = True
                CUE2POPS.StartInfo.UseShellExecute = False
                CUE2POPS.StartInfo.CreateNoWindow = True
                CUE2POPS.EnableRaisingEvents = True

                AddHandler CUE2POPS.OutputDataReceived, Sub(SenderProcess As Object, DataArgs As DataReceivedEventArgs)
                                                            If Not String.IsNullOrEmpty(DataArgs.Data) Then
                                                                'Append output log from CUE2POPS
                                                                If LogTextBox.Dispatcher.CheckAccess() = False Then
                                                                    LogTextBox.Dispatcher.BeginInvoke(Sub()
                                                                                                          LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                                                                          LogTextBox.ScrollToEnd()
                                                                                                      End Sub)
                                                                Else
                                                                    LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                                    LogTextBox.ScrollToEnd()
                                                                End If
                                                            End If
                                                        End Sub

                AddHandler CUE2POPS.ErrorDataReceived, Sub(SenderProcess As Object, DataArgs As DataReceivedEventArgs)
                                                           If Not String.IsNullOrEmpty(DataArgs.Data) Then
                                                               'Append error log from CUE2POPS
                                                               If LogTextBox.Dispatcher.CheckAccess() = False Then
                                                                   LogTextBox.Dispatcher.BeginInvoke(Sub()
                                                                                                         LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                                                                         LogTextBox.ScrollToEnd()
                                                                                                     End Sub)
                                                               Else
                                                                   LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                                   LogTextBox.ScrollToEnd()
                                                               End If
                                                           End If
                                                       End Sub

                AddHandler CUE2POPS.Exited, Sub(SenderProcess As Object, SenderEventArgs As EventArgs)
                                                Dispatcher.BeginInvoke(Sub()
                                                                           Cursor = Cursors.Arrow
                                                                           MsgBox("Done!")
                                                                       End Sub)
                                            End Sub

                'Start CUE2POPS & read process output data
                CUE2POPS.Start()
                CUE2POPS.BeginOutputReadLine()
                CUE2POPS.BeginErrorReadLine()
            Else
                MsgBox("No output folder specified.", MsgBoxStyle.Critical, "Error")
            End If
        Else
            MsgBox("No cue file selected.", MsgBoxStyle.Critical, "Error")
        End If
    End Sub

    Private Sub BrowseCUEButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseCUEButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "CUE files (*.cue)|*.cue", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then

            SelectedCueFileTextBox.Text = OFD.FileName
            NewBaseName = Path.GetFileNameWithoutExtension(OFD.FileName)

            Dim CheckForBinFile As String = Path.GetDirectoryName(OFD.FileName) + "\" + Path.GetFileNameWithoutExtension(OFD.FileName) + ".bin"
            If File.Exists(CheckForBinFile) Then
                SelectedBinTextBox.Text = CheckForBinFile
            End If
        End If
    End Sub

    Private Sub BrowseBINButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseBINButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "BIN files (*.bin)|*.bin", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedBinTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub ConvertToISOButton_Click(sender As Object, e As RoutedEventArgs) Handles ConvertToISOButton.Click
        If Not String.IsNullOrEmpty(SelectedCueFileTextBox.Text) AndAlso File.Exists(SelectedCueFileTextBox.Text) Then

            Cursor = Cursors.Wait

            If Dispatcher.CheckAccess() = False Then
                Dispatcher.BeginInvoke(Sub() LogTextBox.Clear())
            Else
                LogTextBox.Clear()
            End If

            'Create Converted folder if not exists
            If Not Directory.Exists(Environment.CurrentDirectory + "\Converted") Then
                Directory.CreateDirectory(Environment.CurrentDirectory + "\Converted")
                Directory.CreateDirectory(Environment.CurrentDirectory + "\Converted\ISO")
            End If

            'Set BChunk process properties
            BChunk = New Process()
            BChunk.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\bchunk.exe"

            If IsForPSXCheckBox.IsChecked Then
                BChunk.StartInfo.Arguments = "-p """ + SelectedBinTextBox.Text + """ """ + SelectedCueFileTextBox.Text + """ """ + NewBaseName + """"
            Else
                BChunk.StartInfo.Arguments = """" + SelectedBinTextBox.Text + """ """ + SelectedCueFileTextBox.Text + """ """ + NewBaseName + """"
            End If

            BChunk.StartInfo.RedirectStandardOutput = True
            BChunk.StartInfo.RedirectStandardError = True
            BChunk.StartInfo.UseShellExecute = False
            BChunk.StartInfo.CreateNoWindow = True
            BChunk.EnableRaisingEvents = True

            AddHandler BChunk.OutputDataReceived, Sub(SenderProcess As Object, DataArgs As DataReceivedEventArgs)
                                                      If Not String.IsNullOrEmpty(DataArgs.Data) Then
                                                          'Append output log from BChunk
                                                          If LogTextBox.Dispatcher.CheckAccess() = False Then
                                                              LogTextBox.Dispatcher.BeginInvoke(Sub()
                                                                                                    LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                                                                    LogTextBox.ScrollToEnd()
                                                                                                End Sub)
                                                          Else
                                                              LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                              LogTextBox.ScrollToEnd()
                                                          End If
                                                      End If
                                                  End Sub

            AddHandler BChunk.ErrorDataReceived, Sub(SenderProcess As Object, DataArgs As DataReceivedEventArgs)
                                                     If Not String.IsNullOrEmpty(DataArgs.Data) Then
                                                         'Append error log from BChunk
                                                         If LogTextBox.Dispatcher.CheckAccess() = False Then
                                                             LogTextBox.Dispatcher.BeginInvoke(Sub()
                                                                                                   LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                                                                   LogTextBox.ScrollToEnd()
                                                                                               End Sub)
                                                         Else
                                                             LogTextBox.AppendText(DataArgs.Data & vbCrLf)
                                                             LogTextBox.ScrollToEnd()
                                                         End If
                                                     End If
                                                 End Sub

            AddHandler BChunk.Exited, Sub(SenderProcess As Object, SenderEventArgs As EventArgs)

                                          'Move to \Converted\ISO folder
                                          If File.Exists(NewBaseName + "01.iso") Then
                                              File.Move(NewBaseName + "01.iso", Environment.CurrentDirectory + "\Converted\ISO\" + NewBaseName + "01.iso")
                                          End If

                                          Dispatcher.BeginInvoke(Sub()
                                                                     Cursor = Cursors.Arrow
                                                                     MsgBox("Done!")
                                                                 End Sub)
                                      End Sub

            BChunk.Start()
            BChunk.BeginOutputReadLine()
            BChunk.BeginErrorReadLine()
        End If
    End Sub

#End Region

#Region "Extractors"

    Private Sub BrowseSTARFileButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseSTARFileButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "STAR files (*.star)|*.star", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedSTARFilePathTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub BrowseExtractionOutputFolderButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseExtractionOutputFolderButton.Click
        Dim FBD As New Forms.FolderBrowserDialog() With {.RootFolder = Environment.SpecialFolder.Desktop, .Description = "Select a folder where you want to extract the STAR file"}
        If FBD.ShowDialog() = Forms.DialogResult.OK Then
            ExtractionOutputFolderTextBox.Text = FBD.SelectedPath
        End If
    End Sub

    Private Sub ExtractSTARFileButton_Click(sender As Object, e As RoutedEventArgs) Handles ExtractSTARFileButton.Click
        If Not String.IsNullOrEmpty(SelectedSTARFilePathTextBox.Text) Then

            Cursor = Cursors.Wait

            STARGazer = New Process()
            STARGazer.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\stargazer.exe"

            If Not String.IsNullOrEmpty(ExtractionOutputFolderTextBox.Text) Then
                STARGazer.StartInfo.Arguments = $"""{SelectedSTARFilePathTextBox.Text}"" ""{ExtractionOutputFolderTextBox.Text}"""
            Else
                STARGazer.StartInfo.Arguments = $"""{SelectedSTARFilePathTextBox.Text}"""
            End If

            STARGazer.StartInfo.UseShellExecute = False
            STARGazer.StartInfo.CreateNoWindow = True
            STARGazer.EnableRaisingEvents = True

            AddHandler STARGazer.Exited, Sub(SenderProcess As Object, SenderEventArgs As EventArgs)
                                             Dispatcher.BeginInvoke(Sub()
                                                                        Cursor = Cursors.Arrow
                                                                        MsgBox("Done!")
                                                                    End Sub)
                                         End Sub

            STARGazer.Start()
            STARGazer.WaitForExit()
        End If
    End Sub

    Private Sub BrowsePAKFileButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowsePAKFileButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "PAK files (*.pak)|*.pak", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedPAKFilePathTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub ExtractPAKFileButton_Click(sender As Object, e As RoutedEventArgs) Handles ExtractPAKFileButton.Click
        If Not String.IsNullOrEmpty(SelectedPAKFilePathTextBox.Text) Then

            Cursor = Cursors.Wait

            PAKer = New Process()
            PAKer.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\PAKerUtility.exe"
            PAKer.StartInfo.Arguments = $"-x ""{SelectedPAKFilePathTextBox.Text}"""
            PAKer.StartInfo.UseShellExecute = False
            PAKer.StartInfo.CreateNoWindow = True
            PAKer.EnableRaisingEvents = True

            AddHandler PAKer.Exited, Sub(SenderProcess As Object, SenderEventArgs As EventArgs)
                                         Dispatcher.BeginInvoke(Sub()
                                                                    Cursor = Cursors.Arrow
                                                                    MsgBox("Done!")
                                                                End Sub)
                                     End Sub

            PAKer.Start()
            PAKer.WaitForExit()
        End If
    End Sub

#End Region

#Region "Decryptor & Decompressor Stuff"

    Private Structure XorInfo
        Public xorTable As Byte()
        Public xorPtr As Integer
    End Structure

    Private Function XorGetNextByte(ByRef info As XorInfo) As Byte
        Dim ret As Byte = info.xorTable(info.xorPtr)
        info.xorPtr += 1
        If info.xorPtr >= info.xorTable.Length Then
            info.xorPtr = 0
        End If
        Return ret
    End Function

    Public Sub LzDecompress(dest() As Byte, source() As Byte, xorTable() As Byte, destSize As UInteger)
        Dim info As XorInfo
        info.xorTable = xorTable
        info.xorPtr = 0

        Dim ptr As Integer = 0
        Dim sourceIndex As Integer = 0
        Dim flag As UInteger = 0
        Dim count As UInteger = 0
        Dim mask As UInteger = 0
        Dim shift As UInteger = 0

        While ptr < destSize
            If count = 0 Then
                count = 30UI

                Dim raw32 As UInteger = (CUInt(source(sourceIndex)) << 24) Or (CUInt(source(sourceIndex + 1)) << 16) Or (CUInt(source(sourceIndex + 2)) << 8) Or source(sourceIndex + 3)
                Dim xor32 As UInteger = (CUInt(XorGetNextByte(info)) << 24) Or (CUInt(XorGetNextByte(info)) << 16) Or (CUInt(XorGetNextByte(info)) << 8) Or XorGetNextByte(info)

                flag = raw32 Xor xor32
                sourceIndex += 4

                mask = &H3FFFUI >> CInt((flag And 3UI))
                shift = 14UI - (flag And 3UI)
            End If

            If (flag And &H80000000UI) <> 0UI Then
                Dim raw16 As UInteger = (CUInt(source(sourceIndex)) << 8) Or source(sourceIndex + 1)
                Dim xor16 As UInteger = (CUInt(XorGetNextByte(info)) << 8) Or XorGetNextByte(info)
                Dim off_size As UInteger = raw16 Xor xor16
                sourceIndex += 2

                Dim offset As Integer = CInt((off_size And mask) + 1UI)
                Dim length As Integer = CInt((off_size >> CInt(shift)) + 3UI)

                For i As Integer = 1 To length
                    dest(ptr) = dest(ptr - offset)
                    ptr += 1
                Next
            Else
                dest(ptr) = source(sourceIndex) Xor XorGetNextByte(info)
                ptr += 1
                sourceIndex += 1
            End If

            count -= 1UI
            flag <<= 1
        End While
    End Sub

    Private Sub DecompressPSXOSDSys(InputFile As String, OutputFile As String)
        Dim buf() As Byte = File.ReadAllBytes(InputFile)
        Dim HEADER_OFFSET As Integer = 896

        If buf.Length < HEADER_OFFSET + 16 Then
            MsgBox("Input file too small or corrupted.")
            Return
        End If

        Dim headerLen = buf.Length - HEADER_OFFSET
        Dim data(headerLen - 1) As Byte
        Array.Copy(buf, HEADER_OFFSET, data, 0, headerLen)

        Dim uncompressedLen As UInteger = BitConverter.ToUInt32(data, 0)
        Dim xorTableLen As UInteger = BitConverter.ToUInt32(data, 8)

        Dim xorTable(CInt(xorTableLen) - 1) As Byte
        Array.Copy(data, 16, xorTable, 0, xorTableLen)

        Dim compOffset = 16 + CInt(xorTableLen)
        Dim compLen = data.Length - compOffset
        Dim compressedData(compLen - 1) As Byte
        Array.Copy(data, compOffset, compressedData, 0, compLen)

        Dim unpacked(CInt(uncompressedLen) - 1) As Byte
        LzDecompress(unpacked, compressedData, xorTable, uncompressedLen)

        File.WriteAllBytes(OutputFile, unpacked)
        MsgBox("Done!")
    End Sub

    Private Sub BrowseKELFButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseKELFButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "ELF files (*.elf)|*.elf|KELF files (*.kelf)|*.kelf", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedKELFFilePathTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub BrowseDecryptedKELFSavePathButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseDecryptedKELFSavePathButton.Click
        Dim SFD As New Forms.SaveFileDialog() With {.Filter = "ELF files (*.elf)|*.elf|KELF files (*.kelf)|*.kelf", .Title = "Select a save path for the decrypted file"}
        If SFD.ShowDialog() = Forms.DialogResult.OK Then
            DecryptedKELFSavePathTextBox.Text = SFD.FileName
        End If
    End Sub

    Private Sub DecryptKELFButton_Click(sender As Object, e As RoutedEventArgs) Handles DecryptKELFButton.Click
        If Not String.IsNullOrEmpty(SelectedKELFFilePathTextBox.Text) AndAlso Not String.IsNullOrEmpty(DecryptedKELFSavePathTextBox.Text) Then

            Cursor = Cursors.Wait

            KELFTool = New Process()
            KELFTool.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\kelftool.exe"
            KELFTool.StartInfo.Arguments = $"decrypt ""{SelectedKELFFilePathTextBox.Text}"" ""{DecryptedKELFSavePathTextBox.Text}"""
            KELFTool.StartInfo.UseShellExecute = False
            KELFTool.StartInfo.CreateNoWindow = True
            KELFTool.EnableRaisingEvents = True

            AddHandler KELFTool.Exited, Sub(SenderProcess As Object, SenderEventArgs As EventArgs)
                                            Dispatcher.BeginInvoke(Sub()
                                                                       Cursor = Cursors.Arrow
                                                                       MsgBox("Done!")
                                                                   End Sub)
                                        End Sub

            KELFTool.Start()
            KELFTool.WaitForExit()
        Else
            MsgBox("Input error.", MsgBoxStyle.Critical)
        End If
    End Sub

    Private Sub BrowseRELFileButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseRELFileButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "REL files (*.rel)|*.rel", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedRELFilePathTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub BrowseDecryptedRELSavePathButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseDecryptedRELSavePathButton.Click
        Dim SFD As New Forms.SaveFileDialog() With {.Title = "Select a save path for the decrypted REL file"}
        If SFD.ShowDialog() = Forms.DialogResult.OK Then
            DecryptedRELSavePathTextBox.Text = SFD.FileName
        End If
    End Sub

    Private Sub DecryptRELButton_Click(sender As Object, e As RoutedEventArgs) Handles DecryptRELButton.Click
        If Not String.IsNullOrEmpty(SelectedRELFilePathTextBox.Text) AndAlso Not String.IsNullOrEmpty(DecryptedRELSavePathTextBox.Text) Then

            Cursor = Cursors.Wait

            PSXDec = New Process()
            PSXDec.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\psxdec.exe"
            PSXDec.StartInfo.Arguments = $"""{SelectedRELFilePathTextBox.Text}"" ""{DecryptedRELSavePathTextBox.Text}"""
            PSXDec.StartInfo.UseShellExecute = False
            PSXDec.StartInfo.CreateNoWindow = True
            PSXDec.EnableRaisingEvents = True

            AddHandler PSXDec.Exited, Sub(SenderProcess As Object, SenderEventArgs As EventArgs)
                                          Dispatcher.BeginInvoke(Sub()
                                                                     Cursor = Cursors.Arrow
                                                                     MsgBox("Done!")
                                                                 End Sub)
                                      End Sub

            PSXDec.Start()
            PSXDec.WaitForExit()
        Else
            MsgBox("Input error.", MsgBoxStyle.Critical)
        End If
    End Sub

    Private Sub BrowsexosdmainButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowsexosdmainButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "ELF files (*.elf)|*.elf", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedxosdmainFileTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub BrowseDecompressedxosdmainSavePathButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseDecompressedxosdmainSavePathButton.Click
        Dim SFD As New Forms.SaveFileDialog() With {.Filter = "ELF files (*.elf)|*.elf", .Title = "Select a save path for the decrypted & decompressed xosdmain.elf"}
        If SFD.ShowDialog() = Forms.DialogResult.OK Then
            DecompressedxosdmainSavePathTextBox.Text = SFD.FileName
        End If
    End Sub

    Private Sub DecompressxosdmainButton_Click(sender As Object, e As RoutedEventArgs) Handles DecompressxosdmainButton.Click
        If Not String.IsNullOrEmpty(SelectedxosdmainFileTextBox.Text) AndAlso Not String.IsNullOrEmpty(DecompressedxosdmainSavePathTextBox.Text) Then
            DecompressPSXOSDSys(SelectedxosdmainFileTextBox.Text, DecompressedxosdmainSavePathTextBox.Text)
        Else
            MsgBox("Input error.", MsgBoxStyle.Critical)
        End If
    End Sub

    Private Sub BrowseBIOSButton_Click(sender As Object, e As RoutedEventArgs) Handles BrowseBIOSButton.Click
        Dim OFD As New Forms.OpenFileDialog() With {.CheckFileExists = True, .Filter = "BIN files (*.bin)|*.bin", .Multiselect = False}
        If OFD.ShowDialog() = Forms.DialogResult.OK Then
            SelectedBIOSFileTextBox.Text = OFD.FileName
        End If
    End Sub

    Private Sub UnpackButton_Click(sender As Object, e As RoutedEventArgs) Handles UnpackButton.Click
        If Not String.IsNullOrEmpty(SelectedBIOSFileTextBox.Text) Then

            Cursor = Cursors.Wait

            BIOSUnpacker = New Process()
            BIOSUnpacker.StartInfo.FileName = Environment.CurrentDirectory + "\Tools\ps2bios_unpacker.exe"
            BIOSUnpacker.StartInfo.Arguments = $"""{SelectedBIOSFileTextBox.Text}"""
            BIOSUnpacker.StartInfo.UseShellExecute = False
            BIOSUnpacker.StartInfo.CreateNoWindow = True
            BIOSUnpacker.EnableRaisingEvents = True

            AddHandler BIOSUnpacker.Exited, Sub(SenderProcess As Object, SenderEventArgs As EventArgs)
                                                Dispatcher.BeginInvoke(Sub()
                                                                           Cursor = Cursors.Arrow
                                                                           MsgBox("Done!")
                                                                       End Sub)
                                            End Sub

            BIOSUnpacker.Start()
            BIOSUnpacker.WaitForExit()
        Else
            MsgBox("Input error.", MsgBoxStyle.Critical)
        End If
    End Sub

#End Region

End Class

Module DevBroadcastInterface

    Public Const WM_DEVICECHANGE = 537
    Public Const DBT_DEVICEARRIVAL = 32768
    Public Const DBT_DEVICEREMOVECOMPLETE = 32772
    Public Const DBT_DEVTYP_DEVICEINTERFACE = 5
    Public Const DBT_DEVTYP_VOLUME = 2
    Public Const DEVICE_NOTIFY_WINDOW_HANDLE = 0

    <StructLayout(LayoutKind.Sequential)>
    Public Class DEV_BROADCAST_DEVICEINTERFACE
        Public dbcc_size As Integer
        Public dbcc_devicetype As Integer
        Public dbcc_reserved As Integer
        Public dbcc_classguid As Guid
        Public dbcc_name As Short
    End Class

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Public Class DEV_BROADCAST_DEVICEINTERFACE_1
        Public dbcc_size As Integer
        Public dbcc_devicetype As Integer
        Public dbcc_reserved As Integer
        Public dbcc_classguid As Guid
        <MarshalAs(UnmanagedType.ByValArray, SizeConst:=255)>
        Public dbcc_name() As Char
    End Class

    <StructLayout(LayoutKind.Sequential)>
    Public Class DEV_BROADCAST_HDR
        Public dbch_size As Integer
        Public dbch_devicetype As Integer
        Public dbch_reserved As Integer
    End Class

    <StructLayout(LayoutKind.Sequential)>
    Public Class DEV_BROADCAST_VOLUME
        Public dbcv_size As Integer
        Public dbcv_devicetype As Integer
        Public dbcv_reserved As Integer
        Public dbcv_unitmask As Integer
        Public dbcv_flags As Short
    End Class

    Public Declare Auto Function RegisterDeviceNotification Lib "user32.dll" (hRecipient As IntPtr, NotificationFilter As IntPtr, Flags As UInteger) As IntPtr
    Public Declare Function UnregisterDeviceNotification Lib "user32.dll" (Handle As IntPtr) As UInteger

End Module
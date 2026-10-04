Imports System.ComponentModel
Imports System.IO
Imports System.Net
Imports System.Threading
Imports System.Windows.Media.Animation
Imports PSX_XMB_Manager.Structs

Public Class NewMainWindow

    Private MountedDrive As MountedPSXDrive = Nothing

    ' One backend instance for the whole application; every window receives this same instance.
    Private StorageBackend As IPSXStorageBackend
    Private ConnectionCancellation As CancellationTokenSource
    Private LastProbe As BackendProbeResult
    Private LastProbeError As String = ""
    Private BackendTaskRunning As Boolean
    Private SetupRunning As Boolean
    Private SelectorsLoading As Boolean
    Private CloseConfirmed As Boolean
    Private DokanVersion As String = ""

    Private WithEvents ContentDownloader As New WebClient()

    Private Shared ReadOnly GoodBrush As Brush = Brushes.Green
    Private Shared ReadOnly BadBrush As Brush = New SolidColorBrush(Color.FromRgb(&HC1, &H22, &H49))
    Private Shared ReadOnly WarnBrush As Brush = Brushes.Orange
    Private Shared ReadOnly NeutralBrush As Brush = Brushes.Gray

    Private Sub NewMainWindow_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        Title = String.Format("PSX XMB Manager - {0}.{1}.{2}", My.Application.Info.Version.Major, My.Application.Info.Version.Minor, My.Application.Info.Version.Build)

        If Not Directory.Exists(My.Computer.FileSystem.CurrentDirectory + "\Projects") Then
            'Set up a projects directory to save all created projects
            Directory.CreateDirectory(My.Computer.FileSystem.CurrentDirectory + "\Projects")
        Else
            'Load saved projects
            For Each SavedProject In Directory.GetFiles(My.Computer.FileSystem.CurrentDirectory + "\Projects", "*.CFG")

                Dim NewCBProjectItem As New ComboBoxProjectItem()
                If Not String.IsNullOrEmpty(Path.GetFullPath(SavedProject)) Then
                    NewCBProjectItem.ProjectFile = Path.GetFullPath(SavedProject)
                Else
                    MsgBox("A broken project has been detected: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to recreate it.", MsgBoxStyle.Critical, "Error")
                End If
                If Not String.IsNullOrEmpty(Path.GetFileNameWithoutExtension(SavedProject)) Then
                    NewCBProjectItem.ProjectName = Path.GetFileNameWithoutExtension(SavedProject)
                Else
                    MsgBox("A broken project has been detected: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to recreate it.", MsgBoxStyle.Critical, "Error")
                End If

                'Get project state of saved projects
                Dim ProjectState As String = ""
                If File.ReadAllLines(SavedProject).Length > 5 Then
                    If File.ReadAllLines(SavedProject)(5).Split("="c).Length > 1 Then
                        ProjectState = File.ReadAllLines(SavedProject)(5).Split("="c)(1)
                    Else
                        MsgBox("Cannot read the project state of: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to recreate it.", MsgBoxStyle.Critical, "Error")
                    End If
                Else
                    MsgBox("Cannot find the project state of: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to recreate it.", MsgBoxStyle.Critical, "Error")
                End If

                If ProjectState = "FALSE" Then
                    ProjectListComboBox.Items.Add(NewCBProjectItem)
                Else
                    ProjectListComboBox.Items.Add(NewCBProjectItem)
                    PreparedProjectsComboBox.Items.Add(NewCBProjectItem)
                End If
            Next
        End If

        'Set DisplayMemberPath
        ProjectListComboBox.DisplayMemberPath = "ProjectName"
        PreparedProjectsComboBox.DisplayMemberPath = "ProjectName"

        'Connection method and saved values (a connected state is never persisted)
        SelectorsLoading = True
        ConnectionMethodComboBox.Items.Add(New ComboBoxItem With {.Content = "WSL2 NBD (Recommended)", .Tag = StorageBackendKind.WSL2NBD})
        ConnectionMethodComboBox.Items.Add(New ComboBoxItem With {.Content = "Local HDD", .Tag = StorageBackendKind.LocalWindows})
        ConnectionMethodComboBox.SelectedIndex = If(StorageBackendFactory.ParseKind(My.Settings.StorageBackend) = StorageBackendKind.LocalWindows, 1, 0)
        PSXIPTextBox.Text = My.Settings.LastPsxIp
        PSXPortTextBox.Text = If(StorageValidation.IsValidPort(My.Settings.NbdPort), My.Settings.NbdPort, WSL2NBDBackend.DefaultNbdPort).ToString()
        SelectorsLoading = False

        UpdateConnectionUi()
    End Sub

    Private Async Sub NewMainWindow_ContentRendered(sender As Object, e As EventArgs) Handles Me.ContentRendered
        'Dokan is optional: only the local HDD backend uses it to open partitions
        Try
            DokanVersion = Await Task.Run(Function() LocalWindowsBackend.ReadDokanVersion())
        Catch ex As Exception
            DokanVersion = ""
        End Try

        'Probe the selected backend; it never connects to a network PSX on its own
        Await SwitchBackendAsync(SelectedBackendKind())
    End Sub

    Private Async Sub NewMainWindow_Closing(sender As Object, e As CancelEventArgs) Handles Me.Closing
        If CloseConfirmed OrElse StorageBackend Is Nothing Then
            Windows.Application.Current.Shutdown()
            Return
        End If

        Dim state As ConnectionState = StorageBackend.State
        If SetupRunning OrElse Not ConnectionStateRules.CanCloseApplication(state) Then
            e.Cancel = True
            MsgBox("A PSX HDD operation is still running (" + DescribeState(state) + ")." + vbCrLf +
                   "Wait until it has finished before closing PSX XMB Manager.", MsgBoxStyle.Exclamation, "Please wait")
            Return
        End If

        If StorageBackend.Kind = StorageBackendKind.WSL2NBD AndAlso (ConnectionStateRules.IsHddAvailable(state) OrElse state = ConnectionState.Faulted) Then
            Dim answer As MsgBoxResult = MsgBox("Your PSX HDD is still connected through WSL2." + vbCrLf + vbCrLf +
                                                "Yes: disconnect cleanly, then close." + vbCrLf +
                                                "No: close and leave the connection open in WSL." + vbCrLf +
                                                "Cancel: keep PSX XMB Manager open.", MsgBoxStyle.YesNoCancel Or MsgBoxStyle.Question, "Disconnect before closing?")
            If answer = MsgBoxResult.Cancel Then
                e.Cancel = True
                Return
            ElseIf answer = MsgBoxResult.Yes Then
                e.Cancel = True
                If Await DisconnectBackendAsync(showSuccessMessage:=False) Then
                    CloseConfirmed = True
                    Close()
                End If
                Return
            Else
                Dim wslBackend As WSL2NBDBackend = TryCast(StorageBackend, WSL2NBDBackend)
                If wslBackend IsNot Nothing Then wslBackend.ReleaseKeepAlive()
            End If
        End If

        Windows.Application.Current.Shutdown()
    End Sub

#Region "Storage backend"

    Private Function SelectedBackendKind() As StorageBackendKind
        Dim item As ComboBoxItem = TryCast(ConnectionMethodComboBox.SelectedItem, ComboBoxItem)
        If item Is Nothing Then Return StorageBackendKind.WSL2NBD
        Return CType(item.Tag, StorageBackendKind)
    End Function

    Private Async Function SwitchBackendAsync(kind As StorageBackendKind) As Task
        If StorageBackend IsNot Nothing Then
            RemoveHandler StorageBackend.StateChanged, AddressOf StorageBackend_StateChanged
        End If

        StorageBackend = StorageBackendFactory.Create(kind, My.Settings.SelectedWslDistro)
        AddHandler StorageBackend.StateChanged, AddressOf StorageBackend_StateChanged
        MountedDrive = New MountedPSXDrive()
        LastProbe = Nothing
        LastProbeError = ""
        UpdateConnectionUi()

        Await ProbeBackendAsync()

        'A local PS2/PSX HDD is detected automatically, as before; a network PSX is never connected automatically
        If kind = StorageBackendKind.LocalWindows AndAlso StorageBackend.State = ConnectionState.Disconnected Then
            Await ConnectLocalAsync(showErrors:=False)
        End If
    End Function

    Private Async Function ProbeBackendAsync() As Task
        If StorageBackend Is Nothing OrElse BackendTaskRunning Then Return
        BackendTaskRunning = True
        UpdateConnectionUi()
        Try
            Await ProbeCoreAsync()
        Finally
            BackendTaskRunning = False
            UpdateConnectionUi()
        End Try
    End Function

    ''' <summary>
    ''' Probes the backend and never throws. For WSL2 it also applies the distribution selection order and
    ''' re-probes the chosen distribution.
    ''' </summary>
    Private Async Function ProbeCoreAsync() As Task
        Dim backend As IPSXStorageBackend = StorageBackend
        LastProbeError = ""
        Try
            Dim probe As BackendProbeResult = Await backend.ProbeAsync(CancellationToken.None)

            Dim wslBackend As WSL2NBDBackend = TryCast(backend, WSL2NBDBackend)
            If wslBackend IsNot Nothing AndAlso probe.WslInstalled Then
                Dim preferred As String = If(String.IsNullOrEmpty(wslBackend.DistroName), My.Settings.SelectedWslDistro, wslBackend.DistroName)
                Dim chosen As String = WslOutputParsers.SelectDistro(probe.AvailableDistros, preferred)
                If Not String.IsNullOrEmpty(chosen) AndAlso Not String.Equals(chosen, wslBackend.DistroName, StringComparison.OrdinalIgnoreCase) AndAlso
                   Not ConnectionStateRules.IsHddAvailable(wslBackend.State) Then
                    wslBackend.DistroName = chosen
                    probe = Await backend.ProbeAsync(CancellationToken.None)
                End If
                PopulateDistroComboBox(probe.AvailableDistros, wslBackend.DistroName)

                If probe.IsReady Then
                    'Only a working selection is remembered
                    My.Settings.SelectedWslDistro = wslBackend.DistroName
                    My.Settings.WslHelperProtocolVersion = probe.HelperVersion
                    My.Settings.Save()
                End If
            End If

            LastProbe = probe
        Catch ex As Exception
            LastProbe = Nothing
            LastProbeError = ex.Message
        End Try

        'An existing healthy session was adopted (startup stale-state recovery)
        If ConnectionStateRules.IsHddAvailable(backend.State) Then
            MountedDrive = backend.MountedDrive
            If backend.Kind = StorageBackendKind.WSL2NBD Then
                PSXIPTextBox.Text = MountedDrive.ConnectedOnIP
                PSXPortTextBox.Text = MountedDrive.NBDPort.ToString()
            End If
        End If
    End Function

    Private Sub PopulateDistroComboBox(distros As IEnumerable(Of WslDistroInfo), selected As String)
        SelectorsLoading = True
        Try
            WslDistroComboBox.Items.Clear()
            For Each distro As WslDistroInfo In distros
                Dim isWsl2 As Boolean = distro.Version = 2
                Dim item As New ComboBoxItem With {
                    .Content = distro.Name + If(isWsl2, "", " (WSL" + If(distro.Version > 0, distro.Version.ToString(), "?") + ", not supported)"),
                    .Tag = distro.Name,
                    .IsEnabled = isWsl2
                }
                WslDistroComboBox.Items.Add(item)
                If isWsl2 AndAlso String.Equals(distro.Name, selected, StringComparison.OrdinalIgnoreCase) Then WslDistroComboBox.SelectedItem = item
            Next
        Finally
            SelectorsLoading = False
        End Try
    End Sub

    Private Sub StorageBackend_StateChanged(sender As Object, e As EventArgs)
        'Raised on any thread
        Dispatcher.BeginInvoke(Sub()
                                   If sender Is StorageBackend Then
                                       If Not ConnectionStateRules.IsHddAvailable(StorageBackend.State) AndAlso StorageBackend.State <> ConnectionState.Faulted Then
                                           MountedDrive = New MountedPSXDrive()
                                       End If
                                       UpdateConnectionUi()
                                   End If
                               End Sub)
    End Sub

    Private Shared Function DescribeState(state As ConnectionState) As String
        Select Case state
            Case ConnectionState.Connecting
                Return "connecting"
            Case ConnectionState.Disconnecting
                Return "disconnecting"
            Case ConnectionState.BusyRead
                Return "reading from the HDD"
            Case ConnectionState.BusyWrite
                Return "writing to the HDD"
            Case Else
                Return "setting up the WSL2 backend"
        End Select
    End Function

    ''' <summary>All connection UI is derived from the backend state, never from button text.</summary>
    Private Sub UpdateConnectionUi()
        Dim state As ConnectionState = If(StorageBackend Is Nothing, ConnectionState.Unavailable, StorageBackend.State)
        Dim isWsl As Boolean = SelectedBackendKind() = StorageBackendKind.WSL2NBD
        Dim connected As Boolean = ConnectionStateRules.IsHddAvailable(state)
        Dim transitioning As Boolean = state = ConnectionState.Connecting OrElse state = ConnectionState.Disconnecting
        Dim busy As Boolean = BackendTaskRunning OrElse SetupRunning OrElse transitioning OrElse state = ConnectionState.BusyRead OrElse state = ConnectionState.BusyWrite
        Dim wslBackend As WSL2NBDBackend = TryCast(StorageBackend, WSL2NBDBackend)
        Dim hasDistro As Boolean = wslBackend IsNot Nothing AndAlso Not String.IsNullOrEmpty(wslBackend.DistroName)
        Dim visibleForWsl As Visibility = If(isWsl, Visibility.Visible, Visibility.Collapsed)

        'Selectors
        ConnectionMethodComboBox.IsEnabled = Not busy AndAlso Not connected AndAlso state <> ConnectionState.Faulted
        WslDistroLabel.Visibility = visibleForWsl
        WslDistroComboBox.Visibility = visibleForWsl
        WslDistroComboBox.IsEnabled = Not busy AndAlso Not connected AndAlso state <> ConnectionState.Faulted
        PortLabel.Visibility = visibleForWsl
        PSXPortTextBox.Visibility = visibleForWsl
        PSXIPTextBox.Visibility = visibleForWsl
        PSXIPTextBox.IsEnabled = isWsl AndAlso Not busy AndAlso Not connected AndAlso state <> ConnectionState.Faulted
        PSXPortTextBox.IsEnabled = PSXIPTextBox.IsEnabled

        If isWsl Then
            EnterIPLabel.Text = "Enter the IP address of your PSX :"
        ElseIf connected Then
            EnterIPLabel.Text = "Local PS2/PSX HDD detected & connected."
        Else
            EnterIPLabel.Text = "Connect a PS2/PSX formatted HDD to this PC, then click Detect HDD."
        End If

        'Setup and recovery (WSL2 only)
        InstallRepairWslButton.Visibility = visibleForWsl
        RecoverWslButton.Visibility = visibleForWsl
        Dim setupBlocked As String = SetupBlockedReason()
        InstallRepairWslButton.IsEnabled = isWsl AndAlso hasDistro AndAlso Not busy AndAlso Not connected AndAlso
                                           (state = ConnectionState.NeedsSetup OrElse state = ConnectionState.Disconnected OrElse state = ConnectionState.Faulted) AndAlso
                                           setupBlocked = ""
        InstallRepairWslButton.ToolTip = If(setupBlocked = "", "Installs or repairs nbdfuse, hdl_dump, pfsshell, pfsfuse and the PSX XMB Manager helper inside the selected WSL2 distribution.", setupBlocked)
        RecoverWslButton.IsEnabled = isWsl AndAlso Not busy AndAlso state = ConnectionState.Faulted

        'Connect / Disconnect
        If state = ConnectionState.Connecting Then
            ConnectButton.Content = "Connecting..."
        ElseIf state = ConnectionState.Disconnecting Then
            ConnectButton.Content = "Disconnecting..."
        ElseIf connected OrElse state = ConnectionState.Faulted Then
            ConnectButton.Content = "Disconnect"
        Else
            ConnectButton.Content = If(isWsl, "Connect", "Detect HDD")
        End If
        ConnectButton.IsEnabled = Not busy AndAlso (ConnectionStateRules.CanConnect(state) OrElse ConnectionStateRules.CanDisconnect(state))

        'Status rows
        If isWsl Then
            WslStatusTitleLabel.Text = "WSL2 Backend :"
            WslToolsTitleLabel.Text = "Linux PS2 Tools :"
            ShowWslStatus(state)
        Else
            WslStatusTitleLabel.Text = "Backend :"
            WslToolsTitleLabel.Text = "Windows PS2 Tools :"
            SetStatus(WslStatusLabel, "Local HDD (Windows tools)", GoodBrush)
            If LastProbe Is Nothing Then
                SetStatus(WslToolsStatusLabel, If(BackendTaskRunning, "Checking...", If(LastProbeError = "", "-", LastProbeError)), If(BackendTaskRunning, WarnBrush, NeutralBrush))
            ElseIf LastProbe.IsReady Then
                SetStatus(WslToolsStatusLabel, "hdl_dump.exe, pfsshell.exe found", GoodBrush)
            Else
                SetStatus(WslToolsStatusLabel, LastProbe.SetupMessage, BadBrush)
            End If
        End If

        If Not String.IsNullOrEmpty(DokanVersion) Then
            SetStatus(DokanDriverVersionLabel, DokanVersion, GoodBrush)
        ElseIf isWsl Then
            SetStatus(DokanDriverVersionLabel, "Not installed (optional)", NeutralBrush)
        Else
            SetStatus(DokanDriverVersionLabel, "Not installed (needed to open partitions)", WarnBrush)
        End If

        NBDConnectionStatusLabel.Text = If(isWsl, "Connection Status :", "Local Connection :")
        Dim drive As MountedPSXDrive = MountedDrive
        Select Case state
            Case ConnectionState.Connected, ConnectionState.BusyRead, ConnectionState.BusyWrite
                Dim connectedText As String = If(isWsl, "Connected to " + drive.ConnectedOnIP + ":" + drive.NBDPort.ToString(), "Connected")
                If state = ConnectionState.BusyRead Then connectedText += " (reading)"
                If state = ConnectionState.BusyWrite Then connectedText += " (writing)"
                SetStatus(NBDConnectionLabel, connectedText, If(state = ConnectionState.BusyWrite, WarnBrush, GoodBrush))
            Case ConnectionState.Connecting
                SetStatus(NBDConnectionLabel, If(isWsl, "Connecting...", "Detecting..."), WarnBrush)
            Case ConnectionState.Disconnecting
                SetStatus(NBDConnectionLabel, "Disconnecting...", WarnBrush)
            Case ConnectionState.Faulted
                SetStatus(NBDConnectionLabel, "Connection broken: Disconnect or Recover", BadBrush)
            Case ConnectionState.NeedsSetup
                SetStatus(NBDConnectionLabel, "Setup required", WarnBrush)
            Case ConnectionState.Unavailable
                SetStatus(NBDConnectionLabel, "Unavailable", BadBrush)
            Case Else
                SetStatus(NBDConnectionLabel, "Not connected", BadBrush)
        End Select

        If connected Then
            If isWsl Then
                SetStatus(MountStatusLabel, "Mounted by nbdfuse in " + drive.WslDistro, GoodBrush, drive.NativeDevicePath)
            Else
                SetStatus(MountStatusLabel, "On " + drive.HDLDriveName, GoodBrush)
            End If
        ElseIf state = ConnectionState.Faulted Then
            SetStatus(MountStatusLabel, "Stale mount", BadBrush)
        Else
            SetStatus(MountStatusLabel, "Unmounted", BadBrush)
        End If

        InstallProjectButton.IsEnabled = connected
    End Sub

    Private Sub ShowWslStatus(state As ConnectionState)
        Dim probe As BackendProbeResult = LastProbe
        If probe Is Nothing Then
            If BackendTaskRunning Then
                SetStatus(WslStatusLabel, "Checking...", WarnBrush)
                SetStatus(WslToolsStatusLabel, "Checking...", WarnBrush)
            Else
                SetStatus(WslStatusLabel, If(LastProbeError = "", "-", LastProbeError), If(LastProbeError = "", NeutralBrush, BadBrush))
                SetStatus(WslToolsStatusLabel, "-", NeutralBrush)
            End If
            Return
        End If

        Dim help As String = If(probe.SetupMessage = "", "", probe.SetupMessage + vbCrLf + BackendErrorCodes.NextStep(probe.SetupErrorCode))
        If Not probe.WslInstalled OrElse probe.SetupErrorCode = BackendErrorCodes.WslNotInstalled Then
            SetStatus(WslStatusLabel, "WSL is not installed", BadBrush, help)
        ElseIf probe.SetupErrorCode = BackendErrorCodes.NoWsl2Distro Then
            SetStatus(WslStatusLabel, "No WSL2 distribution", BadBrush, help)
        ElseIf probe.SetupErrorCode = BackendErrorCodes.WslDistroNotFound Then
            SetStatus(WslStatusLabel, "Select a WSL2 distribution", WarnBrush, help)
        ElseIf String.IsNullOrEmpty(probe.DistroOsId) AndAlso Not probe.IsReady Then
            SetStatus(WslStatusLabel, "Distribution did not answer", BadBrush, help)
        Else
            Dim osName As String = If(probe.DistroOsName = "", probe.SelectedDistro, probe.DistroOsName)
            SetStatus(WslStatusLabel, "WSL2 - " + osName, If(probe.IsReady, GoodBrush, WarnBrush), help)
        End If

        If probe.IsReady Then
            SetStatus(WslToolsStatusLabel, "Ready (helper v" + probe.HelperVersion.ToString() + ")", GoodBrush,
                      "nbdfuse " + probe.NbdfuseVersion + vbCrLf + "nbdinfo " + probe.NbdinfoVersion)
        ElseIf probe.SetupErrorCode = BackendErrorCodes.HelperVersionMismatch Then
            SetStatus(WslToolsStatusLabel, "Helper outdated: Install / Repair", WarnBrush, BackendErrorCodes.HelperOutdatedMessage)
        ElseIf probe.MissingDependencies.Count > 0 Then
            SetStatus(WslToolsStatusLabel, "Missing: " + String.Join(", ", probe.MissingDependencies), BadBrush, help)
        ElseIf state = ConnectionState.Unavailable Then
            SetStatus(WslToolsStatusLabel, "-", NeutralBrush)
        Else
            SetStatus(WslToolsStatusLabel, "Not ready", WarnBrush, help)
        End If
    End Sub

    Private Shared Sub SetStatus(label As TextBlock, text As String, brush As Brush, Optional details As String = Nothing)
        label.Text = text
        label.Foreground = brush
        If String.IsNullOrEmpty(details) Then
            label.ToolTip = If(String.IsNullOrEmpty(text), Nothing, text)
        Else
            label.ToolTip = text + vbCrLf + vbCrLf + details
        End If
    End Sub

    ''' <summary>Why automatic setup cannot run for the selected distribution, or "" when it can.</summary>
    Private Function SetupBlockedReason() As String
        Dim probe As BackendProbeResult = LastProbe
        If probe Is Nothing OrElse Not probe.WslInstalled OrElse probe.WslVersion <> 2 Then Return "Select an installed WSL2 distribution first."
        If probe.DistroSupportsAutomaticSetup Then Return ""
        If AllLinuxToolsPresent(probe) Then Return ""
        Return "Automatic setup supports Ubuntu and Debian only. Install these manually in '" + probe.SelectedDistro + "': " + String.Join(", ", probe.MissingDependencies)
    End Function

    ''' <summary>True when every Linux tool is installed, so a helper-only setup is enough (any distribution).</summary>
    Private Shared Function AllLinuxToolsPresent(probe As BackendProbeResult) As Boolean
        Return probe.Python3Present AndAlso probe.NbdfusePresent AndAlso probe.NbdinfoPresent AndAlso probe.HdlDumpPresent AndAlso
               probe.PfsShellPresent AndAlso probe.PfsFusePresent AndAlso probe.Fusermount3Present AndAlso probe.FusermountPresent AndAlso
               probe.DevFusePresent
    End Function

    Private Shared Sub ShowBackendError(ex As Exception, title As String)
        Dim backendError As StorageBackendException = TryCast(ex, StorageBackendException)
        If backendError IsNot Nothing Then
            MsgBox(BackendErrorCodes.FormatForUser(backendError), MsgBoxStyle.Critical, title)
        Else
            MsgBox(ex.Message, MsgBoxStyle.Critical, title)
        End If
    End Sub

    Private Async Sub ConnectionMethodComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles ConnectionMethodComboBox.SelectionChanged
        If SelectorsLoading OrElse StorageBackend Is Nothing Then Return
        Dim kind As StorageBackendKind = SelectedBackendKind()
        If kind = StorageBackend.Kind Then Return

        Dim state As ConnectionState = StorageBackend.State
        If ConnectionStateRules.IsHddAvailable(state) OrElse state = ConnectionState.Faulted OrElse BackendTaskRunning OrElse SetupRunning Then
            'Disconnect first; restore the previous selection
            SelectorsLoading = True
            ConnectionMethodComboBox.SelectedIndex = If(StorageBackend.Kind = StorageBackendKind.LocalWindows, 1, 0)
            SelectorsLoading = False
            MsgBox("Disconnect from the current PSX HDD before changing the connection method.", MsgBoxStyle.Information)
            Return
        End If

        My.Settings.StorageBackend = kind.ToString()
        My.Settings.Save()
        Await SwitchBackendAsync(kind)
    End Sub

    Private Async Sub WslDistroComboBox_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles WslDistroComboBox.SelectionChanged
        If SelectorsLoading Then Return
        Dim wslBackend As WSL2NBDBackend = TryCast(StorageBackend, WSL2NBDBackend)
        Dim item As ComboBoxItem = TryCast(WslDistroComboBox.SelectedItem, ComboBoxItem)
        If wslBackend Is Nothing OrElse item Is Nothing OrElse Not item.IsEnabled Then Return
        Dim distro As String = CStr(item.Tag)
        If String.Equals(distro, wslBackend.DistroName, StringComparison.OrdinalIgnoreCase) Then Return

        Try
            wslBackend.DistroName = distro
        Catch ex As StorageBackendException
            ShowBackendError(ex, "WSL2 Distribution")
            PopulateDistroComboBox(If(LastProbe Is Nothing, New List(Of WslDistroInfo), LastProbe.AvailableDistros), wslBackend.DistroName)
            Return
        End Try

        'A WSL2 distribution picked from the list is a valid selection
        My.Settings.SelectedWslDistro = distro
        My.Settings.Save()
        Await ProbeBackendAsync()
    End Sub

    Private Async Sub ConnectButton_Click(sender As Object, e As RoutedEventArgs) Handles ConnectButton.Click
        If StorageBackend Is Nothing OrElse BackendTaskRunning OrElse SetupRunning Then Return
        Dim state As ConnectionState = StorageBackend.State

        If ConnectionStateRules.IsHddAvailable(state) OrElse state = ConnectionState.Faulted Then
            Await DisconnectBackendAsync(showSuccessMessage:=True)
        ElseIf ConnectionStateRules.CanConnect(state) Then
            If StorageBackend.Kind = StorageBackendKind.WSL2NBD Then
                Await ConnectWslAsync()
            Else
                Await ConnectLocalAsync(showErrors:=True)
            End If
        End If
    End Sub

    Private Async Function ConnectWslAsync() As Task
        '1. Validate the PSX IPv4 address and the port
        Dim ip As String = ""
        If Not StorageValidation.TryParseIPv4(PSXIPTextBox.Text.Trim(), ip) Then
            ShowBackendError(New StorageBackendException(BackendErrorCodes.InvalidIp, "'" + PSXIPTextBox.Text.Trim() + "' is not a valid IPv4 address."), "Invalid IP address")
            Return
        End If
        Dim port As Integer
        If Not Integer.TryParse(PSXPortTextBox.Text.Trim(), port) OrElse Not StorageValidation.IsValidPort(port) Then
            ShowBackendError(New StorageBackendException(BackendErrorCodes.InvalidPort, "'" + PSXPortTextBox.Text.Trim() + "' is not a valid port."), "Invalid port")
            Return
        End If

        BackendTaskRunning = True
        UpdateConnectionUi()
        Try
            '2./3. The selected distribution must be WSL2 and fully set up: probe it right before connecting
            Await ProbeCoreAsync()
            Dim probe As BackendProbeResult = LastProbe
            Dim state As ConnectionState = StorageBackend.State

            If ConnectionStateRules.IsHddAvailable(state) Then
                'An existing session was found and adopted: never mount twice
                If MountedDrive.ConnectedOnIP = ip AndAlso MountedDrive.NBDPort = port Then
                    MsgBox("PSX HDD is already connected at " + ip + ":" + port.ToString() + ".", MsgBoxStyle.Information, "Connected")
                Else
                    MsgBox("WSL2 is already connected to the PSX at " + MountedDrive.ConnectedOnIP + ":" + MountedDrive.NBDPort.ToString() + "." + vbCrLf +
                           "Disconnect first to connect to " + ip + ":" + port.ToString() + ".", MsgBoxStyle.Exclamation, "Already connected")
                End If
                Return
            End If
            If state <> ConnectionState.Disconnected Then
                '4. Dependencies missing or an old mount is present: refuse; the setup/recover buttons follow the state
                Dim code As String = BackendErrorCodes.BackendSetupRequired
                Dim problem As String = "The WSL2 backend could not be checked. " + LastProbeError
                If state = ConnectionState.Faulted Then
                    code = BackendErrorCodes.StaleMountState
                    problem = "An old NBD connection (" + If(probe Is Nothing, "unknown", probe.ConnectionStatus) + ") is still present in WSL."
                ElseIf probe IsNot Nothing AndAlso probe.SetupErrorCode <> "" Then
                    code = probe.SetupErrorCode
                    problem = probe.SetupMessage
                End If
                ShowBackendError(New StorageBackendException(code, problem), "Cannot connect")
                Return
            End If

            '5.-7. Connect; the button shows Connecting... from the state
            ConnectionCancellation = New CancellationTokenSource()
            MountedDrive = Await StorageBackend.ConnectAsync(ip, port, ConnectionCancellation.Token)

            'Remember only values that worked
            My.Settings.LastPsxIp = ip
            My.Settings.NbdPort = port
            My.Settings.Save()

            BackendTaskRunning = False
            UpdateConnectionUi()
            MsgBox("PSX HDD is now connected." + vbCrLf + "You can now install a project on the PSX.", MsgBoxStyle.Information, "Success")
        Catch ex As Exception
            ShowBackendError(ex, "Could not connect to the PSX")
        Finally
            BackendTaskRunning = False
            If ConnectionCancellation IsNot Nothing Then
                ConnectionCancellation.Dispose()
                ConnectionCancellation = Nothing
            End If
            UpdateConnectionUi()
        End Try
    End Function

    Private Async Function ConnectLocalAsync(showErrors As Boolean) As Task
        BackendTaskRunning = True
        UpdateConnectionUi()
        Try
            MountedDrive = Await StorageBackend.ConnectAsync("", 0, CancellationToken.None)

            Dim localBackend As LocalWindowsBackend = TryCast(StorageBackend, LocalWindowsBackend)
            If localBackend IsNot Nothing AndAlso localBackend.WmicInstallRequired Then
                If MsgBox("WMIC is not installed on your system but is required." + vbCrLf +
                          "Please install it first from 'Optional features' in your Windows Settings before continuing." + vbCrLf +
                          "Restart PSX XMB Manager when done.", MsgBoxStyle.YesNo, "Warning") = MsgBoxResult.Yes Then
                    Process.Start("ms-settings:optionalfeatures")
                End If
            ElseIf String.IsNullOrEmpty(MountedDrive.DriveID) Then
                MsgBox("Could not determine the full HDD drive path using 'wmic'." + vbCrLf + "Make sure 'wmic' is installed on your PC and try again.", MsgBoxStyle.Exclamation, "Warning")
            End If
        Catch ex As Exception
            If showErrors Then ShowBackendError(ex, "Local HDD")
        Finally
            BackendTaskRunning = False
            UpdateConnectionUi()
        End Try
    End Function

    ''' <summary>Disconnects through the backend; the UI only changes after the backend confirmed the unmount.</summary>
    Private Async Function DisconnectBackendAsync(showSuccessMessage As Boolean) As Task(Of Boolean)
        Dim openPartitions As Integer = StorageBackend.ActiveMounts.Count
        If openPartitions > 0 Then
            If MsgBox(openPartitions.ToString() + " PSX partition(s) are still open and will be closed first." + vbCrLf + "Continue?",
                      MsgBoxStyle.YesNo Or MsgBoxStyle.Question, "Disconnect") <> MsgBoxResult.Yes Then
                Return False
            End If
        End If

        BackendTaskRunning = True
        UpdateConnectionUi()
        Try
            Await StorageBackend.DisconnectAsync(CancellationToken.None)
            MountedDrive = New MountedPSXDrive()
            BackendTaskRunning = False
            UpdateConnectionUi()
            If showSuccessMessage Then
                If StorageBackend.Kind = StorageBackendKind.WSL2NBD Then
                    MsgBox("Your PSX HDD is now disconnected." + vbCrLf + "You can now safely close the NBD server.", MsgBoxStyle.Information)
                Else
                    MsgBox("The local PS2/PSX HDD is now released.", MsgBoxStyle.Information)
                End If
            End If
            Return True
        Catch ex As Exception
            ShowBackendError(ex, "Could not disconnect")
            Return False
        Finally
            BackendTaskRunning = False
            UpdateConnectionUi()
        End Try
    End Function

    Private Async Sub RecoverWslButton_Click(sender As Object, e As RoutedEventArgs) Handles RecoverWslButton.Click
        Dim wslBackend As WSL2NBDBackend = TryCast(StorageBackend, WSL2NBDBackend)
        If wslBackend Is Nothing OrElse BackendTaskRunning OrElse SetupRunning Then Return
        If MsgBox("Recover WSL Connection cleans up an old or broken PSX connection inside '" + wslBackend.DistroName + "':" + vbCrLf +
                  "open partitions and the NBD mount are unmounted, and a hung nbdfuse is stopped." + vbCrLf + vbCrLf +
                  "Use it after a crash, 'wsl --shutdown' or a lost network connection. Continue?",
                  MsgBoxStyle.YesNo Or MsgBoxStyle.Question, "Recover WSL Connection") <> MsgBoxResult.Yes Then
            Return
        End If

        BackendTaskRunning = True
        UpdateConnectionUi()
        Try
            Await wslBackend.RecoverAsync(CancellationToken.None)
            MountedDrive = New MountedPSXDrive()
            Await ProbeCoreAsync()
            MsgBox("The old connection was cleaned up. You can connect to the PSX again.", MsgBoxStyle.Information, "Recovered")
        Catch ex As Exception
            ShowBackendError(ex, "Recovery failed")
        Finally
            BackendTaskRunning = False
            UpdateConnectionUi()
        End Try
    End Sub

    Private Async Sub InstallRepairWslButton_Click(sender As Object, e As RoutedEventArgs) Handles InstallRepairWslButton.Click
        Await RunWslSetupAsync()
    End Sub

    ''' <summary>Install / Repair WSL Backend: runs only after the user confirmed the distribution and what gets installed.</summary>
    Private Async Function RunWslSetupAsync() As Task
        Dim wslBackend As WSL2NBDBackend = TryCast(StorageBackend, WSL2NBDBackend)
        If wslBackend Is Nothing OrElse String.IsNullOrEmpty(wslBackend.DistroName) OrElse BackendTaskRunning OrElse SetupRunning Then Return
        Dim blocked As String = SetupBlockedReason()
        If blocked <> "" Then
            MsgBox(blocked, MsgBoxStyle.Exclamation, "Install / Repair WSL Backend")
            Return
        End If

        Dim probe As BackendProbeResult = LastProbe
        Dim fullSetup As Boolean = probe.DistroSupportsAutomaticSetup
        Dim distroName As String = wslBackend.DistroName
        Dim details As String
        If fullSetup Then
            details = "The following will be installed as root inside the WSL2 distribution '" + distroName + "' (" + probe.DistroOsName + "):" + vbCrLf + vbCrLf +
                      "Packages (apt-get): ca-certificates, git, build-essential, pkg-config, meson, ninja-build, python3, libnbd-bin (nbdfuse, nbdinfo), fuse3, libfuse-dev" + vbCrLf + vbCrLf +
                      "Built from pinned source: hdl_dump (ps2homebrew/hdl-dump 32c296c), pfsshell and pfsfuse (ps2homebrew/pfsshell 8c92467, built with meson 1.3.2 from source)" + vbCrLf + vbCrLf +
                      "PSX XMB Manager helper (protocol " + WSL2NBDBackend.ExpectedHelperProtocolVersion.ToString() + ") in /usr/local/lib/psx-xmb-manager" + vbCrLf + vbCrLf +
                      "/etc/fuse.conf: user_allow_other (lets Windows open mounted partitions)" + vbCrLf + vbCrLf +
                      "This downloads packages and source code and can take several minutes. Continue?"
        Else
            details = "All Linux PS2 tools are already installed in '" + distroName + "'." + vbCrLf + vbCrLf +
                      "Only the PSX XMB Manager helper (protocol " + WSL2NBDBackend.ExpectedHelperProtocolVersion.ToString() + ") will be installed; no packages are installed. Continue?"
        End If
        If MsgBox(details, MsgBoxStyle.YesNo Or MsgBoxStyle.Question, "Install / Repair WSL Backend") <> MsgBoxResult.Yes Then Return

        SetupRunning = True
        UpdateConnectionUi()
        Dim transcript As String = ""
        Dim succeeded As Boolean = False
        Dim failure As Exception = Nothing
        Dim setupResult As ProcessResult = Nothing
        Try
            Dim progress As Action(Of String) = Sub(line) Dispatcher.BeginInvoke(Sub() BackendActivityLabel.Text = line)
            Dim result As ProcessResult = Await wslBackend.InstallOrRepairAsync(fullSetup, progress, CancellationToken.None)
            setupResult = result
            transcript = result.CombinedOutput
            succeeded = result.Succeeded AndAlso result.StandardOutput.Contains(BootstrapReport.SuccessMarker)
        Catch ex As Exception
            failure = ex
            transcript = ex.ToString()
        Finally
            SetupRunning = False
            BackendActivityLabel.Text = ""
        End Try

        'Keep the complete output, then re-check the backend
        Dim logFile As String = BackendLog.SaveTranscript("wsl-bootstrap", transcript)
        Await ProbeBackendAsync()

        If failure IsNot Nothing Then
            ShowBackendError(failure, "Install / Repair WSL Backend")
        ElseIf succeeded AndAlso LastProbe IsNot Nothing AndAlso LastProbe.IsReady Then
            MsgBox("The WSL2 backend is installed and ready in '" + distroName + "'." + vbCrLf + "You can now connect to your PSX.", MsgBoxStyle.Information, "Install / Repair WSL Backend")
        ElseIf succeeded Then
            'The script finished, but the check run afterwards still finds something missing
            Dim problem As String = If(LastProbe Is Nothing OrElse LastProbe.SetupMessage = "", "The backend check did not complete.", LastProbe.SetupMessage)
            Dim nextStep As String = If(LastProbe Is Nothing OrElse LastProbe.SetupErrorCode = "", "", BackendErrorCodes.NextStep(LastProbe.SetupErrorCode))
            MsgBox("The WSL2 backend setup finished, but the check afterwards still reports a problem:" + vbCrLf + vbCrLf + problem +
                   If(nextStep = "", "", vbCrLf + vbCrLf + "What to do: " + nextStep) +
                   If(LastProbe Is Nothing OrElse LastProbe.SetupErrorCode = "", "", vbCrLf + "Error code: " + LastProbe.SetupErrorCode) + vbCrLf + vbCrLf +
                   If(logFile = "", "", "Full output: " + logFile), MsgBoxStyle.Exclamation, "Install / Repair WSL Backend")
        Else
            MsgBox("The WSL2 backend setup did not finish." + vbCrLf + vbCrLf + BootstrapReport.Summarize(setupResult) + vbCrLf + vbCrLf +
                   If(logFile = "", "", "Full output: " + logFile), MsgBoxStyle.Critical, "Install / Repair WSL Backend")
        End If
    End Function

#End Region

#Region "Menu"

    Private Sub StartMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles StartMenuItem.Click
        'Switch to the StartGrid
        StartMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF004671"), Color))
        ProjectsMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        PartitionManagerMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        PS1GameLibraryMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        PS2GameLibraryMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        XMBToolsMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        WslSetupMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))

        Dim ProjectsGridAnimation As New DoubleAnimation With {.From = 1, .To = 0, .Duration = New Duration(TimeSpan.FromMilliseconds(300))}
        Dim StartGridAnimation As New DoubleAnimation With {.From = 0, .To = 1, .Duration = New Duration(TimeSpan.FromMilliseconds(300))}

        StartGrid.Visibility = Visibility.Visible

        ProjectsGrid.BeginAnimation(OpacityProperty, ProjectsGridAnimation)
        StartGrid.BeginAnimation(OpacityProperty, StartGridAnimation)

        ProjectsGrid.Visibility = Visibility.Hidden
    End Sub

    Private Sub ProjectsMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles ProjectsMenuItem.Click
        'Switch to the ProjectsGrid
        StartMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        ProjectsMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF004671"), Color))
        PartitionManagerMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        PS1GameLibraryMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        PS2GameLibraryMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        XMBToolsMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
        WslSetupMenuItem.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))

        Dim ProjectsGridAnimation As New DoubleAnimation With {.From = 0, .To = 1, .Duration = New Duration(TimeSpan.FromMilliseconds(300))}
        Dim StartGridAnimation As New DoubleAnimation With {.From = 1, .To = 0, .Duration = New Duration(TimeSpan.FromMilliseconds(300))}

        ProjectsGrid.Visibility = Visibility.Visible

        StartGrid.BeginAnimation(OpacityProperty, StartGridAnimation)
        ProjectsGrid.BeginAnimation(OpacityProperty, ProjectsGridAnimation)

        StartGrid.Visibility = Visibility.Hidden
    End Sub

    Private Sub PartitionManagerMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles PartitionManagerMenuItem.Click
        If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected Then
            MsgBox("Please connect to the PSX first.", MsgBoxStyle.Information)
        Else
            Dim NewPartitionManager As New PartitionManager() With {.ShowActivated = True, .MountedDrive = StorageBackend.MountedDrive, .StorageBackend = StorageBackend}
            NewPartitionManager.Show()
        End If
    End Sub

    Private Sub PS1GameLibraryMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles PS1GameLibraryMenuItem.Click
        Dim NewGameLibrary As New PS1GameLibrary() With {.ShowActivated = True}
        NewGameLibrary.Show()
    End Sub

    Private Sub PS2GameLibraryMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles PS2GameLibraryMenuItem.Click
        Dim NewGameLibrary As New GameLibrary() With {.ShowActivated = True, .MountedDrive = MountedDrive, .StorageBackend = StorageBackend}
        NewGameLibrary.Show()
    End Sub

    Private Sub XMBToolsMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles XMBToolsMenuItem.Click
        Dim NewAssetsBrowser As New AssetsBrowser() With {.ShowActivated = True}
        NewAssetsBrowser.Show()
    End Sub

    Private Async Sub WslSetupMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles WslSetupMenuItem.Click
        'Show the connection page with the WSL2 backend selected and re-check it
        StartMenuItem_Click(sender, e)
        If StorageBackend Is Nothing OrElse BackendTaskRunning OrElse SetupRunning Then Return
        If StorageBackend.Kind <> StorageBackendKind.WSL2NBD Then
            'SelectionChanged switches to the WSL2 backend and probes it (or explains why it cannot while connected)
            ConnectionMethodComboBox.SelectedIndex = 0
            Return
        End If
        If StorageBackend.IsConnected Then Return

        Await ProbeBackendAsync()
        If StorageBackend.State = ConnectionState.NeedsSetup AndAlso InstallRepairWslButton.IsEnabled AndAlso LastProbe IsNot Nothing Then
            If MsgBox("The WSL2 backend is not ready:" + vbCrLf + LastProbe.SetupMessage + vbCrLf + vbCrLf + "Run Install / Repair WSL Backend now?",
                      MsgBoxStyle.YesNo Or MsgBoxStyle.Question, "WSL2 Setup") = MsgBoxResult.Yes Then
                Await RunWslSetupAsync()
            End If
        End If
    End Sub

    Private Sub DokanDriverMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles DokanDriverMenuItem.Click
        Process.Start(New ProcessStartInfo("https://github.com/dokan-dev/dokany/releases") With {.UseShellExecute = True})
    End Sub

    Private Sub UtilitiesMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles UtilitiesMenuItem.Click
        Dim NewUtilities As New Utilities() With {.ShowActivated = True, .MountedDrive = MountedDrive, .StorageBackend = StorageBackend}
        NewUtilities.Show()
    End Sub

#End Region

#Region "Projects"

    Public Sub ReloadProjects()
        ProjectListComboBox.Items.Clear()
        PreparedProjectsComboBox.Items.Clear()

        If Directory.Exists(My.Computer.FileSystem.CurrentDirectory + "\Projects") Then
            'Load saved projects
            For Each SavedProject In Directory.GetFiles(My.Computer.FileSystem.CurrentDirectory + "\Projects", "*.CFG")

                Dim NewCBProjectItem As New ComboBoxProjectItem()
                If Not String.IsNullOrEmpty(Path.GetFullPath(SavedProject)) Then
                    NewCBProjectItem.ProjectFile = Path.GetFullPath(SavedProject)
                Else
                    MsgBox("A broken project has been detected: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to re-create it.", MsgBoxStyle.Critical, "Error")
                End If
                If Not String.IsNullOrEmpty(Path.GetFileNameWithoutExtension(SavedProject)) Then
                    NewCBProjectItem.ProjectName = Path.GetFileNameWithoutExtension(SavedProject)
                Else
                    MsgBox("A broken project has been detected: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to re-create it.", MsgBoxStyle.Critical, "Error")
                End If

                'Get project state of saved projects
                Dim ProjectState As String = ""
                If File.ReadAllLines(SavedProject).Length > 5 Then
                    If File.ReadAllLines(SavedProject)(5).Split("="c).Length > 1 Then
                        ProjectState = File.ReadAllLines(SavedProject)(5).Split("="c)(1)
                    Else
                        MsgBox("Cannot read the project state of: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to re-create it.", MsgBoxStyle.Critical, "Error")
                    End If
                Else
                    MsgBox("Cannot find the project state of: " + SavedProject + vbCrLf + vbCrLf + "It's recommended to remove this project and to re-create it.", MsgBoxStyle.Critical, "Error")
                End If

                If ProjectState = "FALSE" Then
                    ProjectListComboBox.Items.Add(NewCBProjectItem)
                Else
                    ProjectListComboBox.Items.Add(NewCBProjectItem)
                    PreparedProjectsComboBox.Items.Add(NewCBProjectItem)
                End If
            Next
        Else
            'Set up a projects directory to save all created projects
            Directory.CreateDirectory(My.Computer.FileSystem.CurrentDirectory + "\Projects")
        End If
    End Sub

    Private Sub NewHomebrewProjectMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles NewHomebrewProjectButton.Click
        Dim NewHomebrewProjectWindow As New NewAppProject() With {.ShowActivated = True}
        NewHomebrewProjectWindow.Show()
    End Sub

    Private Sub NewGameProjectMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles NewGameProjectButton.Click
        Dim NewGameProjectWindow As New NewGameProject() With {.ShowActivated = True}
        NewGameProjectWindow.Show()
    End Sub

    Private Sub NewPS1GameProjectButton_Click(sender As Object, e As RoutedEventArgs) Handles NewPS1GameProjectButton.Click
        Dim NewGameProjectWindow As New NewPS1GameProject() With {.ShowActivated = True}
        NewGameProjectWindow.Show()
    End Sub

    Private Sub EditProjectButton_Click(sender As Object, e As RoutedEventArgs) Handles EditProjectButton.Click
        If ProjectListComboBox.SelectedItem IsNot Nothing Then
            'Get project infos
            Dim SelectedProject As ComboBoxProjectItem = CType(ProjectListComboBox.SelectedItem, ComboBoxProjectItem)
            Dim ProjectInfos As String() = File.ReadAllLines(SelectedProject.ProjectFile)

            If ProjectInfos.Length > 4 Then
                Dim ProjectName As String = ProjectInfos(0).Split("="c)(1)
                Dim ProjectSubtitle As String = ProjectInfos(1).Split("="c)(1)
                Dim ProjectDirectory As String = ProjectInfos(2).Split("="c)(1)
                Dim ProjectFile As String = ProjectInfos(3).Split("="c)(1)
                Dim ProjectType As String = ProjectInfos(4).Split("="c)(1)

                If ProjectType = "APP" Then
                    Dim HomebrewInfos As String() = File.ReadAllLines(ProjectDirectory + "\icon.sys")
                    Dim HomebrewProjectEditor As New NewAppProject() With {.Title = "Editing project " + ProjectName + " - " + ProjectDirectory}

                    HomebrewProjectEditor.ProjectNameTextBox.Text = ProjectName
                    HomebrewProjectEditor.ProjectDirectoryTextBox.Text = ProjectDirectory
                    HomebrewProjectEditor.ProjectTitleTextBox.Text = HomebrewInfos(1).Split("="c)(1)
                    HomebrewProjectEditor.ProjectSubTitleTextBox.Text = ProjectSubtitle
                    HomebrewProjectEditor.ProjectSubTitleTextBox.Text = HomebrewInfos(2).Split("="c)(1)
                    HomebrewProjectEditor.ProjectUninstallMsgTextBox.Text = HomebrewInfos(15).Split("="c)(1)
                    HomebrewProjectEditor.ProjectELFFileTextBox.Text = ProjectFile

                    If File.Exists(ProjectDirectory + "\list.ico") Then
                        HomebrewProjectEditor.ProjectIconPathTextBox.Text = ProjectDirectory + "\list.ico"
                    End If

                    HomebrewProjectEditor.Show()
                ElseIf ProjectType = "GAME" Then
                    Dim GameType As String = ProjectInfos(6).Split("="c)(1)
                    Dim GameInfos As String() = File.ReadAllLines(ProjectDirectory + "\icon.sys")

                    Select Case GameType
                        Case "PS1"
                            Dim GameProjectEditor As New NewPS1GameProject() With {.Title = "Editing project " + ProjectName + " - " + ProjectDirectory}
                            GameProjectEditor.ProjectNameTextBox.Text = ProjectName
                            GameProjectEditor.ProjectDirectoryTextBox.Text = ProjectDirectory
                            GameProjectEditor.ProjectTitleTextBox.Text = GameInfos(1).Split("="c)(1)
                            GameProjectEditor.ProjectIDTextBox.Text = ProjectSubtitle
                            GameProjectEditor.ProjectIDTextBox.Text = GameInfos(2).Split("="c)(1)
                            GameProjectEditor.ProjectUninstallMsgTextBox.Text = GameInfos(15).Split("="c)(1)

                            GameProjectEditor.IMAGE0PathTextBox.Text = ProjectFile
                            GameProjectEditor.DISCSInfoTextBox.AppendText(Path.GetFileName(ProjectFile) + vbCrLf)

                            'Check for multiple images and tick MultiDiscCheckBox if we have at least 2 discs
                            For Each ProjectFileLine As String In ProjectInfos
                                If ProjectFileLine.StartsWith("IMAGE1=") Then
                                    GameProjectEditor.IMAGE1PathTextBox.Text = ProjectInfos(7).Split("="c)(1)
                                    GameProjectEditor.MultiDiscCheckBox.IsChecked = True
                                    GameProjectEditor.DISCSInfoTextBox.AppendText(Path.GetFileName(ProjectInfos(7).Split("="c)(1)) + vbCrLf)
                                End If
                                If ProjectFileLine.StartsWith("IMAGE2=") Then
                                    GameProjectEditor.IMAGE2PathTextBox.Text = ProjectInfos(8).Split("="c)(1)
                                    GameProjectEditor.DISCSInfoTextBox.AppendText(Path.GetFileName(ProjectInfos(8).Split("="c)(1)) + vbCrLf)
                                End If
                                If ProjectFileLine.StartsWith("IMAGE3=") Then
                                    GameProjectEditor.IMAGE3PathTextBox.Text = ProjectInfos(9).Split("="c)(1)
                                    GameProjectEditor.DISCSInfoTextBox.AppendText(Path.GetFileName(ProjectInfos(9).Split("="c)(1)))
                                End If
                            Next

                            If File.Exists(ProjectDirectory + "\list.ico") Then
                                GameProjectEditor.ProjectIconPathTextBox.Text = ProjectDirectory + "\list.ico"
                            End If

                            GameProjectEditor.Show()
                        Case "PS2"
                            Dim GameProjectEditor As New NewGameProject() With {.Title = "Editing project " + ProjectName + " - " + ProjectDirectory}
                            GameProjectEditor.ProjectNameTextBox.Text = ProjectName
                            GameProjectEditor.ProjectDirectoryTextBox.Text = ProjectDirectory
                            GameProjectEditor.ProjectTitleTextBox.Text = GameInfos(1).Split("="c)(1)
                            GameProjectEditor.ProjectIDTextBox.Text = ProjectSubtitle
                            GameProjectEditor.ProjectIDTextBox.Text = GameInfos(2).Split("="c)(1)
                            GameProjectEditor.ProjectUninstallMsgTextBox.Text = GameInfos(15).Split("="c)(1)
                            GameProjectEditor.ProjectISOFileTextBox.Text = ProjectFile

                            If File.Exists(ProjectDirectory + "\list.ico") Then
                                GameProjectEditor.ProjectIconPathTextBox.Text = ProjectDirectory + "\list.ico"
                            End If

                            GameProjectEditor.Show()
                    End Select
                End If
            Else
                MsgBox("A broken project has been detected: " + SelectedProject.ProjectFile + vbCrLf + vbCrLf + "It's recommended to remove this project and to re-create it.", MsgBoxStyle.Critical, "Error")
            End If
        End If
    End Sub

    Private Sub DeleteProjectButton_Click(sender As Object, e As RoutedEventArgs) Handles DeleteProjectButton.Click
        If ProjectListComboBox.SelectedItem IsNot Nothing Then
            Dim SelectedProject As ComboBoxProjectItem = CType(ProjectListComboBox.SelectedItem, ComboBoxProjectItem)
            If File.Exists(SelectedProject.ProjectFile) Then
                File.Delete(SelectedProject.ProjectFile)
                ProjectListComboBox.Items.Remove(SelectedProject)
                ReloadProjects()
            End If
        End If
    End Sub

    Private Sub PrepareProjectButton_Click(sender As Object, e As RoutedEventArgs) Handles PrepareProjectButton.Click
        If ProjectListComboBox.SelectedItem IsNot Nothing Then
            Dim SelectedProject As ComboBoxProjectItem = CType(ProjectListComboBox.SelectedItem, ComboBoxProjectItem)
            If File.ReadAllLines(SelectedProject.ProjectFile).Length > 5 Then
                Dim ProjectDIR As String = File.ReadAllLines(SelectedProject.ProjectFile)(2).Split("="c)(1)
                Dim SignedStatus As String = File.ReadAllLines(SelectedProject.ProjectFile)(5).Split("="c)(1)
                Dim SignedELF As Boolean = False

                'Check if KELF already exists
                If File.Exists(ProjectDIR + "\EXECUTE.KELF") Or File.Exists(ProjectDIR + "\boot.elf") Or File.Exists(ProjectDIR + "\boot.kelf") Then SignedELF = True

                If SignedStatus = "TRUE" AndAlso SignedELF = True Then
                    MsgBox("Your Project doesn't need to be prepared again.", MsgBoxStyle.Information)
                Else
                    Dim ProjectELForISO As String = File.ReadAllLines(SelectedProject.ProjectFile)(3).Split("="c)(1)
                    Dim ProjectTYPE As String = File.ReadAllLines(SelectedProject.ProjectFile)(4).Split("="c)(1)

                    If ProjectTYPE = "APP" Then
                        'Wrap the application ELF as EXECUTE.KELF
                        Dim WrapProcess As New Process()
                        WrapProcess.StartInfo.FileName = My.Computer.FileSystem.CurrentDirectory + "\Tools\SCEDoormat_NoME.exe"
                        WrapProcess.StartInfo.Arguments = """" + ProjectELForISO + """ " + ProjectDIR + "\EXECUTE.KELF"
                        WrapProcess.StartInfo.CreateNoWindow = True
                        WrapProcess.Start()
                        WrapProcess.WaitForExit()

                        'Mark project as SIGNED
                        Dim ProjectConfigFileLines() As String = File.ReadAllLines(SelectedProject.ProjectFile)
                        ProjectConfigFileLines(5) = "SIGNED=TRUE"
                        File.WriteAllLines(SelectedProject.ProjectFile, ProjectConfigFileLines)

                        MsgBox("Homebrew Project prepared with success !" + vbCrLf + "You can now proceed with the installation on the PSX.", MsgBoxStyle.Information, "Success")
                        Activate()
                    Else

                        'PS1 games get POPSTARTER and PS2 games get OPL-Launcher
                        Dim GameType As String = File.ReadAllLines(SelectedProject.ProjectFile)(6).Split("="c)(1)

                        Select Case GameType
                            Case "PS1"
                                'Copy included POPSTARTER to project folder
                                If File.Exists(My.Computer.FileSystem.CurrentDirectory + "\Tools\POPSTARTER.KELF") Then
                                    File.Copy(My.Computer.FileSystem.CurrentDirectory + "\Tools\POPSTARTER.KELF", ProjectDIR + "\EXECUTE.KELF", True) 'Save as EXECUTE.KELF
                                Else
                                    MsgBox("POPSTARTER.KELF is missing in the Tools directory.", MsgBoxStyle.Critical, "Error setting up the project")
                                End If
                            Case "PS2"
                                'Copy included OPL-Launcher to project folder
                                If File.Exists(My.Computer.FileSystem.CurrentDirectory + "\Tools\EXECUTE.KELF") Then
                                    File.Copy(My.Computer.FileSystem.CurrentDirectory + "\Tools\EXECUTE.KELF", ProjectDIR + "\EXECUTE.KELF", True)
                                Else
                                    'OPL-Launcher not found...
                                    Dim HomebrewELF As String = ""

                                    HomebrewELF = InputBox("OPL-Launcher has been deleted from the Tools folder." + vbCrLf + "Please enter the full path to the .elf file or leave the URL to download OPL-Launcher.",
                                                               "Missing file",
                                                               "https://github.com/ps2homebrew/OPL-Launcher/releases/download/latest/OPL-Launcher.elf")

                                    If Not String.IsNullOrEmpty(HomebrewELF) Then
                                        If HomebrewELF = "https://github.com/ps2homebrew/OPL-Launcher/releases/download/latest/OPL-Launcher.elf" Then
                                            'Download latest OPL-Launcher
                                            ContentDownloader.DownloadFile("https://github.com/ps2homebrew/OPL-Launcher/releases/download/latest/OPL-Launcher.elf", My.Computer.FileSystem.CurrentDirectory + "\Tools\OPL-Launcher.elf")
                                        End If
                                    Else
                                        MsgBox("Not valid file provided, aborting ...", MsgBoxStyle.Exclamation, "Aborting")
                                        Exit Sub
                                    End If

                                    'Wrap OPL-Launcher as EXECUTE.KELF
                                    Dim WrapProcess As New Process()
                                    WrapProcess.StartInfo.FileName = My.Computer.FileSystem.CurrentDirectory + "\Tools\SCEDoormat_NoME.exe"
                                    WrapProcess.StartInfo.Arguments = """" + My.Computer.FileSystem.CurrentDirectory + "\Tools\OPL-Launcher.elf"" """ + ProjectDIR + "\EXECUTE.KELF"""
                                    WrapProcess.StartInfo.CreateNoWindow = True
                                    WrapProcess.Start()
                                    WrapProcess.WaitForExit()
                                End If
                        End Select

                        'Mark project as SIGNED
                        Dim ProjectConfigFileLines() As String = File.ReadAllLines(SelectedProject.ProjectFile)
                        ProjectConfigFileLines(5) = "SIGNED=TRUE"
                        File.WriteAllLines(SelectedProject.ProjectFile, ProjectConfigFileLines)

                        MsgBox("Game Project is now prepared !" + vbCrLf + "You can now proceed with the installation on the PSX.", MsgBoxStyle.Information, "Success")
                        Activate()
                    End If
                End If

                ReloadProjects()
            Else
                MsgBox("A broken project has been detected: " + SelectedProject.ProjectFile + vbCrLf + vbCrLf + "It's recommended to remove this project and to re-create it.", MsgBoxStyle.Critical, "Error")
            End If
        End If
    End Sub

    Private Sub InstallProjectButton_Click(sender As Object, e As RoutedEventArgs) Handles InstallProjectButton.Click
        If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected Then
            MsgBox("Please connect to the PSX first.", MsgBoxStyle.Information)
        Else
            If PreparedProjectsComboBox.SelectedItem IsNot Nothing Then
                Dim SelectedProject As ComboBoxProjectItem = CType(PreparedProjectsComboBox.SelectedItem, ComboBoxProjectItem)
                If File.Exists(SelectedProject.ProjectFile) Then
                    If File.ReadAllLines(SelectedProject.ProjectFile).Length > 6 Then
                        Dim ProjectTitle As String = File.ReadAllLines(SelectedProject.ProjectFile)(0).Split("="c)(1)
                        If MsgBox("Do you really want to install " + ProjectTitle + " on your PSX ?", MsgBoxStyle.YesNo, "Please confirm") = MsgBoxResult.Yes Then

                            'Identify project type
                            Dim ProjectType As String = File.ReadAllLines(SelectedProject.ProjectFile)(4).Split("="c)(1)
                            Dim NewInstallWindow As New InstallWindow() With {.ProjectToInstall = SelectedProject, .MountedDrive = StorageBackend.MountedDrive, .StorageBackend = StorageBackend, .Title = "Installing " + ProjectTitle}

                            If ProjectType = "APP" Then
                                NewInstallWindow.InstallStatus = "Installing Homebrew, please wait..."
                                NewInstallWindow.InstallationProgressBar.IsIndeterminate = True
                                NewInstallWindow.ShowDialog()
                            ElseIf ProjectType = "GAME" Then
                                Dim GameType As String = File.ReadAllLines(SelectedProject.ProjectFile)(6).Split("="c)(1)

                                Select Case GameType
                                    Case "PS1"
                                        NewInstallWindow.InstallStatus = "Installing PS1 Game, do not close when it freezes or hangs."
                                        NewInstallWindow.InstallationProgressBar.IsIndeterminate = True
                                        NewInstallWindow.InstallForPS1 = True
                                    Case "PS2"
                                        NewInstallWindow.InstallStatus = "Installing PS2 Game, please wait..."
                                        NewInstallWindow.InstallForPS2 = True
                                End Select

                                NewInstallWindow.ShowDialog()
                            End If

                        Else
                            MsgBox("Installation aborted.", MsgBoxStyle.Information, "Aborted")
                        End If
                    Else
                        MsgBox("A broken project has been detected: " + SelectedProject.ProjectFile + vbCrLf + vbCrLf + "It's recommended to remove this project and to re-create it.", MsgBoxStyle.Critical, "Error")
                    End If
                Else
                    MsgBox("Could not find the selected project: " + SelectedProject.ProjectFile, MsgBoxStyle.Critical, "Error")
                End If
            Else
                MsgBox("No project selected.", MsgBoxStyle.Critical, "Error")
            End If
        End If
    End Sub

#End Region

End Class

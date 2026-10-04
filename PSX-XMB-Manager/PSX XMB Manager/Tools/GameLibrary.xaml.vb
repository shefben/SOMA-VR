Imports System.ComponentModel
Imports System.IO
Imports System.Threading
Imports System.Windows.Forms
Imports PSX_XMB_Manager.Structs

Public Class GameLibrary

    Public MountedDrive As MountedPSXDrive
    Public StorageBackend As IPSXStorageBackend

    Dim WithEvents GameLoaderWorker As New BackgroundWorker() With {.WorkerReportsProgress = True}
    Dim WithEvents PSXDatacenterBrowser As New WebBrowser() With {.ScriptErrorsSuppressed = True}
    Dim WithEvents NewLoadingWindow As New SyncWindow() With {.Title = "Loading PS2 files", .ShowActivated = True, .WindowStartupLocation = WindowStartupLocation.CenterScreen}

    'Used for game infos and covers
    Dim URLs As New List(Of String)
    Dim CurrentURL As Integer = 0

    Dim ISOCount As Integer = 0
    Dim GamePartitionsCount As Integer = 0
    Dim Partitions As New List(Of Partition)
    Dim GamePartitions As New List(Of GamePartition)

    Public SelectedGameToModify As PS2Game

    'Selected game context menu (PC)
    Dim WithEvents PCPS2GamesContextMenu As New Controls.ContextMenu()
    Dim WithEvents CreateProjectMenuItem As New Controls.MenuItem() With {.Header = "Create a game project", .Icon = New Controls.Image() With {.Source = New BitmapImage(New Uri("/Images/copy-icon.png", UriKind.Relative))}}

    'Selected game context menu (PSX)
    Dim WithEvents PSXPS2GamesContextMenu As New Controls.ContextMenu()
    Dim WithEvents ModifyPartitionMenuItem As New Controls.MenuItem() With {.Header = "Modify game partition", .Icon = New Controls.Image() With {.Source = New BitmapImage(New Uri("/Images/send-icon.png", UriKind.Relative))}}
    Dim WithEvents RemoveMenuItem As New Controls.MenuItem() With {.Header = "Remove", .Icon = New Controls.Image() With {.Source = New BitmapImage(New Uri("/Images/send-icon.png", UriKind.Relative))}}

    Private Sub GameLibrary_Loaded(sender As Object, e As RoutedEventArgs) Handles Me.Loaded
        'ContextMenu for games on stored on PC
        PCPS2GamesContextMenu.Items.Add(CreateProjectMenuItem)

        'ContextMenu for games on PSX HDD
        PSXPS2GamesContextMenu.Items.Add(ModifyPartitionMenuItem)
        PSXPS2GamesContextMenu.Items.Add(RemoveMenuItem)

        If StorageBackend IsNot Nothing Then AddHandler StorageBackend.MountRemoved, AddressOf StorageBackend_MountRemoved
    End Sub

    Private Sub GameLibrary_Closed(sender As Object, e As EventArgs) Handles Me.Closed
        If StorageBackend IsNot Nothing Then RemoveHandler StorageBackend.MountRemoved, AddressOf StorageBackend_MountRemoved
    End Sub

    Private Shared Sub ShowError(ex As Exception, title As String)
        Dim backendError As StorageBackendException = TryCast(ex, StorageBackendException)
        MsgBox(If(backendError IsNot Nothing, BackendErrorCodes.FormatForUser(backendError), ex.Message), MsgBoxStyle.Critical, title)
    End Sub

    Private Shared Sub ShowToolFailure(tool As String, result As ProcessResult, title As String)
        Dim reason As String = If(result.TimedOut, " timed out.", If(result.Cancelled, " was cancelled.", " failed with exit code " + result.ExitCode.ToString() + "."))
        MsgBox(tool + reason + vbCrLf + vbCrLf + result.CombinedOutput.Trim().Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation, title)
    End Sub

#Region "Game Loader"

    Public Function GetGameID(GameISO As String) As String
        Dim GameID As String = ""

        Using SevenZip As New Process()
            SevenZip.StartInfo.FileName = My.Computer.FileSystem.CurrentDirectory + "\Tools\7z.exe"
            SevenZip.StartInfo.Arguments = "l -ba """ + GameISO + """"
            SevenZip.StartInfo.RedirectStandardOutput = True
            SevenZip.StartInfo.UseShellExecute = False
            SevenZip.StartInfo.CreateNoWindow = True
            SevenZip.Start()

            'Read the output
            Dim OutputReader As StreamReader = SevenZip.StandardOutput
            Dim ProcessOutput As String() = OutputReader.ReadToEnd().Split(New String() {vbCrLf}, StringSplitOptions.None)

            If ProcessOutput.Length > 1 Then
                For Each Line As String In ProcessOutput
                    If Line.Contains("SLES_") Or Line.Contains("SLUS_") Or Line.Contains("SCES_") Or Line.Contains("SCUS_") Or Line.Contains("SLPS_") Or Line.Contains("SCCS_") Or Line.Contains("SLPM_") Or Line.Contains("SLKA_") Then
                        If Line.Contains("Volume:") Then 'ID found in the ISO Header
                            If Line.Split(New String() {"Volume: "}, StringSplitOptions.RemoveEmptyEntries).Length > 1 Then
                                GameID = Line.Split(New String() {"Volume: "}, StringSplitOptions.RemoveEmptyEntries)(1)
                                Exit For
                            End If
                        Else 'ID found in the ISO files
                            If String.Join(" ", Line.Split(New Char() {}, StringSplitOptions.RemoveEmptyEntries)).Split(" "c).Length > 5 Then
                                GameID = String.Join(" ", Line.Split(New Char() {}, StringSplitOptions.RemoveEmptyEntries)).Split(" "c)(5).Trim()
                                Exit For
                            End If
                        End If
                    End If
                Next
            End If
        End Using

        If String.IsNullOrEmpty(GameID) Then
            Return "ID not found"
        Else
            Return GameID
        End If
    End Function

    Private Sub GameLoaderWorker_DoWork(sender As Object, e As DoWorkEventArgs) Handles GameLoaderWorker.DoWork
        For Each GameISO In Directory.EnumerateFiles(e.Argument.ToString(), "*.iso", SearchOption.AllDirectories)

            Dim NewPS2Game As New PS2Game()
            Dim GameID As String = GetGameID(GameISO)
            Dim PS2ISOFileInfo As New FileInfo(GameISO)

            If GameID = "ID not found" Then

                NewPS2Game.GameTitle = "Unknown PS2 game"
                NewPS2Game.GameFilePath = GameISO
                NewPS2Game.GameSize = FormatNumber(PS2ISOFileInfo.Length / 1073741824, 2) + " GB"
                NewPS2Game.GameID = "Unknown ID"

                'Update progress
                Dispatcher.BeginInvoke(Sub()
                                           NewLoadingWindow.LoadProgressBar.Value += 1
                                           NewLoadingWindow.LoadStatusTextBlock.Text = "Loading ISO " + NewLoadingWindow.LoadProgressBar.Value.ToString() + " of " + ISOCount.ToString()
                                       End Sub)

                'Add to the ListView
                If GamesListView.Dispatcher.CheckAccess() = False Then
                    GamesListView.Dispatcher.BeginInvoke(Sub() GamesListView.Items.Add(NewPS2Game))
                Else
                    GamesListView.Items.Add(NewPS2Game)
                End If

            Else
                GameID = GameID.Replace(".", "").Replace("_", "-").Trim()

                NewPS2Game.GameSize = FormatNumber(PS2ISOFileInfo.Length / 1073741824, 2) + " GB"
                NewPS2Game.GameID = GameID
                NewPS2Game.GameFilePath = GameISO

                'Update progress
                Dispatcher.BeginInvoke(Sub()
                                           NewLoadingWindow.LoadProgressBar.Value += 1
                                           NewLoadingWindow.LoadStatusTextBlock.Text = "Loading ISO " + NewLoadingWindow.LoadProgressBar.Value.ToString() + " of " + ISOCount.ToString()
                                       End Sub)

                If Utils.IsURLValid("https://psxdatacenter.com/psx2/games2/" + GameID + ".html") Then
                    URLs.Add("https://psxdatacenter.com/psx2/games2/" + GameID + ".html")
                Else
                    If Utils.IsURLValid("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS2/" + GameID + ".jpg") Then
                        Dispatcher.BeginInvoke(Sub()
                                                   Dim TempBitmapImage = New BitmapImage()
                                                   TempBitmapImage.BeginInit()
                                                   TempBitmapImage.CacheOption = BitmapCacheOption.OnLoad
                                                   TempBitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache
                                                   TempBitmapImage.UriSource = New Uri("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS2/" + GameID + ".jpg", UriKind.RelativeOrAbsolute)
                                                   TempBitmapImage.EndInit()
                                                   NewPS2Game.GameCoverSource = TempBitmapImage
                                               End Sub)
                        NewPS2Game.GameTitle = GetPS2GameTitleFromDatabaseList(GameID)
                    Else
                        Dispatcher.BeginInvoke(Sub()
                                                   NewPS2Game.GameCoverSource = New BitmapImage(New Uri("/Images/blankcover.png", UriKind.RelativeOrAbsolute))
                                               End Sub)
                        NewPS2Game.GameTitle = GetPS2GameTitleFromDatabaseList(GameID)
                    End If
                End If

                'Add to the ListView
                If GamesListView.Dispatcher.CheckAccess() = False Then
                    GamesListView.Dispatcher.BeginInvoke(Sub() GamesListView.Items.Add(NewPS2Game))
                Else
                    GamesListView.Items.Add(NewPS2Game)
                End If
            End If
        Next
    End Sub

    Private Sub GameLoaderWorker_RunWorkerCompleted(sender As Object, e As RunWorkerCompletedEventArgs) Handles GameLoaderWorker.RunWorkerCompleted
        If URLs.Count > 0 Then
            NewLoadingWindow.LoadStatusTextBlock.Text = "Getting " + URLs.Count.ToString() + " available infos with missing covers"
            NewLoadingWindow.LoadProgressBar.Value = 0
            NewLoadingWindow.LoadProgressBar.Maximum = URLs.Count
            GetGameCovers()
        Else
            NewLoadingWindow.Close()
            GamesListView.Items.Refresh()
        End If
    End Sub

    Private Sub GetGameCovers()
        PSXDatacenterBrowser.Navigate(URLs.Item(0))
    End Sub

    Private Sub PSXDatacenterBrowser_DocumentCompleted(sender As Object, e As WebBrowserDocumentCompletedEventArgs) Handles PSXDatacenterBrowser.DocumentCompleted
        RemoveHandler PSXDatacenterBrowser.DocumentCompleted, AddressOf PSXDatacenterBrowser_DocumentCompleted

        Dim GameTitle As String = ""
        Dim GameID As String = ""
        Dim GameRegion As String = ""
        Dim GameGenre As String = ""
        Dim GameDeveloper As String = ""
        Dim GamePublisher As String = ""
        Dim GameReleaseDate As String = ""
        Dim GameDescription As String = ""
        Dim GameCoverURL As String = ""
        Dim GameCoverImage As ImageSource = Nothing
        Dim GamePublisherWebsite As String = ""

        'Get game infos
        Dim InfoRows As HtmlElementCollection = PSXDatacenterBrowser.Document.GetElementsByTagName("tr")
        If InfoRows.Count > 11 Then

            'Game Title
            If Not String.IsNullOrEmpty(InfoRows.Item(4).InnerText) Then
                If InfoRows.Item(4).InnerText.Split(New String() {"OFFICIAL TITLE "}, StringSplitOptions.RemoveEmptyEntries).Length > 0 Then
                    GameTitle = InfoRows.Item(4).InnerText.Split(New String() {"OFFICIAL TITLE "}, StringSplitOptions.RemoveEmptyEntries)(0)
                End If
            End If

            'Game ID
            If Not String.IsNullOrEmpty(InfoRows.Item(6).InnerText) Then
                If InfoRows.Item(6).InnerText.Split(New String() {"SERIAL NUMBER(S) "}, StringSplitOptions.RemoveEmptyEntries).Length > 0 Then
                    GameID = InfoRows.Item(6).InnerText.Split(New String() {"SERIAL NUMBER(S) "}, StringSplitOptions.RemoveEmptyEntries)(0)
                End If
            End If

            'Region
            If Not String.IsNullOrEmpty(InfoRows.Item(7).InnerText) Then
                If InfoRows.Item(7).InnerText.Split(New String() {"REGION "}, StringSplitOptions.RemoveEmptyEntries).Length > 0 Then
                    Dim Region As String = InfoRows.Item(7).InnerText.Split(New String() {"REGION "}, StringSplitOptions.RemoveEmptyEntries)(0)
                    Select Case Region
                        Case "PAL"
                            GameRegion = "Europe"
                        Case "NTSC-U"
                            GameRegion = "US"
                        Case "NTSC-J"
                            GameRegion = "Japan"
                    End Select
                End If
            End If

            'Genre
            If Not String.IsNullOrEmpty(InfoRows.Item(8).InnerText) Then
                If InfoRows.Item(8).InnerText.Split(New String() {"GENRE / STYLE "}, StringSplitOptions.RemoveEmptyEntries).Length > 0 Then
                    GameGenre = InfoRows.Item(8).InnerText.Split(New String() {"GENRE / STYLE "}, StringSplitOptions.RemoveEmptyEntries)(0)
                End If
            End If

            'Developer
            If Not String.IsNullOrEmpty(InfoRows.Item(9).InnerText) Then
                If InfoRows.Item(9).InnerText.Split(New String() {"GENRE / STYLE "}, StringSplitOptions.RemoveEmptyEntries).Length > 0 Then
                    GameDeveloper = InfoRows.Item(9).InnerText.Split(New String() {"DEVELOPER "}, StringSplitOptions.RemoveEmptyEntries)(0)
                End If
            End If

            'Publisher
            If Not String.IsNullOrEmpty(InfoRows.Item(10).InnerText) Then
                If InfoRows.Item(10).InnerText.Split(New String() {"GENRE / STYLE "}, StringSplitOptions.RemoveEmptyEntries).Length > 0 Then
                    GamePublisher = InfoRows.Item(10).InnerText.Split(New String() {"PUBLISHER "}, StringSplitOptions.RemoveEmptyEntries)(0)

                    If GamePublisher.Contains("2K") Then
                        GamePublisherWebsite = "https://2k.com/"
                    ElseIf GamePublisher.Contains("Activision") Then
                        GamePublisherWebsite = "https://www.activision.com/"
                    ElseIf GamePublisher.Contains("Bandai") Then
                        GamePublisherWebsite = "http://www.bandai.com/"
                    ElseIf GamePublisher.Contains("Capcom") Then
                        GamePublisherWebsite = "http://www.capcom.com/"
                    ElseIf GamePublisher.Contains("Electronic Arts") Then
                        GamePublisherWebsite = "http://ea.com/"
                    ElseIf GamePublisher.Contains("EA Sports") Then
                        GamePublisherWebsite = "https://www.easports.com/"
                    ElseIf GamePublisher.Contains("Konami") Then
                        GamePublisherWebsite = "https://www.konami.com/"
                    ElseIf GamePublisher.Contains("Rockstar Games") Then
                        GamePublisherWebsite = "https://www.rockstargames.com/"
                    ElseIf GamePublisher.Contains("Sega") Then
                        GamePublisherWebsite = "http://sega.com/"
                    ElseIf GamePublisher.Contains("Sony Computer Entertainment") Then
                        GamePublisherWebsite = "https://www.sie.com/en/index.html"
                    ElseIf GamePublisher.Contains("THQ") Then
                        GamePublisherWebsite = "https://www.thqnordic.com/"
                    ElseIf GamePublisher.Contains("Ubisoft") Then
                        GamePublisherWebsite = "https://www.ubisoft.com/"
                    End If
                End If
            End If

            'Release Date
            If Not String.IsNullOrEmpty(InfoRows.Item(11).InnerText) Then
                If InfoRows.Item(11).InnerText.Split(New String() {"DATE RELEASED "}, StringSplitOptions.RemoveEmptyEntries).Length > 0 Then
                    GameReleaseDate = InfoRows.Item(11).InnerText.Split(New String() {"DATE RELEASED "}, StringSplitOptions.RemoveEmptyEntries)(0)
                End If
            End If

        End If

        'Get the game cover
        If PSXDatacenterBrowser.Document.GetElementById("table2") IsNot Nothing Then

            If PSXDatacenterBrowser.Document.GetElementById("table2").GetElementsByTagName("img") IsNot Nothing Then
                If PSXDatacenterBrowser.Document.GetElementById("table2").GetElementsByTagName("img").Count > 0 Then
                    GameCoverURL = PSXDatacenterBrowser.Document.GetElementById("table2").GetElementsByTagName("img")(1).GetAttribute("src")
                End If
            End If

            If Not String.IsNullOrEmpty(GameCoverURL) Then
                Dim TempBitmapImage = New BitmapImage()
                TempBitmapImage.BeginInit()
                TempBitmapImage.CacheOption = BitmapCacheOption.OnLoad
                TempBitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache
                TempBitmapImage.UriSource = New Uri(PSXDatacenterBrowser.Document.GetElementById("table2").GetElementsByTagName("img")(1).GetAttribute("src"), UriKind.RelativeOrAbsolute)
                TempBitmapImage.EndInit()
                GameCoverImage = TempBitmapImage
            End If

        ElseIf Utils.IsURLValid("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS2/" + GameID + ".jpg") Then
            Dispatcher.BeginInvoke(Sub()
                                       Dim TempBitmapImage = New BitmapImage()
                                       TempBitmapImage.BeginInit()
                                       TempBitmapImage.CacheOption = BitmapCacheOption.OnLoad
                                       TempBitmapImage.CreateOptions = BitmapCreateOptions.IgnoreImageCache
                                       TempBitmapImage.UriSource = New Uri("https://raw.githubusercontent.com/SvenGDK/PSMT-Covers/main/PS2/" + GameID + ".jpg", UriKind.RelativeOrAbsolute)
                                       TempBitmapImage.EndInit()
                                       GameCoverImage = TempBitmapImage
                                   End Sub)
        End If

        'Get the game description
        If PSXDatacenterBrowser.Document.GetElementById("table16") IsNot Nothing Then
            If PSXDatacenterBrowser.Document.GetElementById("table16").GetElementsByTagName("tr").Count > 0 Then
                GameDescription = PSXDatacenterBrowser.Document.GetElementById("table16").GetElementsByTagName("tr")(0).InnerText
            End If
        End If

        'Add the infos to the game
        If Not String.IsNullOrEmpty(GameID) Then
            For Each Game In GamesListView.Items
                Dim FoundGame As PS2Game = CType(Game, PS2Game)
                If FoundGame.GameID.Contains(GameID) Or FoundGame.GameID = GameID Then
                    FoundGame.GameTitle = GameTitle
                    FoundGame.GameRegion = GameRegion
                    FoundGame.GameDescription = GameDescription
                    FoundGame.GameGenre = GameGenre
                    FoundGame.GameDeveloper = GameDeveloper
                    FoundGame.GamePublisher = GamePublisher
                    FoundGame.GameWebsite = GamePublisherWebsite
                    FoundGame.GameReleaseDate = GameReleaseDate
                    FoundGame.GameCoverURL = GameCoverURL
                    FoundGame.GameCoverSource = GameCoverImage
                    Exit For
                End If
            Next
        End If

        'Continue
        AddHandler PSXDatacenterBrowser.DocumentCompleted, AddressOf PSXDatacenterBrowser_DocumentCompleted

        'Resume until all URLs are done
        If CurrentURL < URLs.Count Then
            PSXDatacenterBrowser.Navigate(URLs.Item(CurrentURL))
            CurrentURL += 1
            NewLoadingWindow.LoadProgressBar.Value = CurrentURL
        Else
            CurrentURL = 0
            URLs.Clear()
            NewLoadingWindow.Close()
            GamesListView.Items.Refresh()
        End If
    End Sub

    ''' <summary>Lists the games of the PSX HDD ("hdl_dump hdl_toc") and then loads their infos and covers.</summary>
    Private Async Function LoadGamePartitionsAsync() As Task
        Dim Result As ProcessResult = Nothing
        Try
            Result = Await StorageBackend.RunHdlDumpAsync({"hdl_toc", StorageBackend.MountedDrive.HDLDriveName}, Nothing, CancellationToken.None)
        Catch ex As Exception
            NewLoadingWindow.Close()
            ShowError(ex, "Could not read the games list")
            Return
        End Try

        If Not Result.Succeeded Then
            NewLoadingWindow.Close()
            ShowToolFailure("hdl_dump hdl_toc", Result, "Could not read the games list")
            Return
        End If

        Dim FoundGames As New List(Of PS2Game)
        Dim GamesWithoutInfos As New HashSet(Of PS2Game)
        Dim AvailableSpaceInGB As Double = -1

        'Checking which game infos exist goes over the network, so it stays off the UI thread
        Await Task.Run(Sub()
                           For Each Line As String In OutputText.SplitLines(Result.StandardOutput)
                               If Line.StartsWith("DVD") Or Line.StartsWith("CD") Then 'Game found
                                   Dim GamePart As GamePartition = PartitionManager.ParseGamePartition(Line)
                                   Dim GameID As String = GamePart.Startup.Replace("_", "-").Replace(".", "")
                                   Dim NewPS2Game As New PS2Game() With {.GameSize = GamePart.Size, .GameID = GameID}

                                   GamePartitions.Add(GamePart)

                                   If Utils.IsURLValid("https://psxdatacenter.com/psx2/games2/" + GameID + ".html") Then
                                       URLs.Add("https://psxdatacenter.com/psx2/games2/" + GameID + ".html")
                                   Else
                                       NewPS2Game.GameTitle = GetPS2GameTitleFromDatabaseList(GameID)
                                       GamesWithoutInfos.Add(NewPS2Game)
                                   End If

                                   FoundGames.Add(NewPS2Game)

                               ElseIf Line.StartsWith("total") Then 'Last line of hdl_dump hdl_toc
                                   Dim HDDSizes As String() = Line.Split({","}, StringSplitOptions.RemoveEmptyEntries)
                                   If HDDSizes.Length > 2 Then AvailableSpaceInGB = Utils.GetIntOnly(HDDSizes(2)) / 1024
                               End If
                           Next
                       End Sub)

        For Each FoundGame As PS2Game In FoundGames
            If GamesWithoutInfos.Contains(FoundGame) Then
                FoundGame.GameCoverSource = New BitmapImage(New Uri("/Images/blankcover.png", UriKind.RelativeOrAbsolute))
            End If
            GamesListView.Items.Add(FoundGame)
        Next
        GamePartitionsCount = FoundGames.Count

        If AvailableSpaceInGB >= 0 Then
            CurrentDirectoryTextBlock.Text = "PSX HDD" + " - Available: " + FormatNumber(AvailableSpaceInGB, 2) + " GB"

            'Left space indicator
            If AvailableSpaceInGB > 16 Then
                CurrentDirectoryTextBlock.Foreground = Brushes.Green
            ElseIf AvailableSpaceInGB >= 10 Then
                CurrentDirectoryTextBlock.Foreground = Brushes.Orange
            Else
                CurrentDirectoryTextBlock.Foreground = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FFC12249"), Color))
            End If
        End If

        TotalGamesTextBlock.Text = GamePartitionsCount.ToString() + " Games"

        'Load covers after getting the game list
        If URLs.Count > 0 Then
            NewLoadingWindow.LoadStatusTextBlock.Text = "Getting " + URLs.Count.ToString() + " available infos with covers."
            NewLoadingWindow.LoadProgressBar.IsIndeterminate = False
            NewLoadingWindow.LoadProgressBar.Value = 0
            NewLoadingWindow.LoadProgressBar.Maximum = URLs.Count
            NewLoadingWindow.Show()
            GetGameCovers()
        Else
            NewLoadingWindow.Close()
            GamesListView.Items.Refresh()
        End If
    End Function

    ''' <summary>Reads all partitions of the PSX HDD ("hdl_dump toc") into <see cref="Partitions"/>. Returns False after reporting an error.</summary>
    Private Async Function LoadPartitionsAsync() As Task(Of Boolean)
        Dim Result As ProcessResult = Await StorageBackend.RunHdlDumpAsync({"toc", StorageBackend.MountedDrive.HDLDriveName}, Nothing, CancellationToken.None)
        If Not Result.Succeeded Then
            ShowToolFailure("hdl_dump toc", Result, "Could not read the partition table")
            Return False
        End If

        Partitions.Clear()
        For Each Line As String In OutputText.SplitLines(Result.StandardOutput)
            If Line.StartsWith("0") Then
                Dim Columns As String() = Line.Split({" "}, StringSplitOptions.RemoveEmptyEntries)
                If Columns.Length >= 5 Then
                    Partitions.Add(New Partition() With {.Type = Columns(0), .Start = Columns(1), .Parts = Columns(2), .Size = Columns(3), .Name = Columns(4)})
                End If
            End If
        Next
        Return True
    End Function

    Public Function GetPS2GameTitleFromDatabaseList(GameID As String) As String
        Dim FoundGameTitle As String = ""
        GameID = GameID.Replace("-", "")

        For Each GameTitle As String In File.ReadLines(My.Computer.FileSystem.CurrentDirectory + "\Tools\ps2ids.txt")
            If GameTitle.Contains(GameID) Then
                If GameTitle.Split(";"c).Length > 1 Then
                    FoundGameTitle = GameTitle.Split(";"c)(1)
                    Exit For
                Else
                    FoundGameTitle = "Unknown PS2 game"
                    Exit For
                End If
            End If
        Next

        If String.IsNullOrEmpty(FoundGameTitle) Then
            Return "Unknown PS2 game"
        Else
            Return FoundGameTitle
        End If
    End Function

#End Region

#Region "Partition Mounting"

    ''' <summary>Closes the loading window once; closing an already closed window throws.</summary>
    Private Sub CloseLoadingWindow()
        If PresentationSource.FromVisual(NewLoadingWindow) IsNot Nothing Then NewLoadingWindow.Close()
    End Sub

    ''' <summary>Mounts the PP partition of <paramref name="Game"/> through the backend. Returns False after reporting an error.</summary>
    Private Async Function MountGamePartitionAsync(Game As PS2Game) As Task(Of Boolean)
        'Get all partitions to find the game's PP partition
        If Not Await LoadPartitionsAsync() Then Return False

        Dim GamePPPartitionIndex As Integer = Partitions.FindIndex(Function(Part) Part.Name.StartsWith("PP." + Game.GameID))
        If GamePPPartitionIndex < 0 Then
            CloseLoadingWindow()
            MsgBox("Could not mount the selected game.", MsgBoxStyle.Exclamation, "Game partition not found.")
            Return False
        End If

        Dim GamePPPartition As Partition = Partitions(GamePPPartitionIndex)
        NewLoadingWindow.LoadStatusTextBlock.Text = "Mounting partition with pfsfuse"

        'The backend waits until the mount is visible in Windows, so no extra delay is needed here
        Game.MountedPartition = Await StorageBackend.MountPfsPartitionAsync(GamePPPartition.Name, If(String.IsNullOrEmpty(Game.GameTitle), GamePPPartition.Name, Game.GameTitle), CancellationToken.None)
        Game.PartitionName = GamePPPartition.Name
        Return True
    End Function

    Private Sub OpenGamePartitionManager(Game As PS2Game)
        Dim NewGamePartitionManager As New GamePartitionManager With {.ShowActivated = True,
            .MountedDrive = StorageBackend.MountedDrive,
            .StorageBackend = StorageBackend,
            .MountedPartition = Game.MountedPartition,
            .AssociatedPartition = Game.PartitionName}
        NewGamePartitionManager.Show()
    End Sub

    ''' <summary>A partition was unmounted (by the game partition manager, another window or a disconnect).</summary>
    Private Sub StorageBackend_MountRemoved(sender As Object, Mount As PfsMountHandle)
        Dispatcher.BeginInvoke(Sub() RemoveMountedPartition(Mount.MountId))
    End Sub

    ''' <summary>Forgets the mount <paramref name="MountId"/> on every listed game that still references it.</summary>
    Public Sub RemoveMountedPartition(MountId As String)
        For Each Game In GamesListView.Items
            Dim FoundGame As PS2Game = TryCast(Game, PS2Game)
            If FoundGame IsNot Nothing AndAlso FoundGame.MountedPartition IsNot Nothing AndAlso FoundGame.MountedPartition.MountId = MountId Then
                FoundGame.MountedPartition = Nothing
            End If
        Next
    End Sub

    ''' <summary>Deletes the PP and the hidden __. partition of <paramref name="Game"/> with pfsshell.</summary>
    Private Async Function DeleteGamePartitionsAsync(Game As PS2Game) As Task
        'Get all partitions to find the PP & hidden partition of the game
        If Not Await LoadPartitionsAsync() Then Return

        Dim HiddenGamePartition As String = ""
        Dim GamePPPartition As String = ""

        For Each Part As Partition In Partitions
            If Part.Name.StartsWith("PP." + Game.GameID) Then
                GamePPPartition = Part.Name
            ElseIf Part.Name.StartsWith("__." + Game.GameID) Then
                HiddenGamePartition = Part.Name
            End If
        Next

        If String.IsNullOrEmpty(GamePPPartition) AndAlso String.IsNullOrEmpty(HiddenGamePartition) Then
            MsgBox("No partition of " + Game.GameID + " was found on the PSX HDD.", MsgBoxStyle.Exclamation, "Error")
            Return
        End If

        '
        'Partitions will be deleted separately in case there's no PP or hidden partition
        '
        For Each PartitionToDelete As String In {GamePPPartition, HiddenGamePartition}
            If String.IsNullOrEmpty(PartitionToDelete) Then Continue For

            'rmpart commands, sent straight to pfsshell
            Dim Commands As New List(Of String) From {
                "device " + StorageBackend.MountedDrive.DriveID,
                "rmpart " + PartitionToDelete,
                "exit"
            }

            Dim Result As ProcessResult = Await StorageBackend.RunPfsShellAsync(Commands, Nothing, CancellationToken.None)
            Dim PFSShellOutput As String = Result.CombinedOutput

            If PFSShellOutput.Contains("No such file or directory") OrElse Result.TimedOut OrElse Result.Cancelled Then
                MsgBox("There was an error while deleting the game partition. More details :" + vbCrLf + PFSShellOutput.Replace(vbLf, vbCrLf), MsgBoxStyle.Exclamation, "Error")
            End If
        Next
    End Function

#End Region

    Private Sub LoadGamesOnPCButton_Click(sender As Object, e As RoutedEventArgs) Handles LoadGamesOnPCButton.Click
        Dim FBD As New FolderBrowserDialog() With {.Description = "Please select a folder containing PS2 ISOs.", .ShowNewFolderButton = False}

        GameTitleTextBlock.Text = ""
        ReleaseDateTextBlock.Text = ""
        DeveloperTextBlock.Text = ""
        PublisherTextBlock.Text = ""
        GameSizeTextBlock.Text = ""
        WebsiteTextBlock.Text = ""
        GenreTextBlock.Text = ""
        RegionTextBlock.Text = ""
        GameIDTextBlock.Text = ""

        If FBD.ShowDialog() = Forms.DialogResult.OK Then
            If Not String.IsNullOrEmpty(FBD.SelectedPath) Then

                CurrentDirectoryTextBlock.Text = FBD.SelectedPath

                LoadGamesOnPCButton.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF004671"), Color))
                LoadPSXGamesButton.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
                ReloadButton.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))

                GamesListView.Items.Clear()
                URLs.Clear()
                ISOCount = Directory.EnumerateFiles(FBD.SelectedPath, "*.iso", SearchOption.AllDirectories).Count
                TotalGamesTextBlock.Text = ISOCount.ToString() + " Games"

                GamesListView.ContextMenu = Nothing
                GamesListView.ContextMenu = PCPS2GamesContextMenu

                NewLoadingWindow = New SyncWindow() With {.Title = "Loading PS2 files", .ShowActivated = True}
                NewLoadingWindow.LoadProgressBar.Maximum = ISOCount
                NewLoadingWindow.LoadStatusTextBlock.Text = "Loading file 1 of " + ISOCount.ToString()
                NewLoadingWindow.Show()

                GameLoaderWorker.RunWorkerAsync(FBD.SelectedPath)
            End If
        End If
    End Sub

    Private Async Sub LoadPSXGamesButton_Click(sender As Object, e As RoutedEventArgs) Handles LoadPSXGamesButton.Click

        GameTitleTextBlock.Text = ""
        ReleaseDateTextBlock.Text = ""
        DeveloperTextBlock.Text = ""
        PublisherTextBlock.Text = ""
        GameSizeTextBlock.Text = ""
        WebsiteTextBlock.Text = ""
        GenreTextBlock.Text = ""
        RegionTextBlock.Text = ""
        GameIDTextBlock.Text = ""
        GamePartitionsCount = 0

        If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected Then
            MsgBox("Please connect to the PSX HDD first.", MsgBoxStyle.Information)
        Else
            CurrentDirectoryTextBlock.Text = "PSX HDD"

            LoadGamesOnPCButton.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))
            LoadPSXGamesButton.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF004671"), Color))
            ReloadButton.Background = New SolidColorBrush(CType(ColorConverter.ConvertFromString("#FF00619C"), Color))

            GamesListView.ContextMenu = Nothing
            GamesListView.ContextMenu = PSXPS2GamesContextMenu

            Await ReloadPSXGamesAsync()
        End If
    End Sub

    Private Async Function ReloadPSXGamesAsync() As Task
        GamesListView.Items.Clear()
        URLs.Clear()
        GamePartitions.Clear()

        NewLoadingWindow = New SyncWindow() With {.Title = "Loading games on PSX HDD", .ShowActivated = True, .WindowStartupLocation = WindowStartupLocation.CenterScreen}
        NewLoadingWindow.LoadProgressBar.IsIndeterminate = True
        NewLoadingWindow.LoadStatusTextBlock.Text = "Please wait"
        NewLoadingWindow.Show()

        Await LoadGamePartitionsAsync()
    End Function

    Private Sub GamesListView_PreviewMouseWheel(sender As Object, e As MouseWheelEventArgs) Handles GamesListView.PreviewMouseWheel
        'The mouse wheel only scrolls vertically, this code allows scrolling horizontally
        Dim GamesListViewScrollViewer As ScrollViewer = TryCast(Utils.GetScrollViewer(GamesListView), ScrollViewer)

        If GamesListViewScrollViewer IsNot Nothing Then
            If e.Delta > 0 Then
                GamesListViewScrollViewer.ScrollToHorizontalOffset(GamesListViewScrollViewer.HorizontalOffset - 2)
            End If

            If e.Delta < 0 Then
                GamesListViewScrollViewer.ScrollToHorizontalOffset(GamesListViewScrollViewer.HorizontalOffset + 2)
            End If
        End If
    End Sub

    Private Sub GamesListView_SelectionChanged(sender As Object, e As SelectionChangedEventArgs) Handles GamesListView.SelectionChanged
        If GamesListView.SelectedItem IsNot Nothing Then
            Dim SelectedPS2Game As PS2Game = CType(GamesListView.SelectedItem, PS2Game)

            GameTitleTextBlock.Text = SelectedPS2Game.GameTitle
            ReleaseDateTextBlock.Text = SelectedPS2Game.GameReleaseDate
            DeveloperTextBlock.Text = SelectedPS2Game.GameDeveloper
            PublisherTextBlock.Text = SelectedPS2Game.GamePublisher
            GameSizeTextBlock.Text = SelectedPS2Game.GameSize
            WebsiteTextBlock.Text = SelectedPS2Game.GameWebsite
            GenreTextBlock.Text = SelectedPS2Game.GameGenre
            RegionTextBlock.Text = SelectedPS2Game.GameRegion
            GameIDTextBlock.Text = SelectedPS2Game.GameID

            Select Case SelectedPS2Game.GameRegion
                Case "Europe"
                    GameRegionImage.Source = New BitmapImage(New Uri("/Images/eu.png", UriKind.RelativeOrAbsolute))
                Case "US"
                    GameRegionImage.Source = New BitmapImage(New Uri("/Images/us.png", UriKind.RelativeOrAbsolute))
                Case "Japan"
                    GameRegionImage.Source = New BitmapImage(New Uri("/Images/jp.png", UriKind.RelativeOrAbsolute))
                Case Else
                    GameRegionImage.Source = Nothing
            End Select

        End If
    End Sub

    Private Sub CreateProjectMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles CreateProjectMenuItem.Click
        If GamesListView.SelectedItem IsNot Nothing Then
            Dim SelectedPS2Game As PS2Game = CType(GamesListView.SelectedItem, PS2Game)
            Dim GameProjectDirectory As String = SelectedPS2Game.GameTitle + " [" + SelectedPS2Game.GameID + "]"
            Dim NewGameProjectDirectory As String = My.Computer.FileSystem.CurrentDirectory + "\Projects\" + SelectedPS2Game.GameTitle + " [" + SelectedPS2Game.GameID + "]"

            'Create new game project directory
            If Not Directory.Exists(NewGameProjectDirectory) Then
                Directory.CreateDirectory(NewGameProjectDirectory)
            End If

            Dim NewGameProjectWindow As New NewGameProject() With {.ShowActivated = True}
            Dim NewGameEditor As New GameEditor() With {.ProjectDirectory = NewGameProjectDirectory, .Title = "Game Ressources Editor - " + NewGameProjectDirectory, .AutoSave = True}

            'Set project information
            NewGameProjectWindow.ProjectISOFileTextBox.Text = SelectedPS2Game.GameFilePath
            NewGameProjectWindow.ProjectNameTextBox.Text = SelectedPS2Game.GameTitle
            NewGameProjectWindow.ProjectDirectoryTextBox.Text = NewGameProjectDirectory
            NewGameProjectWindow.ProjectTitleTextBox.Text = SelectedPS2Game.GameTitle
            NewGameProjectWindow.ProjectIDTextBox.Text = SelectedPS2Game.GameID.Replace("-", "_").Insert(8, ".")
            NewGameProjectWindow.ProjectUninstallMsgTextBox.Text = "Do you want to uninstall this game ?"

            'Write Project settings to .CFG
            Using ProjectWriter As New StreamWriter(My.Computer.FileSystem.CurrentDirectory + "\Projects\" + SelectedPS2Game.GameTitle + ".CFG", False)
                ProjectWriter.WriteLine("TITLE=" + SelectedPS2Game.GameTitle)
                ProjectWriter.WriteLine("ID=" + SelectedPS2Game.GameID.Replace("-", "_").Insert(8, "."))
                ProjectWriter.WriteLine("DIR=" + NewGameProjectDirectory)
                ProjectWriter.WriteLine("ELForISO=" + SelectedPS2Game.GameFilePath)
                ProjectWriter.WriteLine("TYPE=GAME")
                ProjectWriter.WriteLine("SIGNED=FALSE")
                ProjectWriter.WriteLine("GAMETYPE=PS2")
            End Using

            'Write SYSTEM.CNF to project directory
            Using CNFWriter As New StreamWriter(NewGameProjectDirectory + "\SYSTEM.CNF", False)
                CNFWriter.WriteLine("BOOT2 = pfs:/EXECUTE.KELF") 'Loads EXECUTE.KELF
                CNFWriter.WriteLine("VER = 1.01")
                CNFWriter.WriteLine("VMODE = NTSC")
                CNFWriter.WriteLine("HDDUNITPOWER = NICHDD")
            End Using

            'Write icon.sys to project directory
            Using CNFWriter As New StreamWriter(NewGameProjectDirectory + "\icon.sys", False)
                CNFWriter.WriteLine("PS2X")
                CNFWriter.WriteLine("title0=" + SelectedPS2Game.GameTitle)
                CNFWriter.WriteLine("title1=" + SelectedPS2Game.GameID)
                CNFWriter.WriteLine("bgcola=0")
                CNFWriter.WriteLine("bgcol0=0,0,0")
                CNFWriter.WriteLine("bgcol1=0,0,0")
                CNFWriter.WriteLine("bgcol2=0,0,0")
                CNFWriter.WriteLine("bgcol3=0,0,0")
                CNFWriter.WriteLine("lightdir0=1.0,-1.0,1.0")
                CNFWriter.WriteLine("lightdir1=-1.0,1.0,-1.0")
                CNFWriter.WriteLine("lightdir2=0.0,0.0,0.0")
                CNFWriter.WriteLine("lightcolamb=64,64,64")
                CNFWriter.WriteLine("lightcol0=64,64,64")
                CNFWriter.WriteLine("lightcol1=16,16,16")
                CNFWriter.WriteLine("lightcol2=0,0,0")
                CNFWriter.WriteLine("uninstallmes0=Do you want to uninstall this game ?")
                CNFWriter.WriteLine("uninstallmes1=")
                CNFWriter.WriteLine("uninstallmes2=")
            End Using

            'Create game project res & image directory
            If Not Directory.Exists(NewGameProjectDirectory + "\res") Then
                Directory.CreateDirectory(NewGameProjectDirectory + "\res")
            End If
            If Not Directory.Exists(NewGameProjectDirectory + "\res\image") Then
                Directory.CreateDirectory(NewGameProjectDirectory + "\res\image")
            End If

            'Write info.sys to res directory
            Using SYSWriter As New StreamWriter(NewGameProjectDirectory + "\res\info.sys", False)
                SYSWriter.WriteLine("title = " + SelectedPS2Game.GameTitle)
                SYSWriter.WriteLine("title_id = " + SelectedPS2Game.GameID)
                SYSWriter.WriteLine("title_sub_id = 0")
                SYSWriter.WriteLine("release_date = " + SelectedPS2Game.GameReleaseDate)
                SYSWriter.WriteLine("developer_id = " + SelectedPS2Game.GameDeveloper)
                SYSWriter.WriteLine("publisher_id = " + SelectedPS2Game.GamePublisher)
                SYSWriter.WriteLine("note = ")
                SYSWriter.WriteLine("content_web = " + SelectedPS2Game.GameWebsite)
                SYSWriter.WriteLine("image_topviewflag = 0")
                SYSWriter.WriteLine("image_type = 0")
                SYSWriter.WriteLine("image_count = 1")
                SYSWriter.WriteLine("image_viewsec = 600")
                SYSWriter.WriteLine("copyright_viewflag = 0")
                SYSWriter.WriteLine("copyright_imgcount = 1")
                SYSWriter.WriteLine("genre = " + SelectedPS2Game.GameGenre)
                SYSWriter.WriteLine("parental_lock = 1")
                SYSWriter.WriteLine("effective_date = 0")
                SYSWriter.WriteLine("expire_date = 0")

                Select Case SelectedPS2Game.GameRegion
                    Case "Europe"
                        SYSWriter.WriteLine("area = E")
                    Case "US"
                        SYSWriter.WriteLine("area = U")
                    Case "Japan"
                        SYSWriter.WriteLine("area = J")
                    Case Else
                        SYSWriter.WriteLine("area = J")
                End Select

                SYSWriter.WriteLine("violence_flag = 0")
                SYSWriter.WriteLine("content_type = 255")
                SYSWriter.WriteLine("content_subtype = 0")
            End Using

            'Create man.xml
            Using MANWriter As New StreamWriter(NewGameProjectDirectory + "\res\man.xml", False)
                MANWriter.WriteLine("<?xml version=""1.0"" encoding=""UTF-8""?>")
                MANWriter.WriteLine("")
                MANWriter.WriteLine("<MANUAL version=""1.0"">")
                MANWriter.WriteLine("")
                MANWriter.WriteLine("<IMG id=""bg"" src=""./image/0.png"" />")
                MANWriter.WriteLine("")
                MANWriter.WriteLine("<MENUGROUP id=""TOP"">")
                MANWriter.WriteLine("<TITLE id=""TOP-TITLE"" label=""" + SelectedPS2Game.GameTitle + """ />")
                MANWriter.WriteLine("<ITEM id=""M00"" label=""Screenshots""	page=""PIC0000"" />")
                MANWriter.WriteLine("</MENUGROUP>")
                MANWriter.WriteLine("")
                MANWriter.WriteLine("<PAGEGROUP>")
                MANWriter.WriteLine("<PAGE id=""PIC0000"" src=""./image/1.png"" retitem=""M00"" retgroup=""TOP"" />")
                MANWriter.WriteLine("<PAGE id=""PIC0000"" src=""./image/2.png"" retitem=""M00"" retgroup=""TOP"" />")
                MANWriter.WriteLine("</PAGEGROUP>")
                MANWriter.WriteLine("</MANUAL>")
                MANWriter.WriteLine("")
            End Using

            'Open project settings window
            NewGameProjectWindow.Show()

            'Open the Game Editor (in case of additional changes)
            NewGameEditor.Show()

            'Open the Game Editor and try to load values from PSXDatacenter
            If Utils.IsURLValid("https://psxdatacenter.com/psx2/games2/" + SelectedPS2Game.GameID + ".html") Then
                NewGameEditor.PSXDatacenterBrowser.Navigate("https://psxdatacenter.com/psx2/games2/" + SelectedPS2Game.GameID + ".html")
            Else
                'Apply cover, title and region only if no data is available on PSXDatacenter
                NewGameEditor.ApplyKnownValues(SelectedPS2Game.GameID, SelectedPS2Game.GameTitle)
            End If
        End If
    End Sub

    Private Async Sub ModifyPartitionMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles ModifyPartitionMenuItem.Click
        If GamesListView.SelectedItem IsNot Nothing Then
            Dim SelectedPS2Game As PS2Game = CType(GamesListView.SelectedItem, PS2Game)

            If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected Then
                MsgBox("Please connect to the PSX HDD first.", MsgBoxStyle.Information)
                Return
            End If

            If SelectedPS2Game.MountedPartition Is Nothing Then 'Game partition not mounted yet
                SelectedGameToModify = SelectedPS2Game

                NewLoadingWindow = New SyncWindow() With {.Title = "Loading game partitions", .ShowActivated = True, .WindowStartupLocation = WindowStartupLocation.CenterScreen}
                NewLoadingWindow.LoadProgressBar.IsIndeterminate = True
                NewLoadingWindow.LoadStatusTextBlock.Text = "Loading partition"
                NewLoadingWindow.Show()

                Dim Mounted As Boolean = False
                Try
                    Mounted = Await MountGamePartitionAsync(SelectedPS2Game)
                Catch ex As Exception
                    CloseLoadingWindow()
                    ShowError(ex, "Could not mount the selected game")
                End Try
                CloseLoadingWindow()

                If Not Mounted Then Return
            End If

            OpenGamePartitionManager(SelectedPS2Game)
        End If
    End Sub

    Private Sub ReloadButton_Click(sender As Object, e As RoutedEventArgs) Handles ReloadButton.Click
        If Not String.IsNullOrEmpty(CurrentDirectoryTextBlock.Text) Then
            If CurrentDirectoryTextBlock.Text.StartsWith("PSX") Then
                LoadPSXGamesButton_Click(LoadPSXGamesButton, New RoutedEventArgs())
            Else
                GamesListView.Items.Clear()
                URLs.Clear()

                ISOCount = Directory.EnumerateFiles(CurrentDirectoryTextBlock.Text, "*.iso", SearchOption.AllDirectories).Count
                TotalGamesTextBlock.Text = ISOCount.ToString() + " Games"

                GamesListView.ContextMenu = Nothing
                GamesListView.ContextMenu = PCPS2GamesContextMenu

                NewLoadingWindow = New SyncWindow() With {.Title = "Loading PS2 files", .ShowActivated = True}
                NewLoadingWindow.LoadProgressBar.Maximum = ISOCount
                NewLoadingWindow.LoadStatusTextBlock.Text = "Loading file 1 of " + ISOCount.ToString()
                NewLoadingWindow.Show()

                GameLoaderWorker.RunWorkerAsync(CurrentDirectoryTextBlock.Text)
            End If
        End If
    End Sub

    Private Async Sub RemoveMenuItem_Click(sender As Object, e As RoutedEventArgs) Handles RemoveMenuItem.Click
        If GamesListView.SelectedItem IsNot Nothing Then
            Dim SelectedPS2Game As PS2Game = CType(GamesListView.SelectedItem, PS2Game)

            If StorageBackend Is Nothing OrElse Not StorageBackend.IsConnected Then
                MsgBox("Please connect to the PSX HDD first.", MsgBoxStyle.Information)
                Return
            End If

            If SelectedPS2Game.MountedPartition IsNot Nothing Then 'The game partition is still mounted
                MsgBox("The game partition is mounted at " + SelectedPS2Game.MountedPartition.WindowsPath + "." + vbCrLf + "Please unmount it before removing the game.", MsgBoxStyle.Exclamation, "Game partition mounted")
                Return
            End If

            If MsgBox("Do you really want to remoe the game " + SelectedPS2Game.GameTitle + " ?" + vbCrLf + "This operation could be destructive and should be made on the console !", MsgBoxStyle.YesNo, "Please confirm") = MsgBoxResult.Yes Then
                SelectedGameToModify = SelectedPS2Game

                NewLoadingWindow = New SyncWindow() With {.Title = "Removing " + SelectedPS2Game.GameTitle, .ShowActivated = True, .WindowStartupLocation = WindowStartupLocation.CenterScreen}
                NewLoadingWindow.LoadProgressBar.IsIndeterminate = True
                NewLoadingWindow.LoadStatusTextBlock.Text = "Deleting game, please wait"
                NewLoadingWindow.Show()

                Try
                    Await DeleteGamePartitionsAsync(SelectedPS2Game)
                Catch ex As Exception
                    ShowError(ex, "Error")
                Finally
                    NewLoadingWindow.Close()
                End Try

                'Reload
                If StorageBackend.IsConnected Then Await ReloadPSXGamesAsync()
            End If
        End If
    End Sub

End Class

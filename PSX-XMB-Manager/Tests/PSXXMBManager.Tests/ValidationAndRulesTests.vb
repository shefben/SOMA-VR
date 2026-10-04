Imports Xunit

Public Class StorageValidationTests

    <Theory>
    <InlineData("192.168.1.50", True, "192.168.1.50")>
    <InlineData(" 10.0.0.2 ", True, "10.0.0.2")>
    <InlineData("1", False, "")>
    <InlineData("1.2", False, "")>
    <InlineData("256.1.1.1", False, "")>
    <InlineData("::1", False, "")>
    <InlineData("psx.local", False, "")>
    <InlineData("", False, "")>
    Public Sub TryParseIPv4_AcceptsOnlyDottedQuads(text As String, expected As Boolean, normalized As String)
        Dim result As String = Nothing
        Assert.Equal(expected, StorageValidation.TryParseIPv4(text, result))
        Assert.Equal(normalized, result)
    End Sub

    <Fact>
    Public Sub Ports_PartitionNames_MountIds_Distros()
        Assert.True(StorageValidation.IsValidPort(10809))
        Assert.False(StorageValidation.IsValidPort(0))
        Assert.False(StorageValidation.IsValidPort(65536))

        Assert.True(StorageValidation.IsValidPartitionName("PP.SLUS-12345..GAME TITLE"))
        Assert.False(StorageValidation.IsValidPartitionName(New String("A"c, 33)))
        Assert.False(StorageValidation.IsValidPartitionName("PP.BAD" & vbLf & "rmpart __system"))
        Assert.False(StorageValidation.IsValidPartitionName(""))

        Assert.True(StorageValidation.IsSafeMountId("PP.SLUS-12345..GAME-1a2b3c4d"))
        For Each unsafeId As String In {"..", ".", "a/b", "a\b", "a b", "", New String("x"c, 65)}
            Assert.False(StorageValidation.IsSafeMountId(unsafeId), unsafeId)
        Next

        Assert.True(StorageValidation.IsValidDistroName("Ubuntu-24.04"))
        Assert.False(StorageValidation.IsValidDistroName("Ubuntu; rm -rf /"))
        Assert.False(StorageValidation.IsValidDistroName(""))
    End Sub

    <Fact>
    Public Sub LinuxPathToWslUnc_BuildsWslLocalhostAndLegacyPaths()
        Assert.Equal("\\wsl.localhost\Ubuntu-24.04\home\ben\.local\share\psx-xmb-manager\pfs\PP.X-1",
                     StorageValidation.LinuxPathToWslUnc("Ubuntu-24.04", "/home/ben/.local/share/psx-xmb-manager/pfs/PP.X-1"))
        Assert.Equal("\\wsl$\Debian\mnt", StorageValidation.LinuxPathToWslUnc("Debian", "/mnt", "wsl$"))
        Assert.Throws(Of ArgumentException)(Function() StorageValidation.LinuxPathToWslUnc("Debian", "relative/path"))
        Assert.Throws(Of ArgumentException)(Function() StorageValidation.LinuxPathToWslUnc("Debian", "/a\b"))
        Assert.Throws(Of ArgumentException)(Function() StorageValidation.LinuxPathToWslUnc("bad name", "/a"))
    End Sub

    <Fact>
    Public Sub SizeMismatch_IsNeverAccepted()
        Assert.True(StorageValidation.IsExactSizeMatch(1073741824L, 1073741824L))
        Assert.False(StorageValidation.IsExactSizeMatch(1073741824L, 1073741825L))
        Assert.False(StorageValidation.IsExactSizeMatch(1048576000L, 1073741824L))
        Assert.False(StorageValidation.IsExactSizeMatch(0L, 0L))
        Assert.Equal("1 GB", StorageValidation.FormatSize(1073741824L))
        Assert.Equal("931.51 GB", StorageValidation.FormatSize(1000204886016L))
    End Sub
End Class

Public Class CommandLineQuotingTests

    <Fact>
    Public Sub Quote_FollowsWindowsArgvRules()
        Assert.Equal("--exec", CommandLineQuoting.Quote("--exec"))
        Assert.Equal("""""", CommandLineQuoting.Quote(""))
        Assert.Equal("""C:\Program Files\x""", CommandLineQuoting.Quote("C:\Program Files\x"))
        Assert.Equal("""a\""b""", CommandLineQuoting.Quote("a""b"))
        Assert.Equal("""C:\dir with space\\""", CommandLineQuoting.Quote("C:\dir with space\"))
        Assert.Equal("-d Ubuntu --exec /usr/bin/python3 ""a b""", CommandLineQuoting.Join({"-d", "Ubuntu", "--exec", "/usr/bin/python3", "a b"}))
    End Sub
End Class

Public Class ConnectionStateRuleTests

    <Fact>
    Public Sub OperationsAndDisconnect_FollowTheStateMachine()
        Assert.True(ConnectionStateRules.CanConnect(ConnectionState.Disconnected))
        Assert.False(ConnectionStateRules.CanConnect(ConnectionState.Faulted))
        Assert.False(ConnectionStateRules.CanConnect(ConnectionState.NeedsSetup))

        Assert.True(ConnectionStateRules.CanStartOperation(ConnectionState.Connected))
        Assert.False(ConnectionStateRules.CanStartOperation(ConnectionState.BusyRead))
        Assert.False(ConnectionStateRules.CanStartOperation(ConnectionState.Faulted))

        Assert.True(ConnectionStateRules.CanDisconnect(ConnectionState.Connected))
        Assert.True(ConnectionStateRules.CanDisconnect(ConnectionState.Faulted))
        Assert.False(ConnectionStateRules.CanDisconnect(ConnectionState.BusyWrite))
        Assert.False(ConnectionStateRules.CanDisconnect(ConnectionState.BusyRead))

        Assert.False(ConnectionStateRules.CanCloseApplication(ConnectionState.BusyWrite))
        Assert.False(ConnectionStateRules.CanCloseApplication(ConnectionState.Connecting))
        Assert.True(ConnectionStateRules.CanCloseApplication(ConnectionState.Connected))
        Assert.True(ConnectionStateRules.CanCloseApplication(ConnectionState.BusyRead))
    End Sub

    <Fact>
    Public Sub Transitions_RejectSkippingTheBusyAndDisconnectingStates()
        Assert.True(ConnectionStateRules.IsValidTransition(ConnectionState.Disconnected, ConnectionState.Connecting))
        Assert.True(ConnectionStateRules.IsValidTransition(ConnectionState.Connected, ConnectionState.BusyWrite))
        Assert.True(ConnectionStateRules.IsValidTransition(ConnectionState.BusyWrite, ConnectionState.Connected))
        Assert.True(ConnectionStateRules.IsValidTransition(ConnectionState.Faulted, ConnectionState.Disconnecting))

        Assert.False(ConnectionStateRules.IsValidTransition(ConnectionState.BusyWrite, ConnectionState.Disconnecting))
        Assert.False(ConnectionStateRules.IsValidTransition(ConnectionState.BusyRead, ConnectionState.BusyWrite))
        Assert.False(ConnectionStateRules.IsValidTransition(ConnectionState.Connected, ConnectionState.Disconnected))
        Assert.False(ConnectionStateRules.IsValidTransition(ConnectionState.Connecting, ConnectionState.BusyRead))
    End Sub
End Class

Public Class CommandClassificationTests

    <Fact>
    Public Sub HdlDumpVerbs()
        Assert.True(HdlDumpCommands.IsReadOnly("hdl_toc"))
        Assert.True(HdlDumpCommands.IsReadOnly("TOC"))
        Assert.False(HdlDumpCommands.IsReadOnly("modify_header"))
        Assert.False(HdlDumpCommands.IsReadOnly("inject_dvd"))
        Assert.True(HdlDumpCommands.IsCancellable("inject_cd"))
        Assert.False(HdlDumpCommands.IsCancellable("modify"))
    End Sub

    <Fact>
    Public Sub PfsShellCommandsClassification()
        Assert.True(PfsShellCommands.IsReadOnly({"device /x", "mount PP.A", "ls", "umount", "exit"}))
        Assert.False(PfsShellCommands.IsReadOnly({"device /x", "mount PP.A", "put EXECUTE.KELF", "exit"}))
        Assert.False(PfsShellCommands.IsReadOnly({"device /x", "unknownverb", "exit"})) ' unknown counts as a write
        Assert.Equal("rmpart", PfsShellCommands.VerbOf("  RMPART PP.A"))
        Assert.True(PfsShellCommands.ContainsLineBreak({"mount PP.A" & vbLf & "rmpart __system"}))
        Assert.True(PfsShellCommands.ContainsLineBreak({Nothing}))
        Assert.False(PfsShellCommands.ContainsLineBreak({"mount PP.A"}))
    End Sub

    <Fact>
    Public Sub PutFile_UsesLcdInWslAndPutRenameLocally()
        Assert.Equal({"lcd res", "put info.sys", "lcd .."}, PfsShellCommands.PutFile(StorageBackendKind.WSL2NBD, "res\info.sys"))
        Assert.Equal({"lcd res/image", "put 0.png", "lcd ../.."}, PfsShellCommands.PutFile(StorageBackendKind.WSL2NBD, "res\image\0.png"))
        Assert.Equal({"put EXECUTE.KELF"}, PfsShellCommands.PutFile(StorageBackendKind.WSL2NBD, "EXECUTE.KELF"))
        Assert.Equal({"put res\info.sys", "rename res\info.sys info.sys"}, PfsShellCommands.PutFile(StorageBackendKind.LocalWindows, "res\info.sys"))
        Assert.Equal({"put EXECUTE.KELF"}, PfsShellCommands.PutFile(StorageBackendKind.LocalWindows, "EXECUTE.KELF"))
    End Sub

    <Fact>
    Public Sub Timeouts()
        Assert.Equal(OperationTimeouts.HdlDumpToc, OperationTimeouts.ForHdlDump("hdl_toc"))
        Assert.Equal(OperationTimeouts.Unlimited, OperationTimeouts.ForHdlDump("inject_dvd"))
        Assert.Equal(OperationTimeouts.PfsShellMetadata, OperationTimeouts.ForHdlDump("modify_header"))
        Assert.Equal(OperationTimeouts.Unlimited, OperationTimeouts.ForPfsShell({"device /x", "put a"}))
        Assert.Equal(OperationTimeouts.PfsShellMetadata, OperationTimeouts.ForPfsShell({"device /x", "rmpart a"}))
        Assert.Equal(0, OperationTimeouts.ToHelperSeconds(OperationTimeouts.Unlimited))
        Assert.Equal(30, OperationTimeouts.ToHelperSeconds(OperationTimeouts.HdlDumpToc))
        Assert.Equal(OperationTimeouts.Unlimited, OperationTimeouts.WithLaunchOverhead(OperationTimeouts.Unlimited))
        Assert.Equal(TimeSpan.FromSeconds(25), OperationTimeouts.WithLaunchOverhead(TimeSpan.FromSeconds(5)))
    End Sub
End Class

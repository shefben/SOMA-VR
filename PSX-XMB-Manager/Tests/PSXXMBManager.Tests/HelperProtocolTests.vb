Imports System.Reflection
Imports Newtonsoft.Json.Linq
Imports Xunit

Public Class HelperResponseTests

    <Fact>
    Public Sub TryParse_ReadsTheHelperEnvelope()
        Dim response = HelperResponse.TryParse("{""ok"":true,""code"":""OK"",""message"":"""",""stdout"":""out"",""stderr"":"""",""data"":{""raw_path"":""/x/nbd"",""size"":1073741824,""flag"":true}}" & vbLf)
        Assert.NotNull(response)
        Assert.True(response.Ok)
        Assert.Equal("/x/nbd", response.GetString("raw_path"))
        Assert.Equal(1073741824L, response.GetLong("size"))
        Assert.True(response.GetBool("flag"))
        Assert.Equal("", response.GetString("missing"))
        Assert.Equal(0, response.GetInt("missing"))
    End Sub

    <Theory>
    <InlineData("")>
    <InlineData("not json")>
    <InlineData("[1,2]")>
    <InlineData("{""ok"":true}")>
    <InlineData("Traceback (most recent call last):")>
    Public Sub TryParse_RejectsAnythingElse(text As String)
        Assert.Null(HelperResponse.TryParse(text))
    End Sub

    <Fact>
    Public Sub ToProcessResult_MapsExitCodeTimeoutAndCancel()
        Dim ok = HelperResponse.TryParse("{""ok"":true,""code"":""OK"",""stdout"":""a\r\nb"",""data"":{}}").ToProcessResult()
        Assert.True(ok.Succeeded)
        Assert.Equal("a" & vbLf & "b", ok.StandardOutput)

        Dim failed = HelperResponse.TryParse("{""ok"":false,""code"":""HDL_DUMP_FAILED"",""stderr"":""bad"",""data"":{""exit_code"":114}}").ToProcessResult()
        Assert.False(failed.Succeeded)
        Assert.Equal(114, failed.ExitCode)
        Assert.Equal("HDL_DUMP_FAILED", failed.ErrorCode)
        Assert.Equal("bad", failed.CombinedOutput)

        Dim cancelled = HelperResponse.TryParse("{""ok"":false,""code"":""OPERATION_CANCELLED"",""data"":{""exit_code"":130}}").ToProcessResult()
        Assert.True(cancelled.Cancelled)
        Assert.False(cancelled.Succeeded)

        Dim timedOut = HelperResponse.TryParse("{""ok"":false,""code"":""OPERATION_TIMEOUT"",""data"":{}}").ToProcessResult()
        Assert.True(timedOut.TimedOut)
        Assert.Equal(-1, timedOut.ExitCode)
    End Sub

    <Fact>
    Public Sub CombinedOutput_IsStdoutThenStderr()
        Assert.Equal("o" & vbLf & "e", New ProcessResult With {.StandardOutput = "o", .StandardError = "e"}.CombinedOutput)
        Assert.Equal("e", New ProcessResult With {.StandardError = "e"}.CombinedOutput)
    End Sub
End Class

Public Class ErrorCodeTests

    Private Shared Function AllCodes() As IEnumerable(Of String)
        Return GetType(BackendErrorCodes).GetFields(BindingFlags.Public Or BindingFlags.Static) _
            .Where(Function(f) f.IsLiteral AndAlso f.FieldType Is GetType(String) AndAlso f.Name <> "HelperOutdatedMessage") _
            .Select(Function(f) CStr(f.GetRawConstantValue()))
    End Function

    <Fact>
    Public Sub EveryCodeIsUpperSnakeCaseAndHasANextStep()
        Dim codes = AllCodes().ToList()
        Assert.True(codes.Count >= 40, "expected the full error code list")
        For Each code As String In codes
            Assert.Matches("^[A-Z0-9_]+$", code)
            Assert.False(String.IsNullOrWhiteSpace(BackendErrorCodes.NextStep(code)), code)
        Next
        Assert.Equal(codes.Count, codes.Distinct().Count())
    End Sub

    <Fact>
    Public Sub ProblemCodesGetSpecificAdvice()
        Dim generic As String = BackendErrorCodes.NextStep("SOMETHING_ELSE")
        For Each code As String In {BackendErrorCodes.WslNotInstalled, BackendErrorCodes.NoWsl2Distro, BackendErrorCodes.NbdServerUnreachable,
                                    BackendErrorCodes.FuseUnavailable, BackendErrorCodes.BackendSetupRequired, BackendErrorCodes.StaleMountState,
                                    BackendErrorCodes.SizeMismatch, BackendErrorCodes.BackendBusy, BackendErrorCodes.HelperVersionMismatch}
            Assert.NotEqual(generic, BackendErrorCodes.NextStep(code))
        Next
    End Sub

    <Fact>
    Public Sub FormatForUser_ShowsMessageNextStepCodeAndToolOutput()
        Dim text As String = BackendErrorCodes.FormatForUser(New StorageBackendException(BackendErrorCodes.NbdServerUnreachable, "Could not reach nbd://1.2.3.4:10809.", "nbdinfo: connection refused"))
        Assert.Contains("Could not reach nbd://1.2.3.4:10809.", text)
        Assert.Contains("What to do:", text)
        Assert.Contains("Error code: NBD_SERVER_UNREACHABLE", text)
        Assert.Contains("nbdinfo: connection refused", text)
    End Sub
End Class

Public Class EmbeddedScriptTests

    <Fact>
    Public Sub HelperAndBootstrapAreEmbeddedAndAgree()
        Dim helper As String = WSLProcessRunner.LoadEmbeddedText(WSLProcessRunner.HelperResourceName)
        Dim bootstrap As String = WSLProcessRunner.LoadEmbeddedText(WSLProcessRunner.BootstrapResourceName)

        Assert.Contains("PROTOCOL_VERSION = " + WSL2NBDBackend.ExpectedHelperProtocolVersion.ToString(), helper)
        Assert.DoesNotContain("shell=True", helper)
        For Each placeholder As String In {"__PSX_XMB_MODE__", "__PSX_XMB_HELPER_B64__", "__PSX_XMB_PROTOCOL_VERSION__"}
            Assert.Contains(placeholder, bootstrap)
        Next
        Assert.Contains("32c296c69cf9c263fcbe035004aa28c345b3b279", bootstrap)
        Assert.Contains("8c92467b3d715c3698f1f8ce63a8a07e214d6c73", bootstrap)
        ' The bootstrap installs the helper at HELPER_DIR/psx-xmb-helper.py; the backend must call that same path.
        Assert.Contains("HELPER_DIR=""/usr/local/lib/psx-xmb-manager""", bootstrap)
        Assert.Contains("HELPER_PATH=""$HELPER_DIR/psx-xmb-helper.py""", bootstrap)
        Assert.Equal("/usr/local/lib/psx-xmb-manager/psx-xmb-helper.py", WSLProcessRunner.HelperLinuxPath)
        Assert.DoesNotContain(vbCr, helper)
        Assert.DoesNotContain(vbCr, bootstrap)
    End Sub
End Class

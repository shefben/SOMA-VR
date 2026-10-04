Imports System.Text
Imports Xunit

Public Class WslOutputParserTests

    <Fact>
    Public Sub QuietList_SkipsBlankLinesAndMessages()
        Dim names = WslOutputParsers.ParseQuietList("Ubuntu-24.04" & vbCrLf & "Debian" & vbCrLf & vbCrLf & "There are no more" & vbCrLf)
        Assert.Equal({"Ubuntu-24.04", "Debian"}, names)
    End Sub

    <Fact>
    Public Sub VerboseList_ReadsDefaultStateAndVersion()
        Dim text As String =
            "  NAME            STATE           VERSION" & vbCrLf &
            "* Ubuntu-24.04    Running         2" & vbCrLf &
            "  Legacy          Stopped         1" & vbCrLf &
            "  docker-desktop  Stopped         2" & vbCrLf
        Dim distros = WslOutputParsers.ParseVerboseList(text)

        Assert.Equal(3, distros.Count)
        Assert.Equal("Ubuntu-24.04", distros(0).Name)
        Assert.True(distros(0).IsDefault)
        Assert.Equal("Running", distros(0).State)
        Assert.Equal(2, distros(0).Version)
        Assert.Equal(1, distros(1).Version)
        Assert.False(distros(1).IsDefault)
    End Sub

    <Fact>
    Public Sub VerboseList_IgnoresLocalizedHeader()
        ' Header and state are translated on non-English Windows; the header has no numeric last column and a state can be two words.
        Dim distros = WslOutputParsers.ParseVerboseList("  NOM     ÉTAT      VERSION" & vbLf & "* Ubuntu  En cours  2" & vbLf)
        Assert.Single(distros)
        Assert.Equal("Ubuntu", distros(0).Name)
        Assert.Equal("En cours", distros(0).State)
        Assert.Equal(2, distros(0).Version)
        distros = WslOutputParsers.ParseVerboseList("  NAME      STATUS            VERSION" & vbLf & "* Ubuntu-24.04  Wird ausgeführt  2" & vbLf)
        Assert.Equal("Ubuntu-24.04", distros.Single().Name)
        Assert.Equal("Wird ausgeführt", distros.Single().State)
    End Sub

    <Fact>
    Public Sub MergeLists_AddsNamesMissingFromVerboseAsUnknownVersion()
        Dim merged = WslOutputParsers.MergeLists(New List(Of String) From {"Ubuntu", "Other"},
                                                 New List(Of WslDistroInfo) From {New WslDistroInfo With {.Name = "Ubuntu", .Version = 2}})
        Assert.Equal(2, merged.Count)
        Assert.Equal(0, merged.Single(Function(d) d.Name = "Other").Version)
    End Sub

    <Fact>
    Public Sub SelectDistro_FollowsThePlanOrder()
        Dim distros As New List(Of WslDistroInfo) From {
            New WslDistroInfo With {.Name = "Debian", .Version = 2},
            New WslDistroInfo With {.Name = "Ubuntu", .Version = 2},
            New WslDistroInfo With {.Name = "Ubuntu-24.04", .Version = 2},
            New WslDistroInfo With {.Name = "Old", .Version = 1}
        }
        Assert.Equal("Debian", WslOutputParsers.SelectDistro(distros, "debian"))
        Assert.Equal("Ubuntu-24.04", WslOutputParsers.SelectDistro(distros, "Missing"))
        Assert.Equal("Ubuntu-24.04", WslOutputParsers.SelectDistro(distros, "Old")) ' WSL1 is never selected
        Assert.Equal("Ubuntu", WslOutputParsers.SelectDistro(distros.Where(Function(d) d.Name <> "Ubuntu-24.04"), ""))
        Assert.Equal("", WslOutputParsers.SelectDistro(distros.Where(Function(d) d.Name.StartsWith("D") OrElse d.Name = "Old").Append(New WslDistroInfo With {.Name = "Arch", .Version = 2}), ""))
        Assert.Equal("Arch", WslOutputParsers.SelectDistro({New WslDistroInfo With {.Name = "Arch", .Version = 2}}, ""))
    End Sub

    <Fact>
    Public Sub OsRelease_UnquotesValuesAndDetectsSupportedDistros()
        Dim values = WslOutputParsers.ParseOsRelease("# comment" & vbLf & "ID=ubuntu" & vbLf & "PRETTY_NAME=""Ubuntu 24.04 LTS""" & vbLf & "VERSION_ID='24.04'" & vbLf)
        Assert.Equal("ubuntu", values("ID"))
        Assert.Equal("Ubuntu 24.04 LTS", values("PRETTY_NAME"))
        Assert.Equal("24.04", values("VERSION_ID"))
        Assert.True(WslOutputParsers.SupportsAutomaticSetup(values))
        Assert.True(WslOutputParsers.SupportsAutomaticSetup(WslOutputParsers.ParseOsRelease("ID=debian")))
        Assert.False(WslOutputParsers.SupportsAutomaticSetup(WslOutputParsers.ParseOsRelease("ID=fedora")))
    End Sub
End Class

Public Class OutputTextTests

    <Fact>
    Public Sub NormalizeLineEndings_ConvertsCrLfAndLoneCr()
        Assert.Equal("a" & vbLf & "b" & vbLf & "c" & vbLf, OutputText.NormalizeLineEndings("a" & vbCrLf & "b" & vbCr & "c" & vbLf))
        Assert.Equal({"a", "b", ""}, OutputText.SplitLines("a" & vbCrLf & "b" & vbCrLf).ToList())
        Assert.Equal("", OutputText.NormalizeLineEndings(Nothing))
    End Sub

    <Fact>
    Public Sub DecodeWslText_HandlesUtf16WithAndWithoutBomAndUtf8()
        Dim message As String = "Ubuntu-24.04" & vbCrLf
        Assert.Equal(message, OutputText.DecodeWslText(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(message)).ToArray()))
        Assert.Equal(message, OutputText.DecodeWslText(Encoding.Unicode.GetBytes(message)))
        Assert.Equal("é ok", OutputText.DecodeWslText(Encoding.UTF8.GetBytes("é ok")))
        Assert.Equal("x", OutputText.DecodeWslText(New Byte() {&HEF, &HBB, &HBF, AscW("x"c)}))
        Assert.Equal("", OutputText.DecodeWslText(Nothing))
    End Sub
End Class

Public Class HdlTocParserTests

    ' Lines below are formatted exactly like hdl_dump's "%3s %7luKB %*s %-3s %-12s %s" (flags width 15).
    Private Shared Function TocLine(type As String, sizeKb As Long, flags As String, dma As String, startup As String, name As String) As String
        Return String.Format("{0,-3} {1,7}KB {2,15} {3,-3} {4,-12} {5}", type, sizeKb, flags, dma, startup, name)
    End Function

    <Fact>
    Public Sub DvdLine_IsParsed()
        Dim game As HdlTocGame = Nothing
        Assert.True(HdlTocParser.TryParseGameLine(TocLine("DVD", 4194304, "1+2", "*u4", "SLUS_123.45", "Some Game"), game))
        Assert.Equal("DVD", game.Type)
        Assert.Equal(4194304L, game.SizeInKB)
        Assert.Equal("1+2", game.Flags)
        Assert.Equal("*u4", game.DMA)
        Assert.Equal("SLUS_123.45", game.Startup)
        Assert.Equal("Some Game", game.Name)
    End Sub

    <Fact>
    Public Sub CdLineWithShortSize_KeepsTheTitle()
        ' The old split on double spaces returned the DMA/startup columns as the title for this line.
        Dim game As HdlTocGame = Nothing
        Assert.True(HdlTocParser.TryParseGameLine(TocLine("CD", 19530, "0", "*u4", "SLUS_123.45", "Tiny CD Game"), game))
        Assert.Equal("CD", game.Type)
        Assert.Equal(19530L, game.SizeInKB)
        Assert.Equal("Tiny CD Game", game.Name)
    End Sub

    <Fact>
    Public Sub TitleWithDoubleSpacesAndNoDma_IsKeptWhole()
        Dim game As HdlTocGame = Nothing
        Assert.True(HdlTocParser.TryParseGameLine(TocLine("DVD", 1234567, "0", "", "SCES_500.00", "Two  Spaces  Title"), game))
        Assert.Equal("", game.DMA)
        Assert.Equal("SCES_500.00", game.Startup)
        Assert.Equal("Two  Spaces  Title", game.Name)
    End Sub

    <Fact>
    Public Sub RealHdlDumpLine_WithFlagsAndModeTwoDma()
        ' Captured from the pinned Linux hdl_dump after "modify ... +1+3" and "modify ... *m2".
        Dim game As HdlTocGame = Nothing
        Assert.True(HdlTocParser.TryParseGameLine("CD    20480KB             1+3 *m2 SLUS_123.45  Flag Test Game", game))
        Assert.Equal("1+3", game.Flags)
        Assert.Equal("*m2", game.DMA)
        Assert.Equal("SLUS_123.45", game.Startup)
        Assert.Equal("Flag Test Game", game.Name)
    End Sub

    <Fact>
    Public Sub OtherLines_AreRejected()
        Dim game As HdlTocGame = Nothing
        Assert.False(HdlTocParser.TryParseGameLine("type      size flags                dma startup      name", game))
        Assert.False(HdlTocParser.TryParseGameLine("total 953869MB, used 12345MB, available 941524MB", game))
        Assert.False(HdlTocParser.TryParseGameLine("", game))
        Assert.Null(game)
    End Sub
End Class

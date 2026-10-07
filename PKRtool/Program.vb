Imports System.IO

' PKR Tool 1.0 - https://github.com/ravenDS
Module Program
    Private Const UNPACK_SUFFIX As String = "-unpacked"
    Private Const REPACK_SUFFIX As String = "-repacked"

    Function Main(args As String()) As Integer
        Try
            Return Run(args)
        Finally
            ' optional handling here
        End Try
    End Function

    Private Function Run(args As String()) As Integer
        Dim compress As Boolean = False
        Dim noCompress As Boolean = False
        Dim positional As New List(Of String)

        For Each a In args
            Select Case a.ToLowerInvariant()
                Case "-compress", "--compress", "-c"
                    compress = True
                Case "-nocompress", "--nocompress", "-n"
                    noCompress = True
                Case "-h", "--help", "-?", "/?", "help"
                    PrintUsage()
                    Return 0
                Case Else
                    If a.StartsWith("-") Then
                        Console.Error.WriteLine($"Unknown option: {a}")
                        PrintUsage()
                        Return 2
                    End If
                    positional.Add(CleanPath(a))
            End Select
        Next

        If compress AndAlso noCompress Then
            Console.Error.WriteLine("-compress and -nocompress cannot be used together.")
            Return 2
        End If

        If positional.Count = 0 Then
            PrintUsage()
            Return 2
        End If

        Select Case positional(0).ToLowerInvariant()
            Case "unpack"
                If positional.Count < 2 OrElse positional.Count > 3 Then
                    PrintUsage()
                    Return 2
                End If
                Dim outDir As String = If(positional.Count = 3, positional(2), Nothing)
                Return If(TryRun(Sub() DoUnpack(positional(1), outDir)), 0, 1)

            Case "repack"
                If positional.Count < 2 OrElse positional.Count > 3 Then
                    PrintUsage()
                    Return 2
                End If
                Dim outPkr As String = If(positional.Count = 3, positional(2), Nothing)
                Return If(TryRun(Sub() DoRepack(positional(1), outPkr, compress)), 0, 1)

            Case Else
                ' auto mode
                Dim failures As Integer = 0
                For Each p In positional
                    Dim item As String = p
                    Dim ok As Boolean
                    If File.Exists(item) Then
                        If IsPkrArchive(item) Then
                            ok = TryRun(Sub() DoUnpack(item, Nothing))
                        Else
                            Console.Error.WriteLine($"Skipped {Path.GetFileName(item)}: not a PKR2 archive")
                            ok = False
                        End If
                    ElseIf Directory.Exists(item) Then
                        ok = TryRun(Sub() DoRepack(item, Nothing, Not noCompress))
                    Else
                        Console.Error.WriteLine($"Not found: {item}")
                        ok = False
                    End If
                    If Not ok Then failures += 1
                Next
                If positional.Count > 1 Then
                    Console.WriteLine($"{positional.Count - failures} of {positional.Count} item(s) processed.")
                End If
                Return If(failures = 0, 0, 1)
        End Select
    End Function

    ' <dir>\LEVEL.PKR -> <dir>\LEVEL-unpacked\
    Private Sub DoUnpack(pkrPath As String, outDir As String)
        Dim fullPkr As String = Path.GetFullPath(pkrPath)
        If Not File.Exists(fullPkr) Then Throw New FileNotFoundException("Archive not found.", fullPkr)

        If String.IsNullOrEmpty(outDir) Then
            outDir = Path.Combine(Path.GetDirectoryName(fullPkr), Path.GetFileNameWithoutExtension(fullPkr) & UNPACK_SUFFIX)
        End If
        outDir = Path.GetFullPath(outDir)

        Console.WriteLine($"Unpacking {Path.GetFileName(fullPkr)}")
        Console.WriteLine($"  -> {outDir}")
        Dim sw = Stopwatch.StartNew()
        PKRmodule.ExtractPKR(fullPkr, outDir)
        Console.WriteLine($"  done in {sw.Elapsed.TotalSeconds:0.0} s")
    End Sub

    ' <dir>\LEVEL-unpacked\ -> <dir>\LEVEL-repacked\LEVEL.PKR
    Private Sub DoRepack(inputDir As String, outPkr As String, useCompression As Boolean)
        Dim fullIn As String = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        If Not Directory.Exists(fullIn) Then Throw New DirectoryNotFoundException($"Folder not found: {fullIn}")

        If String.IsNullOrEmpty(outPkr) Then
            Dim folderName As String = Path.GetFileName(fullIn)
            Dim parent As String = Path.GetDirectoryName(fullIn)
            If String.IsNullOrEmpty(folderName) OrElse parent Is Nothing Then
                Throw New InvalidOperationException("Cannot repack a drive root; pass a folder.")
            End If
            ' "LEVEL-unpacked" -> "LEVEL", so the archive keeps its original game file name
            Dim baseName As String = folderName
            If baseName.EndsWith(UNPACK_SUFFIX, StringComparison.OrdinalIgnoreCase) AndAlso baseName.Length > UNPACK_SUFFIX.Length Then
                baseName = baseName.Substring(0, baseName.Length - UNPACK_SUFFIX.Length)
            End If
            outPkr = Path.Combine(parent, baseName & REPACK_SUFFIX, baseName & ".PKR")
        End If
        outPkr = Path.GetFullPath(outPkr)

        ' Writing the archive inside the folder being packed would pack it into itself on the next run
        If outPkr.StartsWith(fullIn & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidOperationException("Output archive cannot be inside the folder being packed.")
        End If

        Directory.CreateDirectory(Path.GetDirectoryName(outPkr))

        Console.WriteLine($"Repacking {Path.GetFileName(fullIn)}{If(useCompression, " (zlib)", " (stored)")}")
        Console.WriteLine($"  -> {outPkr}")
        Dim sw = Stopwatch.StartNew()
        PKRmodule.RepackPKR(fullIn, outPkr, useCompression)
        Console.WriteLine($"  done in {sw.Elapsed.TotalSeconds:0.0} s, {New FileInfo(outPkr).Length / 1048576.0:0.0} MB")
    End Sub

    ' file type check by signature, not extension
    Private Function IsPkrArchive(filePath As String) As Boolean
        Try
            Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                Dim magic(3) As Byte
                Return fs.Read(magic, 0, 4) = 4 AndAlso
                       magic(0) = AscW("P"c) AndAlso magic(1) = AscW("K"c) AndAlso
                       magic(2) = AscW("R"c) AndAlso magic(3) = AscW("2"c)
            End Using
        Catch
            Return False
        End Try
    End Function

    Private Function TryRun(action As Action) As Boolean
        Try
            action()
            Return True
        Catch ex As Exception
            Console.Error.WriteLine($"  ERROR: {ex.Message}")
            Return False
        End Try
    End Function

    Private Function CleanPath(p As String) As String
        Return p.Trim().Trim(""""c)
    End Function

    Private Sub PrintUsage()
        Dim exe As String = Path.GetFileNameWithoutExtension(AppDomain.CurrentDomain.FriendlyName)
        PrintBanner()
        Console.WriteLine("Usage:")
        Console.WriteLine($"  {exe} unpack <archive.pkr> [outDir]")
        Console.WriteLine($"  {exe} repack <folder> [out.pkr] [-compress]")
        Console.WriteLine($"  {exe} <archive.pkr | folder> [...] [-nocompress]")
        Console.WriteLine("Note: repack stores files as-is unless -compress is specified.")
        Console.WriteLine()
        Console.WriteLine("Auto mode (or drag & drop onto the exe):")
        Console.WriteLine("  archive.pkr  -> unpack")
        Console.WriteLine("  folder       -> repack, zlib compressed (-nocompress to store files as-is)")
        Console.WriteLine()

    End Sub
    Sub PrintBanner()
        Console.WriteLine()
        Console.WriteLine("  ██████  ██   ██ ██████   ▄             ▄")
        Console.WriteLine("  ██   ██ ██  ██  ██   ██ ▄█▄▄  ▄▄   ▄▄  █")
        Console.WriteLine("  ██████  █████   ██████   █   █  █ █  █ █")
        Console.WriteLine("  ██      ██  ██  ██  ██   █   █  █ █  █ █")
        Console.WriteLine("  ██      ██   ██ ██   ██   ▀▀  ▀▀   ▀▀  ▀")
        Console.WriteLine()
        Console.WriteLine("  Walt Disney World Quest/THPS2 PKR tool - v1.0")
        Console.WriteLine()
        Console.WriteLine("  github.com/ravenDS")
        Console.WriteLine()
    End Sub

End Module
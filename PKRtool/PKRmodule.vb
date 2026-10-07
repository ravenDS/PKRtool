Imports System.IO
Imports System.IO.Compression
Imports System.Text

' PKR Tool 1.0 - https://github.com/ravenDS
Module PKRmodule
    Private Const FLAG_STORED As UInteger = &HFFFFFFFEUI
    Private Const FLAG_ZLIB As UInteger = 2UI
    Private Const NAME_FIELD As Integer = 32
    Private Const MAX_NAME_LEN As Integer = NAME_FIELD - 1   ' keep room for null terminator

    Public Sub ExtractPKR(pkrPath As String, Optional outDir As String = Nothing)
        Dim outputDir As String = Path.Combine(Path.GetDirectoryName(pkrPath), Path.GetFileNameWithoutExtension(pkrPath))

        If Not outDir Is Nothing Then outputDir = outDir
        Using fs As New FileStream(pkrPath, FileMode.Open, FileAccess.Read)
            Using br As New BinaryReader(fs, Encoding.ASCII)

                ' header
                Dim sig As String = ReadFixedString(br, 4)
                If sig <> "PKR2" Then
                    Throw New InvalidDataException("Not a valid PKR2 file.")
                End If

                Dim version As UInteger = br.ReadUInt32()
                Dim numFolders As UInteger = br.ReadUInt32()
                Dim numFiles As UInteger = br.ReadUInt32()

                ' process each folder
                For i As Integer = 0 To CInt(numFolders) - 1
                    Dim folderName As String = ReadFixedString(br, NAME_FIELD)
                    Dim entryOffset As UInteger = br.ReadUInt32()
                    Dim fileCount As UInteger = br.ReadUInt32()

                    Dim folderPath As String = Path.Combine(outputDir, ArchiveFolderToLocalPath(folderName))
                    Directory.CreateDirectory(folderPath)

                    Dim cursor As Long = fs.Position
                    fs.Seek(CLng(entryOffset), SeekOrigin.Begin)

                    For j As Integer = 0 To CInt(fileCount) - 1
                        Dim fileName As String = ReadFixedString(br, NAME_FIELD)
                        Dim flag As UInteger = br.ReadUInt32()
                        Dim dataOffset As UInteger = br.ReadUInt32()
                        Dim rawSize As UInteger = br.ReadUInt32()
                        Dim compSize As UInteger = br.ReadUInt32()

                        Dim fileCursor As Long = fs.Position
                        fs.Seek(CLng(dataOffset), SeekOrigin.Begin)

                        Dim compData As Byte() = br.ReadBytes(CInt(compSize))
                        If compData.Length <> CInt(compSize) Then
                            Throw New EndOfStreamException($"Truncated data for '{folderName}{fileName}'.")
                        End If

                        Dim fileData As Byte()

                        If flag = FLAG_ZLIB OrElse (flag <> FLAG_STORED AndAlso compSize <> rawSize) Then
                            Using ms As New MemoryStream(compData)
                                Using zlib As New ZLibStream(ms, CompressionMode.Decompress)
                                    Using outMs As New MemoryStream(CInt(rawSize))
                                        zlib.CopyTo(outMs)
                                        fileData = outMs.ToArray()
                                    End Using
                                End Using
                            End Using
                        Else
                            fileData = compData
                        End If

                        Dim destPath As String = Path.Combine(folderPath, fileName)
                        File.WriteAllBytes(destPath, fileData)

                        fs.Seek(fileCursor, SeekOrigin.Begin)
                    Next

                    fs.Seek(cursor, SeekOrigin.Begin)
                Next

            End Using
        End Using
    End Sub

    Public Sub RepackPKR(inputDir As String, Optional pkrPath As String = Nothing, Optional useCompression As Boolean = False)
        If String.IsNullOrEmpty(inputDir) Then Throw New ArgumentException("inputDir is required.", NameOf(inputDir))
        If Not Directory.Exists(inputDir) Then Throw New DirectoryNotFoundException(inputDir)

        Dim trimmedRoot As String = inputDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        If String.IsNullOrEmpty(pkrPath) Then
            pkrPath = trimmedRoot & ".PKR"
        End If

        ' collect folders and files
        Dim folders As New List(Of PKRFolder)
        CollectFolders(trimmedRoot, trimmedRoot, folders)

        ' sort names exactly as they are stored in the archive (backslash form, empty root first), then files
        folders.Sort(Function(a, b) CompareNames(a.Name, b.Name))
        For Each f In folders
            f.Files.Sort(Function(a, b) CompareNames(a.Name, b.Name))
        Next

        ' validate names, ASCII only, and must fit 32-byte fields with null terminator
        For Each f In folders
            ValidateName(f.Name, "Folder")
            For Each fe In f.Files
                ValidateName(fe.Name, "File")
            Next
        Next

        ' load every file, recording sizes and flag
        Dim totalFiles As Integer = 0
        For Each f In folders
            For Each fe In f.Files
                Dim raw As Byte() = File.ReadAllBytes(fe.SourcePath)
                fe.RawSize = CUInt(raw.Length)
                fe.Payload = raw
                fe.CompSize = CUInt(raw.Length)
                fe.Flag = FLAG_STORED
                If useCompression Then
                    Dim comp As Byte() = CompressZLib(raw)
                    If comp.Length < raw.Length Then
                        fe.Payload = comp
                        fe.CompSize = CUInt(comp.Length)
                        fe.Flag = FLAG_ZLIB
                    End If
                End If
                totalFiles += 1
            Next
        Next

        ' compute offsets
        ' Header (16) | Folder table (40*N) | File entries (48*M, grouped by folder) | File data
        Const HEADER_SIZE As Integer = 16
        Const FOLDER_ENTRY_SIZE As Integer = 40
        Const FILE_ENTRY_SIZE As Integer = 48
        Dim folderTableSize As Long = CLng(folders.Count) * FOLDER_ENTRY_SIZE
        Dim fileEntriesSize As Long = CLng(totalFiles) * FILE_ENTRY_SIZE

        Dim entryCursor As Long = HEADER_SIZE + folderTableSize
        Dim dataCursor As Long = HEADER_SIZE + folderTableSize + fileEntriesSize
        For Each f In folders
            f.EntryOffset = CUInt(entryCursor)
            entryCursor += f.Files.Count * FILE_ENTRY_SIZE
            For Each fe In f.Files
                fe.DataOffset = CUInt(dataCursor)
                dataCursor += fe.CompSize
            Next
        Next
        If dataCursor > UInteger.MaxValue Then
            Throw New InvalidDataException("Archive exceeds 4 GB, which PKR2 offsets cannot address.")
        End If

        ' write archive
        Using fs As New FileStream(pkrPath, FileMode.Create, FileAccess.Write)
            Using bw As New BinaryWriter(fs, Encoding.ASCII)

                ' header
                bw.Write(Encoding.ASCII.GetBytes("PKR2"))
                bw.Write(1UI)                       ' version
                bw.Write(CUInt(folders.Count))      ' numFolders
                bw.Write(CUInt(totalFiles))         ' numFiles

                ' folder table (folder name field uses 0xCD fill after the null)
                For Each f In folders
                    WriteFixedString(bw, f.Name, NAME_FIELD, &HCD)
                    bw.Write(f.EntryOffset)
                    bw.Write(CUInt(f.Files.Count))
                Next

                ' file entries (grouped by folder in the same order as folder table)
                For Each f In folders
                    For Each fe In f.Files
                        WriteFixedString(bw, fe.Name, NAME_FIELD)
                        bw.Write(fe.Flag)           ' 0xFFFFFFFE = stored, 2 = zlib
                        bw.Write(fe.DataOffset)
                        bw.Write(fe.RawSize)
                        bw.Write(fe.CompSize)
                    Next
                Next

                ' file data, packed back-to-back, no alignment padding
                For Each f In folders
                    For Each fe In f.Files
                        bw.Write(fe.Payload)
                    Next
                Next

            End Using
        End Using
    End Sub

    Private Sub CollectFolders(rootDir As String, dir As String, folders As List(Of PKRFolder))
        Dim files As String() = Directory.GetFiles(dir)
        If files.Length > 0 Then
            Dim relName As String = ""
            If Not String.Equals(Path.GetFullPath(dir), Path.GetFullPath(rootDir), StringComparison.Ordinal) Then
                relName = LocalPathToArchiveFolder(Path.GetRelativePath(rootDir, dir))
            End If
            Dim fld As New PKRFolder With {.Name = relName}
            For Each fp In files
                fld.Files.Add(New PKRFile With {
                    .Name = Path.GetFileName(fp),
                    .SourcePath = fp
                })
            Next
            folders.Add(fld)
        End If
        For Each subDir In Directory.GetDirectories(dir)
            CollectFolders(rootDir, subDir, folders)
        Next
    End Sub

    Private Function ArchiveFolderToLocalPath(archiveName As String) As String
        Return archiveName.Replace("\"c, Path.DirectorySeparatorChar) _
                          .Replace("/"c, Path.DirectorySeparatorChar) _
                          .TrimEnd(Path.DirectorySeparatorChar)
    End Function

    Private Function LocalPathToArchiveFolder(relPath As String) As String
        Dim name As String = relPath.Replace(Path.DirectorySeparatorChar, "\"c) _
                                    .Replace(Path.AltDirectorySeparatorChar, "\"c) _
                                    .Trim("\"c)
        If name = "" OrElse name = "." Then Return ""
        Return name & "\"
    End Function

    ' case-insensitive byte comparison
    ' matches order of the original archives
    Private Function CompareNames(a As String, b As String) As Integer
        Dim n As Integer = Math.Min(a.Length, b.Length)
        For i As Integer = 0 To n - 1
            Dim ca As Integer = AscW(Char.ToLowerInvariant(a(i)))
            Dim cb As Integer = AscW(Char.ToLowerInvariant(b(i)))
            If ca <> cb Then Return ca - cb
        Next
        Return a.Length - b.Length
    End Function

    Private Sub ValidateName(name As String, kind As String)
        For Each c As Char In name
            If AscW(c) > 127 Then Throw New InvalidDataException($"{kind} name '{name}' contains non-ASCII characters.")
        Next
        If name.Length > MAX_NAME_LEN Then
            Throw New InvalidDataException($"{kind} name '{name}' exceeds {MAX_NAME_LEN} characters.")
        End If
    End Sub

    Private Function CompressZLib(data As Byte()) As Byte()
        Using ms As New MemoryStream()
            Using zlib As New ZLibStream(ms, CompressionLevel.Optimal, leaveOpen:=True)
                zlib.Write(data, 0, data.Length)
            End Using
            Return ms.ToArray()
        End Using
    End Function

    ' write ASCII string into fixed-size field, then null terminator (if theres space), then fillByte
    Private Sub WriteFixedString(bw As BinaryWriter, value As String, length As Integer, Optional fillByte As Byte = 0)
        Dim raw As Byte() = Encoding.ASCII.GetBytes(If(value, ""))
        If raw.Length > length Then
            Throw New InvalidDataException($"String '{value}' exceeds {length} bytes.")
        End If
        bw.Write(raw)
        Dim remaining As Integer = length - raw.Length
        If remaining > 0 Then
            bw.Write(CByte(0))                  ' null terminator
            If remaining > 1 Then
                Dim pad(remaining - 2) As Byte
                If fillByte <> 0 Then
                    Array.Fill(pad, fillByte)
                End If
                bw.Write(pad)
            End If
        End If
    End Sub

    Private Function ReadFixedString(br As BinaryReader, length As Integer) As String
        Dim raw As Byte() = br.ReadBytes(length)
        Dim nullIdx As Integer = Array.IndexOf(raw, CByte(0))
        If nullIdx < 0 Then nullIdx = raw.Length
        Return Encoding.ASCII.GetString(raw, 0, nullIdx)
    End Function

    Private Class PKRFolder
        Public Name As String
        Public EntryOffset As UInteger
        Public Files As New List(Of PKRFile)
    End Class

    Private Class PKRFile
        Public Name As String
        Public SourcePath As String
        Public Flag As UInteger
        Public RawSize As UInteger
        Public CompSize As UInteger
        Public DataOffset As UInteger
        Public Payload As Byte()
    End Class
End Module
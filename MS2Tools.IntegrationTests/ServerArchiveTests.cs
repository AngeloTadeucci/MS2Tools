using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MS2Lib;
using MiscUtils;

namespace MS2Tools.IntegrationTests;

[TestClass]
public class ServerArchiveTests
{
    private const string ServerSourcePath = @"D:\Projetos\GitHub\MapleStory2-XML\server";
    private const string TestOutputDir = "ServerArchiveTestOutput";
    private const string ArchiveName = "server";
    private const string HeaderFileExtension = ".m2h";
    private const string DataFileExtension = ".m2d";

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        if (!Directory.Exists(ServerSourcePath))
        {
            Assert.Inconclusive($"Server source folder not found: {ServerSourcePath}");
        }

        if (Directory.Exists(TestOutputDir))
        {
            Directory.Delete(TestOutputDir, true);
        }

        Directory.CreateDirectory(TestOutputDir);
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        if (Directory.Exists(TestOutputDir))
        {
            Directory.Delete(TestOutputDir, true);
        }
    }

    #region Helpers matching exact CLI code

    /// <summary>
    /// Exact copy of MS2Create.Program.GetFilesRelative — no sorting, uses string.Remove extension.
    /// </summary>
    private static (string FullPath, string RelativePath)[] GetFilesRelativeCli(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar))
        {
            path += Path.DirectorySeparatorChar;
        }

        string[] files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
        var result = new (string FullPath, string RelativePath)[files.Length];

        for (int i = 0; i < files.Length; i++)
        {
            result[i] = (files[i], files[i].Remove(path));
        }

        return result;
    }

    /// <summary>
    /// Sorted variant for deterministic comparison tests.
    /// </summary>
    private static (string FullPath, string RelativePath)[] GetFilesRelativeSorted(string path)
    {
        if (!path.EndsWith(Path.DirectorySeparatorChar))
        {
            path += Path.DirectorySeparatorChar;
        }

        string[] files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);

        var result = new (string FullPath, string RelativePath)[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            result[i] = (files[i], files[i].Substring(path.Length));
        }

        return result;
    }

    private static CompressionType GetCompressionTypeFromFileExtension(string filePath) =>
        Path.GetExtension(filePath) switch
        {
            ".png" => CompressionType.Png,
            ".usm" => CompressionType.Usm,
            ".zlib" => CompressionType.Zlib,
            _ => CompressionType.Zlib,
        };

    /// <summary>
    /// Exact copy of MS2Create.Program.AddAndCreateFileToArchive
    /// </summary>
    private static void AddAndCreateFileToArchive(IMS2Archive archive, (string fullPath, string relativePath)[] filePaths, uint index)
    {
        (string filePath, string relativePath) = filePaths[index];

        uint id = index + 1;
        FileStream fsFile = File.OpenRead(filePath);
        IMS2FileInfo info = new MS2FileInfo(id.ToString(), relativePath);
        IMS2FileHeader header = new MS2FileHeader(fsFile.Length, id, 0, GetCompressionTypeFromFileExtension(filePath));
        IMS2File file = new MS2File(archive, fsFile, info, header, false);

        archive.Add(file);
    }

    /// <summary>
    /// Exact copy of MS2Create.Program.CreateArchive — concurrent Task.Run, no sorting.
    /// </summary>
    private static async Task<(string headerPath, string dataPath)> PackageServerFolderExactCli(string outputSubDir)
    {
        string outputPath = Path.Combine(TestOutputDir, outputSubDir);
        Directory.CreateDirectory(outputPath);

        string headerPath = Path.Combine(outputPath, Path.ChangeExtension(ArchiveName, "m2h"));
        string dataPath = Path.Combine(outputPath, Path.ChangeExtension(ArchiveName, "m2d"));

        var filePaths = GetFilesRelativeCli(ServerSourcePath);
        IMS2Archive archive = new MS2Archive(Repositories.Repos[MS2CryptoMode.MS2F]);

        // Exact same concurrent pattern as MS2Create
        var tasks = new Task[filePaths.Length];
        for (uint i = 0; i < filePaths.Length; i++)
        {
            uint ic = i;
            tasks[i] = Task.Run(() => AddAndCreateFileToArchive(archive, filePaths, ic));
        }

        await Task.WhenAll(tasks);

        await archive.SaveConcurrentlyAsync(headerPath, dataPath);

        return (headerPath, dataPath);
    }

    /// <summary>
    /// Exact copy of MS2Extract extraction logic.
    /// Creates a subfolder named after the archive, just like the CLI does.
    /// </summary>
    private static async Task ExtractArchiveExactCli(string headerFile, string dataFile, string destinationPath)
    {
        // MS2Extract creates: destinationPath/archiveName/
        string dstPath = Path.Combine(destinationPath, Path.GetFileNameWithoutExtension(headerFile));
        Directory.CreateDirectory(dstPath);

        using IMS2Archive archive = await MS2Archive.GetAndLoadArchiveAsync(headerFile, dataFile);

        foreach (IMS2File file in archive)
        {
            if (string.IsNullOrWhiteSpace(file.Name))
            {
                continue;
            }

            string fileDestinationPath = Path.Combine(dstPath, file.Name);
            await using Stream stream = await file.GetStreamAsync();
            await stream.CopyToAsync(fileDestinationPath);
        }
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    #endregion

    #region Exact CLI workflow tests

    [TestMethod]
    [Timeout(300000)]
    public async Task CliWorkflow_CreateThenExtract_FilesMatchOriginals()
    {
        // Step 1: MS2Create ./server ./out server MS2F
        var (headerPath, dataPath) = await PackageServerFolderExactCli("cli_roundtrip");

        Assert.IsTrue(File.Exists(headerPath), "Header file should exist");
        Assert.IsTrue(File.Exists(dataPath), "Data file should exist");
        Assert.IsTrue(new FileInfo(headerPath).Length > 0, "Header file should not be empty");
        Assert.IsTrue(new FileInfo(dataPath).Length > 0, "Data file should not be empty");

        // Step 2: MS2Extract ./out/server.m2h ./extracted
        string extractDest = Path.Combine(TestOutputDir, "cli_extracted");
        await ExtractArchiveExactCli(headerPath, dataPath, extractDest);

        // MS2Extract creates: cli_extracted/server/
        string extractedRoot = Path.Combine(extractDest, ArchiveName);
        Assert.IsTrue(Directory.Exists(extractedRoot), "Extracted subfolder should exist");

        // Step 3: Verify every original file matches the extracted file
        var originalFiles = GetFilesRelativeCli(ServerSourcePath);
        int verifiedCount = 0;
        var mismatches = new List<string>();

        foreach (var (fullPath, relativePath) in originalFiles)
        {
            string extractedPath = Path.Combine(extractedRoot, relativePath);

            if (!File.Exists(extractedPath))
            {
                mismatches.Add($"MISSING: {relativePath}");
                continue;
            }

            byte[] originalBytes = await File.ReadAllBytesAsync(fullPath);
            byte[] extractedBytes = await File.ReadAllBytesAsync(extractedPath);

            if (!originalBytes.SequenceEqual(extractedBytes))
            {
                mismatches.Add($"CONTENT MISMATCH: {relativePath} (original={originalBytes.Length}b, extracted={extractedBytes.Length}b)");
            }

            verifiedCount++;
        }

        if (mismatches.Count > 0)
        {
            Assert.Fail($"Found {mismatches.Count} issue(s):\n{string.Join("\n", mismatches)}");
        }

        Assert.AreEqual(originalFiles.Length, verifiedCount,
            $"Should verify all {originalFiles.Length} files");
    }

    [TestMethod]
    [Timeout(300000)]
    public async Task CliWorkflow_CreateMultipleTimes_DeterministicOutput()
    {
        // Run the exact CLI packaging 3 times
        var (h1, d1) = await PackageServerFolderExactCli("cli_det_run1");
        string hHash1 = ComputeFileHash(h1);
        string dHash1 = ComputeFileHash(d1);

        var (h2, d2) = await PackageServerFolderExactCli("cli_det_run2");
        string hHash2 = ComputeFileHash(h2);
        string dHash2 = ComputeFileHash(d2);

        var (h3, d3) = await PackageServerFolderExactCli("cli_det_run3");
        string hHash3 = ComputeFileHash(h3);
        string dHash3 = ComputeFileHash(d3);

        // Log sizes for debugging
        Console.WriteLine($"Run 1: header={new FileInfo(h1).Length}b data={new FileInfo(d1).Length}b");
        Console.WriteLine($"Run 2: header={new FileInfo(h2).Length}b data={new FileInfo(d2).Length}b");
        Console.WriteLine($"Run 3: header={new FileInfo(h3).Length}b data={new FileInfo(d3).Length}b");
        Console.WriteLine($"Header hashes: {hHash1} | {hHash2} | {hHash3}");
        Console.WriteLine($"Data hashes:   {dHash1} | {dHash2} | {dHash3}");

        Assert.AreEqual(hHash1, hHash2,
            $"Header hash mismatch between run 1 and 2.\nRun1: {hHash1}\nRun2: {hHash2}");
        Assert.AreEqual(dHash1, dHash2,
            $"Data hash mismatch between run 1 and 2.\nRun1: {dHash1}\nRun2: {dHash2}");
        Assert.AreEqual(hHash1, hHash3,
            $"Header hash mismatch between run 1 and 3.\nRun1: {hHash1}\nRun3: {hHash3}");
        Assert.AreEqual(dHash1, dHash3,
            $"Data hash mismatch between run 1 and 3.\nRun1: {dHash1}\nRun3: {dHash3}");
    }

    [TestMethod]
    [Timeout(300000)]
    public async Task CliWorkflow_Create_ArchiveFileCountMatchesSource()
    {
        var (headerPath, dataPath) = await PackageServerFolderExactCli("cli_count");

        using IMS2Archive archive = await MS2Archive.GetAndLoadArchiveAsync(headerPath, dataPath);

        var sourceFiles = GetFilesRelativeCli(ServerSourcePath);
        Assert.AreEqual(sourceFiles.Length, (int)archive.Count,
            $"Archive should contain {sourceFiles.Length} files, but has {archive.Count}");
    }

    #endregion

    #region Original sequential tests (for comparison)

    private static async Task<(string headerPath, string dataPath)> PackageServerFolderSequential(string outputSubDir)
    {
        string outputPath = Path.Combine(TestOutputDir, outputSubDir);
        Directory.CreateDirectory(outputPath);

        string headerPath = Path.Combine(outputPath, ArchiveName + HeaderFileExtension);
        string dataPath = Path.Combine(outputPath, ArchiveName + DataFileExtension);

        var archive = new MS2Archive(Repositories.Repos[MS2CryptoMode.MS2F]);
        var filePaths = GetFilesRelativeSorted(ServerSourcePath);

        for (uint i = 0; i < filePaths.Length; i++)
        {
            var (fullPath, relativePath) = filePaths[i];
            uint id = i + 1;
            FileStream fs = File.OpenRead(fullPath);
            IMS2FileInfo info = new MS2FileInfo(id.ToString(), relativePath);
            IMS2FileHeader header = new MS2FileHeader(fs.Length, id, 0, GetCompressionTypeFromFileExtension(fullPath));
            IMS2File file = new MS2File(archive, fs, info, header, false);
            archive.Add(file);
        }

        await archive.SaveConcurrentlyAsync(headerPath, dataPath);

        return (headerPath, dataPath);
    }

    [TestMethod]
    [Timeout(300000)]
    public async Task Sequential_ProducesDeterministicOutput()
    {
        var (h1, d1) = await PackageServerFolderSequential("seq_run1");
        string hHash1 = ComputeFileHash(h1);
        string dHash1 = ComputeFileHash(d1);

        var (h2, d2) = await PackageServerFolderSequential("seq_run2");
        string hHash2 = ComputeFileHash(h2);
        string dHash2 = ComputeFileHash(d2);

        Assert.AreEqual(hHash1, hHash2, $"Header hash mismatch.\nRun1: {hHash1}\nRun2: {hHash2}");
        Assert.AreEqual(dHash1, dHash2, $"Data hash mismatch.\nRun1: {dHash1}\nRun2: {dHash2}");
    }

    [TestMethod]
    [Timeout(300000)]
    public async Task Sequential_ThenExtract_FilesMatchOriginals()
    {
        var (headerPath, dataPath) = await PackageServerFolderSequential("seq_extract");

        string extractPath = Path.Combine(TestOutputDir, "seq_extracted");
        Directory.CreateDirectory(extractPath);

        using (IMS2Archive archive = await MS2Archive.GetAndLoadArchiveAsync(headerPath, dataPath))
        {
            foreach (var file in archive)
            {
                string destPath = Path.Combine(extractPath, file.Name);
                using Stream stream = await file.GetStreamAsync();
                await stream.CopyToAsync(destPath);
            }
        }

        var originalFiles = GetFilesRelativeSorted(ServerSourcePath);
        foreach (var (fullPath, relativePath) in originalFiles)
        {
            string extractedPath = Path.Combine(extractPath, relativePath);
            Assert.IsTrue(File.Exists(extractedPath), $"Missing: {relativePath}");

            byte[] originalBytes = await File.ReadAllBytesAsync(fullPath);
            byte[] extractedBytes = await File.ReadAllBytesAsync(extractedPath);
            CollectionAssert.AreEqual(originalBytes, extractedBytes, $"Content mismatch: {relativePath}");
        }
    }

    #endregion
}

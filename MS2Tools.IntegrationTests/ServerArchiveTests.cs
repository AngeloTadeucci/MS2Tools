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

    private static (string FullPath, string RelativePath)[] GetFilesRelative(string path)
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

    private static async Task<(string headerPath, string dataPath)> PackageServerFolder(string outputSubDir)
    {
        string outputPath = Path.Combine(TestOutputDir, outputSubDir);
        Directory.CreateDirectory(outputPath);

        string headerPath = Path.Combine(outputPath, ArchiveName + HeaderFileExtension);
        string dataPath = Path.Combine(outputPath, ArchiveName + DataFileExtension);

        var archive = new MS2Archive(Repositories.Repos[MS2CryptoMode.MS2F]);
        var filePaths = GetFilesRelative(ServerSourcePath);

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

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    [TestMethod]
    [Timeout(300000)] // 5 min timeout for large archive
    public async Task Package_ServerFolder_ProducesDeterministicOutput()
    {
        // First run
        var (headerPath1, dataPath1) = await PackageServerFolder("run1");
        string headerHash1 = ComputeFileHash(headerPath1);
        string dataHash1 = ComputeFileHash(dataPath1);

        Assert.IsTrue(File.Exists(headerPath1), "Header file should exist after first run");
        Assert.IsTrue(File.Exists(dataPath1), "Data file should exist after first run");
        Assert.IsTrue(new FileInfo(headerPath1).Length > 0, "Header file should not be empty");
        Assert.IsTrue(new FileInfo(dataPath1).Length > 0, "Data file should not be empty");

        // Second run
        var (headerPath2, dataPath2) = await PackageServerFolder("run2");
        string headerHash2 = ComputeFileHash(headerPath2);
        string dataHash2 = ComputeFileHash(dataPath2);

        Assert.AreEqual(headerHash1, headerHash2,
            $"Header hash mismatch between runs.\nRun1: {headerHash1}\nRun2: {headerHash2}");
        Assert.AreEqual(dataHash1, dataHash2,
            $"Data hash mismatch between runs.\nRun1: {dataHash1}\nRun2: {dataHash2}");

        // Third run for extra confidence
        var (headerPath3, dataPath3) = await PackageServerFolder("run3");
        string headerHash3 = ComputeFileHash(headerPath3);
        string dataHash3 = ComputeFileHash(dataPath3);

        Assert.AreEqual(headerHash1, headerHash3,
            $"Header hash mismatch on third run.\nRun1: {headerHash1}\nRun3: {headerHash3}");
        Assert.AreEqual(dataHash1, dataHash3,
            $"Data hash mismatch on third run.\nRun1: {dataHash1}\nRun3: {dataHash3}");
    }

    [TestMethod]
    [Timeout(300000)]
    public async Task Package_ThenExtract_ServerFolder_FilesMatchOriginals()
    {
        var (headerPath, dataPath) = await PackageServerFolder("extract_test");

        string extractPath = Path.Combine(TestOutputDir, "extracted");
        Directory.CreateDirectory(extractPath);

        // Extract
        using (IMS2Archive archive = await MS2Archive.GetAndLoadArchiveAsync(headerPath, dataPath))
        {
            Assert.IsTrue(archive.Count > 0, "Archive should contain files");

            foreach (var file in archive)
            {
                string destPath = Path.Combine(extractPath, file.Name);

                using Stream stream = await file.GetStreamAsync();
                await stream.CopyToAsync(destPath);
            }
        }

        // Verify extracted files match originals
        var originalFiles = GetFilesRelative(ServerSourcePath);
        int verifiedCount = 0;

        foreach (var (fullPath, relativePath) in originalFiles)
        {
            string extractedPath = Path.Combine(extractPath, relativePath);
            Assert.IsTrue(File.Exists(extractedPath),
                $"Extracted file missing: {relativePath}");

            byte[] originalBytes = await File.ReadAllBytesAsync(fullPath);
            byte[] extractedBytes = await File.ReadAllBytesAsync(extractedPath);

            CollectionAssert.AreEqual(originalBytes, extractedBytes,
                $"Content mismatch for: {relativePath}");
            verifiedCount++;
        }

        Assert.AreEqual(originalFiles.Length, verifiedCount,
            "Number of verified files should match number of original files");
    }

    [TestMethod]
    [Timeout(300000)]
    public async Task Package_ServerFolder_ArchiveFileCountMatchesSourceFileCount()
    {
        var (headerPath, dataPath) = await PackageServerFolder("count_test");

        using IMS2Archive archive = await MS2Archive.GetAndLoadArchiveAsync(headerPath, dataPath);

        var sourceFiles = GetFilesRelative(ServerSourcePath);
        Assert.AreEqual(sourceFiles.Length, (int)archive.Count,
            $"Archive should contain {sourceFiles.Length} files, but has {archive.Count}");
    }
}

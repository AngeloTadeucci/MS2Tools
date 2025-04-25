using System.Text;
using MiscUtils;
using MiscUtils.Logging;
using MS2Lib;
using Logger = MiscUtils.Logging.SimpleLogger;
using LogMode = MiscUtils.Logging.LogMode;

namespace MS2Create;

internal class Program {
    private const int MinNumberArgs = 4;
    private const int OptionalNumberArgLog = 5;

    // args
    private static string _sourcePath;
    private static string _destinationPath;
    private static string _archiveName;
    private static MS2CryptoMode _cryptoMode;
    private static LogMode? _argsLogMode;

#if DEBUG
    private static readonly StreamWriter StreamWriter = new StreamWriter("output.log");
#endif

    private static async Task Main(string[] commandLineArgs) {
#if DEBUG
        static void Out(string format, object[] args) {
            StreamWriter.WriteLine(args == null ? format : string.Format(format, args));
        }

        Logger.Out = DebugLogger.Out = Out;
        Logger.LoggingLevel = LogMode.Debug;
#else
        Logger.LoggingLevel = LogMode.Warning;
#endif

        await RunAsync(commandLineArgs);

#if DEBUG
        StreamWriter.Dispose();
        Console.WriteLine("Press any key to close...");
        Console.ReadKey();
#endif
    }

    private static async Task RunAsync(string[] args) {
        if (!ParseArgs(args)) {
            DisplayArgsHelp();
            return;
        }

        if (_argsLogMode.HasValue) {
            Logger.LoggingLevel = _argsLogMode.Value;
        }

        Logger.Verbose($"Archiving folder \"{_sourcePath}\" to \"{_destinationPath}\".");
        try {
            await CreateArchive(_sourcePath, _destinationPath);
        } catch (Exception ex) {
            Logger.Error(ex);
        }
    }

    private static async Task CreateArchive(string sourcePath, string destinationPath) {
        Directory.CreateDirectory(destinationPath);
        string dstArchive = Path.Combine(destinationPath, _archiveName);
        string headerPath = Path.ChangeExtension(dstArchive, "m2h");
        string dataPath = Path.ChangeExtension(dstArchive, "m2d");
        Logger.Info($"Archiving folder \"{_sourcePath}\" into \"{headerPath}\" and \"{dataPath}\"");

        if (!Directory.Exists(sourcePath)) {
            throw new Exception($"Directory doesn't exist \"{sourcePath}\".");
        }

        (string FullPath, string RelativePath)[] filePaths = GetFilesRelative(sourcePath);
        var files = new MS2File[filePaths.Length];
        var tasks = new Task[filePaths.Length];
        IMS2Archive archive = new MS2Archive(Repositories.Repos[_cryptoMode]);

        for (uint i = 0; i < filePaths.Length; i++) {
            uint ic = i;
            tasks[i] = Task.Run(() => AddAndCreateFileToArchive(archive, filePaths, ic));
        }

        await Task.WhenAll(tasks);

        await archive.SaveConcurrentlyAsync(headerPath, dataPath);
    }

    private static void AddAndCreateFileToArchive(IMS2Archive archive, (string fullPath, string relativePath)[] filePaths, uint index) {
        (string filePath, string relativePath) = filePaths[index];

        uint id = index + 1;
        FileStream fsFile = File.OpenRead(filePath);
        IMS2FileInfo info = new MS2FileInfo(id.ToString(), relativePath);
        IMS2FileHeader header = new MS2FileHeader(fsFile.Length, id, 0, GetCompressionTypeFromFileExtension(filePath));
        IMS2File file = new MS2File(archive, fsFile, info, header, false);

        archive.Add(file);
    }

    private static (string FullPath, string RelativePath)[] GetFilesRelative(string path) {
        if (!path.EndsWith('\\')) {
            path += '\\';
        }

        string[] files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
        var result = new (string FullPath, string RelativePath)[files.Length];

        for (int i = 0; i < files.Length; i++) {
            result[i] = (files[i], files[i].Remove(path));
        }

        return result;
    }

    private static CompressionType GetCompressionTypeFromFileExtension(string filePath, CompressionType defaultCompressionType = CompressionType.Zlib) {
        return Path.GetExtension(filePath) switch {
            ".png" => CompressionType.Png,
            ".usm" => CompressionType.Usm,
            ".zlib" => CompressionType.Zlib,
            _ => defaultCompressionType,
        };
    }

    private static void DisplayArgsHelp() {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("MS2Create - Thanks Miyu for this tool");
        sb.AppendLine("Description: ");
        sb.AppendLine("Creates a MapleStory2 archive from a given folder.");
        sb.AppendLine();
        sb.AppendLine("Usage: ");
        sb.AppendLine("MS2Create.exe <source> <destination> <archive name> <mode> [syncMode = Async] [logMode = Warning]");
        sb.AppendLine("<source> - the folder to be archived.");
        sb.AppendLine("<destination> - the folder where the archive will be created.");
        sb.AppendLine("<archive name> - the name of the resulting archive.");
        sb.AppendLine("<mode> - the mode to use to encrypt the archive.");
        sb.AppendLine("List of available modes: MS2F, NS2F, OS2F, PS2F");
        sb.AppendLine("<logMode> - optional; Debug, Verbose, Info, Warning or Error");

        Console.WriteLine(sb.ToString());
    }

    private static bool ParseArgs(string[] args) {
        if (args.Length < MinNumberArgs) {
            Logger.Error("not enough args");
            return false;
        }

        if (args.Any(string.IsNullOrWhiteSpace)) {
            Logger.Error("one or more of the args is not valid");
            return false;
        }

        _sourcePath = Path.GetFullPath(args[0]);
        _destinationPath = Path.GetFullPath(args[1]);
        _archiveName = args[2];
        _cryptoMode = (MS2CryptoMode) Enum.Parse(typeof(MS2CryptoMode), args[3]);

        if (args.Length >= OptionalNumberArgLog) {
            _argsLogMode = (LogMode) Enum.Parse(typeof(LogMode), args[OptionalNumberArgLog - 1]);
        }

        return true;
    }
}

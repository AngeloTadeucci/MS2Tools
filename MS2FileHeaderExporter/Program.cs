﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MiscUtils;
using MiscUtils.IO;
using MiscUtils.Logging;
using MS2Lib;
using Logger = MiscUtils.Logging.SimpleLogger;
using LogMode = MiscUtils.Logging.LogMode;

namespace MS2FileHeaderExporter;

internal class Program {
    private const string HeaderFileExtension = "m2h";
    private const string DataFileExtension = "m2d";

    private const string HeaderExportFileName = "exported_file_header.txt";
    private const string DirectorySeparatorForExportFileName = "_";
    private const string FileTypeMapFileName = "file_types.map";
    private const int FileTypesKeyPadding = 12;
    private const string RootFolderIdMapFileName = "root_folder_id.map";
    private const int RootFolderIdsKeyPadding = 24;

    private const int MinArgsLength = 2;

    private static readonly ConcurrentDictionary<string, HashSet<string>> FileTypes = new ConcurrentDictionary<string, HashSet<string>>();

    private static readonly ConcurrentDictionary<string, HashSet<string>> RootFolderIds = new ConcurrentDictionary<string, HashSet<string>>();

    // args
    private static string SourcePath;
    private static string DestinationPath;
    private static LogMode? ArgsLogMode;

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

        await RunAsync(commandLineArgs).ConfigureAwait(false);

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

        if (ArgsLogMode.HasValue) {
            Logger.LoggingLevel = ArgsLogMode.Value;
        }

        Directory.CreateDirectory(DestinationPath);

        if (Directory.Exists(SourcePath)) {
            Logger.Debug("Directory specified");
            Logger.Verbose($"Exporting all archives from \"{SourcePath}\" to \"{DestinationPath}\".");
            try {
                await ExportArchivesInDirectoryAsync(SourcePath, DestinationPath).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
                return;
            }
        } else {
            Logger.Debug("File Specified");
            Logger.Verbose($"Exporting archive \"{SourcePath}\" to \"{DestinationPath}\".");
            try {
                await ExportArchiveAsync(SourcePath, DestinationPath).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Error(ex);
                return;
            }
        }

        List<KeyValuePair<string, HashSet<string>>> fileTypesList = FileTypes.ToList();
        fileTypesList.Sort((kvp1, kvp2) => kvp1.Key.CompareTo(kvp2.Key));
        List<KeyValuePair<string, HashSet<string>>> rootFolderIdList = RootFolderIds.ToList();
        rootFolderIdList.Sort((kvp1, kvp2) => kvp1.Key.CompareTo(kvp2.Key));

        await using (var sw = new StreamWriter(Path.Combine(DestinationPath, FileTypeMapFileName))) {
            foreach (KeyValuePair<string, HashSet<string>> kvp in fileTypesList) {
                await sw.WriteLineAsync(kvp.Key.PadRight(FileTypesKeyPadding) + " - " + string.Join(", ", kvp.Value)).ConfigureAwait(false);
            }
        }

        await using (var sw = new StreamWriter(Path.Combine(DestinationPath, RootFolderIdMapFileName))) {
            foreach (KeyValuePair<string, HashSet<string>> kvp in rootFolderIdList) {
                await sw.WriteLineAsync(kvp.Key.PadRight(RootFolderIdsKeyPadding) + " - " + string.Join(", ", kvp.Value)).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExportArchivesInDirectoryAsync(string sourcePath, string destinationPath) {
        if (!Directory.Exists(sourcePath)) {
            throw new Exception($"Directory doesn't exist \"{sourcePath}\".");
        }

        foreach ((string headerFile, string dataFile) in GetFiles(sourcePath)) {
            if (!sourcePath.EndsWith(Path.DirectorySeparatorChar.ToString())) {
                sourcePath += Path.DirectorySeparatorChar;
            }

            string directoryNames = Path.GetDirectoryName(headerFile.Remove(sourcePath)).Replace(Path.DirectorySeparatorChar.ToString(), DirectorySeparatorForExportFileName);
            string destinationFilePath = Path.Combine(destinationPath, string.Join(DirectorySeparatorForExportFileName, directoryNames, Path.GetFileNameWithoutExtension(headerFile), HeaderExportFileName));

            await CreateExportArchiveAsync(destinationFilePath, headerFile, dataFile).ConfigureAwait(false);
        }
    }

    private static Task ExportArchiveAsync(string headerFile, string destinationPath) {
        headerFile = Path.ChangeExtension(headerFile, HeaderFileExtension);

        if (!File.Exists(headerFile)) {
            throw new Exception($"File doesn't exist \"{headerFile}\".");
        }

        string dataFile = GetDataFileFromHeaderFile(headerFile);
        string destinationFilePath = Path.Combine(destinationPath, string.Join(DirectorySeparatorForExportFileName, Path.GetFileNameWithoutExtension(headerFile), HeaderExportFileName));

        return CreateExportArchiveAsync(destinationFilePath, headerFile, dataFile);
    }

    private static Task CreateExportArchiveAsync(string destinationPath, string headerFile, string dataFile) {
        Logger.Verbose($"Starting exporting of: header \"{headerFile}\", data \"{dataFile}\".");
        return ExportArchiveAsync(headerFile, dataFile, destinationPath);
    }

    private static async Task ExportArchiveAsync(string headerFile, string dataFile, string destinationFilePath) {
        await using var swExport = new StreamWriter(destinationFilePath);
        using IMS2Archive archive = await MS2Archive.GetAndLoadArchiveAsync(headerFile, dataFile).ConfigureAwait(false);

        foreach (IMS2File file in archive) {
            Logger.Info($"Exporting file \"{file.Name}\". ({file.Header.Id}/{archive.Count})");
            await ExportFileAsync(swExport, file).ConfigureAwait(false);
        }
    }

    private static Task ExportFileAsync(StreamWriter swExport, IMS2File file) {
        string fileName = file.Name;
        if (string.IsNullOrWhiteSpace(fileName)) {
            Logger.Warning($"File number \"{file.Id}\" has no name and will be ignored.");
            return Task.CompletedTask;
        }

        uint id = file.Header.Id;
        CompressionType typeId = file.Header.CompressionType;

        FileTypes.AddOrUpdate(Path.GetExtension(fileName), new HashSet<string> {
            typeId.ToString(),
        }, (_, v) => {
            v.Add(typeId.ToString());
            return v;
        });

        string rootDirectory = PathEx.GetRootDirectory(fileName);
        if (!string.IsNullOrEmpty(rootDirectory)) {
            if (string.IsNullOrEmpty(file.Info.RootFolderId)) {
                Logger.Warning($"Root folder id is empty but it has a root folder ({rootDirectory})!");
            }

            RootFolderIds.AddOrUpdate(rootDirectory, new HashSet<string> {
                file.Info.RootFolderId,
            }, (_, v) => {
                v.Add(file.Info.RootFolderId);
                return v;
            });
        }

        return swExport.WriteLineAsync($"{id:d6} - Type:{typeId}; Info_Id:{file.Info.Id}; Info_Path={file.Info.Path}; Info_RootFolderId={file.Info.RootFolderId}");
    }

    private static IEnumerable<(string headerFile, string dataFile)> GetFiles(string path) {
        string[] headerFiles = Directory.GetFiles(path, $"*.{HeaderFileExtension}", SearchOption.AllDirectories);

        for (int i = 0; i < headerFiles.Length; i++) {
            string headerFile = headerFiles[i];
            string dataFile = GetDataFileFromHeaderFile(headerFile);

            yield return (headerFile, dataFile);
        }
    }

    private static string GetDataFileFromHeaderFile(string headerFile) {
        string dataFile = Path.ChangeExtension(headerFile, DataFileExtension);

        if (!File.Exists(dataFile)) {
            throw new Exception($"Matching data file for [{headerFile}] not found.");
        }

        return dataFile;
    }

    private static void DisplayArgsHelp() {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("MS2FileHeaderExporter Copyright (C) Miyu");
        sb.AppendLine("Description: ");
        sb.AppendLine("Exports the file headers from a given MapleStory2 archive.");
        sb.AppendLine();
        sb.AppendLine("Usage: ");
        sb.AppendLine("MS2FileHeaderExporter.exe <source> <destination>");
        sb.AppendLine("<source> - either a directory to export all");
        sb.AppendLine("\tarchives' files, either a specific archive.");
        sb.AppendLine("<destination> - the folder where all the file data");
        sb.AppendLine("\tfrom the archive will be exported.");

        Console.WriteLine(sb.ToString());
    }

    private static bool ParseArgs(string[] args) {
        if (args.Length < MinArgsLength) {
            Logger.Error("not enough args");
            return false;
        }

        if (args.Any(s => string.IsNullOrWhiteSpace(s))) {
            Logger.Error("one or more of the args is not valid");
            return false;
        }

        SourcePath = Path.GetFullPath(args[0]);
        DestinationPath = Path.GetFullPath(args[1]);

        if (args.Length > MinArgsLength) {
            ArgsLogMode = (LogMode) Enum.Parse(typeof(LogMode), args[MinArgsLength + 0]);
        }

        return true;
    }
}

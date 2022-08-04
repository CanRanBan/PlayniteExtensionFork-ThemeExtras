﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Extras
{
    public class ExtendedTheme
    {
        public string Name => ThemeManifest.Name;
        public string Id => ThemeManifest.Id;
        public string RootPath { get; private set; }
        public string LastChangeFilePath { get; private set; }
        public Models.ThemeManifest ThemeManifest { get; private set; }
        public Models.ThemeExtrasManifest ThemeExtrasManifest { get; private set; }
        public string BackupPath { get; private set; }

        public static IEnumerable<ExtendedTheme> CreateExtendedManifests()
        {
            var api = Playnite.SDK.API.Instance;
            var destopThemeDirectory = Path.Combine(api.Paths.ConfigurationPath, "Themes", "Desktop");
            if (Directory.Exists(destopThemeDirectory))
            {
                var themeDirectories = Directory.GetDirectories(destopThemeDirectory);
                foreach (var themeDirectory in themeDirectories)
                {
                    if (TryCreate(themeDirectory, out var extendedTheme))
                    {
                        yield return extendedTheme;
                    }
                }
            }
        }

        public static bool TryCreate(string themeRootPath, out ExtendedTheme extendedTheme)
        {
            extendedTheme = new ExtendedTheme();
            extendedTheme.RootPath = themeRootPath;
            extendedTheme.LastChangeFilePath = Path.Combine(themeRootPath, "lastChanged.json");

            var extraManifestPath = Path.Combine(themeRootPath, Extras.ExtrasManifestFileName);
            if (!File.Exists(extraManifestPath))
            {
                return false;
            }

            if (!Playnite.SDK.Data.Serialization.TryFromYamlFile<Models.ThemeExtrasManifest>(extraManifestPath, out var extrasManifest))
            {
                return false;
            }

            extendedTheme.ThemeExtrasManifest = extrasManifest;

            var themManifestPath = Path.Combine(themeRootPath, Extras.ThemeManifestFileName);
            if (!File.Exists(themManifestPath))
            {
                return false;
            }
            if (!Playnite.SDK.Data.Serialization.TryFromYamlFile<Models.ThemeManifest>(themManifestPath, out var manifest))
            {
                return false;
            }

            extendedTheme.ThemeManifest = manifest;
            extendedTheme.BackupPath = Path.Combine(Extras.Instance.GetPluginUserDataPath(), "ThemeBackups", manifest.Id);

            return true;
        }

        public IEnumerable<string> GetRelativeFilePaths(string basePath, string relativeDirectoryPath)
        {
            var directorySourcePath = Path.Combine(basePath, relativeDirectoryPath);

            foreach (var file in Directory.GetFiles(directorySourcePath, "*", SearchOption.AllDirectories))
            {
                var relativePath = file.Replace(basePath + Path.DirectorySeparatorChar, "");
                yield return relativePath;
            }
        }

        public void ClearBackup()
        {
            if (Directory.Exists(BackupPath))
            {
                Directory.Delete(BackupPath, true);
            }
        }

        public void Backup()
        {
            if (ThemeExtrasManifest.PersistentPaths is IEnumerable<string> relativePaths)
            {
                var files = new List<string>();
                foreach(var path in relativePaths)
                {
                    string sourcePath = Path.Combine(RootPath, path);
                    if (File.Exists(sourcePath))
                    {
                        files.Add(path);
                    } 
                    else if (Directory.Exists(sourcePath))
                    {
                        files.AddRange(GetRelativeFilePaths(RootPath, path));
                    }
                }
                if (!File.Exists(LastChangeFilePath))
                {
                    var timestamps = new Dictionary<string, DateTime>();
                    foreach (var file in files)
                    {
                        var fullPath = Path.Combine(RootPath, file);
                        if (File.Exists(fullPath))
                        {
                            var lastWrite = File.GetLastWriteTime(fullPath);
                            timestamps[file] = lastWrite;
                        }
                    }
                    var json = Playnite.SDK.Data.Serialization.ToJson(timestamps, true);
                    File.WriteAllText(LastChangeFilePath, json);
                } else
                {
                    var timestamps = Playnite.SDK.Data.Serialization.FromJsonFile<Dictionary<string, DateTime>>(LastChangeFilePath);
                    var anyBackups = false;
                    foreach (var file in files)
                    {
                        if (timestamps.TryGetValue(file, out var lastChanged))
                        {
                            var fullPath = Path.Combine(RootPath, file);
                            var newLastChanged = File.GetLastWriteTime(fullPath);
                            if (newLastChanged != lastChanged)
                            {
                                if (BackupFile(file))
                                {
                                    timestamps[file] = newLastChanged;
                                    anyBackups = true;
                                }
                            }
                        }
                        if (anyBackups)
                        {
                            var json = Playnite.SDK.Data.Serialization.ToJson(timestamps, true);
                            File.WriteAllText(LastChangeFilePath, json);
                        }
                    }
                }
            }
        }

        public bool BackupFile(string relativeFilePath)
        {
            try
            {
                var fileBackupPath = Path.Combine(BackupPath, relativeFilePath);
                var directoryBackupPath = Path.GetDirectoryName(fileBackupPath);
                if (!Directory.Exists(directoryBackupPath))
                {
                    Directory.CreateDirectory(directoryBackupPath);
                }

                string sourceFilePath = Path.Combine(RootPath, relativeFilePath);
                File.Copy(sourceFilePath, fileBackupPath, true);
                Extras.logger.Debug($"Backed {relativeFilePath} from {RootPath} to {BackupPath}.");
                return true;
            }
            catch (Exception ex)
            {
                Extras.logger.Error(ex, $"Failed to backup {relativeFilePath} of theme {Name}.");
                return false;
            }
        }

        public void RestoreFile(string relativeFilePath)
        {
            try
            {
                var fileTargetPath = Path.Combine(RootPath, relativeFilePath);
                var directoryTargetPath = Path.GetDirectoryName(fileTargetPath);
                if (!Directory.Exists(directoryTargetPath))
                {
                    Directory.CreateDirectory(directoryTargetPath);
                }

                string sourceFilePath = Path.Combine(BackupPath, relativeFilePath);
                if (File.Exists(sourceFilePath))
                {
                    File.Copy(sourceFilePath, fileTargetPath, true);
                    Extras.logger.Debug($"Restored {relativeFilePath} from {BackupPath} to {RootPath}.");
                }
            }
            catch (Exception ex)
            {
                if (System.Diagnostics.Debugger.IsAttached && ex is UnauthorizedAccessException)
                {
                    Playnite.SDK.API.Instance.Notifications.Add(new Playnite.SDK.NotificationMessage(
                        "FileRestoreFailed",
                        $"Failed to restore {relativeFilePath} of theme {Name} because file access was denied. If this file is an image used as a theme resource, make sure to set \"CacheOption=\"OnLoad\"\".", 
                        Playnite.SDK.NotificationType.Error
                    ));
                }
                Extras.logger.Error(ex, $"Failed to restore {relativeFilePath} of theme {Name}.");
            }
        }

        public void Restore()
        {
            if (ThemeExtrasManifest.PersistentPaths is IEnumerable<string> relativePaths)
            {
                if (File.Exists(LastChangeFilePath))
                {
                    // Theme wasn't updated, no need to restore
                    return;
                }

                foreach (var path in relativePaths)
                {
                    string sourcePath = Path.Combine(BackupPath, path);
                    if (File.Exists(sourcePath))
                    {
                        RestoreFile(path);
                    }
                    else if (Directory.Exists(sourcePath))
                    {
                        foreach(var relativePath in GetRelativeFilePaths(BackupPath, path))
                        {
                            RestoreFile(relativePath);
                        }
                    }
                }
            }
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

using FATX.FileSystem;

using FATXTools.Database;

namespace FATXTools.Tasks
{
    /// <summary>
    /// This class is similar to SaveContentTask and should both eventually be merged into a single class.
    /// It is responsible for saving files all files including those that were not deleted and those that were 
    /// recovered. Recovered files are recovered using the clusters stored in the <see cref="DatabaseFile.ClusterChain"/>
    /// field. Stray directories are saved to folders that start with "Cluster" and have the cluster index as a suffix.
    /// </summary>
    public class RecoveryTask
    {
        private Volume _volume;
        private IProgress<(int, string)> _progress;
        private CancellationToken _cancellationToken;

        private long _numFiles;
        private int _numSaved = 0;
        private string _currentFile = string.Empty;

        public RecoveryTask(Volume volume, CancellationToken cancellationToken, IProgress<(int, string)> progress)
        {
            _volume = volume;
            _cancellationToken = cancellationToken;
            _progress = progress;
        }

        public static Action<CancellationToken, IProgress<(int, string)>> RunSaveTask(Volume volume, string path, DatabaseFile node)
        {
            return (cancellationToken, progress) =>
            {
                var task = new RecoveryTask(volume, cancellationToken, progress);

                task.Save(path, node);
            };
        }

        public static Action<CancellationToken, IProgress<(int, string)>> RunSaveAllTask(Volume volume, string path, List<DatabaseFile> nodes)
        {
            return (cancellationToken, progress) =>
            {
                try
                {
                    var task = new RecoveryTask(volume, cancellationToken, progress);

                    task.SaveAll(path, nodes);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Save all cancelled");
                }
            };
        }

        public static Action<CancellationToken, IProgress<(int, string)>> RunSaveClustersTask(Volume volume, string path, Dictionary<string, List<DatabaseFile>> clusters)
        {
            return (cancellationToken, progress) =>
            {
                var task = new RecoveryTask(volume, cancellationToken, progress);

                task.SaveClusters(path, clusters);
            };
        }

        /// <summary>
        /// Save a single file node to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="node">The file node to save.</param>
        public void Save(string path, DatabaseFile node)
        {
            _numFiles = node.CountFiles();

            Console.WriteLine($"Saving {_numFiles} files.");

            SaveNode(path, node);
        }

        /// <summary>
        /// Save a list of file nodes to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="nodes">The list of files to save.</param>
        public void SaveAll(string path, List<DatabaseFile> nodes)
        {
            _numFiles = CountFiles(nodes);

            Console.WriteLine($"Saving {_numFiles} files.");

            foreach (var node in nodes)
            {
                SaveNode(path, node);
            }
        }

        public void SaveClusters(string path, Dictionary<string, List<DatabaseFile>> clusters)
        {
            _numFiles = 0;
            foreach (var cluster in clusters)
            {
                _numFiles += CountFiles(cluster.Value);
            }

            foreach (var cluster in clusters)
            {
                string clusterDir = path + "\\" + cluster.Key;

                Directory.CreateDirectory(clusterDir);

                foreach (var node in cluster.Value)
                {
                    if (node.IsDirectory())
                    {
                        SaveDirectory(path, node);
                    }
                    else
                    {
                        SaveNode(path, node);
                    }
                }
            }
        }

        private long CountFiles(List<DatabaseFile> dirents)
        {
            // DirectoryEntry.CountFiles does not count deleted files
            long n = 0;

            foreach (var node in dirents)
            {
                if (node.IsDirectory())
                {
                    n += CountFiles(node.Children) + 1;
                }
                else
                {
                    n++;
                }
            }

            return n;
        }

        /// <summary>
        /// Displays an error dialog window that asks whether or not to retry the IO operation.
        /// </summary>
        /// <param name="e">The exception that occured.</param>
        /// <returns>The user's response as a DialogResult.</returns>
        private DialogResult ShowIOErrorDialog(Exception e)
        {
            return MessageBox.Show($"{e.Message}",
                "Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
        }

        /// <summary>
        /// Writes the file's data blocks to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="node">The file node to save.</param>
        private void WriteFile(string path, DatabaseFile node)
        {
            using (FileStream outFile = File.OpenWrite(path))
            {
                uint bytesLeft = node.FileSize;

                foreach (uint cluster in node.ClusterChain)
                {
                    byte[] clusterData = _volume.ClusterReader.ReadCluster(cluster);

                    var writeSize = Math.Min(bytesLeft, _volume.BytesPerCluster);
                    outFile.Write(clusterData, 0, (int)writeSize);

                    bytesLeft -= writeSize;
                }
            }
        }

        /// <summary>
        /// Sets a file's timestamps.
        /// </summary>
        /// <param name="path">The path to the file.</param>
        /// <param name="node">The file node that holds the timestamps.</param>
        private void FileSetTimeStamps(string path, DatabaseFile node)
        {
            File.SetCreationTime(path, node.CreationTime.AsDateTime());
            File.SetLastWriteTime(path, node.LastWriteTime.AsDateTime());
            File.SetLastAccessTime(path, node.LastAccessTime.AsDateTime());
        }

        /// <summary>
        /// Set's a directory's timestamps. This should be done after all files inside the directory
        /// have been written.
        /// </summary>
        /// <param name="path">The path to the folder.</param>
        /// <param name="node">The file node that holds the timestamps.</param>
        private void DirectorySetTimestamps(string path, DatabaseFile node)
        {
            Directory.SetCreationTime(path, node.CreationTime.AsDateTime());
            Directory.SetLastWriteTime(path, node.LastWriteTime.AsDateTime());
            Directory.SetLastAccessTime(path, node.LastAccessTime.AsDateTime());
        }

        /// <summary>
        /// Executes an IO operation until it succeeds or until the user decides not to retry.
        /// </summary>
        /// <param name="action">The IO operation to execute.</param>
        private void TryIOOperation(Action action)
        {
            var dialogResult = DialogResult.None;

            while (true)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    dialogResult = ShowIOErrorDialog(e);
                }

                if (dialogResult != DialogResult.Retry)
                    break;
            }
        }

        private void ReportProgress()
        {
            var percent = (int)(((float)_numSaved / (float)_numFiles) * 100);
            _progress.Report((percent, $"{_numSaved}/{_numFiles}: {_currentFile}"));
        }

        /// <summary>
        /// Save the file node to the specified path.
        /// </summary>
        /// <param name="path">The path to save the file to.</param>
        /// <param name="node">The file node to save.</param>
        private void SaveFile(string path, DatabaseFile node)
        {
            path = path + "\\" + node.FileName;
            //Console.WriteLine(path);

            _currentFile = node.FileName;
            _numSaved++;
            ReportProgress();

            _volume.ClusterReader.ReadCluster(node.FirstCluster);

            TryIOOperation(() =>
            {
                WriteFile(path, node);

                FileSetTimeStamps(path, node);
            });

            if (_cancellationToken.IsCancellationRequested)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Save the file node for a directory to the specified path.
        /// </summary>
        /// <param name="path">The path to save the directory into.</param>
        /// <param name="node">The directory's file node.</param>
        private void SaveDirectory(string path, DatabaseFile node)
        {
            path = path + "\\" + node.FileName;
            //Console.WriteLine($"{path}");

            _currentFile = node.FileName;
            _numSaved++;
            ReportProgress();

            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            foreach (DatabaseFile child in node.Children)
            {
                SaveFile(path, child);
            }

            TryIOOperation(() =>
            {
                DirectorySetTimestamps(path, node);
            });

            if (_cancellationToken.IsCancellationRequested)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>
        /// Save a file node. This will save either directory or a file depending on the node's type.
        /// </summary>
        /// <param name="path">The path to save the file node to.</param>
        /// <param name="node">The file node to save.</param>
        public void SaveNode(string path, DatabaseFile node)
        {
            if (node.IsDirectory())
            {
                SaveDirectory(path, node);
            }
            else
            {
                SaveFile(path, node);
            }
        }
    }
}

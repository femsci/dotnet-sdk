﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.DotNet.Cli.Utils;

namespace Microsoft.DotNet.MsiInstallerTests
{
    internal class VirtualMachine : IDisposable
    {
        ITestOutputHelper Log { get; }
        public VMControl VMControl { get; }

        static VMStateTree _rootState;
        VMStateTree _currentState;
        VMStateTree _currentAppliedState;

        string _stateFile;

        public VirtualMachine(ITestOutputHelper log)
        {
            Log = log;
            VMControl = new VMControl(log);

            _stateFile = Path.Combine(Environment.CurrentDirectory, "vmstate.json");

            //  Load root state from file, if it exists
            if (File.Exists(_stateFile))
            {
                string json = File.ReadAllText(_stateFile);
                _rootState = JsonSerializer.Deserialize<SerializeableVMStateTree>(json, GetSerializerOptions()).ToVMStateTree();
            }
            else
            {
                if (_rootState == null)
                {
                    _rootState = new VMStateTree()
                    {
                        SnapshotId = "60667B5C-EC88-430E-8C69-5E4214123941",
                        SnapshotName = "Updated expired password, enabled Remote Management",
                    };
                }
            }

            _currentState = _rootState;

            VMControl.ApplySnapshotAsync(_currentState.SnapshotId).Wait();
            _currentAppliedState = _currentState;
        }
        public void Dispose()
        {
            string json = JsonSerializer.Serialize(_rootState.ToSerializeable(), GetSerializerOptions());
            File.WriteAllText(_stateFile, json);

            VMControl.Dispose();
        }

        JsonSerializerOptions GetSerializerOptions()
        {
            return new JsonSerializerOptions()
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Converters = { new JsonStringEnumConverter() }
            };
        }

        public CommandResult RunCommand(params string[] args)
        {
            if (args.Length == 0)
            {
                throw new ArgumentException("Must provide at least one argument", nameof(args));
            }

            var action = new SerializedVMAction
            {
                Type = VMActionType.RunCommand,
                Arguments = args.ToList(),
            };

            var vmActionResult = Apply(action);

            return vmActionResult.ToCommandResult();
        }

        public void CopyFile(string localSource, string vmDestination)
        {
            throw new NotImplementedException();
        }

        public void CopyFolder(string localSource, string vmDestination)
        {
            var action = new SerializedVMAction
            {
                Type = VMActionType.CopyFolderToVM,
                SourcePath = localSource,
                TargetPath = vmDestination,
                ContentId = GetDirectoryContentId(localSource),
            };

            var vmActionResult = Apply(action);

            vmActionResult.ToCommandResult().Should().Pass();
        }

        public void WriteFile(string vmDestination, string contents)
        {
            var action = new SerializedVMAction
            {
                Type = VMActionType.WriteFileToVM,
                TargetPath = vmDestination,
                FileContents = contents,
            };

            var vmActionResult = Apply(action);

            vmActionResult.ToCommandResult().Should().Pass();
        }

        public RemoteDirectory GetRemoteDirectory(string path)
        {
            throw new NotImplementedException();
        }

        VMActionResult Apply(SerializedVMAction action)
        {
            if (_currentState.Actions.TryGetValue(action, out var result))
            {
                _currentState = result.resultingState;
                return result.actionResult;
            }

            if (_currentAppliedState != _currentState)
            {
                VMControl.ApplySnapshotAsync(_currentState.SnapshotId).Wait();
            }

            var actionResult = Run(action);

            string actionDescription = action.GetDescription();

            string newSnapshotId = VMControl.CreateSnapshotAsync(actionDescription).Result;

            var resultingState = new VMStateTree
            {
                SnapshotId = newSnapshotId,
                SnapshotName = actionDescription,
            };

            _currentState.Actions[action] = (actionResult, resultingState);
            _currentState = resultingState;
            _currentAppliedState = resultingState;

            return actionResult;
        }

        VMActionResult Run(SerializedVMAction action)
        {
            if (action.Type == VMActionType.RunCommand)
            {
                var result = VMControl.RunCommandOnVM(action.Arguments.ToArray());
                return new VMActionResult
                {
                    Filename = action.Arguments[0],
                    Arguments = action.Arguments.Skip(1).ToList(),
                    ExitCode = result.ExitCode,
                    StdOut = result.StdOut,
                    StdErr = result.StdErr,
                };
            }
            else if (action.Type == VMActionType.CopyFileToVM)
            {
                throw new NotImplementedException();
            }
            else if (action.Type == VMActionType.CopyFolderToVM)
            {
                var targetSharePath = VMPathToSharePath(action.TargetPath);

                CopyDirectory(action.SourcePath, targetSharePath);

                return VMActionResult.Success();
            }
            else if (action.Type == VMActionType.WriteFileToVM)
            {
                var targetSharePath = VMPathToSharePath(action.TargetPath);
                if (!Directory.Exists(Path.GetDirectoryName(targetSharePath)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetSharePath));
                }

                File.WriteAllText(targetSharePath, action.FileContents);

                return VMActionResult.Success();
            }
            else
            {
                throw new NotImplementedException();
            }
        }

        string GetFileContentId(string path)
        {
            var info = new FileInfo(path);
            return $"{info.LastWriteTimeUtc.Ticks}-{info.Length}";
        }

        string GetDirectoryContentId(string path)
        {
            StringBuilder sb = new StringBuilder();
            var info = new DirectoryInfo(path);

            void ProcessDirectory(DirectoryInfo dir, string relativeTo)
            {
                foreach (var file in dir.GetFiles())
                {
                    sb.AppendLine($"{Path.GetRelativePath(relativeTo, file.FullName)}:{file.LastWriteTimeUtc.Ticks}-{file.Length}");
                }

                foreach (var subDir in dir.GetDirectories())
                {
                    sb.AppendLine(subDir.FullName);
                    ProcessDirectory(subDir, relativeTo);
                }
            }

            ProcessDirectory(info, path);

            return sb.ToString();
        }

        static void CopyDirectory(string sourcePath, string destPath)
        {
            if (!Directory.Exists(destPath))
            {
                Directory.CreateDirectory(destPath);
            }

            foreach (var dir in Directory.GetDirectories(sourcePath))
            {
                CopyDirectory(dir, Path.Combine(destPath, Path.GetFileName(dir)));
            }

            foreach (var file in Directory.GetFiles(sourcePath))
            {
                new FileInfo(file).CopyTo(Path.Combine(destPath, Path.GetFileName(file)), true);
            }
        }

        string VMPathToSharePath(string vmPath)
        {
            var dirInfo = new DirectoryInfo(vmPath);

            if (dirInfo.Root.FullName.Substring(1) != @":\")
            {
                throw new ArgumentException("Unrecognized directory root for: " + vmPath, nameof(vmPath));
            }

            string driveLetter = dirInfo.Root.FullName.Substring(0, 1);

            string pathUnderDrive = dirInfo.FullName.Substring(3);

            return $@"\\{VMControl.VMMachineName}\{driveLetter}$\{pathUnderDrive}";

        }
    }
}

// SPDX-License-Identifier: BSD-3-Clause
// SPDX-FileCopyrightText: Silicon Laboratories Inc. <https://www.silabs.com>
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using ZWaveController.Commands;
using ZWaveController.Enums;
using ZWaveController.Interfaces;
using ZWaveController.Models;

namespace ZWaveController.Services
{
    public class PredefinedPayloadsService : IPredefinedPayloadsService
    {
        private readonly IApplicationModel _applicationModel;
        private readonly IPredefinedCommandsModel _predefinesModel;
        private readonly object _flowLock = new object();
        private CancellationTokenSource _flowCancellation;
        private SendCommand _activeSendCommand;

        public PredefinedPayloadsService(IApplicationModel applicationModel)
        {
            _applicationModel = applicationModel ?? throw new ArgumentNullException(nameof(applicationModel));
            _predefinesModel = _applicationModel.PredefinedCommandsModel ?? throw new ArgumentNullException(nameof(applicationModel.PredefinedCommandsModel));
        }

        private string GetGroupName(string name)
        {
            return _predefinesModel.PredefinedGroups.Any(i => i.Name == name) ? GetGroupName(name + "(1)") : name;
        }

        private void RunCommands(IEnumerable<PredefinedPayload> commands)
        {
            var commandList = commands?
                .Where(item => item != null && item.PayloadItem != null)
                .OrderBy(item => item.Id)
                .ToList();

            if (commandList == null || commandList.Count == 0)
                return;

            CancellationTokenSource cancellationSource;
            lock (_flowLock)
            {
                if (_flowCancellation != null)
                    return;
                _flowCancellation = cancellationSource = new CancellationTokenSource();
            }

            var cancellation = cancellationSource.Token;
            ICommandClassesModel originalModel = null;

            try
            {
                _applicationModel.Invoke(() =>
                {
                    originalModel = _applicationModel.CommandClassesModel;
                    _predefinesModel.IsFlowRunning = true;
                    _predefinesModel.FlowStatus = "Sending";
                });

                if (originalModel == null)
                    throw new InvalidOperationException("CommandClassesModel is null.");

                foreach (var item in commandList)
                {
                    if (cancellation.IsCancellationRequested)
                        throw new OperationCanceledException(cancellation);

                    SendCommand sendCommand = null;
                    using (var sendCompleted = new ManualResetEventSlim(false))
                    {
                        Action onCompleted = () => sendCompleted.Set();
                        CancellationTokenRegistration registration = default(CancellationTokenRegistration);
                        try
                        {
                            _applicationModel.Invoke(() =>
                            {
                                if (cancellation.IsCancellationRequested)
                                    throw new OperationCanceledException(cancellation);

                                var clonedModel = originalModel.Clone(item.PayloadItem);
                                if (clonedModel == null)
                                    throw new InvalidOperationException("Cloned CommandClassesModel is null.");

                                clonedModel.IsExpectCommand = false;
                                _applicationModel.CommandClassesModel = clonedModel;

                                sendCommand = CommandsFactory.CommandControllerSessionGet<SendCommand>();
                                if (sendCommand == null)
                                    throw new InvalidOperationException("SendCommand instance is null.");

                                sendCommand.UpdateTargetDevice();
                                if (sendCommand.IsSleepingTarget)
                                    throw new InvalidOperationException("The selected target is sleeping and cannot send the list.");

                                lock (_flowLock)
                                {
                                    if (cancellation.IsCancellationRequested)
                                        throw new OperationCanceledException(cancellation);
                                    _activeSendCommand = sendCommand;
                                }

                                sendCommand.OnCompleted += onCompleted;
                                registration = cancellation.Register(() => sendCompleted.Set());
                                CommandsFactory.CommandRunner.ExecuteAsync(sendCommand, null);
                            });

                            sendCompleted.Wait(cancellation);
                            if (cancellation.IsCancellationRequested)
                                throw new OperationCanceledException(cancellation);

                            if (sendCommand == null || sendCommand.ExecutionResult != CommandExecutionResult.OK)
                                throw new InvalidOperationException("The controller failed to send a list item.");
                        }
                        finally
                        {
                            registration.Dispose();
                            if (sendCommand != null)
                                sendCommand.OnCompleted -= onCompleted;
                            lock (_flowLock)
                            {
                                if (ReferenceEquals(_activeSendCommand, sendCommand))
                                    _activeSendCommand = null;
                            }
                        }
                    }

                    var delay = _applicationModel.SendDataSettingsModel != null
                        ? _applicationModel.SendDataSettingsModel.DelayResponseMs
                        : 0;
                    if (delay > 0 && cancellation.WaitHandle.WaitOne(delay))
                        throw new OperationCanceledException(cancellation);
                }

                _applicationModel.Invoke(() => _predefinesModel.FlowStatus = "Completed");
            }
            catch (OperationCanceledException)
            {
                _applicationModel.Invoke(() => _predefinesModel.FlowStatus = "Stopped");
            }
            catch (Exception exception)
            {
                _applicationModel.Invoke(() => _predefinesModel.FlowStatus = $"Stopped: {exception.Message}");
            }
            finally
            {
                _applicationModel.Invoke(() =>
                {
                    if (originalModel != null)
                        _applicationModel.CommandClassesModel = originalModel;
                    _predefinesModel.IsFlowRunning = false;
                });
                lock (_flowLock)
                {
                    _activeSendCommand = null;
                    if (ReferenceEquals(_flowCancellation, cancellationSource))
                        _flowCancellation = null;
                    cancellationSource.Dispose();
                }
            }
        }

        public void CancelGroup()
        {
            SendCommand activeSendCommand;
            lock (_flowLock)
            {
                _flowCancellation?.Cancel();
                activeSendCommand = _activeSendCommand;
            }

            activeSendCommand?.Cancel(null);
        }

        public CommandExecutionResult Initialize()
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            _applicationModel.Invoke(() =>
            {
                _predefinesModel.PredefinedGroups.Clear();
                _predefinesModel.SelectedGroup = null;
                _predefinesModel.SelectedItem = null;
            });

            ret = AddGroup("Temporary");
            return ret;
        }

        public CommandExecutionResult LoadGroup(string groupName, string json)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(json))
                return CommandExecutionResult.Failed;

            var list = JsonConvert.DeserializeObject<IList<PayloadItem>>(json)?.ToArray();
            if (list == null)
                return CommandExecutionResult.Failed;

            CommandExecutionResult ret = AddGroup(groupName);
            if (ret == CommandExecutionResult.OK)
            {
                _predefinesModel.SelectedGroup = _predefinesModel.PredefinedGroups.LastOrDefault();
                foreach (var item in list)
                {
                    ret = AddItemToGroup(groupName, item);
                    if (ret == CommandExecutionResult.OK)
                    {
                        _predefinesModel.SelectedItem = _predefinesModel.SelectedGroup?.Items?.FirstOrDefault();
                    }
                }
            }
            return ret;
        }

        public CommandExecutionResult SaveGroup(string groupName, string filePath)
        {
            if (string.IsNullOrWhiteSpace(groupName) || string.IsNullOrWhiteSpace(filePath))
                return CommandExecutionResult.Failed;

            CommandExecutionResult ret = CommandExecutionResult.Failed;
            var group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
            if (group != null)
            {
                try
                {
                    var payloadItems = group.Items.Select(i => i.Item.PayloadItem).ToList();
                    var json = JsonConvert.SerializeObject(payloadItems);
                    File.WriteAllText(filePath, json);
                    ret = CommandExecutionResult.OK;
                }
                catch (Exception)
                {
                    ret = CommandExecutionResult.Failed;
                }
            }
            return ret;
        }

        public CommandExecutionResult AddGroup(string groupName)
        {
            if (string.IsNullOrWhiteSpace(groupName))
                return CommandExecutionResult.Failed;

            CommandExecutionResult ret = CommandExecutionResult.Failed;
            _applicationModel.Invoke(() =>
            {
                var group = _predefinesModel.CreateFavoriteGroup(GetGroupName(groupName));
                _predefinesModel.PredefinedGroups.Add(group);
                _predefinesModel.SelectedGroup = _predefinesModel.PredefinedGroups.LastOrDefault();
                ret = CommandExecutionResult.OK;
            });
            return ret;
        }

        public CommandExecutionResult RenameGroup(string groupName, string newGroupName)
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            if (!string.IsNullOrEmpty(groupName) && !string.IsNullOrEmpty(newGroupName) && !_predefinesModel.PredefinedGroups.Any(x => x.Name == newGroupName))
            {
                var group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
                if (group != null)
                {
                    _applicationModel.Invoke(() =>
                    {
                        group.Name = newGroupName;
                        _predefinesModel.SelectedGroup = _predefinesModel.PredefinedGroups.FirstOrDefault(x => x == group);
                        _predefinesModel.PredefinedGroups = _predefinesModel.PredefinedGroups;
                        ret = CommandExecutionResult.OK;
                    });
                }
            }
            return ret;
        }

        public CommandExecutionResult CloneGroup(string groupName, string newGroupName = null)
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            if (!string.IsNullOrEmpty(groupName) && _predefinesModel.PredefinedGroups.Any(x => x.Name == groupName))
            {
                var cloneName = string.IsNullOrEmpty(newGroupName) || newGroupName == groupName ? GetGroupName(groupName) : newGroupName;
                ret = AddGroup(cloneName);
                if (ret == CommandExecutionResult.OK)
                {
                    var originalGroup = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
                    foreach (var payloadItem in originalGroup?.Items?.Select(x => x.Item.PayloadItem).ToList() ?? new List<PayloadItem>())
                    {
                        AddItemToGroup(cloneName, payloadItem);
                    }
                }
            }
            return ret;
        }

        public CommandExecutionResult DeleteGroup(string groupName)
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            if (!string.IsNullOrEmpty(groupName))
            {
                var group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
                if (group != null)
                {
                    _applicationModel.Invoke(() =>
                    {
                        if (_predefinesModel.PredefinedGroups.Remove(group))
                        {
                            _predefinesModel.SelectedGroup = _predefinesModel.PredefinedGroups.LastOrDefault();
                            _predefinesModel.SelectedItem = null;
                            ret = CommandExecutionResult.OK;
                        }
                    });
                }
            }
            return ret;
        }

        public CommandExecutionResult DeleteGroups()
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            _applicationModel.Invoke(() =>
            {
                _predefinesModel.PredefinedGroups.Clear();
                _predefinesModel.SelectedGroup = null;
                ret = CommandExecutionResult.OK;
            });
            return ret;
        }

        public CommandExecutionResult AddItemToGroup(string groupName, PayloadItem newItem)
        {
            if (newItem == null)
                return CommandExecutionResult.Failed;

            CommandExecutionResult ret = CommandExecutionResult.Failed;
            IPredefinedCommandGroup group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
            if (group != null)
            {
                var favoriteItem = _predefinesModel.GetItem(newItem);
                if (favoriteItem?.Item != null)
                {
                    favoriteItem.Item.Id = group.GetFreeFavoriteItemId();
                    _applicationModel.Invoke(() =>
                    {
                        group.Items.Add(favoriteItem);
                        _predefinesModel.SelectedGroup = _predefinesModel.SelectedGroup;
                        _predefinesModel.SelectedItem = _predefinesModel.SelectedGroup.Items.LastOrDefault();
                        ret = CommandExecutionResult.OK;
                    });
                }
            }
            return ret;
        }

        public CommandExecutionResult DeleteItem(string groupName, uint id)
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            var group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
            if (group != null)
            {
                var favoriteItem = group.Items.FirstOrDefault(i => i.Item.Id == id);
                if (favoriteItem != null)
                {
                    _applicationModel.Invoke(() =>
                    {
                        group.Items.Remove(favoriteItem);
                        uint idx = 0;
                        foreach (var item in group.Items)
                        {
                            item.Item.Id = ++idx;
                        }
                        ret = CommandExecutionResult.OK;
                        _predefinesModel.SelectedGroup = _predefinesModel.SelectedGroup;
                        if (id - 1 > 0)
                        {
                            _predefinesModel.SelectedItem = _predefinesModel.SelectedGroup.Items.FirstOrDefault(x => x.Item.Id == id - 1);
                        }
                        else
                        {
                            _predefinesModel.SelectedItem = null;
                        }
                    });
                }
            }
            return ret;
        }

        public CommandExecutionResult MoveUp(string groupName, uint id)
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            var group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
            if (group != null)
            {
                var currItem = group.Items.FirstOrDefault(i => i.Item.Id == id);
                var prevItem = group.Items.FirstOrDefault(i => i.Item.Id == id - 1);
                if (currItem != null && prevItem != null)
                {
                    _applicationModel.Invoke(() =>
                    {
                        var srcIx = group.Items.IndexOf(currItem);
                        var dstIx = group.Items.IndexOf(prevItem);
                        var temp = group.Items[srcIx];
                        group.Items[srcIx] = group.Items[dstIx];
                        group.Items[dstIx] = temp;
                        currItem.Item.Id = id - 1;
                        prevItem.Item.Id = id;
                        _predefinesModel.SelectedItem = temp;
                        ret = CommandExecutionResult.OK;
                    });
                }
            }
            return ret;
        }

        public CommandExecutionResult MoveDown(string groupName, uint id)
        {
            CommandExecutionResult ret = CommandExecutionResult.Failed;
            var group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
            if (group != null)
            {
                var currItem = group.Items.FirstOrDefault(i => i.Item.Id == id);
                var nextItem = group.Items.FirstOrDefault(i => i.Item.Id == id + 1);
                if (currItem != null && nextItem != null)
                {
                    _applicationModel.Invoke(() =>
                    {
                        var srcIx = group.Items.IndexOf(currItem);
                        var dstIx = group.Items.IndexOf(nextItem);
                        var temp = group.Items[srcIx];
                        group.Items[srcIx] = group.Items[dstIx];
                        group.Items[dstIx] = temp;
                        currItem.Item.Id = id + 1;
                        nextItem.Item.Id = id;
                        _predefinesModel.SelectedItem = temp;
                        ret = CommandExecutionResult.OK;
                    });
                }
            }
            return ret;
        }

        public CommandExecutionResult CopyItem(string groupName, uint id)
        {
            var group = _predefinesModel.PredefinedGroups.FirstOrDefault(i => i.Name == groupName);
            if (group == null)
                return CommandExecutionResult.Failed;

            var favoriteItem = group.Items.FirstOrDefault(i => i.Item.Id == id);
            if (favoriteItem == null)
                return CommandExecutionResult.Failed;

            return AddItemToGroup(groupName, favoriteItem.Item.PayloadItem);
        }

        public void RunGroup(string groupName)
        {
            var group = _predefinesModel.PredefinedGroups.FirstOrDefault(item => item.Name == groupName);
            if (group != null)
                RunCommands(group.Items.Select(item => item.Item).ToList());
        }
    }
}
// SPDX-License-Identifier: BSD-3-Clause
// SPDX-FileCopyrightText: Silicon Laboratories Inc. <https://www.silabs.com>
using System;
using System.Linq;
using Utils;
using ZWave;
using ZWave.BasicApplication;
using ZWave.BasicApplication.Enums;
using ZWave.Devices;
using ZWave.Enums;
using ZWaveController.Enums;
using ZWaveController.Interfaces;
using ZWaveController.Models;

namespace ZWaveController.Commands
{
    /// <summary>Sends one Command Classes payload using its selected settings.</summary>
    public sealed class SendCommand : CommandBasicBase
    {
        private readonly object _syncRoot = new object();
        private ICommandClassesModel _commandClassesModel;
        private NodeTag _activeTarget;

        /// <summary>The result of the most recent send attempt.</summary>
        public CommandExecutionResult ExecutionResult { get; private set; } = CommandExecutionResult.Inconclusive;

        public SendCommand(IControllerSession controllerSession) : base(controllerSession)
        {
            if (controllerSession == null)
                throw new ArgumentNullException(nameof(controllerSession));

            IsTargetSensitive = true;
            UseBackgroundThread = true;
            Text = "Send Data Command";
            _commandClassesModel = controllerSession.ApplicationModel?.CommandClassesModel;
        }

        protected override bool IsExecuteOnEndDeviceAlloved => true;

        public override bool CanExecuteModelDependent(object param)
        {
            var model = ResolveModel();
            return model?.Payload != null &&
                ControllerSession?.ApplicationModel?.SelectedNode != null;
        }

        public override CommandTypes CommandType => CommandTypes.CmdZWaveSendData;

        protected override void ExecuteInner(object param)
        {
            try
            {
                PublishResult(SendData());
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException || exception is NullReferenceException)
            {
                PublishResult(CommandExecutionResult.Failed, exception.Message);
            }
        }

        private ICommandClassesModel ResolveModel()
        {
            var currentModel = ControllerSession?.ApplicationModel?.CommandClassesModel;
            if (currentModel != null)
                _commandClassesModel = currentModel;

            return _commandClassesModel;
        }

        private CommandExecutionResult SendData()
        {
            var session = ControllerSession ?? throw new InvalidOperationException("ControllerSession is null.");
            var appModel = session.ApplicationModel ?? throw new InvalidOperationException("ApplicationModel is null.");
            var model = ResolveModel() ?? throw new InvalidOperationException("CommandClassesModel is null.");

            var payload = model.Payload?.ToArray();
            if (payload == null || payload.Length == 0)
                throw new ArgumentException("Payload is empty.", nameof(model));

            if (model.SecureType == SecureType.SerialApi)
            {
                session.SendSerialApi(payload, out _);
                return CommandExecutionResult.OK;
            }

            var settings = appModel.SendDataSettingsModel ?? throw new InvalidOperationException("SendDataSettingsModel is null.");
            var targetDevice = TargetDevice;
            var substituteSettings = new SubstituteSettings(GetSubstituteFlags(model), settings.S0MaxBytesPerFrameSize);

            if (model.IsSupervisionGetEnabled)
            {
                if (model.IsAutoIncSupervisionSessionId)
                    model.SupervisionSessionId = (byte)((model.SupervisionSessionId + 1) & 0xFF);
                substituteSettings.SubstituteFlags |= SubstituteFlags.UseSupervision;
                substituteSettings.SupervisionSessionId = model.SupervisionSessionId;
                substituteSettings.IsSupervisionStatusUpdatesEnabled = model.IsSupervisionGetStatusUpdatesEnabled;
            }

            if (model.IsMultiChannelEnabled)
            {
                substituteSettings.SubstituteFlags |= SubstituteFlags.UseMultiChannel;
                substituteSettings.SrcEndPoint = targetDevice.EndPointId > 0 ? (byte)0 : model.SourceEndPoint;
                substituteSettings.DstEndPoint = targetDevice.EndPointId > 0 ? targetDevice.EndPointId : model.DestinationEndPoint;
                substituteSettings.IsBitAdress = model.IsBitAddress;
            }

            if (model.IsCrc16Enabled)
                substituteSettings.SubstituteFlags |= SubstituteFlags.UseCrc16Encap;

            var multicast = model.IsForceMulticastEnabled || (model.SelectedNodeItems?.Count ?? 0) > 1;
            var nextTarget = multicast
                ? NodeTag.Empty
                : model.SecureType == SecureType.Broadcast
                    ? new NodeTag(0xFF)
                    : targetDevice;

            lock (_syncRoot)
            {
                _activeTarget = nextTarget;
            }

            if (multicast && model.IsSuppressMulticastFollowUp)
                substituteSettings.SubstituteFlags |= SubstituteFlags.DenyFollowup;

            try
            {
                ActionResult result;
                if (multicast)
                {
                    var targets = model.SelectedNodeItems?
                        .Where(item => item != null)
                        .Select(item => (NodeTag)item.Clone())
                        .ToArray();

                    if (targets == null || targets.Length == 0)
                        throw new InvalidOperationException("No multicast targets are selected.");

                    payload = PreparePayload(payload, model, targetDevice);
                    result = session.SendData(targets, payload, substituteSettings, null, true);
                }
                else if (model.SecureType != SecureType.Broadcast && model.IsExpectCommand && model.ExpectedCommand.Key != 0)
                {
                    var expectedCommand = new[] { model.ExpectedCommand.Key, model.ExpectedCommand.Value };
                    payload = PreparePayload(payload, model, targetDevice);
                    result = session.RequestData(targetDevice, payload, ref expectedCommand,
                        settings.RequestsTimeoutMs, substituteSettings, null);
                }
                else
                {
                    result = session.SendData(nextTarget, payload, substituteSettings, null, true);
                }

                if (result == null || result.State == ActionStates.Failed || result.State == ActionStates.Expired)
                    return CommandExecutionResult.Failed;

                return result.State == ActionStates.Cancelled
                    ? CommandExecutionResult.Canceled
                    : CommandExecutionResult.OK;
            }
            finally
            {
                lock (_syncRoot)
                {
                    _activeTarget = NodeTag.Empty;
                }
                session.ActiveSendToken = null;
            }
        }

        private void PublishResult(CommandExecutionResult result, string errorMessage = null)
        {
            ExecutionResult = result;
            var appModel = ControllerSession?.ApplicationModel;
            if (appModel == null)
                return;

            appModel.Invoke(() =>
            {
                appModel.LastCommandExecutionResult = result;
                appModel.NotifyControllerChanged(
                    result == CommandExecutionResult.OK ? NotifyProperty.CommandSucceeded : NotifyProperty.None,
                    new NotifyCommandData
                    {
                        CommandName = nameof(SendCommand),
                        Message = result == CommandExecutionResult.OK ? "Data Sent" :
                            result == CommandExecutionResult.Canceled ? "Data Send Canceled" : errorMessage ?? "Data Send Failed"
                    });
            });
        }

        public override void PrepareData()
        {
            var model = ResolveModel();
            if (model == null || ControllerSession?.SenderHistoryService == null)
                return;

            var payloadItem = ControllerSession.GetPayloadItem(model);
            if (payloadItem != null)
                ControllerSession.SenderHistoryService.Add(payloadItem);
        }

        protected override void CancelAction(object param)
        {
            var session = ControllerSession;
            if (session == null)
                return;

            NodeTag target;
            lock (_syncRoot)
            {
                target = _activeTarget;
            }

            var token = session.ActiveSendToken;
            if (token != null)
                session.Cancel(target, token);
        }

        protected override bool CanCancelAction(object param) => true;

        private static byte[] PreparePayload(byte[] payload, ICommandClassesModel model, NodeTag target)
        {
            if (model.IsSupervisionGetEnabled)
                payload = SupervisionManager.CreateSupervisionGet(payload, model.SupervisionSessionId,
                    model.IsSupervisionGetStatusUpdatesEnabled);

            if (model.IsMultiChannelEnabled || target.EndPointId > 0)
            {
                var sourceEndPoint = model.IsMultiChannelEnabled ? model.SourceEndPoint : (byte)0;
                var destinationEndPoint = model.IsMultiChannelEnabled ? model.DestinationEndPoint : target.EndPointId;
                payload = MultiChannelManager.Encapsulate(payload, sourceEndPoint, destinationEndPoint,
                    model.IsBitAddress || target.IsBitAddress, model.IsMultiChannelEnabled);
            }

            if (model.IsCrc16Enabled)
                payload = Crc16EncapManager.Encapsulate(payload);

            return payload;
        }

        private static SubstituteFlags GetSubstituteFlags(ICommandClassesModel model)
        {
            if (model.SecureType == SecureType.DefaultSecurity)
                return SubstituteFlags.None;

            return model.SecureType == SecureType.Secure
                ? SubstituteFlags.UseSecurity
                : SubstituteFlags.DenySecurity;
        }
    }
}
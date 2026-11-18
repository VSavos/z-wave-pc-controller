// SPDX-License-Identifier: BSD-3-Clause
// SPDX-FileCopyrightText: Silicon Laboratories Inc. <https://www.silabs.com>
﻿using ZWaveController.Interfaces;


namespace ZWaveController.Commands
{
    /// <summary>
    /// Command to Send Commands from the select goupd of predefined commands.
    /// </summary>
    public class SendPredefinedGroupCommand : PredefinedCommandsCommandBase
    {
        public SendPredefinedGroupCommand(IControllerSession controllerSession) : base(controllerSession)
        {
            IsTargetSensitive = true;
            UseBackgroundThread = true;
            Text = "Send Commands from the select goupd of predefined commands";
        }

        protected override bool CanCancelAction(object param) => true;

        protected override void CancelAction(object param) => ControllerSession.PredefinedPayloadsService.CancelGroup();

        public override bool CanExecuteModelDependent(object param)
        {
            return ApplicationModel.SelectedNode != null &&
                !_model.IsFlowRunning &&
                _model.SelectedGroup != null && 
                _model.SelectedGroup.Items.Count > 0;
        }

        protected override void ExecuteInner(object parameter)
        {
            ControllerSession.PredefinedPayloadsService.RunGroup(_model.SelectedGroup.Name);
        }

    }
}

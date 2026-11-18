# Z-Wave PC Controller User Guide

This guide covers the common workflow for operating the Z-Wave PC Controller (PCC). It focuses on connecting to a controller, working with a network, and finding the main tools. Individual tools may expose additional options; availability depends on the controller and active session.

## 1. Connect to a controller

PCC communicates with a Z-Wave Serial API controller over a serial connection or a supported IP/socket connection.

1. Open **Settings**.
2. For a serial controller, select the device from **Serial Port Data Sources**. Use **Refresh** to update the list or **Detect** to identify the controller.
3. For an IP/socket controller, select an existing entry, or choose **Add** and provide its source type, IP address, port, and any required arguments. Use **Discover** to search for available sources when supported.
4. Select the intended source and connect to it. Check the source name and controller information shown in PCC before operating the network.

If the controller is not listed, verify its connection and settings, then refresh or discover sources again. The project README and wiki contain platform and setup information.

## 2. Navigate the workspace

The main menu opens the available tools. The navigation bar provides quick access to common views. The active source and current view are shown in the application header; the latest log message appears at the bottom, and **Show Log** opens the full log.

| Tool | Typical use |
|---|---|
| **Network Management** | View nodes and perform network operations such as inclusion, exclusion, and node information requests. Select the relevant node before using a node-specific action. |
| **Smart Start** | Manage device provisioning entries and use Smart Start workflows. |
| **Command Classes** | Inspect command classes and send commands to a selected node. See [Send Data and Send List](send-data-user-guide.md) for usage and the send pipeline refactoring overview. |
| **Associations** | View and manage association groups and their destinations. |
| **Setup Route** | Inspect or configure routes where supported. |
| **Network Statistics**, **Topology Map**, and **IMA Network** | Inspect network performance, topology, and connectivity information. |
| **Polling** and **ERTT** | Run their respective polling or communication tests; stop tests when finished. |
| **Firmware Update** and **NVM Backup/Restore** | Perform device firmware operations or controller NVM backup and restore. Follow the relevant device/controller procedures carefully. |

Other tools—including configuration parameters, security, transmit settings, and encryption/decryption—are available from the main menu or toolbar. Their use depends on the task and controller support.

## 3. Work with nodes and commands

1. Open **Network Management** and identify the intended node in the node list.
2. Use the node information and status shown in the view to confirm your selection.
3. Choose the appropriate operation. Inclusion, exclusion, failed-node handling, and controller role changes affect network membership or behavior; use them only when intended.
4. To send a Z-Wave command, open **Command Classes**, select the destination node and command, review the payload and send options, then send it. Use the [Send Data guide](send-data-user-guide.md) for the detailed single-send and Send List workflow.

## 4. Review results and troubleshoot

- Check the result/status shown by the active view and the latest log message.
- Open **Show Log** for detailed controller activity and errors.
- Check the **Commands Queue** if operations are queued or still in progress.
- For connection issues, return to **Settings** and verify the selected source and its connection details.
- Enable communication trace capture in **Settings** when a trace is needed for diagnosis; choose a suitable capture folder and review the resulting files according to your support process.

## 5. Use care with network-changing operations

PCC can send commands to real devices and make changes to a live Z-Wave network. Confirm the active controller, selected node, and operation before sending. Back up controller NVM before restore or other maintenance procedures, and follow the device or controller manufacturer's instructions for firmware and network-management operations.

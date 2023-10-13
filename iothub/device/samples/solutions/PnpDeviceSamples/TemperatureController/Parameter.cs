﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using CommandLine;
using Microsoft.Extensions.Logging;
using System;

namespace Microsoft.Azure.Devices.Client.Samples
{
    /// <summary>
    /// Parameters for the application supplied via command line arguments.
    /// If the parameter is not supplied via command line args, it will look for it in environment variables.
    /// </summary>
    internal class Parameters
    {
        [Option(
            "DeviceSecurityType",
            HelpText = "(Required) The flow that will be used for connecting the device for the sample. Possible case-insensitive values include: dps, connectionString." +
            "\nDefaults to environment variable 'IOTHUB_DEVICE_SECURITY_TYPE'.")]
        public string DeviceSecurityType { get; set; } = "dps";

        [Option(
            'c',
            "PrimaryConnectionString",
            HelpText = "(Required if DeviceSecurityType is 'connectionString'). \nThe primary connection string for the device to simulate." +
            "\nDefaults to environment variable 'IOTHUB_DEVICE_CONNECTION_STRING'.")]
        public string PrimaryConnectionString { get; set; } = "HostName=iothub1neu.azure-devices.net;DeviceId=RaspPiSimulator;SharedAccessKey=zorkOTkqr679wQI8Lis8KqDbQPBLaTB7YAIoTPQMnl4=";

        [Option(
            'e',
            "DpsEndpoint",
            HelpText = "(Required if DeviceSecurityType is 'dps'). \nThe DPS endpoint to use during device provisioning." +
            "\nDefaults to environment variable 'IOTHUB_DEVICE_DPS_ENDPOINT'.")]
        public string DpsEndpoint { get; set; } = "global.azure-devices-provisioning.net";

        [Option(
            'i',
            "DpsIdScope",
            HelpText = "(Required if DeviceSecurityType is 'dps'). \nThe DPS ID Scope to use during device provisioning." +
            "\nDefaults to environment variable 'IOTHUB_DEVICE_DPS_ID_SCOPE'.")]
        public string DpsIdScope { get; set; } = "0ne00AF0BFA";

        [Option(
            'd',
            "DeviceId",
            HelpText = "(Required if DeviceSecurityType is 'dps'). \nThe device registration Id to use during device provisioning." +
            "\nDefaults to environment variable 'IOTHUB_DEVICE_DPS_DEVICE_ID'.")]
        public string DeviceId { get; set; } = "iotneudesicdevice";

        [Option(
            'k',
            "DeviceSymmetricKey",
            HelpText = "(Required if DeviceSecurityType is 'dps'). \nThe device symmetric key to use during device provisioning." +
            "\nDefaults to environment variable 'IOTHUB_DEVICE_DPS_DEVICE_KEY'.")]
        public string DeviceSymmetricKey { get; set; } = "bUEBzjbROlkU0rNfQKQQ05VR5K4e+Coq44/XF0/xwX4=";


        [Option(
            'r',
            "Application running time (in seconds)",
            Required = false,
            HelpText = "The running time for this console application. Leave it unassigned to run the application until it is explicitly canceled using Control+C.")]
        public double? ApplicationRunningTime { get; set; }

        public bool Validate(ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(DeviceSecurityType))
            {
                logger.LogWarning("Device provisioning type not set, please set the environment variable 'IOTHUB_DEVICE_SECURITY_TYPE'" +
                    "or pass in '-s | --DeviceSecurityType' through command line. \nWill default to using 'dps' flow.");

                DeviceSecurityType = "dps";
            }

            return DeviceSecurityType.ToLowerInvariant() switch
            {
                "dps" => !string.IsNullOrWhiteSpace(DpsEndpoint)
                        && !string.IsNullOrWhiteSpace(DpsIdScope)
                        && !string.IsNullOrWhiteSpace(DeviceId)
                        && !string.IsNullOrWhiteSpace(DeviceSymmetricKey),
                "connectionstring" => !string.IsNullOrWhiteSpace(PrimaryConnectionString),
                _ => false,
            };
        }
    }
}
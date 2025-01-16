﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Collections.Generic;

namespace FabricHealer.TelemetryLib
{
    public class RepairData
    {
        public Dictionary<string, (string Source, double Count)> Repairs
        {
            get; set;
        } = [];

        public double RepairCount
        {
            get; set;
        }

        public double FailedRepairs
        {
            get; set;
        }

        public double SuccessfulRepairs
        {
            get; set;
        }

        public double EnabledRepairCount
        {
            get; set;
        }
    }
}
﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;

    /// <summary>
    /// Threshold values for Distributed Tracing
    /// </summary>
    public class CosmosThresholdOptions
    {
        /// <summary>
        /// Latency Threshold for non point operations i.e. Query
        /// </summary>
        /// <value>3 seconds</value>
        public TimeSpan NonPointOperationLatencyThreshold { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Latency Threshold for point operations i.e operation other than Query
        /// </summary>
        /// <value>1 second</value>
        public TimeSpan PointOperationLatencyThreshold { get; set; } = TimeSpan.FromSeconds(1);
    }
}

﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace CosmosBenchmark
{
    using System.Diagnostics.Metrics;
    using App.Metrics;
    using App.Metrics.Timer;

    internal class ReadOperationMetricsCollector : MetricsCollector
    {
        private Counter<long> _counter;

        public ReadOperationMetricsCollector(MetricsContext metricsContext, IMetrics metrics, Counter<long> counter) : base(metricsContext, metrics)
        {
            _counter = counter;
        }

        public override TimerContext GetTimer()
        {
            return this.metrics.Measure.Timer.Time(this.metricsContext.ReadLatencyTimer);
        }

        public override void CollectMetricsOnSuccess()
        {
            this.metrics.Measure.Counter.Increment(this.metricsContext.ReadSuccessMeter);

            _counter.Add(1, new("name", "success"), new("color", "green"));
        }

        public override void CollectMetricsOnFailure()
        {
            this.metrics.Measure.Counter.Increment(this.metricsContext.ReadFailureMeter);

            _counter.Add(1, new("name", "failure"), new("color", "red"));
        }
    }
}

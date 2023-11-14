﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.FaultInjection
{
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Documents.Rntbd;
    using Microsoft.Azure.Documents;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Text;
    using Microsoft.Azure.Documents.FaultInjection;
    using Microsoft.Azure.Cosmos.Common;
    using Microsoft.Azure.Cosmos.Tracing;
    using Microsoft.Azure.Cosmos.FaultInjection.implementataion;

    internal class FaultInjectionRuleStore
    {
        private readonly ConcurrentDictionary<FaultInjectionServerErrorRule, byte> serverResponseDelayRuleSet = new ConcurrentDictionary<FaultInjectionServerErrorRule, byte>();
        private readonly ConcurrentDictionary<FaultInjectionServerErrorRule, byte> serverResponseErrorRuleSet = new ConcurrentDictionary<FaultInjectionServerErrorRule, byte>();
        private readonly ConcurrentDictionary<FaultInjectionServerErrorRule, byte> serverConnectionDelayRuleSet = new ConcurrentDictionary<FaultInjectionServerErrorRule, byte>();
        private readonly ConcurrentDictionary<FaultInjectionConnectionErrorRule, byte> connectionErrorRuleSet = new ConcurrentDictionary<FaultInjectionConnectionErrorRule, byte>();

        private readonly FaultInjectionRuleProcessor ruleProcessor;

        public FaultInjectionRuleStore(DocumentClient client, FaultInjectionApplicationContext applicationContext)
        {
            _= client ?? throw new ArgumentNullException(nameof(client));

            this.ruleProcessor = new FaultInjectionRuleProcessor(
                connectionMode: client.ConnectionPolicy.ConnectionMode,
                collectionCache: client.GetCollectionCacheAsync(NoOpTrace.Singleton).Result,
                globalEndpointManager: client.GlobalEndpointManager,
                addressResolver: client.AddressResolver,
                retryOptions: client.ConnectionPolicy.RetryOptions,
                routingMapProvider: client.GetPartitionKeyRangeCacheAsync(NoOpTrace.Singleton).Result,
                applicationContext: applicationContext);
        }

        public IFaultInjectionRuleInternal ConfigureFaultInjectionRule(FaultInjectionRule rule)
        {
            _ = rule ?? throw new ArgumentNullException(nameof(rule));

            IFaultInjectionRuleInternal effectiveRule = this.ruleProcessor.ProcessFaultInjectionRule(rule);
            
            if (effectiveRule.GetType() == typeof(FaultInjectionConnectionErrorRule))
            {
                this.connectionErrorRuleSet.TryAdd((FaultInjectionConnectionErrorRule)effectiveRule, 0);
            }
            else if (effectiveRule.GetType() == typeof(FaultInjectionServerErrorRule))
            {
                FaultInjectionServerErrorRule serverErrorRule = (FaultInjectionServerErrorRule)effectiveRule;

                switch (serverErrorRule.GetResult().GetServerErrorType())
                {
                    case FaultInjectionServerErrorType.ResponseDelay:
                        this.serverResponseDelayRuleSet.TryAdd(serverErrorRule, 0);
                        break;
                    case FaultInjectionServerErrorType.ConnectionDelay:
                        this.serverConnectionDelayRuleSet.TryAdd(serverErrorRule, 0);
                        break;
                    default:
                        this.serverResponseErrorRuleSet.TryAdd(serverErrorRule, 0);
                        break;
                }
            }

            return effectiveRule;
        }

        public FaultInjectionServerErrorRule? FindRntbdServerResponseErrorRule(ChannelCallArguments args)
        {
            foreach (FaultInjectionServerErrorRule rule in this.serverResponseErrorRuleSet.Keys)
            {
                if (rule.GetConnectionType() == FaultInjectionConnectionType.Direct
                    && rule.IsApplicable(args))
                {
                    return rule;
                }
            }

            return null;
        }

        public FaultInjectionServerErrorRule? FindRntbdServerResponseDelayRule(ChannelCallArguments args)
        {
            foreach (FaultInjectionServerErrorRule rule in this.serverResponseDelayRuleSet.Keys)
            {
                if (rule.GetConnectionType() == FaultInjectionConnectionType.Direct
                    && rule.IsApplicable(args))
                {
                    return rule;
                }
            }

            return null;
        }

        public FaultInjectionServerErrorRule? FindRntbdServerConnectionDelayRule(
            Guid activityId,
            Uri callUri, 
            DocumentServiceRequest request)
        {
            foreach (FaultInjectionServerErrorRule rule in this.serverConnectionDelayRuleSet.Keys)
            {
                if (rule.GetConnectionType() == FaultInjectionConnectionType.Direct
                    && rule.IsApplicable(
                        activityId,
                        callUri,
                        request))
                {
                    return rule;
                }
            }

            return null;
        }

        public bool ContainsRule(FaultInjectionConnectionErrorRule rule)
        {
            return this.connectionErrorRuleSet.ContainsKey(rule);
        }

        public bool RemoveRule(FaultInjectionConnectionErrorRule rule)
        {
            return this.connectionErrorRuleSet.Remove(rule, out byte _);
        }
    }
}

﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Vostok.Airlock;
using Vostok.Clusterclient.Topology;
using Vostok.Logging;

namespace Vostok.AirlockConsumer
{
    public abstract class ConsumerApplication
    {
        private const string defaultKafkaBootstrapEndpoints = "kafka:9092";
        private const string defaultAirlockGateEndpoints = "http://gate:8888";
        private const string defaultAirlockGateApiKey = "UniversalApiKey";

        public ConsumerGroupHost Initialize(ILog log)
        {
            var environmentVariables = GetEnvironmentVariables(log);
            var consumerGroupHostSettings = GetConsumerGroupHostSettings(log, environmentVariables);
            DoInitialize(log, environmentVariables, out var routingKeyFilter, out var processorProvider);
            return new ConsumerGroupHost(consumerGroupHostSettings, log, routingKeyFilter, processorProvider);
        }

        /// <summary>
        /// Wait for necessary environment preparation here (e.g. wait for airlock gate to start linstening its port)
        /// </summary>
        protected abstract void DoInitialize(ILog log, Dictionary<string, string> environmentVariables, out IRoutingKeyFilter routingKeyFilter, out IAirlockEventProcessorProvider processorProvider);

        protected static AirlockConfig GetAirlockConfig(ILog log, Dictionary<string, string> environmentVariables)
        {
            if (!environmentVariables.TryGetValue("AIRLOCK_GATE_API_KEY", out var airlockGateApiKey))
                airlockGateApiKey = defaultAirlockGateApiKey;
            if (!environmentVariables.TryGetValue("AIRLOCK_GATE_ENDPOINTS", out var airlockGateEndpoints))
                airlockGateEndpoints = defaultAirlockGateEndpoints;
            var airlockGateUris = airlockGateEndpoints.Split(new[] {';'}, StringSplitOptions.RemoveEmptyEntries).Select(x => new Uri(x)).ToArray();
            var airlockConfig = new AirlockConfig
            {
                ApiKey = airlockGateApiKey,
                ClusterProvider = new FixedClusterProvider(airlockGateUris),
            };
            log.Info($"AirlockConfig: {airlockConfig.ToPrettyJson()}");
            return airlockConfig;
        }

        private static Dictionary<string, string> GetEnvironmentVariables(ILog log)
        {
            var environmentVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process)
                .Cast<DictionaryEntry>()
                .Where(x => ((string) x.Key).StartsWith("AIRLOCK_"))
                .ToDictionary(x => (string) x.Key, x => (string) x.Value);
            log.Info($"EnvironmentVariables: {environmentVariables.ToPrettyJson()}");
            return environmentVariables;
        }

        private ConsumerGroupHostSettings GetConsumerGroupHostSettings(ILog log, Dictionary<string, string> environmentVariables)
        {
            var consumerGroupId = GetConsumerGroupId(environmentVariables);
            var kafkaBootstrapEndpoints = GetKafkaBootstrapEndpoints(environmentVariables);
            var consumerGroupHostSettings = new ConsumerGroupHostSettings(kafkaBootstrapEndpoints, consumerGroupId);
            log.Info($"ConsumerGroupHostSettings: {consumerGroupHostSettings.ToPrettyJson()}");
            return consumerGroupHostSettings;
        }

        private string GetConsumerGroupId(Dictionary<string, string> environmentVariables)
        {
            if (!environmentVariables.TryGetValue("AIRLOCK_CONSUMER_GROUP_ID", out var consumerGroupId))
                consumerGroupId = $"{GetType().Name}@{Dns.GetHostName()}";
            return consumerGroupId;
        }

        private static string GetKafkaBootstrapEndpoints(Dictionary<string, string> environmentVariables)
        {
            if (!environmentVariables.TryGetValue("AIRLOCK_KAFKA_BOOTSTRAP_ENDPOINTS", out var kafkaBootstrapEndpoints))
                kafkaBootstrapEndpoints = defaultKafkaBootstrapEndpoints;
            return kafkaBootstrapEndpoints;
        }
    }
}
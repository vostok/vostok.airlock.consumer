﻿using Vostok.Airlock;
using Vostok.Airlock.Metrics;
using Vostok.Airlock.Tracing;
using Vostok.AirlockConsumer.MetricsAggregator.TracesToEvents;
using Vostok.Metrics;
using Vostok.Tracing;

namespace Vostok.AirlockConsumer.MetricsAggregator
{
    public class MetricsAggregatorAirlockEventProcessorProvider : IAirlockEventProcessorProvider
    {
        private readonly IMetricScope rootMetricScope;
        private readonly IAirlockClient airlockClient;
        private readonly MetricsAggregatorSettings settings;
        private readonly MetricEventSerializer airlockDeserializer = new MetricEventSerializer();
        private readonly SpanAirlockSerializer spanAirlockSerializer = new SpanAirlockSerializer();

        public MetricsAggregatorAirlockEventProcessorProvider(
            IMetricScope rootMetricScope,
            IAirlockClient airlockClient,
            MetricsAggregatorSettings settings)
        {
            this.rootMetricScope = rootMetricScope;
            this.airlockClient = airlockClient;
            this.settings = settings;
        }

        public IAirlockEventProcessor GetProcessor(string routingKey)
        {
            if (RoutingKey.LastSuffixMatches(routingKey, RoutingKey.TracesSuffix))
            {
                var tracesProcessor = new TracesToEventsProcessor(airlockClient, rootMetricScope, settings, routingKey);
                return new DefaultAirlockEventProcessor<Span>(spanAirlockSerializer, tracesProcessor);
            }
            var processor = new MetricsAggregatorProcessor(airlockClient, rootMetricScope, settings, routingKey);
            return new DefaultAirlockEventProcessor<MetricEvent>(airlockDeserializer, processor);
        }
    }
}
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Confluent.Kafka;
using Confluent.Kafka.Serialization;
using Vostok.Logging;

namespace Vostok.AirlockConsumer
{
    // seems like we should run counsumer groups on a per project
    // todo (avk, 06.10.2017): handle kafka consumer exceptions (introduce decorator)
    public class ConsumerGroupHost : IDisposable
    {
        private readonly ConsumerGroupHostSettings settings;
        private readonly ILog log;
        private readonly IAirlockEventProcessorProvider processorProvider;
        private readonly IRoutingKeyFilter routingKeyFilter;
        private readonly Consumer<Null, byte[]> consumer;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private readonly Dictionary<string, (IAirlockEventProcessor Processor, ProcessorHost ProcessorHost, int[] AssignedPartitions)> processors = new Dictionary<string, (IAirlockEventProcessor Processor, ProcessorHost, int[])>();
        private volatile Thread pollingThread;

        public ConsumerGroupHost(ConsumerGroupHostSettings settings, ILog log, IAirlockEventProcessorProvider processorProvider, IRoutingKeyFilter routingKeyFilter)
        {
            this.settings = settings;
            this.log = log.ForContext(this);
            this.processorProvider = processorProvider;
            this.routingKeyFilter = routingKeyFilter;

            consumer = new Consumer<Null, byte[]>(settings.GetConsumerConfig(), keyDeserializer: null, valueDeserializer: new ByteArrayDeserializer());
            consumer.OnError += (_, error) => { log.Error($"CriticalError: consumerName: {consumer.Name}, memberId: {consumer.MemberId} - {error.ToString()}"); };
            consumer.OnConsumeError += (_, message) => { log.Error($"ConsumeError: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, topic: {message.Topic}, partition: {message.Partition}, offset: {message.Offset}, timestamp: {message.Timestamp.UtcDateTime:O}: {message.Error.ToString()}"); };
            consumer.OnLog += (_, logMessage) =>
            {
                LogLevel logLevel;
                switch (logMessage.Level)
                {
                    case 0:
                    case 1:
                    case 2:
                        logLevel = LogLevel.Fatal;
                        break;
                    case 3:
                        logLevel = LogLevel.Error;
                        break;
                    case 4:
                        logLevel = LogLevel.Warn;
                        break;
                    case 5:
                    case 6:
                        logLevel = LogLevel.Info;
                        break;
                    default:
                        logLevel = LogLevel.Debug;
                        break;
                }
                log.Log(logLevel, null, $"consumerName: {consumer.Name}, memberId: {consumer.MemberId} - {logMessage.Name}|{logMessage.Facility}| {logMessage.Message}");
            };
            consumer.OnStatistics += (_, statJson) => { this.log.Info($"Statistics: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, stat: {statJson}"); };
            consumer.OnPartitionEOF += (_, topicPartitionOffset) => { log.Info($"PartitionEof: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, topicPartition: {topicPartitionOffset.TopicPartition}, next message will be at offset {topicPartitionOffset.Offset}"); };
            consumer.OnOffsetsCommitted += (_, committedOffsets) =>
            {
                if (!committedOffsets.Error)
                    log.Info($"OffsetsCommitted: consumerName: {consumer.Name}, memberId: {consumer.MemberId} successfully committed offsets: [{string.Join(", ", committedOffsets.Offsets)}]");
                else
                    log.Error($"OffsetsCommitted: consumerName: {consumer.Name}, memberId: {consumer.MemberId} failed to commit offsets [{string.Join(", ", committedOffsets.Offsets)}]: {committedOffsets.Error}");
            };
            consumer.OnPartitionsRevoked += (_, topicPartitions) => { OnPartitionsRevoked(topicPartitions); };
            consumer.OnPartitionsAssigned += (_, topicPartitions) => { OnPartitionsAssigned(topicPartitions); };
            consumer.OnMessage += (_, message) => OnMessage(message);
        }

        public void Start()
        {
            if (pollingThread != null)
                throw new InvalidOperationException("ConsumerGroupHost has been already run");
            pollingThread = new Thread(PollingThreadFunc)
            {
                IsBackground = true,
                Name = $"poll-{settings.ConsumerGroupHostId}",
            };
            pollingThread.Start();
        }

        public void Stop()
        {
            if (pollingThread == null)
                throw new InvalidOperationException("ConsumerGroupHost is not started");
            Dispose();
        }

        public void Dispose()
        {
            if (pollingThread == null)
                return;
            cancellationTokenSource.Cancel();
            pollingThread.Join();
            consumer.Dispose();
            cancellationTokenSource.Dispose();
        }

        private void PollingThreadFunc()
        {
            try
            {
                var subscribedToAnything = UpdateSubscrition();
                var sw = Stopwatch.StartNew();
                while (!cancellationTokenSource.IsCancellationRequested)
                {
                    if (subscribedToAnything)
                        consumer.Poll(settings.PollingInterval);
                    else
                        Thread.Sleep(settings.PollingInterval);

                    if (sw.Elapsed > settings.UpdateSubscriptionInterval)
                    {
                        subscribedToAnything = UpdateSubscrition();
                        sw.Restart();
                    }
                }
                StopProcessors(processors.Values.Select(x => x.ProcessorHost).ToList());
            }
            catch (Exception e)
            {
                log.Fatal("Unhandled exception on polling thread", e);
                throw;
            }
        }

        private static void StopProcessors(List<ProcessorHost> processorHostsToStop)
        {
            foreach (var processor in processorHostsToStop)
                processor.CompleteAdding();
            foreach (var processor in processorHostsToStop)
                processor.Join();
        }

        private bool UpdateSubscrition()
        {
            var metadata = consumer.GetMetadata(allTopics: true);
            log.Debug($"GotMetadata: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, metadata: {metadata}");

            var topicsToSubscribeTo = new List<string>();
            foreach (var topicMetadata in metadata.Topics)
            {
                var routingKey = topicMetadata.Topic;
                if (routingKeyFilter.Matches(routingKey))
                    topicsToSubscribeTo.Add(routingKey);
            }

            if (topicsToSubscribeTo.Any())
                consumer.Subscribe(topicsToSubscribeTo);
            log.Warn($"SubscribedToTopics: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, topics: [{string.Join(", ", topicsToSubscribeTo)}]");

            return topicsToSubscribeTo.Any();
        }

        private void OnPartitionsAssigned(List<TopicPartition> topicPartitions)
        {
            log.Warn($"PartitionsAssignmentRequest: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, topicPartitions: [{string.Join(", ", topicPartitions)}]");
            var topicPartitionOffsets = HandlePartitionsAssignment(topicPartitions);
            consumer.Assign(topicPartitionOffsets);
            log.Warn($"PartitionsAssigned: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, topicPartitions: [{string.Join(", ", topicPartitionOffsets)}]");
        }

        private void OnPartitionsRevoked(List<TopicPartition> topicPartitions)
        {
            log.Warn($"PartitionsRevokationRequest: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, topicPartitions: [{string.Join(", ", topicPartitions)}]");
            consumer.Unassign();
            log.Warn($"PartitionsRevoked: consumerName: {consumer.Name}, memberId: {consumer.MemberId}, topicPartitions: [{string.Join(", ", topicPartitions)}]");
        }

        private List<TopicPartitionOffset> HandlePartitionsAssignment(List<TopicPartition> topicPartitions)
        {
            var routingKeysToAssign = new List<string>();
            var topicPartitionOffsets = new List<TopicPartitionOffset>();
            foreach (var topicPartitionsByRoutingKeyGroup in topicPartitions.GroupBy(x => x.Topic))
            {
                var routingKey = topicPartitionsByRoutingKeyGroup.Key;
                routingKeysToAssign.Add(routingKey);
                if (!processors.TryGetValue(routingKey, out var processorInfo))
                {
                    var processor = processorProvider.GetProcessor(routingKey);
                    var processorHost = new ProcessorHost(settings.ConsumerGroupHostId, routingKey, cancellationTokenSource.Token, processor, log, consumer);
                    processorInfo = (processor, processorHost, AssignedPartitions: new int[0]);
                    processors.Add(routingKey, processorInfo);
                    processorHost.Start();
                }

                var partitionsToAssign = topicPartitionsByRoutingKeyGroup.Select(x => x.Partition).ToArray();
                var newPartitions = partitionsToAssign.Except(processorInfo.AssignedPartitions).ToList();
                if (newPartitions.Any())
                {
                    var startTimestampOnRebalance = processorInfo.Processor.GetStartTimestampOnRebalance(routingKey);
                    if (!startTimestampOnRebalance.HasValue)
                        topicPartitionOffsets.AddRange(newPartitions.Select(x => new TopicPartitionOffset(routingKey, x, Offset.Invalid)));
                    else
                    {
                        var timestampToSearch = new Timestamp(startTimestampOnRebalance.Value.ToUnixTimeMilliseconds(), TimestampType.NotAvailable);
                        var timestampsToSearch = newPartitions.Select(x => new TopicPartitionTimestamp(routingKey, x, timestampToSearch));
                        var offsetsForTimes = consumer.OffsetsForTimes(timestampsToSearch, timeout: Timeout.InfiniteTimeSpan);
                        foreach (var topicPartitionOffsetError in offsetsForTimes)
                        {
                            if (!topicPartitionOffsetError.Error)
                            {
                                topicPartitionOffsets.Add(new TopicPartitionOffset(topicPartitionOffsetError.TopicPartition, topicPartitionOffsetError.Offset));
                                // todo (avk, 06.10.2017): maybe we need to seek consumer for these partitions
                            }
                            else
                            {
                                log.Error($"consumerName: {consumer.Name}, memberId: {consumer.MemberId}, failed to get offset for timestamp {startTimestampOnRebalance}: {topicPartitionOffsetError}");
                                topicPartitionOffsets.Add(new TopicPartitionOffset(topicPartitionOffsetError.TopicPartition, Offset.Invalid));
                            }
                        }
                    }
                }
                var remainingPartitions = partitionsToAssign.Except(topicPartitionOffsets.Select(x => x.Partition));
                topicPartitionOffsets.AddRange(remainingPartitions.Select(x => new TopicPartitionOffset(routingKey, x, Offset.Invalid)));
                processorInfo.AssignedPartitions = partitionsToAssign;
            }

            var processorHostsToStop = new List<ProcessorHost>();
            var routingKeysToUnassign = processors.Keys.Except(routingKeysToAssign).ToList();
            foreach (var routingKey in routingKeysToUnassign)
            {
                processorHostsToStop.Add(processors[routingKey].ProcessorHost);
                processors.Remove(routingKey);
            }
            StopProcessors(processorHostsToStop);

            return topicPartitionOffsets;
        }

        private void OnMessage(Message<Null, byte[]> message)
        {
            if (!processors.TryGetValue(message.Topic, out var processor))
                throw new InvalidOperationException($"Invalid routingKey: {message.Topic}");
            processor.ProcessorHost.Enqueue(message);
        }
    }
}
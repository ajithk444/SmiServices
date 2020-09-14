﻿using System.Text;

namespace Smi.Common.Options
{
    /// <summary>
    /// Configuration options needed to receive messages from a RabbitMQ queue.
    /// </summary>
    public class ConsumerOptions : IOptions
    {
        /// <summary>
        /// Name of the queue to consume from.
        /// </summary>
        public string QueueName { get; set; }

        /// <summary>
        /// Max number of messages the queue will send the consumer before receiving an acknowledgement.
        /// </summary>
        public ushort QoSPrefetchCount { get; set; }

        /// <summary>
        /// Automatically acknowledge any messages sent to the consumer.
        /// </summary>
        public bool AutoAck { get; set; }


        /// <summary>
        /// Verifies that the individual options have been populated
        /// </summary>
        /// <returns></returns>
        public bool VerifyPopulated()
        {
            return !string.IsNullOrWhiteSpace(QueueName) && (QoSPrefetchCount != 0);
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("QueueName: " + QueueName);
            sb.Append(", AutoAck: " + AutoAck);
            sb.Append(", QoSPrefetchCount: " + QoSPrefetchCount);
            return sb.ToString();
        }
    }
}

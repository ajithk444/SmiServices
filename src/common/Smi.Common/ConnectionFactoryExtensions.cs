﻿using RabbitMQ.Client;
using Smi.Common.Options;

namespace Smi.Common
{
    public static class ConnectionFactoryExtensions
    {
        public static ConnectionFactory CreateConnectionFactory(this RabbitOptions options)
            => new ConnectionFactory
            {
                HostName = options.RabbitMqHostName,
                VirtualHost = options.RabbitMqVirtualHost,
                Port = options.RabbitMqHostPort,
                UserName = options.RabbitMqUserName,
                Password = options.RabbitMqPassword,
                AutomaticRecoveryEnabled = true,
                UseBackgroundThreadsForIO = true
            };
    }
}

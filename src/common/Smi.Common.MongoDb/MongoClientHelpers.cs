﻿
using System;
using System.Linq;
using Smi.Common.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NLog;


namespace Smi.Common.MongoDB
{
    public static class MongoClientHelpers
    {
        private const string MongoServicePasswordVar = "MONGO_SERVICE_PASSWORD";
        private const string AuthDatabase = "admin"; // Always authenticate against the admin database

        private static readonly ILogger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Creates a <see cref="MongoClient"/> from the given options, and checks that the user has the "readWrite" role for the given database
        /// </summary>
        /// <param name="options"></param>
        /// <param name="applicationName"></param>
        /// <param name="skipAuthentication"></param>
        /// <returns></returns>
        public static MongoClient GetMongoClient(MongoDbOptions options, string applicationName, bool skipAuthentication = false)
        {
            if (!options.AreValid())
                throw new ApplicationException($"Invalid MongoDB options: {options}");

            if (skipAuthentication || options.UserName == string.Empty)
                return new MongoClient(new MongoClientSettings
                {
                    ApplicationName = applicationName,
                    Server = new MongoServerAddress(options.HostName, options.Port),
                    WriteConcern = new WriteConcern(journal: true)
                });

            string password = Environment.GetEnvironmentVariable(MongoServicePasswordVar, EnvironmentVariableTarget.Process);

            if (string.IsNullOrWhiteSpace(password))
                throw new ApplicationException($"MongoDB password must be set in \"{MongoServicePasswordVar}\"");

            MongoCredential credentials = MongoCredential.CreateCredential(AuthDatabase, options.UserName, password);

            var mongoClientSettings = new MongoClientSettings
            {
                ApplicationName = applicationName,
                Credential = credentials,
                Server = new MongoServerAddress(options.HostName, options.Port),
                WriteConcern = new WriteConcern(journal: true)
            };

            var client = new MongoClient(mongoClientSettings);

            try
            {
                IMongoDatabase db = client.GetDatabase(AuthDatabase);
                var queryResult = db.RunCommand<BsonDocument>(new BsonDocument("usersInfo", options.UserName));

                if (!(queryResult["ok"] == 1))
                    throw new ApplicationException($"Could not check authentication for user \"{options.UserName}\"");

                var roles = (BsonArray)queryResult[0][0]["roles"];

                var hasReadWrite = false;
                foreach (BsonDocument role in roles.Select(x => x.AsBsonDocument))
                    if (role["db"].AsString == options.DatabaseName && role["role"].AsString == "readWrite")
                        hasReadWrite = true;

                if (!hasReadWrite)
                    throw new ApplicationException($"User \"{options.UserName}\" does not have readWrite permissions on database \"{options.DatabaseName}\"");

                _logger.Debug($"User \"{options.UserName}\" successfully authenticated to MongoDB database \"{options.DatabaseName}\"");
            }
            catch (MongoAuthenticationException e)
            {
                throw new ApplicationException($"Could not verify authentication for user \"{options.UserName}\" on database \"{options.DatabaseName}\"", e);
            }

            return client;
        }
    }
}

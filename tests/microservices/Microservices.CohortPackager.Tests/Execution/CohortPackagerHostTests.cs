using System;
using System.Collections.Generic;
using System.Threading;
using Microservices.CohortPackager.Execution;
using Microservices.CohortPackager.Execution.ExtractJobStorage;
using Microservices.CohortPackager.Execution.JobProcessing;
using MongoDB.Driver;
using NUnit.Framework;
using Smi.Common.Messages;
using Smi.Common.Messages.Extraction;
using Smi.Common.MessageSerialization;
using Smi.Common.MongoDB;
using Smi.Common.Options;
using Smi.Common.Tests;

namespace Microservices.CohortPackager.Tests.Execution
{
    [TestFixture, RequiresMongoDb, RequiresRabbit]
    public class CohortPackagerHostTests
    {

        #region Fixture Methods 

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            TestLogger.Setup();
        }

        [OneTimeTearDown]
        public void OneTimeTearDown() { }

        #endregion

        #region Test Methods

        [SetUp]
        public void SetUp() { }

        [TearDown]
        public void TearDown() { }

        #endregion

        #region Tests

        private class TestJobCompleteNotifier : IJobCompleteNotifier
        {
            public bool JobCompleted { get; private set; }
            public void NotifyJobCompleted(ExtractJobInfo jobInfo)
            {
                JobCompleted = true;
            }
        }
        
        [Test]
        public void TestCohortPackagerHost_HappyPath()
        {
            Guid jobId = Guid.NewGuid();
            var testExtractionRequestInfoMessage = new ExtractionRequestInfoMessage
            {
                ExtractionModality = "MR",
                JobSubmittedAt = DateTime.UtcNow,
                ProjectNumber = "testProj1",
                ExtractionJobIdentifier = jobId,
                ExtractionDirectory = "test",
                KeyTag = "StudyInstanceUID",
                KeyValueCount = 1,
            };
            var testExtractFileCollectionInfoMessage = new ExtractFileCollectionInfoMessage
            {
                JobSubmittedAt = DateTime.UtcNow,
                ProjectNumber = "testProj1",
                ExtractionJobIdentifier = jobId,
                ExtractionDirectory = "test",
                ExtractFileMessagesDispatched = new JsonCompatibleDictionary<MessageHeader, string>
                {
                    { new MessageHeader(), "study-1-anon-1.dcm" },
                    { new MessageHeader(), "study-1-anon-2.dcm" },
                },
                RejectionReasons = new Dictionary<string, int>
                {
                    {"rejected - blah", 1 },
                },
                KeyValue = "study-1",
            };
            var testExtractFileStatusMessage = new ExtractFileStatusMessage
            {
                JobSubmittedAt = DateTime.UtcNow,
                AnonymisedFileName = "study-1-anon-1.dcm",
                ProjectNumber = "testProj1",
                ExtractionJobIdentifier = jobId,
                ExtractionDirectory = "test",
                Status = ExtractFileStatus.ErrorWontRetry,
                StatusMessage = "Couldn't anonymise",
                DicomFilePath = "study-1-orig-1.dcm",
            };
            var testIsIdentifiableMessage = new IsIdentifiableMessage
            {
                JobSubmittedAt = DateTime.UtcNow,
                AnonymisedFileName = "study-1-anon-2.dcm",
                ProjectNumber = "testProj1",
                ExtractionJobIdentifier = jobId,
                ExtractionDirectory = "test",
                IsIdentifiable = false,
                Report = "[]",
                DicomFilePath = "study-1-orig-2.dcm",
            };


            GlobalOptions globals = GlobalOptions.Load();
            globals.RabbitOptions.RabbitMqHostName = "192.168.56.104";
            globals.MongoDatabases.ExtractionStoreOptions.HostName = "192.168.56.104";
            globals.CohortPackagerOptions.JobWatcherTimeoutInSeconds = 5;

            MongoClient client = MongoClientHelpers.GetMongoClient(globals.MongoDatabases.ExtractionStoreOptions, "test", true);
            client.DropDatabase(globals.MongoDatabases.ExtractionStoreOptions.DatabaseName);

            using (var tester = new MicroserviceTester(
                globals.RabbitOptions,
                globals.CohortPackagerOptions.ExtractRequestInfoOptions,
                globals.CohortPackagerOptions.FileCollectionInfoOptions,
                globals.CohortPackagerOptions.AnonFailedOptions,
                globals.CohortPackagerOptions.VerificationStatusOptions))
            {
                tester.SendMessage(globals.CohortPackagerOptions.ExtractRequestInfoOptions, new MessageHeader(), testExtractionRequestInfoMessage);
                tester.SendMessage(globals.CohortPackagerOptions.FileCollectionInfoOptions, new MessageHeader(), testExtractFileCollectionInfoMessage);
                tester.SendMessage(globals.CohortPackagerOptions.AnonFailedOptions, new MessageHeader(), testExtractFileStatusMessage);
                tester.SendMessage(globals.CohortPackagerOptions.VerificationStatusOptions, new MessageHeader(), testIsIdentifiableMessage);

                var notifier = new TestJobCompleteNotifier();
                var host = new CohortPackagerHost(globals, notifier, null, false);
                host.Start();

                var timeoutSecs = 10;
                while (!notifier.JobCompleted && timeoutSecs > 0)
                {
                    --timeoutSecs;
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }

                host.Stop("Test end");
                Assert.True(notifier.JobCompleted && timeoutSecs >= 0);
            }
        }

        #endregion
    }
}

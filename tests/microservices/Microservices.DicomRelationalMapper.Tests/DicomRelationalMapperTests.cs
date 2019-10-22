﻿
using Dicom;
using FAnsi.Implementations.MicrosoftSQL;
using Microservices.DicomRelationalMapper.Execution;
using Microservices.DicomRelationalMapper.Tests.TestTagGeneration;
using NUnit.Framework;
using Rdmp.Core.Curation;
using Rdmp.Dicom.PipelineComponents.DicomSources;
using Rdmp.Dicom.TagPromotionSchema;
using ReusableLibraryCode.Checks;
using Smi.Common.Options;
using Smi.Common.Tests;
using System;
using System.IO;
using System.Linq;
using Tests.Common;
using DatabaseType = FAnsi.DatabaseType;

namespace Microservices.Tests.RDMPTests
{
    [RequiresRabbit, RequiresRelationalDb(FAnsi.DatabaseType.MicrosoftSQLServer)]
    public class DicomRelationalMapperTests : DatabaseTests
    {
        private DicomRelationalMapperTestHelper _helper;
        private GlobalOptions _globals;

        [OneTimeSetUp]
        public void Setup()
        {
            _globals = GlobalOptions.Load("default.yaml", TestContext.CurrentContext.TestDirectory);
            var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);
            _helper = new DicomRelationalMapperTestHelper();
            _helper.SetupSuite(db, RepositoryLocator, _globals, typeof(DicomDatasetCollectionSource));
        }


        [Test]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(10, false)]
        public void TestLoadingOneImage_SingleFileMessage(int numberOfMessagesToSend, bool mixInATextFile)
        {
            _helper.TruncateTablesIfExists();

            DirectoryInfo d = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, "DicomRelationalMapperTests"));
            d.Create();

            var fi = new FileInfo(Path.Combine(d.FullName, "MyTestFile.dcm"));
            //File.WriteAllBytes(fi.FullName, TestDicomFiles.IM_0001_0013);

            if (mixInATextFile)
            {
                var randomText = new FileInfo(Path.Combine(d.FullName, "RandomTextFile.dcm"));
                File.WriteAllLines(randomText.FullName, new[] { "I love dancing", "all around the world", "boy the world is a big place eh?" });
            }

            //creates the queues, exchanges and bindings
            var tester = new MicroserviceTester(_globals.RabbitOptions, _globals.DicomRelationalMapperOptions);
            tester.CreateExchange(_globals.RabbitOptions.FatalLoggingExchange, null);

            using (var host = new DicomRelationalMapperHost(_globals))
            {
                host.Start();

                var timeline = new TestTimeline(tester);

                //send the message 10 times over a 10 second period
                for (int i = 0; i < numberOfMessagesToSend; i++)
                {
                    timeline
                        .SendMessage(_globals.DicomRelationalMapperOptions, _helper.GetDicomFileMessage(_globals.FileSystemOptions.FileSystemRoot, fi))
                        .Wait(1000);
                }

                //start the timeline
                timeline.StartTimeline();

                new TestTimelineAwaiter().Await(() => host.Consumer.AckCount >= numberOfMessagesToSend);

                Assert.AreEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                Assert.AreEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                Assert.AreEqual(1, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                host.Stop("Test end");
            }

            tester.Shutdown();
        }

        [Test]
        public void TestLoadingOneImage_MileWideTest()
        {
            _helper.TruncateTablesIfExists();

            DirectoryInfo d = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, "DicomFileGeneratorTest"));
            d.Create();

            foreach (var oldFile in d.EnumerateFiles())
                oldFile.Delete();

            var seedDir = d.CreateSubdirectory("Seed");

            var seedFile = new FileInfo(Path.Combine(seedDir.FullName, "MyTestFile.dcm"));
            //File.WriteAllBytes(seedFile.FullName, TestDicomFiles.IM_0001_0013);

            var existingColumns = _helper.ImageTable.DiscoverColumns();

            using (DicomGenerator g = new DicomGenerator(d.FullName, "Seed", 1000))
            {
                g.GenerateTestSet(1, 100, new TestTagDataGenerator(), 300, false);

                //Add all the tags generated to the dataset
                foreach (DicomTag tag in g.RandomTagsAdded)
                {
                    //don't generate unique identifiers
                    if (tag.DictionaryEntry.ValueRepresentations.Contains(DicomVR.UI))
                        continue;

                    var dataType = TagColumnAdder.GetDataTypeForTag(tag.DictionaryEntry.Keyword, new MicrosoftSQLTypeTranslater());

                    //todo: this is still not working correctly, not sure why
                    if (dataType == "smallint")
                        continue;

                    //if we already have the column in our table
                    var colName = DicomTypeTranslation.DicomTypeTranslaterReader.GetColumnNameForTag(tag, false);

                    //don't add it
                    if (existingColumns.Any(c => c.GetRuntimeName().Equals(colName, StringComparison.CurrentCultureIgnoreCase)))
                        continue;



                    var adder = new TagColumnAdder(tag.DictionaryEntry.Keyword, dataType, _helper.ImageTableInfo, new AcceptAllCheckNotifier(), false);
                    adder.SkipChecksAndSynchronization = true;
                    adder.Execute();
                }

                new TableInfoSynchronizer(_helper.ImageTableInfo).Synchronize(new AcceptAllCheckNotifier());

                //creates the queues, exchanges and bindings
                var tester = new MicroserviceTester(_globals.RabbitOptions, _globals.DicomRelationalMapperOptions);
                tester.CreateExchange(_globals.RabbitOptions.FatalLoggingExchange, null);

                using (var host = new DicomRelationalMapperHost(_globals))
                {
                    host.Start();

                    var timeline = new TestTimeline(tester);
                    foreach (var f in g.FilesCreated)
                    {

                        timeline
                            .SendMessage(_globals.DicomRelationalMapperOptions, _helper.GetDicomFileMessage(_globals.FileSystemOptions.FileSystemRoot, f));
                    }

                    //start the timeline
                    timeline.StartTimeline();

                    new TestTimelineAwaiter().Await(() => host.Consumer.MessagesProcessed == 1);

                    Assert.GreaterOrEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                    Assert.GreaterOrEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                    Assert.AreEqual(1, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                    host.Stop("Test end");
                }

                tester.Shutdown();
            }
        }


        [TestCase(10, 1000)]
        public void DicomFileGeneratorTest(int numberOfImges, int intervalInMilliseconds)
        {
            _helper.TruncateTablesIfExists();

            DirectoryInfo d = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, "DicomFileGeneratorTest"));
            d.Create();

            foreach (var oldFile in d.EnumerateFiles())
                oldFile.Delete();

            var seedDir = d.CreateSubdirectory("Seed");

            var seedFile = new FileInfo(Path.Combine(seedDir.FullName, "MyTestFile.dcm"));
            //File.WriteAllBytes(seedFile.FullName, TestDicomFiles.IM_0001_0013);

            using (DicomGenerator g = new DicomGenerator(d.FullName, "Seed", 1000))
            {
                g.GenerateTestSet(numberOfImges, 100, new TestTagDataGenerator(), 100, false);

                //creates the queues, exchanges and bindings
                var tester = new MicroserviceTester(_globals.RabbitOptions, _globals.DicomRelationalMapperOptions);
                tester.CreateExchange(_globals.RabbitOptions.FatalLoggingExchange, null);

                using (var host = new DicomRelationalMapperHost(_globals))
                {
                    host.Start();

                    var timeline = new TestTimeline(tester);
                    foreach (var f in g.FilesCreated)
                    {

                        timeline
                            .SendMessage(_globals.DicomRelationalMapperOptions, _helper.GetDicomFileMessage(_globals.FileSystemOptions.FileSystemRoot, f))
                            .Wait(intervalInMilliseconds);
                    }

                    //start the timeline
                    timeline.StartTimeline();

                    new TestTimelineAwaiter().Await(() => host.Consumer.MessagesProcessed == numberOfImges);

                    Assert.GreaterOrEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                    Assert.GreaterOrEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                    Assert.AreEqual(numberOfImges, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                    host.Stop("Test end");
                }

                tester.Shutdown();
            }
        }

        /// <summary>
        /// Tests the abilities of the DLE to not load identical FileMessage
        /// </summary>
        [Test]
        public void IdenticalDatasetsTest()
        {
            _helper.TruncateTablesIfExists();

            var ds = new DicomDataset();
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, "123");
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, "123");
            ds.AddOrUpdate(DicomTag.StudyInstanceUID, "123");
            ds.AddOrUpdate(DicomTag.PatientID, "123");

            var msg1 = _helper.GetDicomFileMessage(ds, _globals.FileSystemOptions.FileSystemRoot, Path.Combine(_globals.FileSystemOptions.FileSystemRoot, "mydicom.dcm"));
            var msg2 = _helper.GetDicomFileMessage(ds, _globals.FileSystemOptions.FileSystemRoot, Path.Combine(_globals.FileSystemOptions.FileSystemRoot, "mydicom.dcm"));


            //creates the queues, exchanges and bindings
            using (var tester = new MicroserviceTester(_globals.RabbitOptions, _globals.DicomRelationalMapperOptions))
            {
                tester.CreateExchange(_globals.RabbitOptions.FatalLoggingExchange, null);
                tester.SendMessage(_globals.DicomRelationalMapperOptions, msg1);
                tester.SendMessage(_globals.DicomRelationalMapperOptions, msg2);

                _globals.DicomRelationalMapperOptions.RunChecks = true;

                using (var host = new DicomRelationalMapperHost(_globals))
                {
                    host.Start();

                    new TestTimelineAwaiter().Await(() => host.Consumer.MessagesProcessed == 2);

                    Assert.GreaterOrEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                    Assert.GreaterOrEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                    Assert.AreEqual(1, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                    host.Stop("Test end");
                }
                tester.Shutdown();
            }
        }
    }
}
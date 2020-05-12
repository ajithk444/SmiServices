﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using BadMedicine;
using BadMedicine.Dicom;
using Dicom;
using DicomTypeTranslation;
using FAnsi.Implementations.MicrosoftSQL;
using Microservices.DicomRelationalMapper.Execution;
using Microservices.Tests.RDMPTests;
using NUnit.Framework;
using Rdmp.Core.Curation;
using Rdmp.Dicom.PipelineComponents.DicomSources;
using Rdmp.Dicom.TagPromotionSchema;
using ReusableLibraryCode.Checks;
using Smi.Common.Messages;
using Smi.Common.Options;
using Smi.Common.Tests;
using Tests.Common;
using TypeGuesser;
using DatabaseType = FAnsi.DatabaseType;

namespace Microservices.DicomRelationalMapper.Tests
{
    [RequiresRabbit, RequiresRelationalDb(DatabaseType.MicrosoftSQLServer)]
    public class DicomRelationalMapperTests : DatabaseTests
    {
        private DicomRelationalMapperTestHelper _helper;
        private GlobalOptions _globals;
        private MicroserviceTester tester;

        [OneTimeSetUp]
        public void Setup()
        {
            BlitzMainDataTables();

            _globals = GlobalOptions.Load("default.yaml", TestContext.CurrentContext.TestDirectory);
            var db = GetCleanedServer(DatabaseType.MicrosoftSQLServer);
            _helper = new DicomRelationalMapperTestHelper();
            _helper.SetupSuite(db, RepositoryLocator, _globals, typeof(DicomDatasetCollectionSource));

            TestLogger.Setup();

            //creates the queues, exchanges and bindings
            var tester = new MicroserviceTester(_globals.RabbitOptions, _globals.DicomRelationalMapperOptions);
            tester.CreateExchange(_globals.RabbitOptions.FatalLoggingExchange, null);
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            tester?.Shutdown();
        }

        [Test]
        public void Test_DodgyTagNames()
        {
            _helper.TruncateTablesIfExists();

            DirectoryInfo d = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(Test_DodgyTagNames)));
            d.Create();

            var fi = TestData.Create(new FileInfo(Path.Combine(d.FullName, "MyTestFile.dcm")));
            var fi2 = TestData.Create(new FileInfo(Path.Combine(d.FullName, "MyTestFile2.dcm")));
            
            DicomFile dcm;

            using (var stream = File.OpenRead(fi.FullName))
            {    
                dcm = DicomFile.Open(stream);
                dcm.Dataset.AddOrUpdate(DicomTag.PrintRETIRED, "FISH");
                dcm.Dataset.AddOrUpdate(DicomTag.Date, new DateTime(2001,01,01));
                dcm.Save(fi2.FullName);
            }
            
            var adder = new TagColumnAdder(DicomTypeTranslaterReader.GetColumnNameForTag(DicomTag.Date,false), "datetime2", _helper.ImageTableInfo, new AcceptAllCheckNotifier(), false);
            adder.Execute();

            adder = new TagColumnAdder(DicomTypeTranslaterReader.GetColumnNameForTag(DicomTag.PrintRETIRED,false), "datetime2", _helper.ImageTableInfo, new AcceptAllCheckNotifier(), false);
            adder.Execute();
            
            fi.Delete();
            File.Move(fi2.FullName,fi.FullName);

            using (var host = new DicomRelationalMapperHost(_globals, loadSmiLogConfig: false))
            {
                host.Start();
                host.Consumer.TestMessage(_helper.GetDicomFileMessage(_globals.FileSystemOptions.FileSystemRoot, fi), new MessageHeader());

                new TestTimelineAwaiter().Await(() => host.Consumer.AckCount >= 1, null, 30000, () => host.Consumer.DleErrors);

                Assert.AreEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                Assert.AreEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                Assert.AreEqual(1, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                host.Stop("Test end");
            }
        }


        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(10, false)]
        public void TestLoadingOneImage_SingleFileMessage(int numberOfMessagesToSend, bool mixInATextFile)
        {
            _helper.TruncateTablesIfExists();

            DirectoryInfo d = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(TestLoadingOneImage_SingleFileMessage)));
            d.Create();

            var fi = TestData.Create(new FileInfo(Path.Combine(d.FullName, "MyTestFile.dcm")));

            if (mixInATextFile)
            {
                var randomText = new FileInfo(Path.Combine(d.FullName, "RandomTextFile.dcm"));
                File.WriteAllLines(randomText.FullName, new[] { "I love dancing", "all around the world", "boy the world is a big place eh?" });
            }

            using (var host = new DicomRelationalMapperHost(_globals, loadSmiLogConfig: false))
            {
                host.Start();
                //send the message 10 times
                for (int i = 0; i < numberOfMessagesToSend; i++)
                {
                    host.Consumer.TestMessage(_helper.GetDicomFileMessage(_globals.FileSystemOptions.FileSystemRoot, fi), new Smi.Common.Messages.MessageHeader());
                }

                new TestTimelineAwaiter().Await(() => host.Consumer.AckCount+host.Consumer.NackCount >= numberOfMessagesToSend, null, 30000, () => host.Consumer.DleErrors);
                Assert.AreEqual(numberOfMessagesToSend,host.Consumer.AckCount,"Not all messages acked");
                host.Stop("Test finished");

                Assert.AreEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                Assert.AreEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                Assert.AreEqual(1, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");
            }
        }

        [Test]
        public void TestLoadingOneImage_MileWideTest()
        {
            _helper.TruncateTablesIfExists();

            DirectoryInfo d = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(TestLoadingOneImage_MileWideTest)));
            d.Create();

            var r = new Random(5000);
            FileInfo[] files;

            using (var g = new DicomDataGenerator(r, d, "CT")) 
                files = g.GenerateImageFiles(1, r).ToArray();

            Assert.AreEqual(1,files.Length);

            var existingColumns = _helper.ImageTable.DiscoverColumns();

            //Add 200 random tags
            foreach (string tag in TagColumnAdder.GetAvailableTags().OrderBy(a => r.Next()).Take(200))
            {
                string dataType;

                try
                {
                    dataType = TagColumnAdder.GetDataTypeForTag(tag, new MicrosoftSQLTypeTranslater());
                    
                }
                catch (Exception)
                {
                    continue;
                }

                if (existingColumns.Any(c => c.GetRuntimeName().Equals(tag)))
                    continue;

                var adder = new TagColumnAdder(tag, dataType, _helper.ImageTableInfo, new AcceptAllCheckNotifier(), false);
                adder.SkipChecksAndSynchronization = true;
                adder.Execute();
            }

            new TableInfoSynchronizer(_helper.ImageTableInfo).Synchronize(new AcceptAllCheckNotifier());

            using (var host = new DicomRelationalMapperHost(_globals, loadSmiLogConfig: false))
            {
                host.Start();

                using (var timeline = new TestTimeline(tester))
                {
                    foreach (var f in files)
                        host.Consumer.TestMessage(_helper.GetDicomFileMessage(_globals.FileSystemOptions.FileSystemRoot, f),new MessageHeader());

                    //start the timeline
                    timeline.StartTimeline();

                    new TestTimelineAwaiter().Await(() => host.Consumer.MessagesProcessed == 1, null, 30000, () => host.Consumer.DleErrors);

                    Assert.GreaterOrEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                    Assert.GreaterOrEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                    Assert.AreEqual(1, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                    host.Stop("Test end");
                }
            }
        }

        /*
        [TestCase(10, 1000)]
        public void DicomFileGeneratorTest(int numberOfImges, int intervalInMilliseconds)
        {
            _helper.TruncateTablesIfExists();

            DirectoryInfo d = new DirectoryInfo(Path.Combine(TestContext.CurrentContext.TestDirectory, nameof(DicomFileGeneratorTest)));
            d.Create();

            foreach (var oldFile in d.EnumerateFiles())
                oldFile.Delete();

            var seedDir = d.CreateSubdirectory("Seed");

            TestData.Create(new FileInfo(Path.Combine(seedDir.FullName, "MyTestFile.dcm")));

            using (DicomGenerator g = new DicomGenerator(d.FullName, "Seed", 1000))
            {
                g.GenerateTestSet(numberOfImges, 100, new TestTagDataGenerator(), 100, false);

                using (var host = new DicomRelationalMapperHost(_globals, loadSmiLogConfig: false))
                {
                    host.Start();

                    using(var timeline = new TestTimeline(tester))
                    {
                        foreach (var f in g.FilesCreated)
                        {

                            timeline
                                .SendMessage(_globals.DicomRelationalMapperOptions, _helper.GetDicomFileMessage(_globals.FileSystemOptions.FileSystemRoot, f))
                                .Wait(intervalInMilliseconds);
                        }

                        //start the timeline
                        timeline.StartTimeline();


                        new TestTimelineAwaiter().Await(() => host.Consumer.MessagesProcessed == numberOfImges, null, 30000, () => host.Consumer.DleErrors);
                        Assert.GreaterOrEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                        Assert.GreaterOrEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                        Assert.AreEqual(numberOfImges, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                        host.Stop("Test end");
                    }
                    
                }
            }
        }
        */

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

            _globals.DicomRelationalMapperOptions.RunChecks = true;

            using (var host = new DicomRelationalMapperHost(_globals, loadSmiLogConfig: false))
            {
                host.Start();
                host.Consumer.TestMessage(msg1, new Smi.Common.Messages.MessageHeader());
                host.Consumer.TestMessage(msg2, new Smi.Common.Messages.MessageHeader());

                new TestTimelineAwaiter().Await(() => host.Consumer.MessagesProcessed == 2, null, 30000, () => host.Consumer.DleErrors);

                Assert.GreaterOrEqual(1, _helper.SeriesTable.GetRowCount(), "SeriesTable did not have the expected number of rows in LIVE");
                Assert.GreaterOrEqual(1, _helper.StudyTable.GetRowCount(), "StudyTable did not have the expected number of rows in LIVE");
                Assert.AreEqual(1, _helper.ImageTable.GetRowCount(), "ImageTable did not have the expected number of rows in LIVE");

                host.Stop("Test end");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microservices.CohortPackager.Execution.ExtractJobStorage.MongoExtractJobStore.ObjectModel;
using MongoDB.Driver;
using Smi.Common.Helpers;
using Smi.Common.Messages;
using Smi.Common.Messages.Extraction;


namespace Microservices.CohortPackager.Execution.ExtractJobStorage.MongoExtractJobStore
{
    public class MongoExtractJobStore : ExtractJobStore
    {
        private const string ExtractJobCollectionName = "extractJobStore";
        private const string QuarantineCollectionName = "extractJobQuarantine";
        private const string ArchiveCollectionName = "extractJobArchive";
        private const string StatusCollectionPrefix = "temp_statuses_";

        private readonly IMongoDatabase _database;

        // NOTE(rkm 2020-02-25) Lock needed when accessing any of the below collections, but not the status collections
        private readonly object _oJobStoreLock = new object();
        private readonly IMongoCollection<MongoExtractJob> _jobInfoCollection;
        private readonly IMongoCollection<ArchivedMongoExtractJob> _jobArchiveCollection;
        private readonly IMongoCollection<QuarantinedMongoExtractJob> _jobQuarantineCollection;

        private readonly FindOptions<MongoExtractJob> _findOptions = new FindOptions<MongoExtractJob>
        {
            BatchSize = 1,
            NoCursorTimeout = false
        };

        private readonly DateTimeProvider _dateTimeProvider;

        public MongoExtractJobStore(IMongoDatabase database)
            : this(database, new DateTimeProvider()) { }

        public MongoExtractJobStore(IMongoDatabase database, DateTimeProvider dateTimeProvider)
        {
            _database = database;
            _dateTimeProvider = dateTimeProvider;

            _jobInfoCollection = _database.GetCollection<MongoExtractJob>(ExtractJobCollectionName);
            _jobArchiveCollection = _database.GetCollection<ArchivedMongoExtractJob>(ArchiveCollectionName);
            _jobQuarantineCollection = _database.GetCollection<QuarantinedMongoExtractJob>(QuarantineCollectionName);

            long count = _jobInfoCollection.CountDocuments(FilterDefinition<MongoExtractJob>.Empty);

            Logger.Info(count > 0 ? $"Connected to job store with {count} existing jobs" : "Empty job store created successfully");
        }

        protected override void PersistMessageToStoreImpl(ExtractionRequestInfoMessage requestInfoMessage, IMessageHeader header)
        {
            Guid jobIdentifier = requestInfoMessage.ExtractionJobIdentifier;
            ExtractJobHeader jobHeader = ExtractJobHeader.FromMessageHeader(header, _dateTimeProvider);

            lock (_oJobStoreLock)
            {
                if (InArchiveCollection(jobIdentifier, out MongoExtractJob _) || InQuarantineCollection(jobIdentifier, out _))
                    throw new ApplicationException(
                        "Received an ExtractionRequestInfoMessage for a job that exists in the archive or quarantine");

                if (InJobCollection(jobIdentifier, out MongoExtractJob existing))
                {
                    if (existing.JobStatus != ExtractJobStatus.WaitingForJobInfo)
                        throw new ApplicationException(
                            "Received ExtractionRequestInfoMessage for a job which has already started processing");

                    existing.Header = jobHeader;
                    existing.ProjectNumber = requestInfoMessage.ProjectNumber;
                    existing.ExtractionDirectory = requestInfoMessage.ExtractionDirectory;
                    existing.KeyCount = requestInfoMessage.KeyValueCount;
                    existing.ExtractionModality = requestInfoMessage.ExtractionModality;
                    existing.JobSubmittedAt = requestInfoMessage.JobSubmittedAt;

                    if (existing.KeyCount == existing.FileCollectionInfo.Count)
                        existing.JobStatus = ExtractJobStatus.WaitingForStatuses;

                    _jobInfoCollection.ReplaceOne(GetFilterForSpecificJob<MongoExtractJob>(jobIdentifier), existing);

                    return;
                }

                var newJobInfo = new MongoExtractJob
                {
                    Header = jobHeader,
                    ExtractionJobIdentifier = requestInfoMessage.ExtractionJobIdentifier,
                    ProjectNumber = requestInfoMessage.ProjectNumber,
                    JobSubmittedAt = requestInfoMessage.JobSubmittedAt,
                    JobStatus = ExtractJobStatus.WaitingForCollectionInfo,
                    ExtractionDirectory = requestInfoMessage.ExtractionDirectory,
                    KeyTag = requestInfoMessage.KeyTag,
                    KeyCount = requestInfoMessage.KeyValueCount,
                    FileCollectionInfo = new List<MongoExpectedFilesForKey>(),
                    ExtractionModality = requestInfoMessage.ExtractionModality
                };

                _jobInfoCollection.InsertOne(newJobInfo);
            }
        }

        protected override void PersistMessageToStoreImpl(ExtractFileCollectionInfoMessage collectionInfoMessage, IMessageHeader header)
        {
            Guid jobIdentifier = collectionInfoMessage.ExtractionJobIdentifier;
            var expectedFiles = new List<ExpectedAnonymisedFileInfo>();

            // Extract the list of expected anonymised files from the message
            collectionInfoMessage.ExtractFileMessagesDispatched.ToList().ForEach(
                x => expectedFiles.Add(
                    new ExpectedAnonymisedFileInfo
                    {
                        ExtractFileMessageGuid = x.Key.MessageGuid,
                        AnonymisedFilePath = x.Value
                    }));

            // TODO(rkm 2020-02-06) RejectionReasons
            var rejectionReasons = new List<string>();

            var expectedFilesForKey = new MongoExpectedFilesForKey
            {
                Header = ExtractFileCollectionHeader.FromMessageHeader(header, _dateTimeProvider),
                Key = collectionInfoMessage.KeyValue,
                AnonymisedFiles = expectedFiles
            };

            lock (_oJobStoreLock)
            {
                if (InArchiveCollection(jobIdentifier, out MongoExtractJob _) || InQuarantineCollection(jobIdentifier, out _))
                    throw new ApplicationException("Received an ExtractFileCollectionInfoMessage for a job that exists in the archive or quarantine");

                // Most likely already have an entry for this

                if (InJobCollection(jobIdentifier, out MongoExtractJob existing))
                {
                    existing.FileCollectionInfo.Add(expectedFilesForKey);

                    if (existing.FileCollectionInfo.Count == existing.KeyCount)
                        existing.JobStatus = ExtractJobStatus.WaitingForStatuses;

                    _jobInfoCollection.ReplaceOne(GetFilterForSpecificJob<MongoExtractJob>(jobIdentifier), existing);
                    return;
                }

                // Else create a blank one with just the new MongoExpectedFilesForKey

                var newJobInfo = new MongoExtractJob
                {
                    ExtractionJobIdentifier = jobIdentifier,
                    JobSubmittedAt = collectionInfoMessage.JobSubmittedAt,
                    JobStatus = ExtractJobStatus.WaitingForJobInfo,
                    KeyTag = collectionInfoMessage.KeyValue,
                    FileCollectionInfo = new List<MongoExpectedFilesForKey> { expectedFilesForKey },
                };

                _jobInfoCollection.InsertOne(newJobInfo);
            }
        }

        protected override void PersistMessageToStoreImpl(ExtractFileStatusMessage fileStatusMessage, IMessageHeader header)
        {
            string collectionName = StatusCollectionPrefix + fileStatusMessage.ExtractionJobIdentifier;

            var newStatus = new MongoExtractedFileStatusDocument
            {
                Header = new MongoExtractedFileStatusHeaderDocument
                {
                    FileStatusMessageGuid = header.MessageGuid,
                    ProducerIdentifier = header.ProducerExecutableName + "(" + header.ProducerProcessID + ")",
                    ReceivedAt = _dateTimeProvider.UtcNow(),
                },

                Status = fileStatusMessage.Status.ToString(),
                StatusMessage = fileStatusMessage.StatusMessage
            };

            // TODO(rkm 2020-02-25) If the collection doesn't exist then it could have been archived/killed already - check first
            IMongoCollection<MongoExtractedFileStatusDocument> statusCollection = _database.GetCollection<MongoExtractedFileStatusDocument>(collectionName);
            statusCollection.InsertOne(newStatus);

        }

        protected override void PersistMessageToStoreImpl(IsIdentifiableMessage anonVerificationMessage, IMessageHeader header)
        {
            throw new NotImplementedException();
        }

        protected override List<ExtractJobInfo> GetLatestJobInfoImpl(Guid jobId = default)
        {
            // TODO(rkm 2020-02-27 Check why this is for WaitingForStatuses only
            FilterDefinition<MongoExtractJob> filter = Builders<MongoExtractJob>.Filter.Eq(x => x.JobStatus, ExtractJobStatus.WaitingForStatuses);

            // If we have been passed a specific GUID, search for that job only
            if (jobId != default(Guid))
                filter = filter & Builders<MongoExtractJob>.Filter.Eq(x => x.ExtractionJobIdentifier, jobId);

            lock (_oJobStoreLock)
            {
                long docsInCollection = _jobInfoCollection.CountDocuments(FilterDefinition<MongoExtractJob>.Empty);
                Logger.Debug(docsInCollection + " documents in the job collection");

                if (docsInCollection == 0)
                    return new List<ExtractJobInfo>();

                List<MongoExtractJob> jobs = GetExtractJobs(filter).Result;

                var toRet = new List<ExtractJobInfo>();

                foreach (MongoExtractJob job in jobs)
                {
                    try
                    {
                        IMongoCollection<MongoExtractedFileStatusDocument> statusCollection = _database.GetCollection<MongoExtractedFileStatusDocument>(StatusCollectionPrefix + job.ExtractionJobIdentifier);
                        List<MongoExtractedFileStatusDocument> statuses = statusCollection.Find(FilterDefinition<MongoExtractedFileStatusDocument>.Empty).ToList();
                        toRet.Add(MongoExtractJobInfoExtensions.FromMongoJobInfo(job, statuses));
                    }
                    catch (Exception e)
                    {
                        Logger.Error("Error with job data for " + job.ExtractionJobIdentifier + ". Sending to quarantine");
                        QuarantineJob(job.ExtractionJobIdentifier, e);
                    }
                }

                return toRet;
            }
        }

        protected override void CleanupJobDataImpl(Guid extractionJobIdentifier)
        {
            // TODO(rkm 2020-02-06) Make this transactional

            lock (_oJobStoreLock)
            {
                MongoExtractJob toArchive;
                if (!InJobCollection(extractionJobIdentifier, out toArchive))
                    throw new ApplicationException("Could not find job " + extractionJobIdentifier + " in the job store");

                // Convert to an archived job, and update the status
                var archiveJob = new ArchivedMongoExtractJob(toArchive)
                {
                    JobStatus = ExtractJobStatus.Archived,
                    ArchivedAt = _dateTimeProvider.UtcNow()
                };

                try
                {
                    _jobArchiveCollection.InsertOne(archiveJob);
                }
                catch (MongoDuplicateKeyException e)
                {
                    throw new ApplicationException("Extract job " + extractionJobIdentifier + "was already present in the archive", e);
                }

                DeleteResult a = _jobInfoCollection.DeleteOne(GetFilterForSpecificJob<MongoExtractJob>(extractionJobIdentifier));

                if (!(a.IsAcknowledged || a.DeletedCount != 1))
                    throw new Exception("Job data was archived but could not delete original from job store");
            }

            Logger.Debug("Job " + extractionJobIdentifier + " archived");
        }

        protected override void QuarantineJobImpl(Guid extractionJobIdentifier, Exception cause)
        {
            // TODO(rkm 2020-02-06) Make this transactional

            lock (_oJobStoreLock)
            {
                MongoExtractJob toQuarantine;
                if (!InJobCollection(extractionJobIdentifier, out toQuarantine))
                    throw new ApplicationException("Could not find job " + extractionJobIdentifier + " in the job store");

                var quarantineInfo = new QuarantinedMongoExtractJob(toQuarantine, cause);

                try
                {
                    _jobQuarantineCollection.InsertOne(quarantineInfo);
                }
                catch (MongoDuplicateKeyException e)
                {
                    throw new ApplicationException("Extract job " + extractionJobIdentifier + "was already present in the quarantine", e);
                }

                DeleteResult a = _jobInfoCollection.DeleteOne(GetFilterForSpecificJob<MongoExtractJob>(extractionJobIdentifier));

                if (!(a.IsAcknowledged || a.DeletedCount != 1))
                    throw new Exception("Job data was quarantined but could not delete original from job store");
            }

            Logger.Debug("Job " + extractionJobIdentifier + " quarantined");
        }

        #region Helper Methods

        private static FilterDefinition<T> GetFilterForSpecificJob<T>(Guid extractionJobIdentifier) where T : MongoExtractJob
        {
            return Builders<T>.Filter.Eq(x => x.ExtractionJobIdentifier, extractionJobIdentifier);
        }

        private bool InJobCollection(Guid extractionJobIdentifier, out MongoExtractJob mongoExtractJob)
        {
            mongoExtractJob = _jobInfoCollection.Find(GetFilterForSpecificJob<MongoExtractJob>(extractionJobIdentifier))
                .SingleOrDefault();

            return mongoExtractJob != null;
        }

        private bool InArchiveCollection(Guid extractionJobIdentifier, out MongoExtractJob mongoExtractJob)
        {
            mongoExtractJob = _jobArchiveCollection
                .Find(GetFilterForSpecificJob<ArchivedMongoExtractJob>(extractionJobIdentifier)).SingleOrDefault();

            return mongoExtractJob != null;
        }

        private bool InQuarantineCollection(Guid extractionJobIdentifier, out MongoExtractJob mongoExtractJob)
        {
            mongoExtractJob = _jobQuarantineCollection
                .Find(GetFilterForSpecificJob<QuarantinedMongoExtractJob>(extractionJobIdentifier)).SingleOrDefault();

            return mongoExtractJob != null;
        }

        private async Task<List<MongoExtractJob>> GetExtractJobs(FilterDefinition<MongoExtractJob> filter)
        {
            var toReturn = new List<MongoExtractJob>();

            using (IAsyncCursor<MongoExtractJob> cursor = await _jobInfoCollection.FindAsync(filter, _findOptions))
            {
                while (await cursor.MoveNextAsync())
                    toReturn.AddRange(cursor.Current);
            }

            return toReturn;
        }

        #endregion
    }
}
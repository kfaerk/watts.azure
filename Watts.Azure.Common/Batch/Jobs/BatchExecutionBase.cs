namespace Watts.Azure.Common.Batch.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Interfaces.Batch;
    using Interfaces.General;
    using Interfaces.Wrappers;
    using Microsoft.Azure.Batch;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Objects;
    using Storage.Objects;

    /// <summary>
    /// Base class for batch execution.
    /// </summary>
    public class BatchExecutionBase
    {
        private readonly IBatchAccount account;
        private readonly IPrepareInputFiles prepareInputFiles;
        private readonly ICloudAccountFactory cloudAccountFactory;
        private readonly ILog log;

        private IStorageAccount storageAccount;
        private IAzureBlobClient blobClient;

        private readonly List<TaskOutput> taskOutputs = new List<TaskOutput>();

        private TaskOutputHelper taskOutputHelper;

        /// <summary>
        /// Creates a new instance of BatchExecutionBase.
        /// </summary>
        /// <param name="account">The batch account to run the execution under</param>
        /// <param name="settings">The settings, i.e. the size of nodes, number of nodes, storage accounts, etc.</param>
        /// <param name="prepareInputFiles">Object that can prepare input files for processing.</param>
        /// <param name="dependencyResolver">Dependency resolver to use when finding dependencies of the batch task assembly</param>
        /// <param name="cloudAccountFactory">Provides cloud accounts in a mockable way</param>
        /// <param name="log">A log to place debug and error message in (can be null)</param>
        public BatchExecutionBase(IBatchAccount account, IBatchExecutionSettings settings, IPrepareInputFiles prepareInputFiles, IBatchDependencyResolver dependencyResolver, ICloudAccountFactory cloudAccountFactory, ILog log)
            : this(account, settings, prepareInputFiles, new List<IBatchDependencyResolver>() { dependencyResolver }, cloudAccountFactory, log)
        {
        }

        /// <summary>
        /// Creates a new instance of BatchExecutionBase.
        /// </summary>
        /// <param name="account">The batch account to run the execution under</param>
        /// <param name="settings">The settings, i.e. the size of nodes, number of nodes, storage accounts, etc.</param>
        /// <param name="prepareInputFiles">Object that can prepare input files for processing.</param>
        /// <param name="dependencyResolvers">Dependency resolvers to use when finding dependencies of the batch task assembly</param>
        /// <param name="cloudAccountFactory">Provides cloud accounts in a mockable way</param>
        /// <param name="log">A log to place debug and error message in (can be null)</param>
        public BatchExecutionBase(IBatchAccount account, IBatchExecutionSettings settings, IPrepareInputFiles prepareInputFiles, List<IBatchDependencyResolver> dependencyResolvers, ICloudAccountFactory cloudAccountFactory, ILog log)
        {
            this.account = account;
            this.Settings = settings;
            this.prepareInputFiles = prepareInputFiles;
            this.DependencyResolvers = dependencyResolvers;
            this.cloudAccountFactory = cloudAccountFactory;
            this.log = log;
            this.taskOutputHelper = new TaskOutputHelper(this.account);

            this.Initialize();
        }

        /// <summary>
        /// Delegate for preparing input files for a batch processing job.
        /// </summary>
        /// <param name="storageAccount"></param>
        /// <param name="blobClient"></param>
        /// <returns>A list of strings</returns>
        public delegate Task<List<string>> PrepareInputFilesDelegate(CloudStorageAccount storageAccount, CloudBlobClient blobClient);

        /// <summary>
        /// Execution settings containing everything related to the batch
        /// </summary>
        public IBatchExecutionSettings Settings { get; set; }

        /// <summary>
        /// The dependency resolvers which will be invoked to find the files to upload.
        /// </summary>
        public List<IBatchDependencyResolver> DependencyResolvers { get; set; }

        /// <summary>
        /// The batch account
        /// </summary>
        public IBatchAccount Account => this.account;

        /// <summary>
        /// Create the storage account and the blob client to use when uploading application and input files.
        /// </summary>
        public void Initialize()
        {
            // Retrieve the storage account
            this.storageAccount = this.cloudAccountFactory.GetStorageAccount(this.Settings.StorageConnectionString);

            // Create the blob client, for use in obtaining references to blob storage containers
            this.blobClient = this.storageAccount.CreateCloudBlobClient();
        }

        /// <summary>
        /// Execute the batch by creating the pool and job in batch, uploading all needed files, starting the job, monitoring until done.
        /// </summary>
        /// <returns>Execution task</returns>
        public async Task StartBatch()
        {
            var poolExists = await this.JobAlreadyExistsAsync();

            if (!poolExists)
            {
                await this.PrepareExecutionContainerAsync();
                var applicationFiles = await this.UploadDependencies();

                var inputFilePaths = await this.prepareInputFiles.PrepareFiles();
                var inputFiles = await this.UploadInputFiles(inputFilePaths);

                try
                {
                    await this.ExecuteJob(applicationFiles, inputFiles);
                }
                catch (Exception ex)
                {
                    // TODO Should we throw this??
                    this.LogError(
                        $"An exception occurred in the task execution loop in BatchExecutionBase. Exception message: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("Pool already exists, will monitor...");
                await this.MonitorJobUntilCompletionAsync();
                await this.CleanUpIfRequired();
            }
        }

        public List<TaskOutput> GetExecutionOutput()
        {
            return this.taskOutputs;
        }

        /// <summary>
        /// Delete the job specified in settings.batchpoolsetup.jobid using the given batchclient.
        /// </summary>
        /// <param name="batchClient"></param>
        /// <returns></returns>
        public async Task DeleteJobAsync(IAzureBatchClient batchClient)
        {
            try
            {
                await batchClient.JobOperations.DeleteJobAsync(this.Settings.BatchPoolSetup.JobId);
            }
            catch (Exception ex)
            {
                this.LogError($"Unable to delete job after batch run. Exception: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Delete the pool specified in settings.batchpoolsetup.PoolId
        /// </summary>
        /// <param name="batchClient"></param>
        /// <returns></returns>
        public async Task DeletePoolAsync(IAzureBatchClient batchClient)
        {
            try
            {
                await batchClient.PoolOperations.DeletePoolAsync(this.Settings.BatchPoolSetup.PoolId);
            }
            catch (Exception ex)
            {
                this.LogError($"Unable to delete pool after batch run. Exception: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Prepare the and input containers if they do not already exist.
        /// </summary>
        /// <returns></returns>
        public async Task PrepareExecutionContainerAsync()
        {
            if (!this.Settings.IsValid)
            {
                throw new InvalidOperationException(
                    "One ore more account credential strings have not been populated. Please ensure that your Batch and Storage account credentials have been specified.");
            }

            // Create containers and upload dependencies
            await this.account.CreateContainerIfNotExistAsync(this.blobClient, this.Settings.ExecutableInfos.First().BatchExecutableContainerName);
            await this.account.CreateContainerIfNotExistAsync(this.blobClient, this.Settings.ExecutableInfos.First().BatchInputContainerName);
        }

        /// <summary>
        /// Dispose this BatchExecution.
        /// </summary>
        public virtual void Dispose()
        {
            this.account.Dispose();
        }

        /// <summary>
        /// Execute the job, i.e. create the pool, the job and add tasks, wait for them to complete and finally tear down the
        /// created resources if the settings specify CleanUpAfterwards.
        /// </summary>
        /// <param name="applicationFiles"></param>
        /// <param name="inputFiles"></param>
        /// <returns></returns>
        internal async Task ExecuteJob(List<ResourceFile> applicationFiles, List<ResourceFile> inputFiles)
        {
            // Create the pool that will contain the compute nodes that will execute the tasks.
            // The ResourceFile collection that we pass in is used for configuring the pool's StartTask
            // which is executed each time a node first joins the pool (or is rebooted or reimaged).
            try
            {
                DateTime startTime = DateTime.Now;

                var pool = await
                    this.account.CreatePoolAsync(this.Settings.BatchPoolSetup.PoolId, applicationFiles,
                        this.Settings.Applications);

                // Create the job that will run the tasks.
                var job = await
                    this.account.CreateJobAsync(this.Settings.BatchPoolSetup.JobId,
                        this.Settings.BatchPoolSetup.PoolId);

                // Add the tasks to the job. We need to supply a container shared access signature for the
                // tasks so that they can upload their output to Azure Storage.
                var tasks = await
                    this.account.AddTasksAsync(this.Settings.BatchPoolSetup.JobId, inputFiles,
                        this.Settings.Applications);

                // Monitor task success/failure
                await this.MonitorJobUntilCompletionAsync();

                // The batch is finished. Record the end time and possibly statistics (depending on configuration)
                var endTime = DateTime.Now;
                TimeSpan duration = endTime - startTime;

                Console.WriteLine("The batch completed in {0}", duration);

                if (this.Settings.SaveStatistics)
                {
                    await
                        this.SaveStatistics(new DateTimeOffset(startTime), new DateTimeOffset(endTime),
                            inputFiles.Count);
                }

                // If there is an outputcontainer specified, download the output files.
                if (this.Settings.ShouldDownloadOutput)
                {
                    await this.DownloadOutputAsync(job, tasks);
                }
            }
            catch (Exception ex)
            {
                this.LogError("Exception : " + ex.Message);
                this.Report($"Exception: {ex}");
                throw;
            }
            finally
            {
                await this.CleanUpIfRequired();
            }
        }

        /// <summary>
        /// Download task output locally and return a list of objects that contain the output (stdout and stderr) of the tasks.
        /// </summary>
        /// <param name="pool"></param>
        /// <param name="job"></param>
        /// <param name="tasks"></param>
        internal async Task DownloadOutputAsync(CloudJob job, List<CloudTask> tasks)
        {
            for (int i = 0; i < tasks.Count; i++)
            {
                var taskOutput = await this.TryGetTaskOutput(job, tasks[i]);

                this.taskOutputs.Add(taskOutput);
            }
        }

        internal async Task<TaskOutput> TryGetTaskOutput(CloudJob job, CloudTask task, bool swallowExceptions = false)
        {
            TaskOutput taskOutput = TaskOutput.Empty;

            try
            {
                string[] stdOutLines = await this.taskOutputHelper.GetStdOut(job, task);
                string[] stdErrLines = await this.taskOutputHelper.GetStdErr(job, task);

                taskOutput = new TaskOutput()
                {
                    Name = task.DisplayName,
                    StdOut = stdOutLines.ToList(),
                    StdErr = stdErrLines.ToList()
                };

                return taskOutput;
            }
            catch(Exception) 
            {
                if (!swallowExceptions)
                {
                    throw;
                }
            }

            return taskOutput;
        }

        /// <summary>
        /// If the settings specify that we should clean up after the execution, delete containers, the pool and the job.
        /// </summary>
        /// <returns></returns>
        internal async Task CleanUpIfRequired()
        {
            if (this.Settings.CleanupAfterwards)
            {
                this.Report("Cleaning up after batch run...");
                this.Report($"Deleting container {this.Settings.ExecutableInfos.First().BatchExecutableContainerName}");

                // Clean up Storage resources
                try
                {
                    await
                        this.account.DeleteContainerAsync(this.blobClient, this.Settings.ExecutableInfos.First().BatchExecutableContainerName);
                }
                catch (Exception ex)
                {
                    this.Report($"Exception {ex}");
                    throw;
                }

                this.Report(
                    $"Deleting container {this.Settings.ExecutableInfos.First().BatchInputContainerName}");

                try
                {
                    await this.account.DeleteContainerAsync(this.blobClient, this.Settings.ExecutableInfos.First().BatchInputContainerName);
                }
                catch (Exception ex)
                {
                    this.Report($"Exception when deleting container {this.Settings.ExecutableInfos.First().BatchInputContainerName} {ex}");
                    throw;
                }

                this.Report("Deleting Job...");
                await this.DeleteJobAsync(this.account.BatchClient);

                this.Report("Deleting pool");
                await this.DeletePoolAsync(this.account.BatchClient);
            }
        }

        /// <summary>
        /// Get a bool indicating whether the job already exists.
        /// </summary>
        /// <returns></returns>
        internal async Task<bool> JobAlreadyExistsAsync()
        {
            return await Task.Run(async () => this.account.BatchClient.JobOperations.ListJobs()
                .Any(p => p.Id == this.Settings.BatchPoolSetup.JobId));
        }

        internal async Task MonitorJobUntilCompletionAsync()
        {
            // Monitor task success/failure, specifying a maximum amount of time to wait for the tasks to complete
            var monitor = new AzureBatchTaskMonitor(
                                this.account.BatchClient,
                                this.Settings.BatchPoolSetup.JobId,
                                this.account.ProgressDelegate,
                                this.Settings.ReportStatusFormat);

            await Task.Run(async () => monitor.StartMonitoring());

            // Wait for the job to finish or the timeout period to have passed.
            monitor.FinishedMutex.WaitOne(this.Settings.TimeoutInMinutes * 60 * 1000);
        }

        internal void Report(string progress)
        {
            this.Account.ProgressDelegate?.Invoke(progress);
        }

        internal async Task SaveStatistics(DateTimeOffset startTime, DateTimeOffset endTime, int numberOfTasks)
        {
            try
            {
                BatchRunStatisticsTableStorage statisticsStorage =
                    new BatchRunStatisticsTableStorage(this.Settings.StorageConnectionString);

                BatchStatisticsEntity statisticsEntity = new BatchStatisticsEntity(
                                        this.Settings.BatchPoolSetup.PoolId,
                                        this.Settings.BatchPoolSetup.JobId,
                                        startTime,
                                        endTime,
                                        this.Settings.MachineConfig.NumberOfNodes,
                                        numberOfTasks,
                                        this.Settings.MachineConfig.Size,
                                        metadata: string.Empty,
                                        type: "BatchStatistics");

                await statisticsStorage.SaveStatisticsAsync(statisticsEntity);
            }
            catch (Exception ex)
            {
                this.LogError($"Could not save statistics, ex: {ex}");
            }
        }

        /// <summary>
        /// Upload the executable and its dependencies to the 'application' blob container.
        /// Dependencies are resolved using the IDependencyResolver.
        /// </summary>
        /// <returns></returns>
        internal async Task<List<ResourceFile>> UploadDependencies()
        {
            var applicationFilePaths = new List<string>();

            // Invoke dependency resolvers to get the files to upload.
            this.DependencyResolvers.ForEach(
                d => applicationFilePaths.AddRange(d.Resolve()));

            applicationFilePaths =
                applicationFilePaths.GroupBy(p => p.Split(new string[] { "\\" }, StringSplitOptions.None).Last())
                    .Select(q => q.First())
                    .ToList();

            var retVal = await this.account.UploadFilesToContainerAsync(this.blobClient, this.Settings.ExecutableInfos.First().BatchExecutableContainerName, applicationFilePaths);

            return retVal;
        }

        /// <summary>
        /// Upload all input files (at the specified local paths) to the 'input' blob container.
        /// </summary>
        /// <param name="filePaths"></param>
        /// <returns></returns>
        internal async Task<List<ResourceFile>> UploadInputFiles(List<string> filePaths)
        {
            var retVal = await this.account.UploadFilesToContainerAsync(this.blobClient, this.Settings.ExecutableInfos.First().BatchInputContainerName, filePaths);

            return retVal;
        }

        /// <summary>
        /// Log an error to the current log if one is specified.
        /// </summary>
        /// <param name="statement"></param>
        internal void LogError(string statement)
        {
            if (this.log != null)
            {
                try
                {
                    this.log.Error(statement);
                }
                catch (Exception ex)
                {
                    Console.Error.Write($"Could not log statement, {ex.Message}");
                }
            }
        }
    }
}
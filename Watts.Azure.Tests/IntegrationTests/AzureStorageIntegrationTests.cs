namespace Watts.Azure.Tests.IntegrationTests
{
    using System;
    using System.IO;
    using System.Reflection;
    using Azure.Utils.Interfaces.Batch;
    using Common.Storage.Objects;
    using FluentAssertions;
    using NUnit.Framework;
    using Objects;

    /// <summary>
    /// Tests related to storing and retrieving data in Azure.
    /// </summary>
    [TestFixture]
    public class AzureStorageIntegrationTests
    {
        private IBatchEnvironment environment;
        private AzureBlobStorage blobStorageUnderTest;

        private TestEnvironmentConfig config;

        [SetUp]
        public void Setup()
        {
            this.config = new TestEnvironmentConfigHandler(Constants.CredentialsFilePath).GetTestEnvironment();

            this.environment = this.config.BatchEnvironment;

            this.blobStorageUnderTest = AzureBlobStorage.Connect(this.environment.GetBatchStorageConnectionString(), "testcontainer");
        }

        /// <summary>
        /// Tests that it is possible to store a blob (a text file) to the storage account being tested.
        /// </summary>
        [Test]
        [Category("IntegrationTest"), Category("Azure Blob Storage")]
        public void StoreBlob()
        {
            // ARRANGE
            string testDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string blobName = "myBlob.txt";
            string tempFile = "TempFile.txt";
            string tempFilePath = Path.Combine(testDirectory, tempFile);

            string[] fileLines = new[] { "line1", "line2" };

            File.WriteAllLines(tempFilePath, fileLines);

            // ACT
            this.blobStorageUnderTest.UploadFromFile(tempFilePath, blobName);

            var downloadedBlob = this.blobStorageUnderTest.GetBlobContents(blobName);
            var splitContent = downloadedBlob.Split(new string[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);

            // ASSERT
            splitContent.Length.Should().Be(2, "because we wrote two lines to the file that we uploaded to blob storage");
            fileLines[0].Should().Be(splitContent[0], "because this should match the first string we wrote to the file we uploaded to blob storage");
            fileLines[1].Should().Be(splitContent[1], "because this should match the second string we wrote to the file we uploaded to blob storage");

            // CLEAN UP
            this.blobStorageUnderTest.DeleteContainerIfExists();
        }

        /// <summary>
        /// Tests that it is possible to upload a file to a file share in Azure.
        /// </summary>
        [Category("IntegrationTest"), Category("Azure File Storage")]
        [Test]
        public void TestUploadFile()
        {
            // ARRANGE
            string testDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string filename = "TestUploadFile.txt";
            string filePath = Path.Combine(testDirectory, filename);
            File.WriteAllText(filePath, "This is an automated test");
            string sharename = "integration-test-share";

            // ACT
            AzureFileshare share = AzureFileshare.Connect(this.config.FileshareConnectionString, sharename);
            share.CreateIfDoesntExist(this.config.TestFileShareAccount.Credentials.AccountName, this.config.FileshareAccountKey);
            share.SaveDataToFile(filePath);

            // ASSERT
            string localFileCopyName = Path.Combine(testDirectory, "TestUploadFile_Downloaded.txt");
            if (File.Exists(localFileCopyName)) { File.Delete(localFileCopyName); }

            share.DownloadFile(filename, localFileCopyName);

            File.ReadAllText(filePath).Should().Be(File.ReadAllText(localFileCopyName), "because the contents of the file that we uploaded and then downloaded, should be equal");

            // Delete the local files
            File.Delete(filePath);
            File.Delete(localFileCopyName);
        }
    }
}
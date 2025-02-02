namespace Grace.SDK

open Azure.Core.Pipeline
open Azure.Storage
open Azure.Storage.Blobs
open Azure.Storage.Blobs.Models
open Azure.Storage.Blobs.Specialized
open FSharpPlus
open Grace.SDK.Common
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Services
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors.Storage
open NodaTime.Text
open System
open System.Buffers
open System.Collections.Generic
open System.Globalization
open System.IO
open System.IO.Compression
open System.IO.Enumeration
open System.Linq
open System.Net
open System.Net.Http.Json
open System.Threading.Tasks
open System.Text

module Storage =

    let GetFileFromObjectStorage (fileVersion: FileVersion) correlationId =
        task {
            try
                match Current().ObjectStorageProvider with
                | AzureBlobStorage ->
                    let httpClient = getHttpClient correlationId
                    let serviceUrl = $"{Current().ServerUri}/storage/getDownloadUri"
                    let jsonContent = jsonContent fileVersion
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                    //logToConsole $"response.StatusCode: {response.StatusCode}; blobUriWithSasToken: {blobUriWithSasToken}"

                    let blobClient = BlobClient(Uri(blobUriWithSasToken))
                    let relativeDirectory = if fileVersion.RelativeDirectory = Constants.RootDirectoryPath then String.Empty else getNativeFilePath fileVersion.RelativeDirectory
                    let tempFilePath = Path.Combine(Environment.GetEnvironmentVariable("temp"), relativeDirectory, fileVersion.GetObjectFileName)
                    let objectFilePath = Path.Combine(Current().ObjectDirectory, fileVersion.RelativePath, fileVersion.GetObjectFileName)
                    let tempFileInfo = FileInfo(tempFilePath)
                    let objectFileInfo = FileInfo(objectFilePath)
                    Directory.CreateDirectory(tempFileInfo.Directory.FullName) |> ignore
                    Directory.CreateDirectory(objectFileInfo.Directory.FullName) |> ignore
                    //logToConsole $"tempFilePath: {tempFilePath}; objectFilePath: {objectFilePath}"
                    let! azureResponse = blobClient.DownloadToAsync(tempFilePath)
                    if not <| azureResponse.IsError then
                        File.Move(tempFilePath, objectFilePath, overwrite = true)
                        //if fileVersion.IsBinary then
                        //    File.Move(tempFilePath, objectFilePath, overwrite = true)
                        //else
                        //    use tempFileStream = tempFileInfo.OpenRead()
                        //    tempFileStream.Position <- 0
                        //    use gzStream = new GZipStream(tempFileStream, CompressionMode.Decompress, leaveOpen = false)
                        //    use fileWriter = objectFileInfo.OpenWrite()

                        //    do! gzStream.CopyToAsync(fileWriter)
                        //    logToConsole $"In GetFileFromObjectStorage: After CopyToAsync(). {fileVersion.RelativePath}"
                            
                        //    do! fileWriter.FlushAsync()
                        //    logToConsole $"In GetFileFromObjectStorage: After FlushAsync(). {fileVersion.RelativePath}"

                        //    logToConsole $"After tempFileInfo.Delete(). {fileVersion.RelativePath}"
                        tempFileInfo.Delete()
                        return Ok (GraceReturnValue.Create "Retrieved all files from object storage." correlationId)
                    else 
                        tempFileInfo.Delete()
                        return Error (GraceError.Create (StorageError.getErrorMessage FailedCommunicatingWithObjectStorage) correlationId)
                | AWSS3 -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | GoogleCloudStorage -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | ObjectStorageProvider.Unknown -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
            with ex ->
                logToConsole $"Exception handling {fileVersion.RelativePath}: {createExceptionResponse ex}"
                return Error (GraceError.Create (StorageError.getErrorMessage ObjectStorageException) correlationId)
        }

    let FilesExistInObjectStorage (fileVersions: List<FileVersion>) correlationId =
        task {
            try
                match Current().ObjectStorageProvider with
                | AzureBlobStorage ->
                    let httpClient = getHttpClient correlationId
                    let serviceUrl = $"{Current().ServerUri}/storage/filesExistInObjectStorage"
                    let jsonContent = jsonContent fileVersions
                    let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                    if response.IsSuccessStatusCode then
                        let! returnValue = response.Content.ReadFromJsonAsync<GraceReturnValue<List<UploadMetadata>>>(Constants.JsonSerializerOptions)
                        return Ok returnValue
                    else
                        let graceError = (GraceError.Create (StorageError.getErrorMessage FailedToGetUploadUrls) correlationId)
                        let fileVersionList = StringBuilder()
                        for fileVersion in fileVersions do fileVersionList.Append($"{fileVersion.RelativePath}; ") |> ignore
                        return Error graceError |> enhance ("fileVersions", fileVersionList.ToString())
                | AWSS3 -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | GoogleCloudStorage -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                | ObjectStorageProvider.Unknown -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
            with ex ->
                let exceptionResponse = createExceptionResponse ex
                return Error (GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let SaveFileToObjectStorageWithMetadata (fileVersion: FileVersion) (blobUriWithSasToken: Uri) (metadata: Dictionary<string, string>) correlationId = 
        task {
            try
                //logToConsole $"In SDK.Storage.SaveFileToObjectStorageWithMetadata: fileVersion.RelativePath: {fileVersion.RelativePath}."
                let fileInfo = FileInfo(Path.Combine(Current().RootDirectory, fileVersion.RelativePath))
                metadata.TryAdd("CorrelationId", correlationId) |> ignore
                metadata.TryAdd("OwnerId", $"{Current().OwnerId}") |> ignore
                metadata.TryAdd("OrganizationId", $"{Current().OrganizationId}") |> ignore
                metadata.TryAdd("RepositoryName", $"{Current().RepositoryName}") |> ignore
                metadata.TryAdd("RepositoryId", $"{Current().RepositoryId}") |> ignore
                metadata.TryAdd("Sha256Hash", fileVersion.Sha256Hash) |> ignore
                metadata.TryAdd("OriginalSize", $"{fileInfo.Length}") |> ignore

                match Current().ObjectStorageProvider with
                    | ObjectStorageProvider.Unknown -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | ObjectStorageProvider.AzureBlobStorage ->
                        try
                            use transport = new HttpClientTransport(getHttpClient correlationId)
                            let blobClientOptions = BlobClientOptions(Transport = transport)
                            blobClientOptions.Retry.NetworkTimeout <- TimeSpan.FromMinutes(60.0)
                            let blockBlobClient = BlockBlobClient(blobUriWithSasToken, blobClientOptions)
                            //logToConsole $"In Storage.SDK.SaveFileToObjectStorageWithMetadata; Uri: {blobUriWithSasToken}"
                            let! blobAlreadyExists = blockBlobClient.ExistsAsync()
                            if not <| (blobAlreadyExists.Value) then
                                let storageTransferOptions = StorageTransferOptions(MaximumConcurrency = Environment.ProcessorCount * 4)
                                let blobUploadOptions = BlobUploadOptions(Metadata = metadata, Tags = metadata, TransferOptions = storageTransferOptions)
                                blobUploadOptions.HttpHeaders <- BlobHttpHeaders(
                                    ContentType = getContentType fileInfo (fileVersion.IsBinary), 
                                    CacheControl = Constants.BlobCacheControl,
                                    ContentDisposition = $"""attachment; creation-date="{fileVersion.CreatedAt.ToString(InstantPattern.General.PatternText, CultureInfo.InvariantCulture)}" """)

                                let objectFilePath = $"{Current().ObjectDirectory}{Path.DirectorySeparatorChar}{fileVersion.RelativePath}{Path.DirectorySeparatorChar}{fileVersion.GetObjectFileName}"
                                let normalizedObjectFilePath = Path.GetFullPath(objectFilePath)

                                use fileStream = File.Open(normalizedObjectFilePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                                let! blobContentInfo =
                                    task {
                                        if fileVersion.IsBinary then
                                            // If the file is a binary file, stream it to Blob Storage without compressing it.
                                            return! blockBlobClient.UploadAsync(fileStream, blobUploadOptions)
                                        else
                                            // If the file is not a binary file, gzip it, and stream the compressed file to Blob Storage.
                                            //blobUploadOptions.HttpHeaders.ContentEncoding <- "gzip"
                                            //use memoryStream = new MemoryStream(64 * 1024)  // Setting initial capacity larger than most files will need.
                                            //use gzipStream = new GZipStream(memoryStream, CompressionLevel.SmallestSize, leaveOpen = false)
                                            //do! fileStream.CopyToAsync(gzipStream, bufferSize = (64 * 1024))
                                            //do! gzipStream.FlushAsync()
                                            //memoryStream.Position <- 0
                                            //return! blockBlobClient.UploadAsync(memoryStream, blobUploadOptions)
                                            return! blockBlobClient.UploadAsync(fileStream, blobUploadOptions)
                                        }

                                if blobContentInfo.GetRawResponse().Status = 201 then
                                    let returnValue = GraceReturnValue.Create "File successfully saved to object storage." correlationId
                                    returnValue.Properties.Add(nameof(Sha256Hash), $"{fileVersion.Sha256Hash}")
                                    returnValue.Properties.Add(nameof(RelativePath), $"{fileVersion.RelativePath}")
                                    returnValue.Properties.Add(nameof(RepositoryId), $"{fileVersion.RepositoryId}")
                                    return Ok returnValue
                                else
                                    let error = (GraceError.Create $"Failed to upload file {normalizedObjectFilePath} to object storage." correlationId)
                                    return Error error
                            else
                                let returnValue = GraceReturnValue.Create "File already uploaded to object storage." correlationId
                                returnValue.Properties.Add(nameof(Sha256Hash), $"{fileVersion.Sha256Hash}")
                                returnValue.Properties.Add(nameof(RelativePath), $"{fileVersion.RelativePath}")
                                returnValue.Properties.Add(nameof(RepositoryId), $"{fileVersion.RepositoryId}")
                                return Ok returnValue
                        with ex ->
                            let exceptionResponse = createExceptionResponse ex
                            return Error (GraceError.Create (exceptionResponse.ToString()) correlationId)
                    | ObjectStorageProvider.AWSS3 -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | ObjectStorageProvider.GoogleCloudStorage -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
            with ex ->
                let exceptionResponse = createExceptionResponse ex
                return Error (GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let SaveFileToObjectStorage (fileVersion: FileVersion) (blobUriWithSasToken: Uri) correlationId =
        SaveFileToObjectStorageWithMetadata fileVersion blobUriWithSasToken (Dictionary<string, string>()) correlationId

    let GetUploadUri (fileVersion: FileVersion) correlationId =
        task {
            try
                match Current().ObjectStorageProvider with
                    | ObjectStorageProvider.Unknown -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | ObjectStorageProvider.AzureBlobStorage ->
                        let httpClient = getHttpClient correlationId
                        let serviceUrl = $"{Current().ServerUri}/storage/getUploadUri"
                        let jsonContent = jsonContent fileVersion
                        let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                        let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                        //logToConsole $"blobUriWithSasToken: {blobUriWithSasToken}"
                        return Ok (GraceReturnValue.Create blobUriWithSasToken correlationId)
                    | ObjectStorageProvider.AWSS3 -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | ObjectStorageProvider.GoogleCloudStorage -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
            with ex ->
                let exceptionResponse = createExceptionResponse ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error (GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

    let GetDownloadUri (fileVersion: FileVersion) correlationId =
        task {
            try
                match Current().ObjectStorageProvider with
                    | ObjectStorageProvider.Unknown -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | ObjectStorageProvider.AzureBlobStorage ->
                        let httpClient = getHttpClient correlationId
                        let serviceUrl = $"{Current().ServerUri}/storage/getDownloadUri"
                        let jsonContent = jsonContent fileVersion
                        let! response = httpClient.PostAsync(serviceUrl, jsonContent)
                        let! blobUriWithSasToken = response.Content.ReadAsStringAsync()
                        //logToConsole $"blobUriWithSasToken: {blobUriWithSasToken}"
                        return Ok (GraceReturnValue.Create blobUriWithSasToken correlationId)
                    | ObjectStorageProvider.AWSS3 -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
                    | ObjectStorageProvider.GoogleCloudStorage -> return Error (GraceError.Create (StorageError.getErrorMessage NotImplemented) correlationId)
            with ex ->
                let exceptionResponse = createExceptionResponse ex
                logToConsole $"exception: {exceptionResponse.ToString()}"
                return Error (GraceError.Create (exceptionResponse.ToString()) correlationId)
        }

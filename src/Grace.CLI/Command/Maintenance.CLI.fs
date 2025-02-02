﻿namespace Grace.Cli.Command

open Grace.Cli.Common
open Grace.Cli.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation.Errors
open Spectre.Console
open System
open System.Collections.Concurrent
open System.CommandLine
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Linq
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Collections.Generic
open Grace.Shared.Parameters.Directory

module Maintenance =

    type CommonParameters() = 
        inherit ParameterBase()

    let private UpdateIndex = 
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                if parseResult |> verbose then printParseResult parseResult

                if parseResult |> showOutput then
                    let! graceStatus = progress.Columns(progressColumns).StartAsync(fun progressContext ->
                        task {
                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading existing Grace index file.[/]")
                            let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Computing new Grace index file.[/]", autoStart = false)
                            let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Writing new Grace index file.[/]", autoStart = false)
                            let t3 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure files are in the object cache.[/]", autoStart = false)
                            let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure object cache index is up-to-date.[/]", autoStart = false)
                            let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure files are uploaded to object storage.[/]", autoStart = false)
                            let t6 = progressContext.AddTask($"[{Color.DodgerBlue1}]Ensure directory versions are uploaded to Grace Server.[/]", autoStart = false)
                            t0.Increment(0.0)
                            let! previousGraceStatus = readGraceStatusFile()
                            t0.Increment(100.0)

                            t1.StartTask()
                            t1.Increment(0.0)
                            let! graceStatus = createGraceStatusFile previousGraceStatus
                            t1.Value <- 100.0

                            t2.StartTask()
                            do! writeGraceStatusFile graceStatus
                            t2.Value <- 100.0

                            t3.StartTask()
                            let fileVersions = ConcurrentDictionary<RelativePath, LocalFileVersion>()
                            let plr = Parallel.ForEach(graceStatus.Index.Values, Constants.ParallelOptions, (fun ldv ->
                                for fileVersion in ldv.Files do
                                    fileVersions.TryAdd(fileVersion.RelativePath, fileVersion) |> ignore
                            ))
                            let incrementAmount = 100.0 / double fileVersions.Count

                            let plr = Parallel.ForEach(fileVersions, Constants.ParallelOptions, (fun kvp _ ->
                                let fileVersion = kvp.Value
                                let fullObjectPath = fileVersion.FullObjectPath
                                if not <| File.Exists(fullObjectPath) then
                                    Directory.CreateDirectory(Path.GetDirectoryName(fullObjectPath)) |> ignore
                                    File.Copy(Path.Combine(Current().RootDirectory, fileVersion.RelativePath), fullObjectPath)
                                t3.Increment(incrementAmount)))
                            t3.Value <- 100.0

                            t4.StartTask()
                            let! objectCache = readGraceObjectCacheFile()
                            let incrementAmount = 100.0 / double graceStatus.Index.Count
                            let plr = Parallel.ForEach(graceStatus.Index.Values, Constants.ParallelOptions, (fun ldv ->
                                if not <| objectCache.Index.ContainsKey(ldv.DirectoryId) then
                                    objectCache.Index.AddOrUpdate(ldv.DirectoryId, (fun _ -> ldv), (fun _ _ -> ldv)) |> ignore
                                    t4.Increment(incrementAmount)
                            ))
                            do! writeGraceObjectCacheFile objectCache
                            t4.Value <- 100.0

                            t5.StartTask()
                            let incrementAmount = 100.0 / double fileVersions.Count
                            match Current().ObjectStorageProvider with
                            | ObjectStorageProvider.Unknown -> ()
                            | AzureBlobStorage -> 
                                // Breaking the uploads into chunks allows us to interleave checking to see if files are already uploaded with actually uploading them when they don't.
                                let chunkSize = 32
                                let fileVersionGroups = fileVersions.Chunk(chunkSize)
                                let succeeded = ConcurrentQueue<GraceReturnValue<string>>()
                                let errors = ConcurrentQueue<GraceError>()
                                
                                do! Parallel.ForEachAsync(fileVersionGroups, Constants.ParallelOptions, (fun fileVersions ct ->
                                    ValueTask(task {
                                        let! graceResult = Storage.FilesExistInObjectStorage (fileVersions.Select(fun f -> f.Value.ToFileVersion).ToList()) (getCorrelationId parseResult)
                                        match graceResult with
                                        | Ok graceReturnValue ->
                                            logToConsole $"In Ok"
                                            let uploadMetadata = graceReturnValue.ReturnValue
                                            // First, increment the counter for the files that we don't have to upload.
                                            t5.Increment(incrementAmount * double (fileVersions.Count() - uploadMetadata.Count))
                                            let filesIndexedBySha256Hash = Dictionary<Sha256Hash, LocalFileVersion>(fileVersions.Select(fun kvp -> KeyValuePair(kvp.Value.Sha256Hash, kvp.Value)))

                                            do! Parallel.ForEachAsync(uploadMetadata, Constants.ParallelOptions, (fun upload ct ->
                                                ValueTask(task {
                                                    let fileVersion = filesIndexedBySha256Hash[upload.Sha256Hash].ToFileVersion
                                                    let! result = Storage.SaveFileToObjectStorage fileVersion (upload.BlobUriWithSasToken) (getCorrelationId parseResult)
                                                    // Increment the counter for each file that we do upload.
                                                    t5.Increment(incrementAmount)
                                                    match result with
                                                    | Ok result -> succeeded.Enqueue(result)
                                                    | Error error -> errors.Enqueue(error)
                                                })))

                                        | Error error ->
                                            AnsiConsole.Write((new Panel($"{error}"))
                                                                  .BorderColor(Color.Red3))
                                    })))

                                if errors.Count = 0 then
                                    ()
                                else
                                    AnsiConsole.MarkupLine($"{errors.Count} errors occurred.")
                                    let mutable error = GraceError.Create String.Empty String.Empty
                                    while not <| errors.IsEmpty do
                                        if errors.TryDequeue(&error) then AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
                            | AWSS3 -> ()
                            | GoogleCloudStorage -> ()
                            t5.Value <- 100.0

                            t6.StartTask()
                            let chunkSize = 16
                            let succeeded = ConcurrentQueue<GraceReturnValue<string>>()
                            let errors = ConcurrentQueue<GraceError>()
                            let incrementAmount = 100.0 / double graceStatus.Index.Count

                            // We'll segment the uploads by the number of segments in the path, 
                            //   so we process the deepest paths first, and the new children exist before the parent is created.
                            //   Within each segment group, we'll parallelize the processing for performance.
                            let segmentGroups = graceStatus.Index.Values
                                                    .GroupBy(fun dv -> countSegments dv.RelativePath)
                                                    .OrderByDescending(fun group -> group.Key)
                                    
                            for group in segmentGroups do
                                let directoryVersionGroups = group.Chunk(chunkSize)
                                do! Parallel.ForEachAsync(directoryVersionGroups, Constants.ParallelOptions, (fun directoryVersionGroup ct ->
                                    ValueTask(task {
                                        let param = SaveDirectoryVersionsParameters()
                                        param.DirectoryVersions <- directoryVersionGroup.Select(fun dv -> dv.ToDirectoryVersion).ToList()
                                        param.CorrelationId <- getCorrelationId parseResult
                                        let! sdvResult = Directory.SaveDirectoryVersions param
                                        match sdvResult with
                                        | Ok result -> succeeded.Enqueue(result)
                                        | Error error -> errors.Enqueue(error)
                                        t6.Increment(incrementAmount * double directoryVersionGroup.Length)
                                    })))
                            t6.Value <- 100.0

                            AnsiConsole.MarkupLine($"[{Colors.Important}]succeeded: {succeeded.Count}; errors: {errors.Count}.[/]")
                            let mutable error = GraceError.Create String.Empty String.Empty
                            while not <| errors.IsEmpty do
                                errors.TryDequeue(&error) |> ignore
                                if error.Error.Contains("TRetval") then
                                    logToConsole $"********* {error.Error}"
                                AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
                            return graceStatus
                        })
                    
                    let fileCount = graceStatus.Index.Values.Select(fun directoryVersion -> directoryVersion.Files.Count).Sum()
                    let totalFileSize = graceStatus.Index.Values.Sum(fun directoryVersion -> directoryVersion.Files.Sum(fun f -> int64 f.Size))
                    let rootDirectoryVersion = graceStatus.Index.Values.First(fun d -> d.RelativePath = Constants.RootDirectoryPath)
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of directories scanned: {graceStatus.Index.Count}.[/]")
                    AnsiConsole.MarkupLine($"[{Colors.Highlighted}]Number of files scanned: {fileCount}; total file size: {totalFileSize:N0}.[/]")
                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(0, 8)}[/]"
                else
                    let! previousGraceStatus = readGraceStatusFile()
                    let! graceStatus = createGraceStatusFile previousGraceStatus
                    do! writeGraceStatusFile graceStatus
                    
                    let fileVersions = ConcurrentDictionary<RelativePath, LocalFileVersion>()
                    let plr = Parallel.ForEach(graceStatus.Index.Values, Constants.ParallelOptions, (fun ldv ->
                        for fileVersion in ldv.Files do
                            fileVersions.TryAdd(fileVersion.RelativePath, fileVersion) |> ignore
                    ))

                    let plr = Parallel.ForEach(fileVersions, Constants.ParallelOptions, (fun kvp _ ->
                        let fileVersion = kvp.Value
                        let fullObjectPath = fileVersion.FullObjectPath
                        if not <| File.Exists(fullObjectPath) then
                            Directory.CreateDirectory(Path.GetDirectoryName(fullObjectPath)) |> ignore
                            File.Copy(Path.Combine(Current().RootDirectory, fileVersion.RelativePath), fullObjectPath)))

                    match Current().ObjectStorageProvider with
                    | ObjectStorageProvider.Unknown -> ()
                    | AzureBlobStorage -> 
                        let chunkSize = 32
                        let fileVersionGroups = fileVersions.Chunk(chunkSize)
                        let succeeded = ConcurrentQueue<GraceReturnValue<string>>()
                        let errors = ConcurrentQueue<GraceError>()
                        
                        do! Parallel.ForEachAsync(fileVersionGroups, Constants.ParallelOptions, (fun fileVersions ct ->
                            ValueTask(task {
                                let! graceResult = Storage.FilesExistInObjectStorage (fileVersions.Select(fun f -> f.Value.ToFileVersion).ToList()) (getCorrelationId parseResult)
                                match graceResult with
                                | Ok graceReturnValue ->
                                    let uploadMetadata = graceReturnValue.ReturnValue
                                    let filesIndexedBySha256Hash = Dictionary<Sha256Hash, LocalFileVersion>(fileVersions.Select(fun kvp -> KeyValuePair(kvp.Value.Sha256Hash, kvp.Value)))

                                    do! Parallel.ForEachAsync(uploadMetadata, Constants.ParallelOptions, (fun upload ct ->
                                        ValueTask(task {
                                            let fileVersion = filesIndexedBySha256Hash[upload.Sha256Hash].ToFileVersion
                                            let! result = Storage.SaveFileToObjectStorage fileVersion (upload.BlobUriWithSasToken) (getCorrelationId parseResult)
                                            match result with
                                            | Ok result -> succeeded.Enqueue(result)
                                            | Error error -> errors.Enqueue(error)
                                        })))
                                | Error error -> AnsiConsole.MarkupLine($"[{Colors.Error}]{error}[/]")
                            })))

                        if errors.Count = 0 then
                            ()
                        else
                            AnsiConsole.MarkupLine($"{errors.Count} errors occurred.")
                            let mutable error = GraceError.Create String.Empty String.Empty
                            while not <| errors.IsEmpty do
                                if errors.TryDequeue(&error) then AnsiConsole.MarkupLine($"[{Colors.Error}]{error.Error.EscapeMarkup()}[/]")
                    | AWSS3 -> ()
                    | GoogleCloudStorage -> ()
            } :> Task)

    let private Scan = 
        CommandHandler.Create(fun (parseResult: ParseResult) (parameters: CommonParameters) ->
            task {
                if parseResult |> verbose then printParseResult parseResult
                
                if parseResult |> showOutput then
                    let! (differences, newDirectoryVersions) = progress.Columns(progressColumns).StartAsync(fun progressContext ->
                        task {
                            let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace index file.[/]")
                            let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)
                            let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Computing root directory SHA-256 value.[/]", autoStart = false)
                            t0.Increment(0.0)
                            let! previousGraceStatus = readGraceStatusFile()
                            t0.Increment(100.0)
                            t1.StartTask()
                            t1.Increment(0.0)
                            let! differences = scanForDifferences previousGraceStatus
                            t1.Increment(100.0)
                            t2.StartTask()
                            t2.Increment(0.0)
                            let! (newGraceIndex, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                            t2.Increment(100.0)
                            return (differences, newDirectoryVersions)
                        })
                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Number of differences: {differences.Count}[/]"
                    for difference in differences do
                        let x = sprintf "%A" difference
                        AnsiConsole.MarkupLine $"[{Colors.Important}]{x}[/]"
                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Number of new DirectoryVersions: {newDirectoryVersions.Count}[/]"
                    for ldv in newDirectoryVersions do
                        AnsiConsole.MarkupLine $"[{Colors.Important}]SHA-256: {ldv.Sha256Hash.Substring(0, 8)}; DirectoryId: {ldv.DirectoryId}; RelativePath: {ldv.RelativePath}[/]"
                    //AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Root SHA-256 hash: {rootDirectoryVersion.Sha256Hash.Substring(8)}[/]"
                else
                    let! previousGraceStatus = readGraceStatusFile()
                    let! differences = scanForDifferences previousGraceStatus
                    let! (newGraceIndex, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]Number of differences: {differences.Count}[/]"
                    for difference in differences do
                        let x = sprintf "%A" difference
                        AnsiConsole.MarkupLine $"[{Colors.Important}]{x}[/]"
                    AnsiConsole.MarkupLine $"[{Colors.Highlighted}]newDirectoryVersions.Count: {newDirectoryVersions.Count}[/]"
                    for ldv in newDirectoryVersions do
                        AnsiConsole.MarkupLine $"[{Colors.Important}]SHA-256: {ldv.Sha256Hash.Substring(0, 8)}; DirectoryId: {ldv.DirectoryId}; RelativePath: {ldv.RelativePath}[/]"                    
            } :> Task)

    let Build = 
        let maintenanceCommand = new Command("maintenance", Description = "Performs various maintenance tasks.")
        maintenanceCommand.AddAlias("maint")

        let updateIndexCommand = new Command("update-index", Description = "Recreates the local Grace index file based on the current working directory contents.")
        updateIndexCommand.Handler <- UpdateIndex
        maintenanceCommand.AddCommand(updateIndexCommand)

        let scanCommand = new Command("scan", Description = "Scans the working directory contents for changes.")
        scanCommand.Handler <- Scan
        maintenanceCommand.AddCommand(scanCommand)
    
        maintenanceCommand

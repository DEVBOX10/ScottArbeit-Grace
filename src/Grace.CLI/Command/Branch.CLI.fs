namespace Grace.Cli.Command

open FSharpPlus
open Grace.Cli.Common
open Grace.Cli.Services
open Grace.SDK
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Client.Theme
open Grace.Shared.Dto.Reference
open Grace.Shared.Parameters.Branch
open Grace.Shared.Parameters.Directory
open Grace.Shared.Types
open Grace.Shared.Utilities
open Grace.Shared.Validation
open Grace.Shared.Validation.Errors.Branch
open NodaTime
open NodaTime.TimeZones
open Spectre.Console
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.CommandLine
open System.CommandLine.NamingConventionBinder
open System.CommandLine.Parsing
open System.Globalization
open System.IO
open System.IO.Enumeration
open System.Linq
open System.Security.Cryptography
open System.Threading.Tasks
open System.Text
open System.Text.Json

module Branch =

    type CommonParameters() = 
        inherit ParameterBase()
        member val public BranchId: string = String.Empty with get, set
        member val public BranchName: string = String.Empty with get, set
        member val public OwnerId: string = String.Empty with get, set
        member val public OwnerName: string = String.Empty with get, set
        member val public OrganizationId: string = String.Empty with get, set
        member val public OrganizationName: string = String.Empty with get, set
        member val public RepositoryId: string = String.Empty with get, set
        member val public RepositoryName: string = String.Empty with get, set

    module private Options =
        let validateGuid (validate: OptionResult) =
            let mutable guid = Guid.Empty
            if Guid.TryParse(validate.GetValueOrDefault<String>(), &guid) = false then
                validate.ErrorMessage <- BranchError.getErrorMessage InvalidBranchId

        let branchId = new Option<String>([|"--branchId"; "-i"|], IsRequired = false, Description = "The branch's ID <Guid>.", Arity = ArgumentArity.ExactlyOne, getDefaultValue = (fun _ -> $"{Current().BranchId}"))
        let branchName = new Option<String>([|"--branchName"; "-b"|], IsRequired = false, Description = "The name of the branch. [default: current branch]", Arity = ArgumentArity.ExactlyOne)
        let branchNameRequired = new Option<String>([|"--branchName"; "-b"|], IsRequired = true, Description = "The name of the branch.", Arity = ArgumentArity.ExactlyOne)
        let ownerId = new Option<String>("--ownerId", IsRequired = false, Description = "The repository's owner ID <Guid>.", Arity = ArgumentArity.ZeroOrOne, getDefaultValue = (fun _ -> $"{Current().OwnerId}"))
        let ownerName = new Option<String>("--ownerName", IsRequired = false, Description = "The repository's owner name. [default: current owner]", Arity = ArgumentArity.ExactlyOne)
        let organizationId = new Option<String>("--organizationId", IsRequired = false, Description = "The repository's organization ID <Guid>.", Arity = ArgumentArity.ExactlyOne, getDefaultValue = (fun _ -> $"{Current().OrganizationId}"))
        let organizationName = new Option<String>("--organizationName", IsRequired = false, Description = "The repository's organization name. [default: current organization]", Arity = ArgumentArity.ZeroOrOne)
        let repositoryId = new Option<String>([|"--repositoryId"; "-r"|], IsRequired = false, Description = "The repository's ID <Guid>.", Arity = ArgumentArity.ExactlyOne, getDefaultValue = (fun _ -> $"{Current().RepositoryId}"))
        let repositoryName = new Option<String>([|"--repositoryName"; "-n"|], IsRequired = false, Description = "The name of the repository. [default: current repository]", Arity = ArgumentArity.ExactlyOne)
        let parentBranchId = new Option<String>([|"--parentBranchId"|], IsRequired = false, Description = "The parent branch's ID <Guid>.", Arity = ArgumentArity.ExactlyOne)
        let parentBranchName = new Option<String>([|"--parentBranchName"|], IsRequired = false, Description = "The name of the parent branch. [default: current branch]", Arity = ArgumentArity.ExactlyOne, getDefaultValue = (fun _ -> $"{Current().BranchName}"))
        let newName = new Option<String>("--newName", IsRequired = true, Description = "The new name of the branch.", Arity = ArgumentArity.ExactlyOne)
        let message = new Option<String>([|"--message"; "-m"|], IsRequired = false, Description = "The text to store with this reference.", Arity = ArgumentArity.ExactlyOne)
        let messageRequired = new Option<String>([|"--message"; "-m"|], IsRequired = true, Description = "The text to store with this reference.", Arity = ArgumentArity.ExactlyOne)
        let referenceType = (new Option<String>("--referenceType", IsRequired = false, Description = "The type of reference.", Arity = ArgumentArity.ExactlyOne))
                                .FromAmong(Utilities.listCases(typeof<ReferenceType>))
        let doNotSwitch = new Option<bool>("--doNotSwitch", IsRequired = false, Description = "Do not switch to the new branch as the current branch.", Arity = ArgumentArity.ZeroOrOne)
        let fullSha = new Option<bool>("--fullSha", IsRequired = false, Description = "Show the full SHA-256 value in output.", Arity = ArgumentArity.ZeroOrOne)
        let maxCount = new Option<int>("--maxCount", IsRequired = false, Description = "The maximum number of results to return.", Arity = ArgumentArity.ExactlyOne)
        maxCount.SetDefaultValue(30)
        let referenceId = new Option<String>([|"--referenceId"|], IsRequired = false, Description = "The reference ID to switch to <Guid>.", Arity = ArgumentArity.ExactlyOne)
        let sha256Hash = new Option<String>([|"--sha256Hash"|], IsRequired = false, Description = "The full or partial SHA-256 hash value of the version to switch to.", Arity = ArgumentArity.ExactlyOne)
        let enabled = new Option<bool>("--enabled", IsRequired = false, Description = "True to enable the feature; false to disable it.", Arity = ArgumentArity.ZeroOrOne)
        
    let mustBeAValidGuid (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: BranchError) =
        let mutable guid = Guid.Empty
        if parseResult.CommandResult.FindResultFor(option) <> null 
                && not <| String.IsNullOrEmpty(value) 
                && (Guid.TryParse(value, &guid) = false || guid = Guid.Empty)
                then 
            Error (GraceError.Create (BranchError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok (parseResult, parameters)

    let mustBeAValidGraceName (parseResult: ParseResult) (parameters: CommonParameters) (option: Option) (value: string) (error: BranchError) =
        if parseResult.CommandResult.FindResultFor(option) <> null && not <| Constants.GraceNameRegex.IsMatch(value) then 
            Error (GraceError.Create (BranchError.getErrorMessage error) (parameters.CorrelationId))
        else
            Ok (parseResult, parameters)

    let private CommonValidations parseResult commonParameters =
        let ``BranchId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.branchId commonParameters.BranchId InvalidBranchId

        let ``BranchName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.branchName commonParameters.BranchName InvalidBranchName

        let ``OwnerId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.ownerId commonParameters.OwnerId InvalidOwnerId

        let ``OwnerName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.ownerName commonParameters.OwnerName InvalidOwnerName
            
        let ``OrganizationId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.organizationId commonParameters.OrganizationId InvalidOrganizationId

        let ``OrganizationName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.organizationName commonParameters.OrganizationName InvalidOrganizationName
            
        let ``RepositoryId must be a Guid`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGuid parseResult commonParameters Options.repositoryId commonParameters.RepositoryId InvalidRepositoryId
            
        let ``RepositoryName must be a valid Grace name`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            mustBeAValidGraceName parseResult commonParameters Options.repositoryName commonParameters.RepositoryName InvalidRepositoryName

        let ``Grace index file must exist`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not <| File.Exists(Current().GraceStatusFile) then 
                Error (GraceError.Create (BranchError.getErrorMessage IndexFileNotFound) commonParameters.CorrelationId)
            else
                Ok (parseResult, commonParameters)

        let ``Grace object cache file must exist`` (parseResult: ParseResult, commonParameters: CommonParameters) =
            if not <| File.Exists(Current().GraceStatusFile) then 
                Error (GraceError.Create (BranchError.getErrorMessage ObjectCacheFileNotFound) commonParameters.CorrelationId)
            else
                Ok (parseResult, commonParameters)

        (parseResult, commonParameters)
            |>  ``BranchId must be a Guid``
            >>= ``BranchName must be a valid Grace name``
            >>= ``OwnerId must be a Guid``
            >>= ``OwnerName must be a valid Grace name``
            >>= ``OrganizationId must be a Guid``
            >>= ``OrganizationName must be a valid Grace name``
            >>= ``RepositoryId must be a Guid``
            >>= ``RepositoryName must be a valid Grace name``
            >>= ``Grace index file must exist``
            >>= ``Grace object cache file must exist``

    let private ``BranchName must not be empty`` (parseResult: ParseResult, commonParameters: CommonParameters) =
        if (parseResult.HasOption(Options.branchNameRequired) || parseResult.HasOption(Options.branchName))
                && not <| String.IsNullOrEmpty(commonParameters.BranchName) then 
            Ok (parseResult, commonParameters)
        else
            Error (GraceError.Create (BranchError.getErrorMessage BranchNameIsRequired) (commonParameters.CorrelationId))

    /// Adjusts parameters to account for whether Id's or Name's were specified by the user.
    let normalizeIdsAndNames<'T when 'T :> CommonParameters> (parseResult: ParseResult) (parameters: 'T) =
        // If the name was specified on the command line, but the id wasn't, drop the default assignment of the id.
        if parseResult.CommandResult.FindResultFor(Options.ownerId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.ownerName)) && not <| parseResult.CommandResult.FindResultFor(Options.ownerName).IsImplicit then
            parameters.OwnerId <- String.Empty
        if parseResult.CommandResult.FindResultFor(Options.organizationId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.organizationName)) && not <| parseResult.CommandResult.FindResultFor(Options.organizationName).IsImplicit then
            parameters.OrganizationId <- String.Empty
        if parseResult.CommandResult.FindResultFor(Options.repositoryId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.repositoryName)) && not <| parseResult.CommandResult.FindResultFor(Options.repositoryName).IsImplicit then
            parameters.RepositoryId <- String.Empty
        if parseResult.CommandResult.FindResultFor(Options.branchId).IsImplicit && not <| isNull(parseResult.CommandResult.FindResultFor(Options.branchName)) && not <| parseResult.CommandResult.FindResultFor(Options.branchName).IsImplicit then
            parameters.BranchId <- String.Empty
        parameters
            
    /// Populates OwnerId, OrganizationId, RepositoryId, and BranchId based on the command parameters and the contents of graceconfig.json.
    let getIds<'T when 'T :> CommonParameters> (parameters: 'T) = 
        let ownerId = if not <| String.IsNullOrEmpty(parameters.OwnerId) || not <| String.IsNullOrEmpty(parameters.OwnerName)
                      then parameters.OwnerId 
                      else $"{Current().OwnerId}"
        let organizationId = if not <| String.IsNullOrEmpty(parameters.OrganizationId) || not <| String.IsNullOrEmpty(parameters.OrganizationName)
                             then parameters.OrganizationId
                             else $"{Current().OrganizationId}"
        let repositoryId = if not <| String.IsNullOrEmpty(parameters.RepositoryId) || not <| String.IsNullOrEmpty(parameters.RepositoryName)
                           then parameters.RepositoryId 
                           else $"{Current().RepositoryId}"
        let branchId = if not <| String.IsNullOrEmpty(parameters.BranchId) || not <| String.IsNullOrEmpty(parameters.BranchName)
                       then parameters.BranchId
                       else $"{Current().BranchId}"
        (ownerId, organizationId, repositoryId, branchId)

    // Create subcommand.
    type CreateParameters() = 
        inherit CommonParameters()
        member val public ParentBranchId = String.Empty with get, set
        member val public ParentBranchName = String.Empty with get, set
    let private createHandler (parseResult: ParseResult) (createParameters: CreateParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult createParameters >>= ``BranchName must not be empty``
                match validateIncomingParameters with
                | Ok _ -> 
                    let branchId = if parseResult.FindResultFor(Options.branchId).IsImplicit then Guid.NewGuid().ToString() else createParameters.BranchId
                    let parameters = Parameters.Branch.CreateBranchParameters(
                        RepositoryId = createParameters.RepositoryId,
                        RepositoryName = createParameters.RepositoryName,
                        OwnerId = createParameters.OwnerId,
                        OwnerName = createParameters.OwnerName,
                        OrganizationId = createParameters.OrganizationId,
                        OrganizationName = createParameters.OrganizationName,
                        BranchId = branchId,
                        BranchName = createParameters.BranchName,
                        ParentBranchId = createParameters.ParentBranchId,
                        ParentBranchName = createParameters.ParentBranchName,
                        CorrelationId = createParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Branch.Create(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Branch.Create(parameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Create =
        CommandHandler.Create(fun (parseResult: ParseResult) (createParameters: CreateParameters) ->
            task {
                let! result = createHandler parseResult createParameters
                match result with
                | Ok returnValue ->
                    // Update the Grace configuration file with the newly-created branch.
                    if not <| parseResult.HasOption(Options.doNotSwitch) then
                        let newConfig = Current()
                        newConfig.BranchId <- Guid.Parse(returnValue.Properties[nameof(BranchId)])
                        newConfig.BranchName <- BranchName (returnValue.Properties[nameof(BranchName)])
                        updateConfiguration newConfig
                | Error _ -> ()
                return result |> renderOutput parseResult
            })

    type SetNameParameters() =
        inherit CommonParameters()
        member val public NewName: string = String.Empty with get, set
    let private setNameHandler (parseResult: ParseResult) (setNameParameters: SetNameParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult setNameParameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let parameters = Parameters.Branch.SetBranchNameParameters(BranchId = setNameParameters.BranchId, BranchName = setNameParameters.BranchName, 
                        NewName= setNameParameters.NewName, CorrelationId = setNameParameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Branch.SetName(parameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Branch.SetName(parameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private SetName =
        CommandHandler.Create(fun (parseResult: ParseResult) (setNameParameters: SetNameParameters) ->
            task {                
                let! result = setNameHandler parseResult (setNameParameters |> normalizeIdsAndNames parseResult)
                return result |> renderOutput parseResult
            })

    type CreateReferenceCommand = CreateReferenceParameters -> Task<GraceResult<String>>
    type CreateRefParameters() =
        inherit CommonParameters()
        member val public DirectoryVersion = DirectoryVersion.Default with get, set
        member val public Message = String.Empty with get, set
    let createReferenceHandler (parseResult: ParseResult) (parameters: CreateRefParameters) (command: CreateReferenceCommand) (commandType: string) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    //let sha256Bytes = SHA256.HashData(Encoding.ASCII.GetBytes(rnd.NextInt64().ToString("x8")))
                    //let sha256Hash = Seq.fold (fun (sb: StringBuilder) currentByte ->
                    //    sb.Append(sprintf $"{currentByte:X2}")) (StringBuilder(sha256Bytes.Length)) sha256Bytes
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace status file.[/]")
                                    let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)
                                    let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)
                                    let t3 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)
                                    let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)
                                    let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new {commandType}.[/]", autoStart = false)
                                
                                    //let mutable rootDirectoryId = DirectoryId.Empty
                                    //let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                                    let rootDirectoryVersion = ref (DirectoryId.Empty, Sha256Hash String.Empty)

                                    match! getGraceWatchStatus() with
                                    | Some graceWatchStatus ->
                                        t0.Value <- 100.0
                                        t1.Value <- 100.0
                                        t2.Value <- 100.0
                                        t3.Value <- 100.0
                                        t4.Value <- 100.0
                                        rootDirectoryVersion.Value <- (graceWatchStatus.RootDirectoryId, graceWatchStatus.RootDirectorySha256Hash)
                                    | None ->
                                        t0.StartTask()   // Read Grace status file.
                                        let! previousGraceStatus = readGraceStatusFile()
                                        let mutable newGraceStatus = previousGraceStatus
                                        t0.Value <- 100.0

                                        t1.StartTask()  // Scan for differences.
                                        let! differences = scanForDifferences previousGraceStatus
                                        t1.Value <- 100.0

                                        t2.StartTask()  // Create new directory versions.
                                        let! (updatedGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                                        newGraceStatus <- updatedGraceStatus
                                        rootDirectoryVersion.Value <- (newGraceStatus.RootDirectoryId, newGraceStatus.RootDirectorySha256Hash)
                                        t2.Value <- 100.0

                                        t3.StartTask()  // Upload to object storage.
                                        let updatedRelativePaths = 
                                            differences.Select(fun difference ->
                                                match difference.DifferenceType with
                                                | Add -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                                | Change -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                                | Delete -> None)
                                                .Where(fun relativePathOption -> relativePathOption.IsSome)
                                                .Select(fun relativePath -> relativePath.Value)

                                        let newFileVersions = updatedRelativePaths.Select(fun relativePath -> 
                                            newDirectoryVersions.First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath)).Files.First(fun file -> file.RelativePath = relativePath))

                                        let mutable lastFileUploadInstant = newGraceStatus.LastSuccessfulFileUpload
                                        if newFileVersions.Count() > 0 then
                                            let! uploadResult = uploadFilesToObjectStorage newFileVersions (getCorrelationId parseResult)
                                            lastFileUploadInstant <- getCurrentInstant()
                                        t3.Value <- 100.0

                                        t4.StartTask()  // Upload directory versions.
                                        let mutable lastDirectoryVersionUpload = newGraceStatus.LastSuccessfulDirectoryVersionUpload
                                        if newDirectoryVersions.Count > 0 then
                                            let saveParameters = SaveDirectoryVersionsParameters()
                                            saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()
                                            let! uploadDirectoryVersions = Directory.SaveDirectoryVersions saveParameters
                                            lastDirectoryVersionUpload <- getCurrentInstant()
                                        t4.Value <- 100.0

                                        newGraceStatus <- {newGraceStatus with LastSuccessfulFileUpload = lastFileUploadInstant; LastSuccessfulDirectoryVersionUpload = lastDirectoryVersionUpload}
                                        do! writeGraceStatusFile newGraceStatus
                                
                                    t5.StartTask()  // Create new reference.

                                    let (rootDirectoryId, rootDirectorySha256Hash) = rootDirectoryVersion.Value
                                    let sdkParameters = 
                                        Parameters.Branch.CreateReferenceParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName, 
                                            OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                            OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                            RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                            DirectoryId = rootDirectoryId, Sha256Hash = rootDirectorySha256Hash,
                                            Message = parameters.Message, CorrelationId = parameters.CorrelationId)
                                    let! result = command sdkParameters
                                    t5.Value <- 100.0
                                
                                    return result
                                })
                    else
                        let! previousGraceStatus = readGraceStatusFile()
                        let! differences = scanForDifferences previousGraceStatus
                        let! (newGraceIndex, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                        let updatedRelativePaths = 
                            differences.Select(fun difference ->
                                match difference.DifferenceType with
                                | Add -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                | Change -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                | Delete -> None)
                                .Where(fun relativePathOption -> relativePathOption.IsSome)
                                .Select(fun relativePath -> relativePath.Value)
                        let newFileVersions = updatedRelativePaths.Select(fun relativePath -> 
                            newDirectoryVersions.First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath)).Files.First(fun file -> file.RelativePath = relativePath))
                        let! uploadResult = uploadFilesToObjectStorage newFileVersions (getCorrelationId parseResult)
                        let saveParameters = SaveDirectoryVersionsParameters()
                        saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()
                        let! uploadDirectoryVersions = Directory.SaveDirectoryVersions saveParameters
                        let rootDirectoryVersion = getRootDirectoryVersion previousGraceStatus
                        let sdkParameters = 
                            Parameters.Branch.CreateReferenceParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName, 
                                OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                DirectoryId = rootDirectoryVersion.DirectoryId, Sha256Hash = rootDirectoryVersion.Sha256Hash,
                                Message = parameters.Message, CorrelationId = parameters.CorrelationId)
                        let! result = command sdkParameters
                        return result                    
                | Error error -> return Error error
            with ex ->
                return Error (GraceError.Create $"{createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    
    let promotionHandler (parseResult: ParseResult) (parameters: CreateRefParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ ->
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace status file.[/]")
                                    let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Checking if the promotion is valid.[/]", autoStart = false)
                                    let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]", autoStart = false)
                                                                    
                                    // Read Grace status file.
                                    let! graceStatus = readGraceStatusFile()
                                    let rootDirectoryId = graceStatus.RootDirectoryId
                                    let rootDirectorySha256Hash = graceStatus.RootDirectorySha256Hash
                                    t0.Value <- 100.0

                                    // Check if the promotion is valid; i.e. it's allowed by the ReferenceTypes enabled in the repository.
                                    t1.StartTask()
                                    // For single-step promotion, the current branch's latest commit will become the parent branch's next promotion.
                                    // If our current state is not the latest commit, print a warning message.

                                    // Get the Dto for the current branch. That will have its latest commit.
                                    let branchGetParameters = 
                                        Parameters.Branch.GetBranchParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName, 
                                            OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                            OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                            RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                            CorrelationId = parameters.CorrelationId)
                                    let! branchResult = Branch.Get(branchGetParameters)
                                    match branchResult with
                                    | Ok branchReturnValue -> 
                                        // If we succeeded, get the parent branch Dto. That will have its latest promotion.
                                        let! parentBranchResult = Branch.GetParentBranch(branchGetParameters)
                                        match parentBranchResult with
                                        | Ok parentBranchReturnValue -> 
                                            // Yay, we have both Dto's.
                                            let branchDto = branchReturnValue.ReturnValue
                                            let parentBranchDto = parentBranchReturnValue.ReturnValue

                                            // Get the references for the latest commit and/or promotion on the current branch.
                                            //let getReferenceParameters = 
                                            //    Parameters.Branch.GetReferenceParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName, 
                                            //        OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                            //        OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                            //        RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                            //        ReferenceId = $"{branchDto.LatestCommit}", CorrelationId = parameters.CorrelationId)
                                            //let! referenceResult = Branch.GetReference(getReferenceParameters)
                                            
                                            let referenceIds = List<ReferenceId>()
                                            if branchDto.LatestCommit <> ReferenceId.Empty then
                                                referenceIds.Add(branchDto.LatestCommit)
                                            if branchDto.LatestPromotion <> ReferenceId.Empty then
                                                referenceIds.Add(branchDto.LatestPromotion)
                                            if referenceIds.Count > 0 then
                                                let getReferencesByReferenceIdParameters = 
                                                    Parameters.Repository.GetReferencesByReferenceIdParameters(
                                                        OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                        OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                        RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                                        ReferenceIds = referenceIds, CorrelationId = parameters.CorrelationId)
                                                match! Repository.GetReferencesByReferenceId(getReferencesByReferenceIdParameters) with
                                                | Ok returnValue ->
                                                    let references = returnValue.ReturnValue
                                                    let latestPromotableReference =
                                                        references.OrderByDescending(fun reference -> reference.CreatedAt).First()
                                                    // If the current branch's latest reference is not the latest commit - i.e. they've done more work in the branch 
                                                    //   after the commit they're expecting to promote - print a warning.
                                                    //match getReferencesByReferenceIdResult with 
                                                    //| Ok returnValue -> 
                                                    //    let references = returnValue.ReturnValue
                                                    //    if referenceDto.DirectoryId <> graceStatus.RootDirectoryId then
                                                    //        logToAnsiConsole Colors.Important $"Note: the branch has been updated since the latest commit."
                                                    //| Error error -> () // I don't really care if this call fails, it's just a warning message.
                                                    t1.Value <- 100.0
                                                
                                                    // If the current branch is based on the parent's latest promotion, then we can proceed with the promotion.
                                                    if branchDto.BasedOn = parentBranchDto.LatestPromotion then
                                                        t2.StartTask()
                                                        let promotionParameters = 
                                                            Parameters.Branch.CreateReferenceParameters(BranchId = $"{parentBranchDto.BranchId}",
                                                                OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                                OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                                RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                                                DirectoryId = latestPromotableReference.DirectoryId, Sha256Hash = latestPromotableReference.Sha256Hash,
                                                                Message = parameters.Message, CorrelationId = parameters.CorrelationId)
                                                        let! promotionResult = Branch.Promote(promotionParameters)
                                                        match promotionResult with
                                                        | Ok returnValue ->
                                                            logToAnsiConsole Colors.Verbose $"Succeeded doing promotion."
                                                            let promotionReferenceId = returnValue.Properties["ReferenceId"]
                                                            let rebaseParameters = Parameters.Branch.RebaseParameters(
                                                                BranchId = $"{branchDto.BranchId}", RepositoryId = $"{branchDto.RepositoryId}",
                                                                OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                                OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                                BasedOn = (Guid.Parse(promotionReferenceId)))
                                                            let! rebaseResult = Branch.Rebase(rebaseParameters)
                                                            t2.Value <- 100.0
                                                            match rebaseResult with
                                                            | Ok returnValue -> 
                                                                logToAnsiConsole Colors.Verbose $"Succeeded doing rebase."
                                                                return promotionResult
                                                            | Error error -> return Error error
                                                        | Error error -> 
                                                            t2.Value <- 100.0
                                                            return Error error

                                                    else
                                                        return Error (GraceError.Create (BranchError.getErrorMessage BranchIsNotBasedOnLatestPromotion) (parseResult |> getCorrelationId))
                                                | Error error -> 
                                                    t2.Value <- 100.0
                                                    return Error error
                                            else
                                                return Error (GraceError.Create (BranchError.getErrorMessage PromotionNotAvailableBecauseThereAreNoPromotableReferences) (parseResult |> getCorrelationId))
                                        | Error error -> 
                                            t1.Value <- 100.0
                                            return Error error
                                    | Error error -> 
                                        t1.Value <- 100.0
                                        return Error error
                                })
                    else
                        // Same result, with no output.
                        return Error (GraceError.Create "Need to implement the else clause." (parseResult |> getCorrelationId))
                | Error error -> return Error error
            with ex ->
                return Error (GraceError.Create $"{createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Promote =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {        
                try
                    let! result = promotionHandler parseResult (createReferencesParameters |> normalizeIdsAndNames parseResult)
                    return result |> renderOutput parseResult
                with ex -> 
                    logToAnsiConsole Colors.Error (Markup.Escape($"{createExceptionResponse ex}"))
                    return -1
            })
    
    let private Commit =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {
                let command (parameters: CreateReferenceParameters) = 
                    task {
                        return! Branch.Commit(parameters)
                    }

                let! result = createReferenceHandler parseResult (createReferencesParameters |> normalizeIdsAndNames parseResult) command (nameof(Commit).ToLowerInvariant())
                return result |> renderOutput parseResult
            })

    let private Checkpoint =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {                
                let command (parameters: CreateReferenceParameters) = 
                    task {
                        return! Branch.Checkpoint(parameters)
                    }

                let! result = createReferenceHandler parseResult (createReferencesParameters |> normalizeIdsAndNames parseResult) command (nameof(Checkpoint).ToLowerInvariant())
                return result |> renderOutput parseResult
            })

    let private Save =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {                
                let command (parameters: CreateReferenceParameters) = 
                    task {
                        return! Branch.Save(parameters)
                    }

                let! result = createReferenceHandler parseResult (createReferencesParameters |> normalizeIdsAndNames parseResult) command (nameof(Save).ToLowerInvariant())
                return result |> renderOutput parseResult
            })

    let private Tag =
        CommandHandler.Create(fun (parseResult: ParseResult) (createReferencesParameters: CreateRefParameters) ->
            task {                
                let command (parameters: CreateReferenceParameters) = 
                    task {
                        return! Branch.Tag(parameters)
                    }

                let! result = createReferenceHandler parseResult (createReferencesParameters |> normalizeIdsAndNames parseResult) command (nameof(Tag).ToLowerInvariant())
                return result |> renderOutput parseResult
            })

    type EnableFeatureCommand = EnableFeatureParameters -> Task<GraceResult<string>>
    type EnableFeaturesParameters() =
        inherit CommonParameters()
        member val public Enabled = true with get, set
    let enableFeatureHandler (parseResult: ParseResult) (parameters: EnableFeaturesParameters) (command: EnableFeatureCommand) (commandType: string) =
        task {
            return Ok (GraceReturnValue.Create "Not yet implemented." (getCorrelationId parseResult))
        }

    let private EnablePromotion =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParameters: EnableFeaturesParameters) ->
            task {
                let command (parameters: EnableFeatureParameters) =
                    task {
                        return! Branch.EnablePromotion(parameters)
                    }
                    
                let! result = enableFeatureHandler parseResult (enableFeaturesParameters |> normalizeIdsAndNames parseResult) command "promotion"
                return result |> renderOutput parseResult
            })

    let private EnableCommit =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParameters: EnableFeaturesParameters) ->
            task {
                let command (parameters: EnableFeatureParameters) =
                    task {
                        return! Branch.EnableCommit(parameters)
                    }

                let! result = enableFeatureHandler parseResult (enableFeaturesParameters |> normalizeIdsAndNames parseResult) command "commit"
                return result |> renderOutput parseResult
            })

    let private EnableCheckpoint =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParameters: EnableFeaturesParameters) ->
            task {
                let command (parameters: EnableFeatureParameters) =
                    task {
                        return! Branch.EnableCheckpoint(parameters)
                    }

                let! result = enableFeatureHandler parseResult (enableFeaturesParameters |> normalizeIdsAndNames parseResult) command "checkpoint"
                return result |> renderOutput parseResult
            })

    let private EnableSave =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParameters: EnableFeaturesParameters) ->
            task {
                let command (parameters: EnableFeatureParameters) =
                    task {
                        return! Branch.EnableSave(parameters)
                    }

                let! result = enableFeatureHandler parseResult (enableFeaturesParameters |> normalizeIdsAndNames parseResult) command "save"
                return result |> renderOutput parseResult
            })

    let private EnableTag =
        CommandHandler.Create(fun (parseResult: ParseResult) (enableFeaturesParameters: EnableFeaturesParameters) ->
            task {
                let command (parameters: EnableFeatureParameters) =
                    task {
                        return! Branch.EnableTag(parameters)
                    }

                let! result = enableFeatureHandler parseResult (enableFeaturesParameters |> normalizeIdsAndNames parseResult) command "tag"
                return result |> renderOutput parseResult
            })

    type GetReferenceQuery = GetReferencesParameters -> Task<GraceResult<IEnumerable<ReferenceDto>>>
    type GetRefParameters() =
        inherit CommonParameters()
        member val public MaxCount = 50 with get, set
    let getReferenceHandler (parseResult: ParseResult) (parameters: GetRefParameters) (query: GetReferenceQuery) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ -> 
                    let sdkParameters = Parameters.Branch.GetReferencesParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName, 
                                            OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                            OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                            RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                            MaxCount = parameters.MaxCount, CorrelationId = parameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                        let! result = query sdkParameters 
                                        t0.Increment(100.0)
                                        return result
                                    })
                    else
                        return! query sdkParameters 
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }

    let private writeReferenceOutput (parseResult: ParseResult) (references: IEnumerable<ReferenceDto>) =
        if references.Count() > 0 then
            let sortedResults = references.OrderByDescending(fun row -> row.CreatedAt)
            let table = Table(Border = TableBorder.DoubleEdge)
            table.AddColumns([| TableColumn($"[{Colors.Important}]Type[/]"); TableColumn($"[{Colors.Important}]Message[/]"); TableColumn($"[{Colors.Important}]SHA-256[/]"); TableColumn($"[{Colors.Important}]When[/]", Alignment = Justify.Right); TableColumn($"[{Colors.Important}][/]") |]) |> ignore
            for row in sortedResults do
                //printfn "%A" row
                let sha256Hash = if parseResult.HasOption(Options.fullSha) then
                                     $"{row.Sha256Hash}"
                                 else
                                     $"{row.Sha256Hash}".Substring(0, 8)
                let localCreatedAtTime = row.CreatedAt.ToDateTimeUtc().ToLocalTime()
                let x = 8
                let referenceTime = $"""{localCreatedAtTime.ToString("g", CultureInfo.CurrentUICulture)}"""
                table.AddRow([| $"{discriminatedUnionCaseNameToString(row.ReferenceType)}"; $"{row.ReferenceText}"; sha256Hash; ago row.CreatedAt; $"[{Colors.Deemphasized}]{referenceTime}[/]" |]) |> ignore
            AnsiConsole.Write(table)
        
    let private GetReferences =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {
                let query (parameters: GetReferencesParameters) = 
                    task {
                        return! Branch.GetReferences(parameters)
                    }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query
                match result with
                | Ok graceReturnValue ->
                    let intReturn = result |> renderOutput parseResult
                    if parseResult |> showOutput then
                        writeReferenceOutput parseResult graceReturnValue.ReturnValue
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Returned {graceReturnValue.ReturnValue.Count()} rows.[/]")
                    return intReturn
                | Error error ->
                    return result |> renderOutput parseResult
            })

    let private GetPromotions =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {                
                let query (parameters: GetReferencesParameters) = 
                    task {
                        return! Branch.GetPromotions(parameters)
                    }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query
                match result with
                | Ok graceReturnValue ->
                    let intReturn = result |> renderOutput parseResult
                    if parseResult |> showOutput then
                        writeReferenceOutput parseResult graceReturnValue.ReturnValue
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Returned {graceReturnValue.ReturnValue.Count()} rows.[/]")
                    return intReturn
                | Error error ->
                    return result |> renderOutput parseResult
            })

    let private GetCommits =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {                
                let query (parameters: GetReferencesParameters) = 
                    task {
                        return! Branch.GetCommits(parameters)
                    }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query
                match result with
                | Ok graceReturnValue ->
                    let intReturn = result |> renderOutput parseResult
                    if parseResult |> showOutput then
                        writeReferenceOutput parseResult graceReturnValue.ReturnValue
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Returned {graceReturnValue.ReturnValue.Count()} rows.[/]")
                    return intReturn
                | Error error ->
                    return result |> renderOutput parseResult
            })

    let private GetCheckpoints =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {                
                let query (parameters: GetReferencesParameters) = 
                    task {
                        let! checkpointResult = Branch.GetCheckpoints(parameters)
                        let! commitResult = Branch.GetCommits(parameters)
                        match checkpointResult with
                        | GraceResult.Ok checkpoints ->
                            match commitResult with
                            | Ok commits -> return GraceResult.Ok (GraceReturnValue.Create (checkpoints.ReturnValue.Concat(commits.ReturnValue).OrderByDescending(fun ref -> ref.CreatedAt).Take(getReferencesParameters.MaxCount)) (getCorrelationId parseResult))
                            | GraceResult.Error error -> return GraceResult.Error error 
                        | GraceResult.Error error -> return GraceResult.Error error 
                    }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query
                match result with
                | Ok graceReturnValue ->
                    let intReturn = result |> renderOutput parseResult
                    if parseResult |> showOutput then
                        writeReferenceOutput parseResult graceReturnValue.ReturnValue
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Returned {graceReturnValue.ReturnValue.Count()} rows.[/]")
                    return intReturn
                | Error error ->
                    return result |> renderOutput parseResult
            })

    let private GetSaves =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {                
                let query (parameters: GetReferencesParameters) = 
                    task {
                        return! Branch.GetSaves(parameters)
                    }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query
                match result with
                | Ok graceReturnValue ->
                    let intReturn = result |> renderOutput parseResult
                    if parseResult |> showOutput then
                        writeReferenceOutput parseResult graceReturnValue.ReturnValue
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Returned {graceReturnValue.ReturnValue.Count()} rows.[/]")
                    return intReturn
                | Error error ->
                    return result |> renderOutput parseResult
            })

    let private GetTags =
        CommandHandler.Create(fun (parseResult: ParseResult) (getReferencesParameters: GetRefParameters) ->
            task {                
                let query (parameters: GetReferencesParameters) = 
                    task {
                        return! Branch.GetTags(parameters)
                    }

                let! result = getReferenceHandler parseResult (getReferencesParameters |> normalizeIdsAndNames parseResult) query
                match result with
                | Ok graceReturnValue ->
                    let intReturn = result |> renderOutput parseResult
                    if parseResult |> showOutput then
                        writeReferenceOutput parseResult graceReturnValue.ReturnValue
                        AnsiConsole.MarkupLine($"[{Colors.Important}]Returned {graceReturnValue.ReturnValue.Count()} rows.[/]")
                    return intReturn
                | Error error ->
                    return result |> renderOutput parseResult
            })

    type SwitchParameters() =
        inherit CommonParameters()
        member val public ReferenceId = String.Empty with get, set
        member val public Sha256Hash = Sha256Hash String.Empty with get, set
    let private switchHandler parseResult (parameters: SwitchParameters) =
        task {
            try
                try
                    if parseResult |> verbose then printParseResult parseResult
                    let validateIncomingParameters = CommonValidations parseResult parameters
                    match validateIncomingParameters with
                    | Ok _ -> 
                        let mutable rootDirectoryId = DirectoryId.Empty
                        let mutable rootDirectorySha256Hash = Sha256Hash String.Empty
                        let mutable previousDirectoryIds: HashSet<DirectoryId> = null

                        if parseResult |> showOutput then
                            return! progress.Columns(progressColumns)
                                    .StartAsync(fun progressContext ->
                                    task {
                                        let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Reading Grace index file.[/]")
                                        let t1 = progressContext.AddTask($"[{Color.DodgerBlue1}]Scanning working directory for changes.[/]", autoStart = false)
                                        let t2 = progressContext.AddTask($"[{Color.DodgerBlue1}]Creating new directory verions.[/]", autoStart = false)
                                        let t3 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading changed files to object storage.[/]", autoStart = false)
                                        let t4 = progressContext.AddTask($"[{Color.DodgerBlue1}]Uploading new directory versions.[/]", autoStart = false)
                                        let t5 = progressContext.AddTask($"[{Color.DodgerBlue1}]Create a save reference.[/]", autoStart = false)
                                        let t6 = progressContext.AddTask($"[{Color.DodgerBlue1}]Getting new branch version from server.[/]", autoStart = false)
                                        let t7 = progressContext.AddTask($"[{Color.DodgerBlue1}]Updating object cache and working directory.[/]", autoStart = false)

                                        t0.Increment(0.0)
                                        let! previousGraceStatus = readGraceStatusFile()
                                        let mutable newGraceStatus = previousGraceStatus
                                        t0.Value <- 100.0

                                        match! getGraceWatchStatus() with
                                        | Some graceWatchStatus ->
                                            t1.Value <- 100.0
                                            t2.Value <- 100.0
                                            t3.Value <- 100.0
                                            t4.Value <- 100.0
                                            t5.Value <- 100.0
                                            rootDirectoryId <- graceWatchStatus.RootDirectoryId
                                            rootDirectorySha256Hash <- graceWatchStatus.RootDirectorySha256Hash
                                            previousDirectoryIds <- graceWatchStatus.DirectoryIds
                                        | None ->
                                            t1.StartTask()
                                            let! differences = scanForDifferences previousGraceStatus
                                            t1.Value <- 100.0

                                            t2.StartTask()
                                            let! (updatedGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                                            newGraceStatus <- updatedGraceStatus
                                            rootDirectoryId <- newGraceStatus.RootDirectoryId
                                            rootDirectorySha256Hash <- newGraceStatus.RootDirectorySha256Hash
                                            previousDirectoryIds <- newGraceStatus.Index.Keys.ToHashSet()
                                            t2.Value <- 100.0

                                            t3.StartTask()
                                            let updatedRelativePaths = 
                                                differences.Select(fun difference ->
                                                    match difference.DifferenceType with
                                                    | Add -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                                    | Change -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                                    | Delete -> None)
                                                    .Where(fun relativePathOption -> relativePathOption.IsSome)
                                                    .Select(fun relativePath -> relativePath.Value)

                                            let newFileVersions = updatedRelativePaths.Select(fun relativePath -> 
                                                newDirectoryVersions.First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath)).Files.First(fun file -> file.RelativePath = relativePath))

                                            let! uploadResult = uploadFilesToObjectStorage newFileVersions (getCorrelationId parseResult)
                                            t3.Value <- 100.0

                                            t4.StartTask()
                                            let saveParameters = SaveDirectoryVersionsParameters()
                                            saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()
                                            let! uploadDirectoryVersions = Directory.SaveDirectoryVersions saveParameters
                                            t4.Value <- 100.0

                                            t5.StartTask()
                                            let! saveResult = createSaveReference newGraceStatus.Index[rootDirectoryId] $"Save created during branch switch." (getCorrelationId parseResult)
                                            match saveResult with 
                                            | Ok returnValue  -> ()
                                            | Error error ->
                                                logToAnsiConsole Colors.Error $"{error}"
                                            t5.Value <- 100.0
                                    
                                        t6.StartTask()
                                        // If we have a branch name or ID to switch to, cool, let's do it.
                                        //logToAnsiConsole Colors.Verbose $"parameters.BranchName: {parameters.BranchName}; parameters.BranchId: {parameters.BranchId}."
                                    
                                        if (not <| String.IsNullOrEmpty(parameters.BranchId) && parseResult.FindResultFor(Options.branchId).IsImplicit = false)
                                            || (not <| String.IsNullOrEmpty(parameters.BranchName) && parseResult.FindResultFor(Options.branchName).IsImplicit = false) then
                                            // First, let's make sure we're not switching to the current branch, which is a no-op.
                                            //if not <| ((parseResult.FindResultFor(Options.branchId).IsImplicit = false && parameters.BranchId = Current().BranchId.ToString())
                                            //    || (parseResult.FindResultFor(Options.branchName).IsImplicit = false && parameters.BranchName = Current().BranchName.ToString())) then
                                            //if not <| parseResult.FindResultFor(Options.branchId).IsImplicit 
                                                // Get branch information based on Id and name.
                                            //logToAnsiConsole Colors.Verbose "Get branch information based on Id and name."
                                            let getParameters = Parameters.Branch.GetBranchParameters( 
                                                OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                                CorrelationId = parameters.CorrelationId)
                                            if not <| parseResult.FindResultFor(Options.branchId).IsImplicit then getParameters.BranchId <- parameters.BranchId
                                            if not <| parseResult.FindResultFor(Options.branchName).IsImplicit then getParameters.BranchName <- parameters.BranchName
                                            let! branchGetResult = Branch.Get(getParameters)
                                            match branchGetResult with 
                                            | Ok returnValue ->
                                                //logToAnsiConsole Colors.Verbose "Found branch information."
                                                let branchDto = returnValue.ReturnValue
                                                let getVersionParameters = 
                                                    Parameters.Branch.GetBranchVersionParameters(BranchId = $"{branchDto.BranchId}",
                                                        OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                        OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                        RepositoryId = $"{branchDto.RepositoryId}",
                                                        ReferenceId = parameters.ReferenceId, Sha256Hash = parameters.Sha256Hash, CorrelationId = parameters.CorrelationId)
                                                //logToAnsiConsole Colors.Verbose "Calling GetVersion."
                                                let! result = Branch.GetVersion getVersionParameters
                                                t6.Value <- 100.0

                                                t7.StartTask()
                                                match result with
                                                | Ok returnValue ->
                                                    //logToAnsiConsole Colors.Verbose "Succeeded calling Branch.GetVersion."
                                                    let directoryIds = returnValue.ReturnValue
                                                    let missingDirectoryIds = directoryIds.Where(fun directoryId -> not <| previousDirectoryIds.Contains(directoryId)).ToList()
                                        
                                                    // Get missing directory versions from server.
                                                    let getByDirectoryIdParameters = Parameters.Directory.GetByDirectoryIdsParameters(RepositoryId = parameters.RepositoryId, DirectoryId = $"{rootDirectoryId}", DirectoryIds = missingDirectoryIds)
                                                    match! Directory.GetByDirectoryIds getByDirectoryIdParameters with
                                                    | Ok returnValue ->
                                                        //logToAnsiConsole Colors.Verbose $"Succeeded calling Directory.GetByDirectoryIds."
                                                        // Create a new version of GraceStatus that includes the new DirectoryVersions.
                                                        let newDirectoryVersions = returnValue.ReturnValue
                                                        let newerGraceStatus = updateGraceStatusWithNewDirectoryVersions newGraceStatus newDirectoryVersions
                                                        //logToAnsiConsole Colors.Verbose $"Succeeded calling updateGraceStatusWithNewDirectoryVersions."

                                                        // Identify files that we don't already have in object cache and download them.
                                                        for directoryVersion in newerGraceStatus.Index.Values do
                                                            match! downloadFilesFromObjectStorage directoryVersion.Files (getCorrelationId parseResult) with
                                                            | Ok _ -> 
                                                                //logToAnsiConsole Colors.Verbose $"Succeeded downloading files from object storage for {directoryVersion.RelativePath}."
                                                                ()
                                                            | Error error -> 
                                                                logToAnsiConsole Colors.Verbose $"Failed downloading files from object storage for {directoryVersion.RelativePath}."
                                                                logToAnsiConsole Colors.Error $"{error}"

                                                        // Write the UpdatesInProgress file to let grace watch know to ignore these changes.
                                                        // This file is deleted in the finally clause.
                                                        do! File.WriteAllTextAsync(getUpdateInProgressFileName(), "This file won't exist for long.")

                                                        // Update working directory based on new GraceStatus.Index
                                                        updateWorkingDirectory newGraceStatus newerGraceStatus newDirectoryVersions
                                                        //logToAnsiConsole Colors.Verbose $"Succeeded calling updateWorkingDirectory."

                                                        // Save the new Grace Status.
                                                        do! writeGraceStatusFile newerGraceStatus

                                                        // Update graceconfig.json.
                                                        let configuration = Current()
                                                        configuration.BranchId <- branchDto.BranchId
                                                        configuration.BranchName <- branchDto.BranchName
                                                        updateConfiguration configuration
                                                        
                                                        File.Delete(getUpdateInProgressFileName())
                                                    | Error error -> 
                                                        logToAnsiConsole Colors.Verbose $"Failed calling Directory.GetByDirectoryIds."
                                                        logToAnsiConsole Colors.Error $"{error}"
                                                | Error error ->
                                                    logToAnsiConsole Colors.Verbose "Failed calling GetVersion."
                                                    logToAnsiConsole Colors.Error $"{error}"
                                                t7.Value <- 100
                                            | Error error -> logToAnsiConsole Colors.Error $"{error}"
                                    
                                            return 0
                                            //else
                                            //    logToAnsiConsole Colors.Highlighted $"The specified branch is already the current branch."
                                            //    t5.StopTask()
                                            //    t6.StopTask()
                                            //    return -1
                                        else
                                            logToAnsiConsole Colors.Error (BranchError.getErrorMessage BranchError.EitherBranchIdOrBranchNameRequired)
                                            t6.StopTask()
                                            t7.StopTask()
                                            return -1
                                    })
                        else
                            let! previousGraceStatus = readGraceStatusFile()
                            let mutable newGraceStatus = previousGraceStatus

                            match! getGraceWatchStatus() with
                            | Some graceWatchStatus ->
                                rootDirectoryId <- graceWatchStatus.RootDirectoryId
                                rootDirectorySha256Hash <- graceWatchStatus.RootDirectorySha256Hash
                                previousDirectoryIds <- graceWatchStatus.DirectoryIds
                            | None ->
                                let! differences = scanForDifferences previousGraceStatus

                                let! (updatedGraceStatus, newDirectoryVersions) = getNewGraceStatusAndDirectoryVersions previousGraceStatus differences
                                newGraceStatus <- updatedGraceStatus
                                rootDirectoryId <- newGraceStatus.RootDirectoryId
                                rootDirectorySha256Hash <- newGraceStatus.RootDirectorySha256Hash
                                previousDirectoryIds <- newGraceStatus.Index.Keys.ToHashSet()

                                let updatedRelativePaths = 
                                    differences.Select(fun difference ->
                                        match difference.DifferenceType with
                                        | Add -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                        | Change -> match difference.FileSystemEntryType with | FileSystemEntryType.File -> Some difference.RelativePath | FileSystemEntryType.Directory -> None
                                        | Delete -> None)
                                        .Where(fun relativePathOption -> relativePathOption.IsSome)
                                        .Select(fun relativePath -> relativePath.Value)

                                let newFileVersions = updatedRelativePaths.Select(fun relativePath -> 
                                    newDirectoryVersions.First(fun dv -> dv.Files.Exists(fun file -> file.RelativePath = relativePath)).Files.First(fun file -> file.RelativePath = relativePath))

                                let! uploadResult = uploadFilesToObjectStorage newFileVersions (getCorrelationId parseResult)

                                let saveParameters = SaveDirectoryVersionsParameters()
                                saveParameters.DirectoryVersions <- newDirectoryVersions.Select(fun dv -> dv.ToDirectoryVersion).ToList()
                                let! uploadDirectoryVersions = Directory.SaveDirectoryVersions saveParameters

                                let! saveResult = createSaveReference newGraceStatus.Index[rootDirectoryId] $"Save created during branch switch." (getCorrelationId parseResult)
                                match saveResult with 
                                | Ok returnValue  -> ()
                                | Error error ->
                                    logToAnsiConsole Colors.Error $"{error}"
                       
                            // If we have a branch name or ID to switch to, cool, let's do it.
                            //logToAnsiConsole Colors.Verbose $"parameters.BranchName: {parameters.BranchName}; parameters.BranchId: {parameters.BranchId}."
                        
                            if (not <| String.IsNullOrEmpty(parameters.BranchId) && parseResult.FindResultFor(Options.branchId).IsImplicit = false)
                                || (not <| String.IsNullOrEmpty(parameters.BranchName) && parseResult.FindResultFor(Options.branchName).IsImplicit = false) then
                                // First, let's make sure we're not switching to the current branch, which is a no-op.
                                //if not <| ((parseResult.FindResultFor(Options.branchId).IsImplicit = false && parameters.BranchId = Current().BranchId.ToString())
                                //    || (parseResult.FindResultFor(Options.branchName).IsImplicit = false && parameters.BranchName = Current().BranchName.ToString())) then
                                //if not <| parseResult.FindResultFor(Options.branchId).IsImplicit 
                                    // Get branch information based on Id and name.
                                //logToAnsiConsole Colors.Verbose "Get branch information based on Id and name."
                                let getParameters = Parameters.Branch.GetBranchParameters( 
                                    OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                    OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                    RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                    CorrelationId = parameters.CorrelationId)
                                if not <| parseResult.FindResultFor(Options.branchId).IsImplicit then getParameters.BranchId <- parameters.BranchId
                                if not <| parseResult.FindResultFor(Options.branchName).IsImplicit then getParameters.BranchName <- parameters.BranchName
                                let! branchGetResult = Branch.Get(getParameters)
                                match branchGetResult with 
                                | Ok returnValue ->
                                    //logToAnsiConsole Colors.Verbose "Found branch information."
                                    let branchDto = returnValue.ReturnValue
                                    let getVersionParameters = 
                                        Parameters.Branch.GetBranchVersionParameters(BranchId = $"{branchDto.BranchId}",
                                            OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                            OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                            RepositoryId = $"{branchDto.RepositoryId}",
                                            ReferenceId = parameters.ReferenceId, Sha256Hash = parameters.Sha256Hash, CorrelationId = parameters.CorrelationId)
                                    //logToAnsiConsole Colors.Verbose "Calling GetVersion."
                                    let! result = Branch.GetVersion getVersionParameters

                                    match result with
                                    | Ok returnValue ->
                                        //logToAnsiConsole Colors.Verbose "Succeeded calling Branch.GetVersion."
                                        let directoryIds = returnValue.ReturnValue
                                        let missingDirectoryIds = directoryIds.Where(fun directoryId -> not <| previousDirectoryIds.Contains(directoryId)).ToList()
                            
                                        // Get missing directory versions from server.
                                        let getByDirectoryIdParameters = Parameters.Directory.GetByDirectoryIdsParameters(RepositoryId = parameters.RepositoryId, DirectoryId = $"{rootDirectoryId}", DirectoryIds = missingDirectoryIds)
                                        match! Directory.GetByDirectoryIds getByDirectoryIdParameters with
                                        | Ok returnValue ->
                                            //logToAnsiConsole Colors.Verbose $"Succeeded calling Directory.GetByDirectoryIds."
                                            // Create a new version of GraceStatus that includes the new DirectoryVersions.
                                            let newDirectoryVersions = returnValue.ReturnValue
                                            let newerGraceStatus = updateGraceStatusWithNewDirectoryVersions newGraceStatus newDirectoryVersions
                                            //logToAnsiConsole Colors.Verbose $"Succeeded calling updateGraceStatusWithNewDirectoryVersions."

                                            // Identify files that we don't already have in object cache and download them.
                                            for directoryVersion in newerGraceStatus.Index.Values do
                                                match! downloadFilesFromObjectStorage directoryVersion.Files (getCorrelationId parseResult) with
                                                | Ok _ -> 
                                                    //logToAnsiConsole Colors.Verbose $"Succeeded downloading files from object storage for {directoryVersion.RelativePath}."
                                                    ()
                                                | Error error -> 
                                                    logToAnsiConsole Colors.Verbose $"Failed downloading files from object storage for {directoryVersion.RelativePath}."
                                                    logToAnsiConsole Colors.Error $"{error}"

                                            // Update working directory based on new GraceStatus.Index
                                            updateWorkingDirectory newGraceStatus newerGraceStatus newDirectoryVersions
                                            //logToAnsiConsole Colors.Verbose $"Succeeded calling updateWorkingDirectory."

                                            // Save the new Grace Status.
                                            do! writeGraceStatusFile newerGraceStatus

                                            // Update graceconfig.json.
                                            let configuration = Current()
                                            configuration.BranchId <- branchDto.BranchId
                                            configuration.BranchName <- branchDto.BranchName
                                            updateConfiguration configuration
                                        | Error error -> 
                                            logToAnsiConsole Colors.Verbose $"Failed calling Directory.GetByDirectoryIds."
                                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                    | Error error ->
                                        logToAnsiConsole Colors.Verbose "Failed calling GetVersion."
                                        logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                | Error error -> logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                        
                                return 0
                            else
                                logToAnsiConsole Colors.Highlighted $"Not implemented yet."
                                return -1
                    | Error error -> return -1
                with
                    | ex -> return -1
            finally
                File.Delete(getUpdateInProgressFileName())
        }
    let private Switch =
        CommandHandler.Create(fun (parseResult: ParseResult) (switchParameters: SwitchParameters) ->
            task {
                let! result = switchHandler parseResult switchParameters
                return result
            })

    type RebaseParameters() =
        inherit CommonParameters()
        member val public BasedOn: ReferenceId = ReferenceId.Empty with get, set
    let rebaseHandler parseResult (parameters: RebaseParameters) (graceStatus: GraceStatus) =
        task {
            // --------------------------------------------------------------------------------------------------------------------------------------
            // Get a diff between the promotion from the parent branch that the current branch is based on, and the latest promotion from the parent branch.
            //   These are the changes that we expect to apply to the current branch.

            // Get a diff between the latest reference on this branch and the promotion that it's based on from the parent branch.
            //   This will be what's changed in the current branch since it was last rebased.
            
            // If a file has changed in the first diff, but not in the second diff, cool, we can automatically copy them.
            // If a file has changed in the second diff, but not in the first diff, cool, we can keep those changes.
            // If a file has changed in both, we have to check the two diffs at the line-level to see if there are any conflicts.

            // Then we call Branch.Rebase() to actually record the update.
            // --------------------------------------------------------------------------------------------------------------------------------------
            
            // First, get the current branchDto so we have the latest promotion that it's based on.
            let branchGetParameters = Parameters.Branch.GetBranchParameters(OwnerId = $"{Current().OwnerId}", 
                OrganizationId = $"{Current().OrganizationId}", RepositoryId = $"{Current().RepositoryId}", BranchId = $"{Current().BranchId}",
                CorrelationId = parameters.CorrelationId)
            match! Branch.Get(branchGetParameters) with
            | Ok returnValue ->
                let branchDto = returnValue.ReturnValue
                
                // Now, get the parent branch information so we have its latest promotion.
                match! Branch.GetParentBranch(branchGetParameters) with
                | Ok returnValue ->
                    let parentBranchDto = returnValue.ReturnValue

                    if branchDto.BasedOn = parentBranchDto.LatestPromotion then
                        printfn $"The current branch is already based on the latest promotion."
                        return 0
                    else
                        // Now, get ReferenceDtos for current.BasedOn and parent.LatestPromotion so we have their DirectoryId's.
                        let getReferencesByReferenceIdParameters = Parameters.Repository.GetReferencesByReferenceIdParameters(OwnerId = $"{Current().OwnerId}", 
                            OrganizationId = $"{Current().OrganizationId}", RepositoryId = $"{Current().RepositoryId}", CorrelationId = parameters.CorrelationId,
                            ReferenceIds = [| branchDto.BasedOn; branchDto.LatestCommit; parentBranchDto.LatestPromotion |])
                        match! Repository.GetReferencesByReferenceId(getReferencesByReferenceIdParameters) with
                        | Ok returnValue ->
                            let referenceDtos = returnValue.ReturnValue
                            let latestCommit = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = branchDto.LatestCommit), ReferenceDto.Default)
                            let parentLatestPromotion = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = parentBranchDto.LatestPromotion), ReferenceDto.Default)
                            let basedOn = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = branchDto.BasedOn), ReferenceDto.Default)

                            // Get the latest reference from the current branch.
                            let getReferencesParameters = Parameters.Branch.GetReferencesParameters(OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                BranchId = $"{branchDto.BranchId}", MaxCount = 1, CorrelationId = parameters.CorrelationId)
                            //logToAnsiConsole Colors.Verbose $"getReferencesParameters: {getReferencesParameters |> serialize)}"
                            match! Branch.GetReferences(getReferencesParameters) with
                            | Ok returnValue ->
                                let latestReference = if returnValue.ReturnValue.Count() > 0 then returnValue.ReturnValue.First() else ReferenceDto.Default
                                logToAnsiConsole Colors.Verbose $"latestReference: {serialize latestReference}"
                                // Now we have all of the references we need, so we have DirectoryId's to do diffs with.

                                let! (diffs, errors) =
                                    task {
                                        if basedOn.DirectoryId <> DirectoryId.Empty then
                                            // First diff: parent promotion that current branch is based on vs. parent's latest promotion.
                                            let diffParameters = Parameters.Diff.GetDiffParameters(DirectoryId1 = basedOn.DirectoryId, DirectoryId2 = parentLatestPromotion.DirectoryId, CorrelationId = parameters.CorrelationId)
                                            logToAnsiConsole Colors.Verbose $"First diff: {Markup.Escape(serialize diffParameters)}"
                                            let! firstDiff = Diff.GetDiff(diffParameters)

                                            match firstDiff with
                                            | Ok returnValue ->
                                                let diff = returnValue.ReturnValue
                                                logToAnsiConsole Colors.Verbose $"diff: {Markup.Escape(serialize diff)}"
                                            | Error error -> logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))

                                            // Second diff: latest reference on current branch vs. parent promotion that current branch is based on.
                                            let diffParameters = Parameters.Diff.GetDiffParameters(DirectoryId1 = latestReference.DirectoryId, DirectoryId2 = basedOn.DirectoryId, CorrelationId = parameters.CorrelationId)
                                            logToAnsiConsole Colors.Verbose $"Second diff: {Markup.Escape(serialize diffParameters)}"
                                            let! secondDiff = Diff.GetDiff(diffParameters)

                                            let returnValue = Result.partition [firstDiff; secondDiff]
                                            return returnValue
                                        else
                                            // This should only happen when first creating a repository, when main has no promotions.
                                            // Only one diff possible: latest reference on current branch vs. parent's latest promotion.
                                            let diffParameters = Parameters.Diff.GetDiffParameters(DirectoryId1 = latestReference.DirectoryId, DirectoryId2 = parentLatestPromotion.DirectoryId, CorrelationId = parameters.CorrelationId)
                                            logToAnsiConsole Colors.Verbose $"Initial diff: {Markup.Escape(serialize diffParameters)}"
                                            let! diff = Diff.GetDiff(diffParameters)
                                            let returnValue = Result.partition [diff]
                                            return returnValue
                                     }
                                
                                // So, right now, if repo just created, and BasedOn is empty, we'll have a single diff.
                                // That fails a few lines below here.
                                // Have to decide what to do in this case.

                                if errors.Count() = 0 then
                                    // Yay! We have our two diffs.
                                    let diff1 = diffs[0].ReturnValue
                                    let diff2 = diffs[1].ReturnValue

                                    let filesToDownload = List<FileSystemDifference>()
                                    
                                    // Identify which files have been changed in the first diff, but not in the second diff.
                                    //   We can just download and copy these files into place in the working directory.
                                    for fileDifference in diff1.Differences do
                                        if not <| diff2.Differences.Any(fun d -> d.RelativePath = fileDifference.RelativePath) then
                                            // Copy different file version into place - similar to how we do it for switch
                                            filesToDownload.Add(fileDifference)

                                    let getParentLatestPromotionDirectoryParameters = Parameters.Directory.GetParameters(RepositoryId = $"{branchDto.RepositoryId}", 
                                        DirectoryId = $"{parentLatestPromotion.DirectoryId}", CorrelationId = parameters.CorrelationId)
                                    let getLatestReferenceDirectoryParameters = Parameters.Directory.GetParameters(RepositoryId = $"{branchDto.RepositoryId}", 
                                        DirectoryId = $"{latestReference.DirectoryId}", CorrelationId = parameters.CorrelationId)
                                    
                                    // Get the directory versions for the parent promotion that we're rebasing on, and the latest reference.
                                    let! d1 = Directory.GetDirectoryVersionsRecursive(getParentLatestPromotionDirectoryParameters)
                                    let! d2 = Directory.GetDirectoryVersionsRecursive(getLatestReferenceDirectoryParameters)
                                    
                                    let createFileVersionLookupDictionary (directoryVersions: IEnumerable<DirectoryVersion>) =
                                        let lookup = Dictionary<RelativePath, LocalFileVersion>()
                                        directoryVersions 
                                            |> Seq.map (fun dv -> dv.ToLocalDirectoryVersion (dv.CreatedAt.ToDateTimeUtc())) 
                                            |> Seq.map (fun dv -> dv.Files) 
                                            |> Seq.concat 
                                            |> Seq.iter (fun file -> lookup.Add(file.RelativePath, file))
                                        lookup
                                    
                                    let (directories, errors) = Result.partition [ d1; d2 ]
                                    if errors.Count() = 0 then
                                        let parentLatestPromotionDirectoryVersions = directories[0].ReturnValue
                                        let latestReferenceDirectoryVersions = directories[1].ReturnValue
                                        
                                        let parentLatestPromotionLookup = createFileVersionLookupDictionary parentLatestPromotionDirectoryVersions
                                        let latestReferenceLookup = createFileVersionLookupDictionary latestReferenceDirectoryVersions                                            
                                        
                                        // Get the specific FileVersions for those files from the contents of the parent's latest promotion.
                                        let fileVersionsToDownload =
                                            filesToDownload 
                                                |> Seq.where (fun fileToDownload -> parentLatestPromotionLookup.ContainsKey($"{fileToDownload.RelativePath}")) 
                                                |> Seq.map (fun fileToDownload -> parentLatestPromotionLookup[$"{fileToDownload.RelativePath}"])
                                        logToAnsiConsole Colors.Verbose $"fileVersionsToDownload: {fileVersionsToDownload.Count()}"
                                        for f in fileVersionsToDownload do
                                            logToAnsiConsole Colors.Verbose  $"relativePath: {f.RelativePath}"

                                        // Download those FileVersions from object storage, and copy them into the working directory.
                                        match! downloadFilesFromObjectStorage fileVersionsToDownload parameters.CorrelationId with
                                        | Ok _ -> 
                                            logToAnsiConsole Colors.Verbose $"Succeeded in downloadFilesFromObjectStorage."
                                            fileVersionsToDownload |> Seq.iter (fun file -> 
                                                logToAnsiConsole Colors.Verbose $"Copying {file.RelativePath} from {file.FullObjectPath} to {file.FullName}."
                                                // Delete the existing file in the working directory.
                                                File.Delete(file.FullName)
                                                // Copy the version from the object cache to the working directory.
                                                File.Copy(file.FullObjectPath, file.FullName))
                                            logToAnsiConsole Colors.Verbose $"Copied files into place."
                                        | Error error -> 
                                            AnsiConsole.WriteLine($"[{Colors.Error}]{Markup.Escape(error)}[/]")

                                        // If a file has changed in the second diff, but not in the first diff, cool, we can keep those changes, nothing to be done.
                                        
                                        // If a file has changed in both, we have to check the two diffs at the line-level to see if there are any conflicts.
                                        let mutable potentialPromotionConflicts = false
                                        for diff1Difference in diff1.Differences do
                                            let diff2DifferenceQuery = diff2.Differences.Where(fun d -> d.RelativePath = diff1Difference.RelativePath &&
                                                                                                        d.FileSystemEntryType = FileSystemEntryType.File &&
                                                                                                        d.DifferenceType = DifferenceType.Change)
                                        
                                            if diff2DifferenceQuery.Count() = 1 then
                                                // We have a file that's changed in both diffs.
                                                let diff2Difference = diff2DifferenceQuery.First()
                                                
                                                // Check the Sha256Hash values; if they're identical, ignore the file.
                                                //let fileVersion1 = parentLatestPromotionLookup[$"{diff1Difference.RelativePath}"]
                                                let fileVersion1 = parentLatestPromotionLookup.FirstOrDefault(fun kvp -> kvp.Key = $"{diff1Difference.RelativePath}")
                                                //let fileVersion2 = latestReferenceLookup[$"{diff2Difference.RelativePath}"]
                                                let fileVersion2 = latestReferenceLookup.FirstOrDefault(fun kvp -> kvp.Key = $"{diff2Difference.RelativePath}")
                                                //if (not <| isNull(fileVersion1) && not <| isNull(fileVersion2)) && (fileVersion1.Value.Sha256Hash <> fileVersion2.Value.Sha256Hash) then
                                                if (fileVersion1.Value.Sha256Hash <> fileVersion2.Value.Sha256Hash) then
                                                    // Compare them at a line level; if there are no overlapping lines, we can just modify the working-directory version.
                                                    // ...
                                                    // For now, we're just going to show a message.
                                                    AnsiConsole.MarkupLine($"[{Colors.Important}]Potential promotion conflict: file {diff1Difference.RelativePath} has been changed in both the latest promotion, and in the current branch.[/]")
                                                    AnsiConsole.MarkupLine($"[{Colors.Important}]fileVersion1.Sha256Hash: {fileVersion1.Value.Sha256Hash}; fileVersion1.LastWriteTimeUTC: {fileVersion1.Value.LastWriteTimeUtc}.[/]")
                                                    AnsiConsole.MarkupLine($"[{Colors.Important}]fileVersion2.Sha256Hash: {fileVersion2.Value.Sha256Hash}; fileVersion2.LastWriteTimeUTC: {fileVersion2.Value.LastWriteTimeUtc}.[/]")
                                                    potentialPromotionConflicts <- true

                                        if not <| potentialPromotionConflicts then
                                            // Yay! No promotion conflicts.
                                            let rebaseParameters = Parameters.Branch.RebaseParameters(BranchId = $"{branchDto.BranchId}", RepositoryId = $"{branchDto.RepositoryId}",
                                                OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                                OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                                BasedOn = parentLatestPromotion.ReferenceId)
                                            match! Branch.Rebase(rebaseParameters) with
                                            | Ok returnValue ->
                                                AnsiConsole.MarkupLine($"[{Colors.Important}]Rebase succeeded.[/]")
                                                AnsiConsole.MarkupLine($"[{Colors.Verbose}]({serialize returnValue})[/]")
                                                return 0
                                            | Error error ->
                                                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                                return -1
                                        else 
                                            AnsiConsole.MarkupLine($"[{Colors.Highlighted}]A potential promotion conflict was detected. Rebase not successful.[/]")
                                            return -1
                                    else
                                        logToAnsiConsole Colors.Error (Markup.Escape($"{errors.First()}"))
                                        return -1
                                else
                                    logToAnsiConsole Colors.Error (Markup.Escape($"{errors.First()}"))
                                    return -1
                            | Error error ->
                                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                return -1
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return -1
                | Error error ->
                    logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                    return -1
            | Error error ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                return -1
        }

    let private Rebase =
        CommandHandler.Create(fun (parseResult: ParseResult) (rebaseParameters: RebaseParameters) ->
            task {
                let! graceStatus = readGraceStatusFile()
                let! result = rebaseHandler parseResult (rebaseParameters |> normalizeIdsAndNames parseResult) graceStatus
                return result
            })

    type StatusParameters() =
        inherit CommonParameters()
    let private newStatusHandler parseResult (parameters: StatusParameters) =
        task {
            try
                return 0
            with ex ->
                logToAnsiConsole Colors.Error (Markup.Escape($"{ex.Message}"))
                return -1
        }

    let private statusHandler parseResult (parameters: StatusParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                // Show repo and branch names.
                let getParameters = Parameters.Branch.GetBranchParameters( 
                    OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                    OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                    RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                    BranchId = $"{Current().BranchId}", CorrelationId = parameters.CorrelationId)
                match! Branch.Get(getParameters) with
                | Ok returnValue -> 
                    let branchDto = returnValue.ReturnValue
                    match! Branch.GetParentBranch(getParameters) with
                    | Ok returnValue ->
                        let parentBranchDto = returnValue.ReturnValue

                        // Now that I have the current and parent branch, I can get the details for the latest promotion, latest commit, latest checkpoint, and latest save.
                        let referenceIds = [| parentBranchDto.LatestPromotion; branchDto.LatestCommit; branchDto.LatestCheckpoint; branchDto.LatestSave; branchDto.BasedOn |]
                        let getReferencesByIdParameters = Parameters.Repository.GetReferencesByReferenceIdParameters(
                            OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                            OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                            RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                            ReferenceIds = referenceIds)
                        match! Repository.GetReferencesByReferenceId(getReferencesByIdParameters) with
                        | Ok returnValue -> 
                            let referenceDtos = returnValue.ReturnValue
                            let latestSave = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = branchDto.LatestSave), ReferenceDto.Default)
                            let latestCheckpoint = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = branchDto.LatestCheckpoint), ReferenceDto.Default)
                            let latestCommit = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = branchDto.LatestCommit), ReferenceDto.Default)
                            let latestParentBranchPromotion = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = parentBranchDto.LatestPromotion), ReferenceDto.Default)
                            let basedOn = referenceDtos.FirstOrDefault((fun ref -> ref.ReferenceId = branchDto.BasedOn), ReferenceDto.Default)
                            
                            let getReferencesParameters = Parameters.Branch.GetReferencesParameters(OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                                OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                                RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                                BranchId = $"{branchDto.BranchId}", MaxCount = 1, CorrelationId = parameters.CorrelationId)
                            match! Branch.GetReferences(getReferencesParameters) with
                            | Ok returnValue ->
                                let latestReference =
                                    if returnValue.ReturnValue.Count() > 0 then returnValue.ReturnValue.First() else ReferenceDto.Default
                                    
                                let getReferenceRowValue referenceDto =
                                    if referenceDto.ReferenceId = ReferenceId.Empty then
                                        $"  None"
                                    else
                                        if parseResult |> verbose then
                                            $"  {getShortenedSha256Hash referenceDto.Sha256Hash} - {ago referenceDto.CreatedAt} - {instantToLocalTime referenceDto.CreatedAt} [{Colors.Deemphasized}]- {referenceDto.ReferenceId} - {referenceDto.DirectoryId}[/]"
                                        else
                                            $"  {getShortenedSha256Hash referenceDto.Sha256Hash} - {ago referenceDto.CreatedAt} - {instantToLocalTime referenceDto.CreatedAt} [{Colors.Deemphasized}]- {referenceDto.ReferenceId}[/]"

                                let table = Table(Border = TableBorder.DoubleEdge)
                                table.AddColumns(String.replicate (Current().OwnerName.Length) "_", String.replicate (Current().OwnerName.Length) "_")  // Using Current().OwnerName.Length is aesthetically pleasing, there's no deeper reason for it.
                                     .AddRow($"[{Colors.Important}]Owner[/]", $"[{Colors.Important}]{Current().OwnerName}[/] [{Colors.Deemphasized}]- {Current().OwnerId}[/]")
                                     .AddRow($"[{Colors.Important}]Organization[/]", $"[{Colors.Important}]{Current().OrganizationName}[/] [{Colors.Deemphasized}]- {Current().OrganizationId}[/]")
                                     .AddRow($"[{Colors.Important}]Repository[/]", $"[{Colors.Important}]{Current().RepositoryName}[/] [{Colors.Deemphasized}]- {Current().RepositoryId}[/]")
                                     .AddRow(String.Empty, String.Empty)
                                     .AddRow($"[{Colors.Important}]Branch[/]", $"[{Colors.Important}]{branchDto.BranchName}[/] [{Colors.Deemphasized}]- {branchDto.BranchId}[/]")
                                     .AddRow($"  - latest save ", getReferenceRowValue latestSave)
                                     .AddRow($"  - latest checkpoint ", getReferenceRowValue latestCheckpoint)
                                     .AddRow($"  - latest commit", getReferenceRowValue latestCommit)
                                     .AddRow($"  - based on", getReferenceRowValue basedOn)
                                     .AddRow(String.Empty, String.Empty)
                                    |> ignore
                                
                                if branchDto.ParentBranchId <> Constants.DefaultParentBranchId then
                                     table.AddRow($"[{Colors.Important}]Parent branch[/]", $"[{Colors.Important}]{parentBranchDto.BranchName}[/] [{Colors.Deemphasized}]- {parentBranchDto.BranchId}[/]")
                                          .AddRow($"  - latest promotion", getReferenceRowValue latestParentBranchPromotion)
                                          .AddRow($"", $"  {latestParentBranchPromotion.ReferenceText}")
                                        |> ignore
                                else
                                    table.AddRow($"[{Colors.Important}]Parent branch[/]", $"[{Colors.Important}]  None[/]") |> ignore
                                
                                if branchDto.BasedOn = parentBranchDto.LatestPromotion then 
                                    table.AddRow($"", $"[{Colors.Added}]  Based on latest promotion.[/]")
                                        |> ignore
                                else
                                    table.AddRow($"", $"[{Colors.Important}]  Not based on latest promotion.[/]")
                                        |> ignore

                                // Need to add this portion of the header after everything else is rendered so we know the width.
                                //let headerWidth = if table.Columns[0].Width.HasValue then table.Columns[0].Width.Value else 27  // 27 = longest current text
                                //table.Columns[0].Header <- Markup(String.replicate headerWidth "_")
                                AnsiConsole.Write(table)
                                return 0
                            | Error error ->
                                logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                                return -1
                        | Error error ->
                            logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                            return -1
                    | Error error ->
                        logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                        return -1
                | Error error ->
                    logToAnsiConsole Colors.Error (Markup.Escape($"{error}"))
                    return -1
            with ex -> 
                logToAnsiConsole Colors.Error (Markup.Escape($"{createExceptionResponse ex}"))
                return -1
        }
    let private Status =
        CommandHandler.Create(fun (parseResult: ParseResult) (statusParameters: StatusParameters) ->
            task {
                let! result = statusHandler parseResult (statusParameters |> normalizeIdsAndNames parseResult)
                return result
            })
    
    type DeleteParameters() = 
        inherit CommonParameters()
    let private deleteHandler (parseResult: ParseResult) (parameters: DeleteParameters) =
        task {
            try
                if parseResult |> verbose then printParseResult parseResult
                let validateIncomingParameters = CommonValidations parseResult parameters
                match validateIncomingParameters with
                | Ok _ ->
                    let parameters = parameters |> normalizeIdsAndNames parseResult
                    let deleteParameters = Parameters.Branch.DeleteBranchParameters(BranchId = parameters.BranchId, BranchName = parameters.BranchName, 
                        OwnerId = parameters.OwnerId, OwnerName = parameters.OwnerName,
                        OrganizationId = parameters.OrganizationId, OrganizationName = parameters.OrganizationName,
                        RepositoryId = parameters.RepositoryId, RepositoryName = parameters.RepositoryName,
                        CorrelationId = parameters.CorrelationId)
                    if parseResult |> showOutput then
                        return! progress.Columns(progressColumns)
                                .StartAsync(fun progressContext ->
                                task {
                                    let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
                                    let! result = Branch.Delete(deleteParameters)
                                    t0.Increment(100.0)
                                    return result
                                })
                    else
                        return! Branch.Delete(deleteParameters)
                | Error error -> return Error error
            with
                | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
        }
    let private Delete =
        CommandHandler.Create(fun (parseResult: ParseResult) (deleteParameters: DeleteParameters) ->
            task {                
                let! result = deleteHandler parseResult deleteParameters
                return result |> renderOutput parseResult
            })

    //type UndeleteParameters() = 
    //    inherit CommonParameters()
    //let private undeleteHandler (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) =
    //    task {
    //        try
    //            if parseResult |> verbose then printParseResult parseResult
    //            let validateIncomingParameters = CommonValidations parseResult undeleteParameters
    //            match validateIncomingParameters with
    //            | Ok _ -> 
    //                let parameters = Parameters.Owner.UndeleteParameters(OwnerId = undeleteParameters.OwnerId, OwnerName = undeleteParameters.OwnerName, CorrelationId = undeleteParameters.CorrelationId)
    //                if parseResult |> showOutput then
    //                    return! progress.Columns(progressColumns)
    //                            .StartAsync(fun progressContext ->
    //                            task {
    //                                let t0 = progressContext.AddTask($"[{Color.DodgerBlue1}]Sending command to the server.[/]")
    //                                let! result = Owner.Undelete(parameters)
    //                                t0.Increment(100.0)
    //                                return result
    //                            })
    //                else
    //                    return! Owner.Undelete(parameters)
    //            | Error error -> return Error error
    //        with
    //            | ex -> return Error (GraceError.Create $"{Utilities.createExceptionResponse ex}" (parseResult |> getCorrelationId))
    //    }
    //let private Undelete =
    //    CommandHandler.Create(fun (parseResult: ParseResult) (undeleteParameters: UndeleteParameters) ->
    //        task {                
    //            let! result = undeleteHandler parseResult undeleteParameters
    //            return result |> renderOutput parseResult
    //        })

    let Build = 
        let addCommonOptions (command: Command) =
            command |> addOption Options.ownerName
                    |> addOption Options.ownerId
                    |> addOption Options.organizationName
                    |> addOption Options.organizationId
                    |> addOption Options.repositoryName
                    |> addOption Options.repositoryId
                    |> addOption Options.branchName 
                    |> addOption Options.branchId 
                    
        // Create main command and aliases, if any.`
        let branchCommand = new Command("branch", Description = "Create, change, or delete branch-level information.")
        branchCommand.AddAlias("br")

        // Add subcommands.
        let branchCreateCommand = new Command("create", Description = "Create a new branch.")
                                    |> addOption Options.branchNameRequired |> addOption Options.branchId 
                                    |> addOption Options.parentBranchName |> addOption Options.parentBranchId 
                                    |> addOption Options.ownerName |> addOption Options.ownerId
                                    |> addOption Options.organizationName |> addOption Options.organizationId
                                    |> addOption Options.repositoryName |> addOption Options.repositoryId
                                    |> addOption Options.doNotSwitch
        branchCreateCommand.Handler <- Create
        branchCommand.AddCommand(branchCreateCommand)

        let switchCommand = new Command("switch", Description = "Switches your current branch to another branch or specific reference.") |> addOption Options.sha256Hash |> addOption Options.referenceId |> addCommonOptions
        switchCommand.Handler <- Switch
        branchCommand.AddCommand(switchCommand)

        let statusCommand = new Command("status", Description = "Displays status information about the current repository and branch.") |> addOption Options.referenceId |> addCommonOptions
        statusCommand.Handler <- Status
        branchCommand.AddCommand(statusCommand)

        let promoteCommand = new Command("promote", Description = "Promotes a commit into the parent branch.") |> addOption Options.message |> addCommonOptions
        promoteCommand.Handler <- Promote
        branchCommand.AddCommand(promoteCommand)

        let commitCommand = new Command("commit", Description = "Create a commit.") |> addOption Options.messageRequired |> addCommonOptions
        commitCommand.Handler <- Commit
        branchCommand.AddCommand(commitCommand)
        
        let checkpointCommand = new Command("checkpoint", Description = "Create a checkpoint.") |> addOption Options.message |> addCommonOptions
        checkpointCommand.Handler <- Checkpoint
        branchCommand.AddCommand(checkpointCommand)
        
        let saveCommand = new Command("save", Description = "Create a save.") |> addOption Options.message |> addCommonOptions
        saveCommand.Handler <- Save
        branchCommand.AddCommand(saveCommand)
        
        let tagCommand = new Command("tag", Description = "Create a tag.") |> addOption Options.messageRequired |> addCommonOptions
        tagCommand.Handler <- Tag
        branchCommand.AddCommand(tagCommand)

        let rebaseCommand = new Command("rebase", Description = "Rebase this branch on a promotion from the parent branch.") |> addCommonOptions
        rebaseCommand.Handler <- Rebase
        branchCommand.AddCommand(rebaseCommand)

        let enablePromotionCommand = new Command("enable-promotion", Description = "Enable or disable promotions on this branch.") |> addOption Options.enabled |> addCommonOptions
        enablePromotionCommand.Handler <- EnablePromotion
        branchCommand.AddCommand(enablePromotionCommand)

        let enableCommitCommand = new Command("enable-commit", Description = "Enable or disable commits on this branch.") |> addOption Options.enabled |> addCommonOptions
        enableCommitCommand.Handler <- EnableCommit
        branchCommand.AddCommand(enableCommitCommand)

        let enableCheckpointsCommand = new Command("enable-checkpoints", Description = "Enable or disable checkpoints on this branch.") |> addOption Options.enabled |> addCommonOptions
        enableCheckpointsCommand.Handler <- EnableCheckpoint
        branchCommand.AddCommand(enableCheckpointsCommand)

        let enableSaveCommand = new Command("enable-save", Description = "Enable or disable saves on this branch.") |> addOption Options.enabled |> addCommonOptions
        enableSaveCommand.Handler <- EnableSave
        branchCommand.AddCommand(enableSaveCommand)

        let enableTagCommand = new Command("enable-tag", Description = "Enable or disable tags on this branch.") |> addOption Options.enabled |> addCommonOptions
        enableTagCommand.Handler <- EnableTag
        branchCommand.AddCommand(enableTagCommand)

        let setNameCommand = new Command("set-name", Description = "Change the name of the branch.") |> addOption Options.newName |> addCommonOptions
        setNameCommand.Handler <- SetName
        branchCommand.AddCommand(setNameCommand)

        let getReferencesCommand = new Command("get-references", Description = "Retrieves a list of the most recent references from the branch.") |> addCommonOptions |> addOption Options.maxCount |> addOption Options.fullSha
        getReferencesCommand.Handler <- GetReferences
        branchCommand.AddCommand(getReferencesCommand)

        let getPromotionsCommand = new Command("get-promotions", Description = "Retrieves a list of the most recent promotions from the branch.") |> addCommonOptions |> addOption Options.maxCount |> addOption Options.fullSha
        getPromotionsCommand.Handler <- GetPromotions
        branchCommand.AddCommand(getPromotionsCommand)

        let getCommitsCommand = new Command("get-commits", Description = "Retrieves a list of the most recent commits from the branch.") |> addCommonOptions |> addOption Options.maxCount |> addOption Options.fullSha
        getCommitsCommand.Handler <- GetCommits
        branchCommand.AddCommand(getCommitsCommand)

        let getCheckpointsCommand = new Command("get-checkpoints", Description = "Retrieves a list of the most recent checkpoints from the branch.") |> addCommonOptions |> addOption Options.maxCount |> addOption Options.fullSha
        getCheckpointsCommand.Handler <- GetCheckpoints
        branchCommand.AddCommand(getCheckpointsCommand)

        let getSavesCommand = new Command("get-saves", Description = "Retrieves a list of the most recent saves from the branch.") |> addCommonOptions |> addOption Options.maxCount |> addOption Options.fullSha
        getSavesCommand.Handler <- GetSaves
        branchCommand.AddCommand(getSavesCommand)

        let getTagsCommand = new Command("get-tags", Description = "Retrieves a list of the most recent tags from the branch.") |> addCommonOptions |> addOption Options.maxCount |> addOption Options.fullSha
        getTagsCommand.Handler <- GetTags
        branchCommand.AddCommand(getTagsCommand)

        let deleteCommand = new Command("delete", Description = "Delete the branch.") |> addCommonOptions
        deleteCommand.Handler <- Delete
        branchCommand.AddCommand(deleteCommand)

        //let undeleteCommand = new Command("undelete", Description = "Undelete a deleted owner.") |> addCommonOptions
        //undeleteCommand.Handler <- Undelete
        //branchCommand.AddCommand(undeleteCommand)

        branchCommand

﻿namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Commands
open Grace.Actors.Constants
open Grace.Actors.Interfaces
open Grace.Shared
open Grace.Shared.Client.Configuration
open Grace.Shared.Dto.Reference
open Grace.Shared.Types
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Collections.Generic
open System.Threading.Tasks
open Types

module Reference =

    let GetActorId (referenceId) = ActorId($"{referenceId}")

    type ReferenceActor(host: ActorHost) =
        inherit Actor (host)

        let actorName = ActorName.Reference
        let log = host.LoggerFactory.CreateLogger(actorName)
        let dtoStateName = "ReferenceDtoState"
        let mutable referenceDto = None
        
        override this.OnActivateAsync() =
            let stateManager = this.StateManager
            log.LogInformation($"{getCurrentInstantExtended()} Activated ReferenceActor {host.Id}.")
            task {
                let! retrievedDto = Storage.RetrieveState<ReferenceDto> stateManager dtoStateName
                match retrievedDto with
                    | Some retrievedDto -> referenceDto <- Some retrievedDto
                    | None -> ()
            } :> Task

        interface IReferenceActor with
            member this.Exists() = (if referenceDto.IsSome then true else false) |> returnTask
            member this.Get() = Task.FromResult(referenceDto.Value)
            member this.GetReferenceType() = Task.FromResult(referenceDto.Value.ReferenceType)

            member this.Create(referenceId, branchId, directoryId, sha256Hash, referenceType, referenceText) =
                let stateManager = this.StateManager
                task {
                    referenceDto <- Some {ReferenceDto.Default with
                                            ReferenceId = referenceId
                                            BranchId = branchId
                                            DirectoryId = directoryId
                                            Sha256Hash = sha256Hash
                                            ReferenceType = referenceType
                                            ReferenceText = referenceText}

                    do! Storage.SaveState stateManager dtoStateName referenceDto.Value
                    return referenceDto.Value
                }
            
            member this.Delete(correlationId: string) =
                let stateManager = this.StateManager
                task {
                    let! deleteSucceeded = Storage.DeleteState stateManager dtoStateName
                    return Ok (GraceReturnValue.Create "Reference deleted." correlationId)
                }

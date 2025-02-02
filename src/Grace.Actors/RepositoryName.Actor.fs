﻿namespace Grace.Actors

open Dapr.Actors
open Dapr.Actors.Runtime
open Grace.Actors.Constants
open Grace.Shared.Utilities
open Microsoft.Extensions.Logging
open NodaTime
open System
open System.Threading.Tasks

module RepositoryName =

    let mutable actorStartTime = Instant.MinValue
    let mutable logScope: IDisposable = null

    let GetActorId (repositoryName: string) = ActorId(repositoryName)

    type IRepositoryNameActor =
        inherit IActor
        /// <summary>
        /// Sets the RepositoryId that matches the RepositoryName.
        /// </summary>
        abstract member SetRepositoryId: repositoryName: string -> Task

        /// <summary>
        /// Returns the RepositoryId for the given RepositoryName.
        /// </summary>
        abstract member GetRepositoryId: unit -> Task<String option>

    type RepositoryNameActor(host: ActorHost) =
        inherit Actor(host)

        let actorName = Constants.ActorName.RepositoryName
    
        let log = host.LoggerFactory.CreateLogger(actorName)

        let mutable cachedRepositoryId: string option = None

        override this.OnPreActorMethodAsync(context) =
            actorStartTime <- getCurrentInstant()
            logScope <- log.BeginScope("Actor {actorName}", actorName)
            //log.LogInformation("{CurrentInstant}: Started {ActorName}.{MethodName} Id: {Id}.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId())
            Task.CompletedTask

        override this.OnPostActorMethodAsync(context) =
            let duration = getCurrentInstant().Minus(actorStartTime)
            log.LogInformation("{CurrentInstant}: Finished {ActorName}.{MethodName} Id: {Id}; Duration: {duration}ms.", getCurrentInstantExtended(), actorName, context.MethodName, this.Id.GetId(), duration.TotalMilliseconds.ToString("F3"))
            logScope.Dispose()
            Task.CompletedTask

        interface IRepositoryNameActor with
            member this.GetRepositoryId() = Task.FromResult(cachedRepositoryId)

            member this.SetRepositoryId(repositoryId: string) =
                let mutable guid = Guid.Empty
                if Guid.TryParse(repositoryId, &guid) && guid <> Guid.Empty then
                    cachedRepositoryId <- Some repositoryId
                Task.CompletedTask

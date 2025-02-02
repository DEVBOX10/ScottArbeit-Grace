﻿namespace Grace.Shared.Validation

open FSharp.Control
open System.Threading.Tasks

module Utilities =

    /// <summary>
    /// Retrieves the first error from a list of validations.
    /// </summary>
    /// <param name="validations">A list of Result values.</param>
    let getFirstError (validations: Task<Result<'T, 'TError>> list) =
        task {
            let! firstError = validations
                              |> TaskSeq.ofTaskList
                              |> TaskSeq.tryFind(fun validation -> Result.isError validation)
            return match firstError with
                    | Some result -> match result with | Ok _ -> None | Error error -> Some error   // This line will always return Some error
                    | None -> None
        }

    /// <summary>
    /// Checks if any of a list of validations fail.
    ///</summary>
    /// <param name="validations">A list of Result values.</param>
    let anyFail validations =
        task {
            return! validations
                    |> TaskSeq.ofTaskList
                    |> TaskSeq.exists(fun validation -> Result.isError validation)
        }

    /// <summary>
    /// Checks that all validations in a list pass.
    /// </summary>
    /// <param name="validations">A list of Result values.</param>
    let allPass validations =
        task {
            let! anyFail = anyFail validations
            return not anyFail
        }

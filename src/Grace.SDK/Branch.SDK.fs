﻿namespace Grace.SDK

open Grace.SDK.Common
open Grace.Shared.Dto.Branch
open Grace.Shared.Dto.Diff
open Grace.Shared.Dto.Reference
open Grace.Shared.Parameters.Branch
open Grace.Shared.Types
open Grace.Shared.Utilities
open System
open System.Collections.Generic

type Branch() =

    /// <summary>
    /// Creates a new branch.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new branch.</param>
    static member public Create(parameters: CreateBranchParameters) =
        postServer<CreateBranchParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Create)}")

    /// Rebases a branch on a promotion from the parent branch.
    static member public Rebase(parameters: RebaseParameters) =
        postServer<RebaseParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Rebase)}")

    /// <summary>
    /// Creates a promotion reference in the parent branch of this branch.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new reference.</param>
    static member public Promote(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Promote)}")
    
    /// <summary>
    /// Creates a commit reference in this branch.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new reference.</param>
    static member public Commit(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Commit)}")
    
    /// <summary>
    /// Creates a checkpoint reference in this branch.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new reference.</param>
    static member public Checkpoint(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Checkpoint)}")
    
    /// <summary>
    /// Creates a save reference in this branch.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new reference.</param>
    static member public Save(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Save)}")
    
    /// <summary>
    /// Creates a tag reference in this branch.
    /// </summary>
    /// <param name="parameters">Values to use when creating the new reference.</param>
    static member public Tag(parameters: CreateReferenceParameters) =
        postServer<CreateReferenceParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Tag)}")

    /// Sets the flag to allow promotion in this branch.
    static member public EnablePromotion(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.EnablePromotion)}")

    /// Sets the flag to allow commits in this branch.
    static member public EnableCommit(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.EnableCommit)}")

    /// Sets the flag to allow checkpoints in this branch.
    static member public EnableCheckpoint(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.EnableCheckpoint)}")

    /// Sets the flag to allow saves in this branch.
    static member public EnableSave(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.EnableSave)}")

    /// Sets the flag to allow tags in this branch.
    static member public EnableTag(parameters: EnableFeatureParameters) =
        postServer<EnableFeatureParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.EnableTag)}")

    /// Gets the diffs between a set of references.
    static member public GetDiffsForReferenceType(parameters: GetDiffsForReferenceTypeParameters) =
        postServer<GetDiffsForReferenceTypeParameters, (IReadOnlyList<ReferenceDto> * IReadOnlyList<DiffDto>)>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetDiffsForReferenceType)}")

    /// Gets the metadata for a specific reference from a branch.
    static member public GetReference(parameters: GetReferenceParameters) =
        postServer<GetReferenceParameters, ReferenceDto>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetReference)}")

    /// <summary>
    /// Gets the references from a branch.
    /// </summary>
    /// <param name="parameters">Values to use when retrieving references from a branch.</param>
    static member public GetReferences(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetReferences)}")

    /// <summary>
    /// Gets the promotions from a branch.
    /// </summary>
    /// <param name="parameters">Values to use when retrieving references from a branch.</param>
    static member public GetPromotions(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetPromotions)}")

    /// <summary>
    /// Gets the commits from a branch.
    /// </summary>
    /// <param name="parameters">Values to use when retrieving references from a branch.</param>
    static member public GetCommits(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetCommits)}")

    /// <summary>
    /// Gets the checkpoints from a branch.
    /// </summary>
    /// <param name="parameters">Values to use when retrieving references from a branch.</param>
    static member public GetCheckpoints(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetCheckpoints)}")

    /// <summary>
    /// Gets the saves from a branch.
    /// </summary>
    /// <param name="parameters">Values to use when retrieving references from a branch.</param>
    static member public GetSaves(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetSaves)}")

    /// <summary>
    /// Gets the tags from a branch.
    /// </summary>
    /// <param name="parameters">Values to use when retrieving references from a branch.</param>
    static member public GetTags(parameters: GetReferencesParameters) =
        postServer<GetReferencesParameters, IEnumerable<ReferenceDto>>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetTags)}")

    /// <summary>
    /// Sets the name of a branch.
    /// </summary>
    /// <param name="parameters">Values to use when setting the name of the branch.</param>
    static member public SetName(parameters: SetBranchNameParameters) =
        postServer<SetBranchNameParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.SetName)}")

    /// Gets the metadata for a branch.
    static member public Get(parameters: GetBranchParameters) =
        postServer<GetBranchParameters, BranchDto>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Get)}")

    /// Gets the metadata for the parent branch.
    static member public GetParentBranch(parameters: BranchParameters) =
        postServer<BranchParameters, BranchDto>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetParentBranch)}")

    /// Gets a specific version of a branch from the server.
    static member public GetVersion(parameters: GetBranchVersionParameters) =
        postServer<GetBranchVersionParameters, IEnumerable<DirectoryId>>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.GetVersion)}")

    /// Delete the branch.
    static member public Delete(parameters: DeleteBranchParameters) =
        postServer<DeleteBranchParameters, string>(parameters |> ensureCorrelationIdIsSet, $"branch/{nameof(Branch.Delete)}")

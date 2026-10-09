namespace AgentLang.SubagentTrials.IoMaintenance156.Fixture

open System

type PublishOutcome =
    | Created
    | Unchanged
    | Conflict

/// An in-memory, deterministic file provider. Observation helpers do not affect counters.
type VirtualFiles(initialFiles: seq<string * string>) =
    let mutable files = Map.ofSeq initialFiles
    let mutable reads = 0
    let mutable writes = 0

    member _.Exists(path: string) =
        reads <- reads + 1
        Map.containsKey path files

    member _.Read(path: string) =
        reads <- reads + 1

        match Map.tryFind path files with
        | Some contents -> contents
        | None -> raise (InvalidOperationException("MISSING_FILE"))

    member _.Write(path: string, contents: string) =
        writes <- writes + 1
        files <- Map.add path contents files

    member _.ReadCount = reads

    member _.WriteCount = writes

    member _.Snapshot() = files

    member _.Peek(path: string) = Map.tryFind path files

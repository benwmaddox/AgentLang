namespace AgentLang.FlowHttp

open System
open System.Collections.Generic
open AgentLang
open SQLitePCL

/// The portable database API (docs/LINUX-SERVER-PLAN.md piece C) as host
/// operations: db.query and db.execute over the stdlib DbValue/DbRow/SqlError
/// records (src/stdlib/db.flow). The interpreter provider below and the native
/// adapter (native/http_host/flow_http_db.c) follow the same SQLite C API
/// steps, so results and SqlError codes agree.
module Db =
    let queryId = HostOperationId "db.query"
    let executeId = HostOperationId "db.execute"
    let operationIds = Set.ofList [ queryId; executeId ]

    let manifest : Map<HostOperationId, HostOperationDefinition> =
        Map.ofList
            [ queryId,
              { Id = queryId
                WordName = "db.query"
                Inputs = [ TString; TList(TNamed "DbValue") ]
                Outputs = [ TResult(TList(TNamed "DbRow"), TNamed "SqlError") ]
                Effects = Set.singleton "db.read"
                Documentation = "Runs one SQL literal (prepared once per worker) with ordered parameters and returns its rows." }
              executeId,
              { Id = executeId
                WordName = "db.execute"
                Inputs = [ TString; TList(TNamed "DbValue") ]
                Outputs = [ TResult(TInt, TNamed "SqlError") ]
                Effects = Set.singleton "db.write"
                Documentation = "Runs one SQL literal (prepared once per worker) with ordered parameters and returns the changed row count." } ]

    /// Native host manifest operations: tag, name, boundary type names, effects.
    let nativeOperations =
        [ 1u, "db.query", [ "String"; "List<DbValue>" ], [ "Result<List<DbRow>, SqlError>" ], [ "db.read" ]
          2u, "db.execute", [ "String"; "List<DbValue>" ], [ "Result<Int, SqlError>" ], [ "db.write" ] ]

    let private sqliteOk = 0
    let private sqliteRow = 100
    let private sqliteDone = 101
    /// SQLITE_RANGE: parameter count differs from the statement's.
    let parameterCountErrorCode = 25L
    let parameterCountMessage = "parameter count does not match the statement"

    let private initialized = lazy (Batteries_V2.Init())

    let private dbError (code: int64) (message: string) =
        RecordValue("SqlError", Map.ofList [ "code", IntValue code; "message", StringValue message ])

    let private dbValue kind (intValue: int64) (text: string) (blob: byte[]) =
        RecordValue(
            "DbValue",
            Map.ofList
                [ "kind", EnumValue("DbValueKind", kind, None)
                  "int-value", IntValue intValue
                  "text-value", StringValue text
                  "blob-value", BytesValue blob ])

    /// One worker's connection: WAL and the PRAGMAs of the Campfire host
    /// (extended result codes, 5 s busy timeout, foreign keys, FULL sync).
    type Connection(path: string) =
        do initialized.Force()
        let mutable handle: sqlite3 = null
        let statements = Dictionary<string, sqlite3_stmt>(StringComparer.Ordinal)
        do
            // SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_NOMUTEX | SQLITE_OPEN_PRIVATECACHE
            let rc = raw.sqlite3_open_v2(path, &handle, 0x2 ||| 0x4 ||| 0x8000 ||| 0x40000, null)
            if rc <> sqliteOk then invalidOp $"SQLite open failed ({rc})."
            raw.sqlite3_extended_result_codes(handle, 1) |> ignore
            raw.sqlite3_busy_timeout(handle, 5000) |> ignore
            for pragma in [ "PRAGMA foreign_keys=ON"; "PRAGMA read_uncommitted=0"; "PRAGMA journal_mode=WAL"; "PRAGMA synchronous=FULL"; "PRAGMA wal_autocheckpoint=1000" ] do
                if raw.sqlite3_exec(handle, pragma) <> sqliteOk then invalidOp $"SQLite setup failed: {pragma}"

        member _.Handle = handle
        member _.PreparedCount = statements.Count

        /// Prepared once per connection per SQL text, then reset and reused.
        member _.Statement(sql: string) : Result<sqlite3_stmt, int64 * string> =
            match statements.TryGetValue sql with
            | true, statement -> Ok statement
            | _ ->
                let mutable statement: sqlite3_stmt = null
                let rc = raw.sqlite3_prepare_v2(handle, sql, &statement)
                if rc <> sqliteOk then
                    Error(int64 (raw.sqlite3_extended_errcode handle), raw.sqlite3_errmsg(handle).utf8_to_string())
                else
                    statements[sql] <- statement
                    Ok statement

        member private this.Bind(statement: sqlite3_stmt, parameters: Value list) : Result<unit, int64 * string> =
            raw.sqlite3_reset statement |> ignore
            raw.sqlite3_clear_bindings statement |> ignore
            if raw.sqlite3_bind_parameter_count statement <> parameters.Length then
                Error(parameterCountErrorCode, parameterCountMessage)
            else
                let mutable failure = None
                parameters
                |> List.iteri (fun index parameter ->
                    if failure.IsNone then
                        let position = index + 1
                        let rc =
                            match parameter with
                            | RecordValue("DbValue", fields) ->
                                match fields["kind"] with
                                | EnumValue(_, "int-value", None) ->
                                    match fields["int-value"] with
                                    | IntValue value -> raw.sqlite3_bind_int64(statement, position, value)
                                    | _ -> invalidOp "DbValue.int-value"
                                | EnumValue(_, "text-value", None) ->
                                    match fields["text-value"] with
                                    | StringValue value -> raw.sqlite3_bind_text(statement, position, value)
                                    | _ -> invalidOp "DbValue.text-value"
                                | EnumValue(_, "blob-value", None) ->
                                    match fields["blob-value"] with
                                    | BytesValue value -> raw.sqlite3_bind_blob(statement, position, ReadOnlySpan<byte>(value))
                                    | _ -> invalidOp "DbValue.blob-value"
                                | _ -> raw.sqlite3_bind_null(statement, position)
                            | _ -> invalidOp "DbValue parameter"
                        if rc <> sqliteOk then
                            failure <- Some(int64 (raw.sqlite3_extended_errcode handle), raw.sqlite3_errmsg(handle).utf8_to_string()))
                match failure with
                | Some error -> Error error
                | None -> Ok()

        member private _.Failure(statement: sqlite3_stmt) =
            let error = int64 (raw.sqlite3_extended_errcode handle), raw.sqlite3_errmsg(handle).utf8_to_string()
            raw.sqlite3_reset statement |> ignore
            error

        member this.Query(sql: string, parameters: Value list) : Result<Value list, int64 * string> =
            match this.Statement sql |> Result.bind (fun statement -> this.Bind(statement, parameters) |> Result.map (fun () -> statement)) with
            | Error error -> Error error
            | Ok statement ->
                let rows = ResizeArray<Value>()
                let mutable result = None
                while result.IsNone do
                    let rc = raw.sqlite3_step statement
                    if rc = sqliteRow then
                        let count = raw.sqlite3_column_count statement
                        let values =
                            [ for column in 0 .. count - 1 do
                                match raw.sqlite3_column_type(statement, column) with
                                | 1 -> dbValue "int-value" (raw.sqlite3_column_int64(statement, column)) "" [||]
                                | 5 -> dbValue "null-value" 0L "" [||]
                                | 4 -> dbValue "blob-value" 0L "" (raw.sqlite3_column_blob(statement, column).ToArray())
                                | _ -> dbValue "text-value" 0L (raw.sqlite3_column_text(statement, column).utf8_to_string()) [||] ]
                        rows.Add(RecordValue("DbRow", Map.ofList [ "values", ListValue(TNamed "DbValue", values) ]))
                    elif rc = sqliteDone then
                        raw.sqlite3_reset statement |> ignore
                        result <- Some(Ok(List.ofSeq rows))
                    else
                        result <- Some(Error(this.Failure statement))
                result.Value

        member this.Execute(sql: string, parameters: Value list) : Result<int64, int64 * string> =
            match this.Statement sql |> Result.bind (fun statement -> this.Bind(statement, parameters) |> Result.map (fun () -> statement)) with
            | Error error -> Error error
            | Ok statement ->
                let mutable result = None
                while result.IsNone do
                    let rc = raw.sqlite3_step statement
                    if rc = sqliteDone then
                        raw.sqlite3_reset statement |> ignore
                        result <- Some(Ok(int64 (raw.sqlite3_changes handle)))
                    elif rc <> sqliteRow then
                        result <- Some(Error(this.Failure statement))
                result.Value

        interface IDisposable with
            member _.Dispose() =
                for statement in statements.Values do raw.sqlite3_finalize statement |> ignore
                statements.Clear()
                if not (isNull handle) then
                    raw.sqlite3_close_v2 handle |> ignore
                    handle <- null

    let private resultValue okType (result: Result<Value, int64 * string>) =
        match result with
        | Ok value -> ResultValue(okType, TNamed "SqlError", Ok value)
        | Error(code, message) -> ResultValue(okType, TNamed "SqlError", Error(dbError code message))

    /// Interpreter provider over one connection (one worker).
    let createProvider (path: string) : HostOperationProvider =
        let connection = new Connection(path)
        let invoke id (arguments: Value list) =
            try
                match arguments with
                | [ StringValue sql; ListValue(_, parameters) ] when id = queryId ->
                    let result = connection.Query(sql, parameters) |> Result.map (fun rows -> ListValue(TNamed "DbRow", rows))
                    Ok [ resultValue (TList(TNamed "DbRow")) result ]
                | [ StringValue sql; ListValue(_, parameters) ] when id = executeId ->
                    let result = connection.Execute(sql, parameters) |> Result.map IntValue
                    Ok [ resultValue TInt result ]
                | _ when operationIds.Contains id -> Error HostOperationFailure.ProviderContractMismatch
                | _ -> Error HostOperationFailure.MissingHandler
            with _ -> Error HostOperationFailure.InvocationFailed
        { OperationIds = operationIds
          Invoke = invoke
          Dispose = fun () -> (connection :> IDisposable).Dispose() }

    /// SQL passed to db.query/db.execute must be a string literal so a worker
    /// can prepare it once. Returns the offending call sites (empty when valid).
    let nonLiteralSqlCalls (source: string) =
        Text.RegularExpressions.Regex.Matches(source, @"\bdb\.(query|execute)\(\s*(?!"")")
        |> Seq.map (fun m ->
            let line = source.Substring(0, m.Index) |> Seq.filter ((=) '\n') |> Seq.length
            $"line {line + 1}: {m.Value.Trim()}")
        |> List.ofSeq

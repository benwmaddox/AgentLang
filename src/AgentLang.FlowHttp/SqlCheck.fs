namespace AgentLang.FlowHttp

open System
open System.IO
open System.Security.Cryptography
open System.Text
open AgentLang
open SQLitePCL

/// Build-time SQL checks (report 301). Every db.query/db.execute literal in the
/// app's sources is prepared against the app's committed schema (an in-memory
/// SQLite database built from schema.sql), so an unknown table or column, a
/// syntax error, a wrong parameter count, a write inside db.query (declared
/// db.read) or rows from db.execute fail the build at the Flow line.
/// CREATE TABLE statements (attached-test fixtures) must name a schema table
/// and only its columns. The schema's hash is compiled into the server, which
/// refuses a database with another schema.
/// db.rows<Row> literals (report 305) also check the result columns against Row:
/// one column per field, named after it ($created_at for created-at) in field
/// order, a declared column type that fits the field, and no nullable table
/// column read into a field that is not an Option.
module SqlCheck =
    type Site =
        { File: string
          Line: int
          Column: int
          Operation: string
          Sql: string
          /// Elements of a list-literal parameter argument; None when the argument is not a literal.
          ParameterCount: int option
          /// db.rows<Row>: the row record's name.
          RowType: string option }

    let private initialized = lazy (Batteries_V2.Init())

    let rec private expressionSites file (sites: ResizeArray<Site>) expression =
        let recurse = expressionSites file sites
        let statements = List.iter (statementSites file sites)
        let arguments (items: FlowArgument list) =
            for argument in items do
                match argument with
                | FlowArgument.Positional value | FlowArgument.Named(_, value, _) -> recurse value
                | FlowArgument.WordReference _ -> ()
        match expression with
        | FlowExpression.Call(("db.query" | "db.execute") as operation, args, span) ->
            match args with
            | FlowArgument.Positional(FlowExpression.Literal(LString sql, _)) :: rest ->
                let count =
                    match rest with
                    | [ FlowArgument.Positional(FlowExpression.ListLiteral(items, _)) ] -> Some items.Length
                    | [ FlowArgument.Positional(FlowExpression.Container(FlowContainerConstructor.ListEmpty, _, _, _)) ] -> Some 0
                    | [ FlowArgument.Positional(FlowExpression.Container(FlowContainerConstructor.ListSingleton, _, _, _)) ] -> Some 1
                    | _ -> None
                sites.Add { File = file; Line = span.Line; Column = span.Column; Operation = operation; Sql = sql; ParameterCount = count; RowType = None }
            | _ -> ()
            arguments args
        | FlowExpression.Call(name, args, span) when name.StartsWith("db.rows<", StringComparison.Ordinal) && name.EndsWith(">", StringComparison.Ordinal) ->
            match args with
            | FlowArgument.Positional(FlowExpression.Literal(LString sql, _)) :: _ ->
                let rowType = name.Substring(8, name.Length - 9)
                sites.Add { File = file; Line = span.Line; Column = span.Column; Operation = "db.rows"; Sql = sql; ParameterCount = None; RowType = Some rowType }
            | _ -> ()
            arguments args
        | FlowExpression.Call(_, args, _) | FlowExpression.RootCall(_, args, _) -> arguments args
        | FlowExpression.DotCall(receiver, _, args, _) -> recurse receiver; arguments args
        | FlowExpression.Property(receiver, _, _) -> recurse receiver
        | FlowExpression.Equality(left, right, _) | FlowExpression.Binary(_, left, right, _) -> recurse left; recurse right
        | FlowExpression.If(condition, thenBranch, elseBranch, _) -> recurse condition; statements thenBranch; statements elseBranch
        | FlowExpression.Compact(body, _) -> statements body
        | FlowExpression.ListLiteral(items, _) -> List.iter recurse items
        | FlowExpression.Container(_, _, payload, _) -> Option.iter recurse payload
        | FlowExpression.MatchOption(scrutinee, some, none, _) -> recurse scrutinee; statements some.Statements; statements none.Statements
        | FlowExpression.MatchResult(scrutinee, ok, error, _) -> recurse scrutinee; statements ok.Statements; statements error.Statements
        | FlowExpression.MatchEnum(scrutinee, cases, _) -> recurse scrutinee; for case in cases do statements case.Statements
        | FlowExpression.Literal _ | FlowExpression.Local _ -> ()

    and private statementSites file sites statement =
        let recurse = expressionSites file sites
        match statement with
        | FlowStatement.Let(_, value, _) | FlowStatement.LetMany(_, value, _) | FlowStatement.Var(_, value, _) | FlowStatement.Assign(_, value, _) -> recurse value
        | FlowStatement.Evaluate value -> recurse value
        | FlowStatement.Return(values, _) -> List.iter recurse values
        | FlowStatement.ForRange(_, first, last, body, _) -> recurse first; recurse last; List.iter (statementSites file sites) body
        | FlowStatement.ForEach(_, _, items, body, _) -> recurse items; List.iter (statementSites file sites) body
        | FlowStatement.Break _ | FlowStatement.Continue _ -> ()

    let private parse (path: string) (source: string) =
        match FlowParser.parseDocumentWithVersion 2 (Path.GetFileName path) source with
        | Error diagnostic -> failwith (Diagnostics.render diagnostic)
        | Ok document -> document

    /// The record definitions of a source, for db.rows row types.
    let records (path: string) (source: string) = (parse path source).Records

    /// Every db.query/db.execute/db.rows call with a literal SQL argument, in words, tests and test files.
    let sites (path: string) (source: string) =
        let found = ResizeArray<Site>()
        let file = Path.GetFileName path
        let document = parse path source
        do
            let body = List.iter (statementSites file found)
            for word in document.Words do body word.Body
            for test in document.Tests do body test.Body
            for scope in document.TestFiles do
                for replacement in scope.Overrides do body replacement.Definition.Body
                for test in scope.Tests do body test.Body
        List.ofSeq found

    let private check (rc: int) (db: sqlite3) (what: string) =
        if rc <> raw.SQLITE_OK then failwith $"{what}: {raw.sqlite3_errmsg(db).utf8_to_string()}"

    let private openMemory () =
        initialized.Force()
        let mutable db: sqlite3 = null
        check (raw.sqlite3_open(":memory:", &db)) db "open in-memory schema database"
        db

    let private exec (db: sqlite3) (sql: string) =
        let mutable message: string = null
        if raw.sqlite3_exec(db, sql, null, null, &message) <> raw.SQLITE_OK then failwith message

    /// The column names of a table, in order.
    let private columns (db: sqlite3) (table: string) =
        let mutable statement: sqlite3_stmt = null
        check (raw.sqlite3_prepare_v2(db, "SELECT name FROM pragma_table_info(?1)", &statement)) db "table_info"
        raw.sqlite3_bind_text(statement, 1, table) |> ignore
        let names = ResizeArray<string>()
        while raw.sqlite3_step(statement) = raw.SQLITE_ROW do
            names.Add(raw.sqlite3_column_text(statement, 0).utf8_to_string())
        raw.sqlite3_finalize(statement) |> ignore
        List.ofSeq names

    /// The canonical schema: every named object except FTS shadow tables, in a fixed order.
    /// The native host runs the same query on the database it opens (flow_http_db.c).
    let canonicalQuery =
        "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT IN (SELECT name FROM pragma_table_list WHERE type = 'shadow') ORDER BY type, name"

    /// The canonical schema rows, fields separated by 0x1F and rows ended by 0x1E. The
    /// server compares it, byte for byte, with the same text from the database it opens.
    let canonicalText (db: sqlite3) =
        let mutable statement: sqlite3_stmt = null
        check (raw.sqlite3_prepare_v2(db, canonicalQuery, &statement)) db "canonical schema"
        let text = StringBuilder()
        while raw.sqlite3_step(statement) = raw.SQLITE_ROW do
            for column in 0..3 do
                text.Append(raw.sqlite3_column_text(statement, column).utf8_to_string()) |> ignore
                text.Append(if column = 3 then '\u001e' else '\u001f') |> ignore
        raw.sqlite3_finalize(statement) |> ignore
        text.ToString()

    /// SHA-256 (lowercase hex) of the canonical text, for reports.
    let schemaHash (db: sqlite3) =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText db))).ToLowerInvariant()

    /// The canonical text as a C string literal body (octal escapes, so no escape runs on).
    let cLiteral (text: string) =
        let builder = StringBuilder()
        for b in Encoding.UTF8.GetBytes text do
            match char b with
            | '\\' -> builder.Append("\\\\") |> ignore
            | '"' -> builder.Append("\\\"") |> ignore
            | c when b >= 0x20uy && b < 0x7Fuy && c <> '?' -> builder.Append(c) |> ignore
            | _ -> builder.Append("\\").Append(Convert.ToString(int b, 8).PadLeft(3, '0')) |> ignore
        builder.ToString()

    /// SQLite's type affinity of a declared column type (the rules of section 3.1 of datatype3).
    let private affinity (declared: string) =
        let upper = declared.ToUpperInvariant()
        if upper.Contains "INT" then "INTEGER"
        elif upper.Contains "CHAR" || upper.Contains "CLOB" || upper.Contains "TEXT" then "TEXT"
        elif upper = "" || upper.Contains "BLOB" then "BLOB"
        elif upper.Contains "REAL" || upper.Contains "FLOA" || upper.Contains "DOUB" then "REAL"
        else "NUMERIC"

    /// db.rows<Row>: the statement's result columns against the row record's fields.
    let private checkRows (db: sqlite3) (statement: sqlite3_stmt) (at: string) (row: RecordDefinition) (errors: ResizeArray<string>) =
        let columnCount = raw.sqlite3_column_count statement
        let fields = Array.ofList row.Fields
        if columnCount <> fields.Length then
            errors.Add $"{at}: the statement returns {columnCount} column(s) but {row.Name} has {fields.Length} field(s)"
        for index in 0 .. (min columnCount fields.Length) - 1 do
            let field = fields[index]
            let expected = field.Name.Replace('-', '_')
            let column = raw.sqlite3_column_name(statement, index).utf8_to_string()
            if not (String.Equals(column, expected, StringComparison.OrdinalIgnoreCase)) then
                errors.Add $"{at}: column {index + 1} is {column} but {row.Name} field {index + 1} is {field.Name}; name the column {expected} (AS {expected})"
            else
                let optional, inner =
                    match field.Type with
                    | TOption inner -> true, inner
                    | other -> false, other
                // Expression columns have no declared type; the host checks each value when it reads it.
                let declared = raw.sqlite3_column_decltype(statement, index).utf8_to_string()
                if not (isNull declared) then
                    let kind = affinity declared
                    let fits =
                        match inner with
                        | TInt | TBool -> kind = "INTEGER" || kind = "NUMERIC"
                        // NUMERIC keeps text that is not a number as text (Rails' datetime(6) columns).
                        | TString -> kind = "TEXT" || kind = "NUMERIC"
                        | TBytes -> kind = "BLOB"
                        | _ -> true
                    if not fits then
                        errors.Add $"{at}: column {column} is declared {declared} ({kind} affinity) but {row.Name}.{field.Name} is {Types.format field.Type}"
                    let table = raw.sqlite3_column_table_name(statement, index).utf8_to_string()
                    let origin = raw.sqlite3_column_origin_name(statement, index).utf8_to_string()
                    if not optional && not (isNull table) && not (isNull origin) then
                        let mutable dataType: string = null
                        let mutable collation: string = null
                        let mutable notNull = 0
                        let mutable primaryKey = 0
                        let mutable autoIncrement = 0
                        let databaseName = raw.sqlite3_column_database_name(statement, index).utf8_to_string()
                        if raw.sqlite3_table_column_metadata(db, databaseName, table, origin, &dataType, &collation, &notNull, &primaryKey, &autoIncrement) = raw.SQLITE_OK then
                            // INTEGER PRIMARY KEY is the rowid: never NULL.
                            let rowid = primaryKey <> 0 && String.Equals(dataType, "INTEGER", StringComparison.OrdinalIgnoreCase)
                            if notNull = 0 && not rowid then
                                errors.Add $"{at}: column {table}.{origin} may be NULL but {row.Name}.{field.Name} is {Types.format field.Type}; make it Option<{Types.format field.Type}> or the column NOT NULL"

    type Result =
        { Errors: string list
          Checked: int
          Statements: int
          SchemaHash: string
          SchemaText: string
          SqliteVersion: string }

    /// Checks every site against schema.sql. `statementLimit` is the native host's
    /// per-worker prepared-statement table (FH_DB_STATEMENTS).
    let run (schemaPath: string) (sources: (string * string) list) (statementLimit: int) =
        let rowRecords =
            sources
            |> List.collect (fun (path, source) -> records path source)
            |> List.map (fun record -> record.Name, record)
            |> Map.ofList
        let db = openMemory ()
        try
            exec db (File.ReadAllText schemaPath)
            let hash = schemaHash db
            let schemaText = canonicalText db
            let errors = ResizeArray<string>()
            let all = sources |> List.collect (fun (path, source) -> sites path source)
            for site in all do
                let at = $"{site.File}:{site.Line}:{site.Column}: {site.Operation}"
                let trimmed = site.Sql.TrimStart()
                if trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) then
                    // Test-fixture DDL: each table it makes must be a schema table, with only schema columns.
                    let scratch = openMemory ()
                    try
                        try
                            exec scratch site.Sql
                            let mutable statement: sqlite3_stmt = null
                            check (raw.sqlite3_prepare_v2(scratch, "SELECT name FROM pragma_table_list WHERE type IN ('table', 'virtual') AND name NOT LIKE 'sqlite_%'", &statement)) scratch "fixture tables"
                            while raw.sqlite3_step(statement) = raw.SQLITE_ROW do
                                let table = raw.sqlite3_column_text(statement, 0).utf8_to_string()
                                match columns db table with
                                | [] -> errors.Add $"{at}: fixture table {table} is not in {Path.GetFileName schemaPath}"
                                | real ->
                                    for column in columns scratch table do
                                        if not (List.contains column real) then
                                            errors.Add $"{at}: fixture column {table}.{column} is not in {Path.GetFileName schemaPath}"
                            raw.sqlite3_finalize(statement) |> ignore
                        with error -> errors.Add $"{at}: {error.Message}"
                    finally raw.sqlite3_close_v2(scratch) |> ignore
                else
                    let mutable statement: sqlite3_stmt = null
                    let mutable tail: string = null
                    let rc = raw.sqlite3_prepare_v2(db, site.Sql, &statement, &tail)
                    if rc <> raw.SQLITE_OK then
                        errors.Add $"{at}: {raw.sqlite3_errmsg(db).utf8_to_string()}"
                    else
                        if not (String.IsNullOrWhiteSpace tail) then
                            errors.Add $"{at}: one statement per literal; text follows the first statement"
                        let parameters = raw.sqlite3_bind_parameter_count statement
                        match site.ParameterCount with
                        | Some count when count <> parameters ->
                            errors.Add $"{at}: the statement has {parameters} parameter(s) but the call passes {count}"
                        | _ -> ()
                        let readonly = raw.sqlite3_stmt_readonly statement <> 0
                        let columnCount = raw.sqlite3_column_count statement
                        if (site.Operation = "db.query" || site.Operation = "db.rows") && not readonly then
                            errors.Add $"{at}: the statement writes, but {site.Operation} is declared db.read; use db.execute"
                        if site.Operation = "db.query" && columnCount = 0 then
                            errors.Add $"{at}: the statement returns no columns; use db.execute"
                        match site.RowType |> Option.bind rowRecords.TryFind with
                        | Some row -> checkRows db statement $"{at}<{row.Name}>" row errors
                        | None -> ()
                        if site.Operation = "db.execute" && columnCount > 0 && readonly then
                            errors.Add $"{at}: the statement only reads rows; use db.query"
                        raw.sqlite3_finalize(statement) |> ignore
            let distinct = all |> List.map (fun site -> site.Sql) |> List.distinct |> List.length
            if distinct > statementLimit then
                errors.Add $"the app has {distinct} distinct SQL literals; the native host prepares at most {statementLimit} per worker"
            { Errors = List.ofSeq errors
              Checked = all.Length
              Statements = distinct
              SchemaHash = hash
              SchemaText = schemaText
              SqliteVersion = raw.sqlite3_libversion().utf8_to_string() }
        finally
            raw.sqlite3_close_v2(db) |> ignore

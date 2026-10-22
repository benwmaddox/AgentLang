module AgentLang.SqlCheck.Tests

// Build-time SQL checks (report 301): deliberate slips must fail with the line
// and the reason, Campfire's own sources must pass, and the schema hash must
// follow the schema.

open System
open System.IO
open AgentLang.FlowHttp

let private repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let private schema = Path.Combine(repo, "applications/campfire/schema.sql")
let private assertionCount = ref 0

let private check label condition =
    assertionCount.Value <- assertionCount.Value + 1
    if not condition then failwith $"Assertion failed: {label}"

let private errorsFor (body: string) =
    let source = $"fn slip.case() -> Int {{\n    effects db.read, db.write\n{body}\n    0\n}}\n"
    (SqlCheck.run schema [ "slip.flow", source ] 256).Errors

let private fails label (expected: string) (body: string) =
    let errors = errorsFor body
    let text = String.Join(" | ", errors)
    check $"{label}: one error naming '{expected}' (got: {text})" (errors.Length = 1 && text.Contains(expected, StringComparison.Ordinal))
    check $"{label}: the error gives the Flow line (got: {text})" (text.StartsWith("slip.flow:", StringComparison.Ordinal))

let private passes label (body: string) =
    let errors = errorsFor body
    let text = String.Join(" | ", errors)
    check $"{label} passes (got: {text})" errors.IsEmpty

let private run () =
    let call (operation: string) (sql: string) (parameters: string) =
        $"    let ignored = {operation}(\"{sql}\", {parameters})"
    let none = "list.empty<DbValue>()"
    passes "a valid query" (call "db.query" "SELECT id, name FROM users WHERE id = ?1" "[db.int-param(1)]")
    passes "a valid write" (call "db.execute" "UPDATE users SET name = ?1 WHERE id = ?2" "[db.text-param(\"Ann\"), db.int-param(1)]")
    passes "a parameter list held in a local is not counted" ("    let parameters = [db.int-param(1)]\n" + call "db.query" "SELECT id FROM users WHERE id = ?1" "parameters")
    passes "the FTS5 index (Campfire search)" (call "db.query" "SELECT rowid FROM message_search_index WHERE message_search_index MATCH ?1" "[db.text-param(\"cat\")]")
    passes "a fixture table with schema columns" (call "db.execute" "CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL)" none)
    fails "a misspelled column" "no such column: nmae" (call "db.query" "SELECT id, nmae FROM users" none)
    fails "a missing table" "no such table: user" (call "db.query" "SELECT id FROM user" none)
    fails "a syntax error" "syntax error" (call "db.query" "SELEC id FROM users" none)
    fails "too many parameters passed" "has 1 parameter(s) but the call passes 2" (call "db.query" "SELECT id FROM users WHERE id = ?1" "[db.int-param(1), db.int-param(2)]")
    fails "too few parameters passed" "has 2 parameter(s) but the call passes 0" (call "db.query" "SELECT id FROM users WHERE id = ?1 AND name = ?2" none)
    fails "a write inside db.query" "db.query is declared db.read" (call "db.query" "UPDATE users SET name = 'x' RETURNING id" none)
    fails "a read through db.execute" "use db.query" (call "db.execute" "SELECT id FROM users" none)
    fails "two statements in one literal" "one statement per literal" (call "db.execute" "DELETE FROM bans; DELETE FROM users" none)
    fails "a fixture column the schema lacks" "fixture column users.nickname" (call "db.execute" "CREATE TABLE users (id INTEGER PRIMARY KEY, nickname TEXT)" none)
    fails "a fixture table the schema lacks" "fixture table people" (call "db.execute" "CREATE TABLE people (id INTEGER PRIMARY KEY)" none)
    // db.rows<Row> (report 305): result columns against the row record.
    let rowsErrors (row: string) (sql: string) =
        let source = $"record UserRow {{
{row}
}}

record ByMin {{
    min-id: Int
}}

fn slip.case() -> Int {{
    effects db.read
    let ignored = db.rows<UserRow>(\"{sql}\", byMin.new(min-id = 1))
    0
}}
"
        (SqlCheck.run schema [ "slip.flow", source ] 256).Errors
    let rowsPass label row sql =
        let text = String.Join(" | ", rowsErrors row sql)
        check $"{label} passes (got: {text})" (text = "")
    let rowsFailCount count label (expected: string) row sql =
        let errors = rowsErrors row sql
        let text = String.Join(" | ", errors)
        check $"{label}: {count} error(s), naming '{expected}' (got: {text})" (errors.Length = count && text.Contains(expected, StringComparison.Ordinal) && text.StartsWith("slip.flow:", StringComparison.Ordinal))
    let rowsFail = rowsFailCount 1
    let userRow = "    id: Int
    name: String
    bio: Option<String>
    created-at: String
    role: Int"
    rowsPass "a db.rows query" userRow "SELECT id, name, bio, created_at, role FROM users WHERE id >= $min_id"
    rowsPass "expression columns named with AS" "    id: Int
    shown: Bool" "SELECT id, status = 0 AS shown FROM users WHERE id >= $min_id"
    rowsPass "a NOT NULL column into an Option" "    name: Option<String>" "SELECT name FROM users WHERE id >= $min_id"
    rowsFailCount 2 "columns in another order" "column 1 is name but UserRow field 1 is id" "    id: Int
    name: String" "SELECT name, id FROM users WHERE id >= $min_id"
    rowsFail "an unnamed expression column" "name the column total (AS total)" "    total: Int" "SELECT count(*) FROM users WHERE id >= $min_id"
    rowsFail "a missing column" "returns 1 column(s) but UserRow has 2 field(s)" "    id: Int
    name: String" "SELECT id FROM users WHERE id >= $min_id"
    rowsFail "a text column into Int" "column name is declared varchar (TEXT affinity) but UserRow.name is Int" "    name: Int" "SELECT name FROM users WHERE id >= $min_id"
    rowsFail "an integer column into String" "column role is declared INTEGER (INTEGER affinity) but UserRow.role is String" "    role: String" "SELECT role FROM users WHERE id >= $min_id"
    rowsFail "a nullable column into a plain field" "column users.bio may be NULL but UserRow.bio is String; make it Option<String>" "    bio: String" "SELECT bio FROM users WHERE id >= $min_id"
    rowsFail "a write through db.rows" "db.rows is declared db.read" "    id: Int" "UPDATE users SET name = 'x' WHERE id >= $min_id RETURNING id"
    // Calls in tests are checked too.
    let inTest = "fn slip.ok() -> Int {\n    1\n}\n\ntest slip.ok/one {\n    let rows = db.query(\"SELECT nope FROM users\", list.empty<DbValue>())\n    expect slip.ok() == 1\n}\n"
    let testErrors = (SqlCheck.run schema [ "slip.flow", inTest ] 256).Errors
    let testText = String.Join(" | ", testErrors)
    check $"a query inside a test is checked ({testText})" (testErrors.Length = 1 && testErrors.Head.Contains("no such column: nope", StringComparison.Ordinal))
    // The native statement table bounds the distinct literals.
    let many = String.Join("\n", [ for k in 1..3 -> call "db.query" $"SELECT {k} FROM users" none ])
    let limited = (SqlCheck.run schema [ "slip.flow", $"fn slip.case() -> Int {{\n    effects db.read\n{many}\n    0\n}}\n" ] 2).Errors
    let limitedText = String.Join(" | ", limited)
    check $"more distinct literals than the host prepares fail ({limitedText})" (limited |> List.exists (fun e -> e.Contains("at most 2 per worker", StringComparison.Ordinal)))

    // Campfire's own sources pass, every literal is found, and the hash follows the schema.
    let campfire = Path.Combine(repo, "applications/campfire")
    let sources =
        [ yield! Directory.GetFiles(campfire, "*.flow"); yield! Directory.GetFiles(Path.Combine(campfire, "server"), "*.flow") ]
        |> List.map (fun path -> path, File.ReadAllText path)
    let result = SqlCheck.run schema sources 256
    let campfireText = String.Join(" | ", result.Errors)
    check $"Campfire's SQL matches its schema ({campfireText})" result.Errors.IsEmpty
    let literalCount = sources |> List.sumBy (fun (_, text) -> Text.RegularExpressions.Regex.Matches(text, @"\bdb\.(query|execute|rows<[\w.]+>)\(\s*""").Count)
    check $"every literal call is found ({result.Checked} of {literalCount})" (result.Checked = literalCount)
    let changed = Path.Combine(Path.GetTempPath(), $"schema-{Guid.NewGuid():N}.sql")
    File.WriteAllText(changed, File.ReadAllText schema + "\nCREATE INDEX extra ON users (name);\n")
    let changedHash = (SqlCheck.run changed [] 256).SchemaHash
    File.Delete changed
    check "a changed schema changes the hash" (changedHash <> result.SchemaHash)
    check "the hash is stable" ((SqlCheck.run schema [] 256).SchemaHash = result.SchemaHash)
    printfn "sql check: %d assertions passed (Campfire: %d calls, %d statements, SQLite %s)" assertionCount.Value result.Checked result.Statements result.SqliteVersion

[<EntryPoint>]
let main _ =
    try
        run ()
        0
    with error ->
        eprintfn "%s" (error.ToString())
        1

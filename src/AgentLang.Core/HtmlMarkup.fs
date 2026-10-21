namespace AgentLang

open System
open System.Text

/// A small, strict HTML tokenizer shared by the `string.html-fragment?`
/// validator (run time) and the template skeleton check (compile time).
///
/// A *fragment* is balanced markup, not a whole document: every opened element
/// is closed in order (void elements and `/>` excepted), comments and
/// `<!doctype …>` are complete, and `<script>`/`<style>` hold raw text up to
/// their own end tag. Anything the tokenizer does not understand is rejected,
/// so a check never silently accepts malformed markup.
///
/// Template holes are opaque balanced units. A hole may sit in element content
/// (`String` or `Html`) or inside a quoted attribute value (`String` only). A
/// String hole may also sit in `<script>`/`<style>` raw text, where escaping
/// keeps it from ending the element. A hole anywhere else (tag or attribute
/// names, unquoted values, between attributes, comments; an Html hole in raw
/// text) is rejected.
///
/// The Flow template check adds context rules (report 302), given each hole's
/// source kind (HoleKind):
/// - in a URL attribute (href, src, action, ...), a hole before any '/', '?',
///   '#' or ':' of the value could start a scheme such as javascript:, so it
///   must be a Url (validated by string.url-safe?) or an Int;
/// - on* handlers, style and srcdoc, and <style> text, take only Int holes:
///   HTML escaping can't make text safe as script or CSS;
/// - <script> text takes no holes unless the script's type marks it as an
///   HTML template (text/template, text/html, text/x-...), whose holes are
///   escaped text like any other.
/// The IR verifier keeps the structural check, which knows only String holes.

/// What a template hole held before lowering, for the context rules.
[<RequireQualifiedAccess>]
type HoleKind =
    | Text
    | Url
    | Int
    | Markup

module HtmlMarkup =
    let private voidElements =
        set [ "area"; "base"; "br"; "col"; "embed"; "hr"; "img"; "input"; "link"; "meta"; "source"; "track"; "wbr" ]

    let private rawTextElements = set [ "script"; "style" ]

    /// Attributes whose value the browser treats as a URL.
    let private urlAttributes =
        set [ "href"; "src"; "action"; "formaction"; "poster"; "cite"; "data"; "background"; "manifest"; "ping"; "xlink:href"; "srcset"; "longdesc"; "usemap"; "codebase"; "archive"; "icon"; "profile"; "lowsrc"; "dynsrc" ]

    /// Attributes whose value is script, CSS or a document: only Int holes.
    let private codeAttribute (name: string) = name.StartsWith("on", StringComparison.Ordinal) || name = "style" || name = "srcdoc"

    /// <script type> values whose text is an HTML template, not script or JSON.
    let private templateScriptType (value: string) =
        let kind = value.Trim().ToLowerInvariant()
        kind = "text/template" || kind = "text/html" || kind.StartsWith("text/x-", StringComparison.Ordinal)

    /// Same bound as the native checker (AL_OWNING_HTML_MAX_DEPTH), which keeps
    /// its element stack in a fixed array.
    let maxDepth = 256

    type private State =
        | Data
        | TagOpen
        | TagName
        | EndTagOpen
        | EndTagName
        | AfterEndTagName
        | BeforeAttribute
        | AttributeName
        | AfterAttributeName
        | BeforeAttributeValue
        | AttributeValueDouble
        | AttributeValueSingle
        | AttributeValueUnquoted
        | SelfClosing
        | MarkupDeclaration
        | Comment
        | Doctype
        | RawText

    let private describe state =
        match state with
        | Data -> "element content"
        | TagOpen | TagName | EndTagOpen | EndTagName | AfterEndTagName -> "a tag name"
        | BeforeAttribute | AttributeName | AfterAttributeName | SelfClosing -> "a tag, between attributes"
        | BeforeAttributeValue | AttributeValueUnquoted -> "an unquoted attribute value"
        | AttributeValueDouble | AttributeValueSingle -> "a quoted attribute value"
        | MarkupDeclaration | Doctype -> "a markup declaration"
        | Comment -> "a comment"
        | RawText -> "<script> or <style> raw text"

    let private isTagNameStart (ch: char) = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
    let private isTagNameChar (ch: char) = isTagNameStart ch || (ch >= '0' && ch <= '9') || ch = '-' || ch = ':'
    let private isSpace (ch: char) = ch = ' ' || ch = '\t' || ch = '\n' || ch = '\r' || ch = '\f'
    let private isAttributeNameChar (ch: char) =
        not (isSpace ch) && ch <> '"' && ch <> '\'' && ch <> '>' && ch <> '/' && ch <> '=' && ch <> '<' && int ch >= 0x20

    /// The shared tokenizer. With `kinds` (one per hole, in order) the context
    /// rules apply; without, only the structure is checked.
    let private checkCore (parts: HtmlTemplatePart list) (kinds: HoleKind list option) : Result<unit, int * string> =
        let stack = Collections.Generic.Stack<string>()
        let name = StringBuilder()
        let mutable state = Data
        let mutable rawElement = ""
        // Context: the current attribute's name and value, whether the value has had a
        // hole or a character that ends a URL scheme, and the current tag's type attribute.
        let attribute = StringBuilder()
        let value = StringBuilder()
        let mutable valueSawHole = false
        let mutable valueSchemeFixed = false
        let mutable tagType = ""
        let mutable rawTemplate = false
        let holeKinds = kinds |> Option.map Array.ofList
        let mutable holeIndex = 0
        let mutable failure: (int * string) option = None
        let fail index message = if failure.IsNone then failure <- Some(index, message)
        let push index tag =
            if stack.Count >= maxDepth then fail index $"Markup nests deeper than {maxDepth} elements."
            else stack.Push tag
        let finishStartTag index selfClosed =
            let tag = name.ToString().ToLowerInvariant()
            if selfClosed || voidElements.Contains tag then state <- Data
            elif rawTextElements.Contains tag then
                rawElement <- tag
                rawTemplate <- tag = "script" && templateScriptType tagType
                state <- RawText
            else
                push index tag
                state <- Data
        let finishEndTag index =
            let tag = name.ToString().ToLowerInvariant()
            if voidElements.Contains tag then fail index $"</{tag}> closes a void element."
            elif stack.Count = 0 then fail index $"</{tag}> has no matching open element."
            elif stack.Peek() <> tag then fail index $"</{tag}> closes <{stack.Peek()}>; elements must close in order."
            else stack.Pop() |> ignore
            state <- Data
        let startValue () =
            valueSawHole <- false
            valueSchemeFixed <- false
            value.Clear() |> ignore
        let endValue () =
            if attribute.ToString().ToLowerInvariant() = "type" then
                // A hole in type= leaves the script's kind unknown: treat it as script.
                tagType <- if valueSawHole then "" else value.ToString()
        /// The context rules for one hole (only when kinds were given).
        let checkContext index (kind: HoleKind) =
            let attributeName = attribute.ToString().ToLowerInvariant()
            match state with
            | AttributeValueDouble | AttributeValueSingle ->
                if codeAttribute attributeName && kind <> HoleKind.Int then
                    fail index $"A hole in {attributeName}=\"...\" must be an Int: HTML escaping does not make text safe as script or CSS."
                elif urlAttributes.Contains attributeName && not valueSawHole && not valueSchemeFixed && kind <> HoleKind.Url && kind <> HoleKind.Int then
                    fail index $"A hole at the start of {attributeName}=\"...\" could begin a javascript: URL; make it a Url (Url.new validates it), or start the value with literal text such as '/'."
                valueSawHole <- true
            | RawText when rawElement = "style" && kind <> HoleKind.Int ->
                fail index "A hole in <style> text must be an Int: HTML escaping does not make text safe as CSS."
            | RawText when rawElement = "script" && not rawTemplate ->
                fail index "A <script> holds script or JSON, where HTML escaping is not enough; put the value in a data- attribute, or use a template script type such as text/template."
            | _ -> ()
        parts
        |> List.iteri (fun index part ->
            if failure.IsNone then
                match part, holeKinds with
                | (HtmlTemplatePart.StringHole | HtmlTemplatePart.IntHole | HtmlTemplatePart.HtmlHole | HtmlTemplatePart.HtmlListHole), Some kinds ->
                    if holeIndex < kinds.Length then checkContext index kinds[holeIndex]
                    holeIndex <- holeIndex + 1
                | _ -> ()
            if failure.IsNone then
                match part with
                | HtmlTemplatePart.StringHole | HtmlTemplatePart.IntHole ->
                    match state with
                    // In <script>/<style> raw text a String hole is escaped like
                    // everywhere else (as ERB does), so it can never contain '<'
                    // and cannot end the element.
                    | Data | AttributeValueDouble | AttributeValueSingle | RawText -> ()
                    | other -> fail index $"A template hole cannot appear in {describe other}."
                | HtmlTemplatePart.HtmlHole | HtmlTemplatePart.HtmlListHole ->
                    match state with
                    | Data -> ()
                    | AttributeValueDouble | AttributeValueSingle -> fail index "An Html hole cannot appear inside an attribute value; use a String (it is escaped)."
                    | other -> fail index $"A template hole cannot appear in {describe other}."
                | HtmlTemplatePart.Text text ->
                    let mutable i = 0
                    while failure.IsNone && i < text.Length do
                        let ch = text[i]
                        match state with
                        | Data ->
                            if ch = '<' then state <- TagOpen
                        | TagOpen ->
                            if isTagNameStart ch then
                                name.Clear().Append(ch) |> ignore
                                tagType <- ""
                                state <- TagName
                            elif ch = '/' then state <- EndTagOpen
                            elif ch = '!' then
                                name.Clear() |> ignore
                                state <- MarkupDeclaration
                            else fail index "'<' must start a tag; write &lt; for a literal less-than sign."
                        | TagName ->
                            if isTagNameChar ch then name.Append(ch) |> ignore
                            elif isSpace ch then state <- BeforeAttribute
                            elif ch = '>' then finishStartTag index false
                            elif ch = '/' then state <- SelfClosing
                            else fail index $"Unexpected '{ch}' in a tag name."
                        | EndTagOpen ->
                            if isTagNameStart ch then
                                name.Clear().Append(ch) |> ignore
                                state <- EndTagName
                            else fail index "'</' must be followed by a tag name."
                        | EndTagName ->
                            if isTagNameChar ch then name.Append(ch) |> ignore
                            elif isSpace ch then state <- AfterEndTagName
                            elif ch = '>' then finishEndTag index
                            else fail index $"Unexpected '{ch}' in an end tag."
                        | AfterEndTagName ->
                            if ch = '>' then finishEndTag index
                            elif not (isSpace ch) then fail index "An end tag cannot have attributes."
                        | BeforeAttribute ->
                            if isSpace ch then ()
                            elif ch = '>' then finishStartTag index false
                            elif ch = '/' then state <- SelfClosing
                            elif isAttributeNameChar ch then
                                attribute.Clear().Append(ch) |> ignore
                                state <- AttributeName
                            else fail index $"Unexpected '{ch}' where an attribute name was expected."
                        | AttributeName ->
                            if isAttributeNameChar ch then attribute.Append(ch) |> ignore
                            elif ch = '=' then state <- BeforeAttributeValue
                            elif isSpace ch then state <- AfterAttributeName
                            elif ch = '>' then finishStartTag index false
                            elif ch = '/' then state <- SelfClosing
                            else fail index $"Unexpected '{ch}' in an attribute name."
                        | AfterAttributeName ->
                            if isSpace ch then ()
                            elif ch = '=' then state <- BeforeAttributeValue
                            elif ch = '>' then finishStartTag index false
                            elif ch = '/' then state <- SelfClosing
                            elif isAttributeNameChar ch then
                                attribute.Clear().Append(ch) |> ignore
                                state <- AttributeName
                            else fail index $"Unexpected '{ch}' after an attribute name."
                        | BeforeAttributeValue ->
                            if isSpace ch then ()
                            elif ch = '"' then
                                startValue ()
                                state <- AttributeValueDouble
                            elif ch = '\'' then
                                startValue ()
                                state <- AttributeValueSingle
                            elif ch = '>' || ch = '=' || ch = '<' || ch = '`' then fail index "An attribute value is missing after '='."
                            else
                                startValue ()
                                value.Append(ch) |> ignore
                                state <- AttributeValueUnquoted
                        | AttributeValueDouble | AttributeValueSingle ->
                            if (state = AttributeValueDouble && ch = '"') || (state = AttributeValueSingle && ch = '\'') then
                                endValue ()
                                state <- BeforeAttribute
                            else
                                value.Append(ch) |> ignore
                                if ch = '/' || ch = '?' || ch = '#' || ch = ':' then valueSchemeFixed <- true
                        | AttributeValueUnquoted ->
                            if isSpace ch then
                                endValue ()
                                state <- BeforeAttribute
                            elif ch = '>' then
                                endValue ()
                                finishStartTag index false
                            elif ch = '"' || ch = '\'' || ch = '=' || ch = '<' || ch = '`' then fail index $"Unexpected '{ch}' in an unquoted attribute value."
                            else value.Append(ch) |> ignore
                        | SelfClosing ->
                            if ch = '>' then finishStartTag index true
                            else fail index "'/' in a tag must be followed by '>'."
                        | MarkupDeclaration ->
                            name.Append(ch) |> ignore
                            let text = name.ToString()
                            if text = "--" then
                                name.Clear() |> ignore
                                state <- Comment
                            elif text.Length = 7 && String.Equals(text, "doctype", StringComparison.OrdinalIgnoreCase) then
                                if stack.Count <> 0 then fail index "<!doctype> may appear only at the top level."
                                state <- Doctype
                            elif not ("--".StartsWith(text, StringComparison.Ordinal) || "doctype".StartsWith(text, StringComparison.OrdinalIgnoreCase)) then
                                fail index "'<!' must start a comment (<!--) or <!doctype>."
                        | Comment ->
                            name.Append(ch) |> ignore
                            if name.Length >= 3 && name.ToString(name.Length - 3, 3) = "-->" then
                                name.Clear() |> ignore
                                state <- Data
                        | Doctype ->
                            if ch = '>' then state <- Data
                            elif ch = '<' then fail index "Unexpected '<' in <!doctype>."
                        | RawText ->
                            // Raw text ends only at its own end tag.
                            let closing = "</" + rawElement
                            if ch = '<'
                               && i + closing.Length <= text.Length
                               && String.Compare(text, i, closing, 0, closing.Length, StringComparison.OrdinalIgnoreCase) = 0
                               && (i + closing.Length = text.Length || not (isTagNameChar text[i + closing.Length])) then
                                push index rawElement
                                name.Clear().Append(rawElement) |> ignore
                                i <- i + closing.Length - 1
                                state <- EndTagName
                        i <- i + 1)
        match failure with
        | Some error -> Error error
        | None ->
            let last = max 0 (parts.Length - 1)
            if state <> Data then Error(last, $"The markup ends inside {describe state}.")
            elif stack.Count > 0 then Error(last, $"<{stack.Peek()}> is not closed.")
            else Ok()

    /// Checks a template (or, with one Text part, a plain fragment): structure only.
    /// Returns the index of the offending part and a message on failure.
    let check (parts: HtmlTemplatePart list) : Result<unit, int * string> = checkCore parts None

    /// Structure plus the context rules, given each hole's source kind in order.
    let checkContexts (parts: HtmlTemplatePart list) (kinds: HoleKind list) : Result<unit, int * string> = checkCore parts (Some kinds)

    /// `string.html-fragment?`: true when the text is balanced, well-formed markup.
    /// (Defined below; see also HtmlBuiltIn for the built-in Html type.)
    let isFragment (value: string) =
        not (isNull value) && (check [ HtmlTemplatePart.Text value ] |> Result.isOk)

/// The built-in Url type (report 302): a String validated by string.url-safe?.
/// An html template needs a Url where a hole starts a URL attribute's value.
/// The declaration is added automatically to the first Flow/2 document that uses it.
module UrlBuiltIn =
    let typeName = "Url"
    let validatorPrimitive = "string.url-safe?"
    let source = "type Url : String {\n    validate string.url-safe?\n}\n"

    let usedBy (text: string) =
        not (isNull text) && Text.RegularExpressions.Regex.IsMatch(text, @"(?<![A-Za-z0-9_.-])Url(?![A-Za-z0-9_-])")

/// The built-in Html type. `Html.new(text)` escapes its String (like a String
/// template hole); `html.raw(text)` keeps markup and is checked by
/// `string.html-fragment?` (at compile time for literals). The declaration is
/// added automatically to the first Flow/2 document that uses Html.
module HtmlBuiltIn =
    let typeName = "Html"
    let rawName = "html.raw"
    let source = "type Html : String {\n    validate string.html-fragment?\n}\n"

    let usedBy (text: string) =
        not (isNull text)
        && (text.Contains("html\"", StringComparison.Ordinal)
            || text.Contains("html.raw", StringComparison.Ordinal)
            || Text.RegularExpressions.Regex.IsMatch(text, @"(?<![A-Za-z0-9_.-])Html(?![A-Za-z0-9_-])"))

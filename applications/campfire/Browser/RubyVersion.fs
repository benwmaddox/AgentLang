// Ruby-version behavior is adapted from 37signals' Campfire User-Agent implementation
// at pinned source revision 6dae2fd3c1a2dce61ca11beb731bcce2b42b67ad
// (useragent gem 0.16.11); see MIT-LICENSE.37signals.
namespace AgentLang.Campfire.Browser

open System
open System.Text

module RubyVersion =
    type private Segment =
        | Integer of string
        | Text of string

    let private isAsciiDigit value = value >= '0' && value <= '9'
    let private isAsciiLetter value =
        (value >= 'A' && value <= 'Z')
        || (value >= 'a' && value <= 'z')

    let private isRubySpace = function
        | ' ' | '\t' | '\n' | '\u000b' | '\u000c' | '\r' -> true
        | _ -> false

    let private isBlank (value: string) =
        value |> Seq.forall isRubySpace

    let private isComparable (value: string) =
        let digits =
            value
            |> Seq.takeWhile isAsciiDigit
            |> Seq.length

        not (isBlank value)
        && digits > 0
        && (digits = value.Length || value[digits] = '.')

    let private normalizedInteger (digits: string) =
        let trimmed = digits.TrimStart([| '0' |])
        if trimmed.Length = 0 then "0" else trimmed

    let private scanSequences (value: string) =
        let segments = ResizeArray<Segment>()
        let mutable index = 0

        while index < value.Length do
            if isAsciiDigit value[index] then
                let start = index
                while index < value.Length && isAsciiDigit value[index] do
                    index <- index + 1
                segments.Add(Integer(normalizedInteger (value.Substring(start, index - start))))
            elif isAsciiLetter value[index] then
                let mutable finish = index + 1
                while finish < value.Length
                      && (isAsciiLetter value[finish]
                          || isAsciiDigit value[finish]
                          || value[finish] = '-') do
                    finish <- finish + 1

                if finish = value.Length then
                    segments.Add(Text(value.Substring(index)))
                    index <- finish
                else
                    index <- index + 1
            else
                index <- index + 1

        segments |> Seq.toArray

    let private segments (value: string) =
        if isBlank value then
            [||]
        elif isComparable value then
            scanSequences value
        else
            [| Text value |]

    let private compareUtf8 (left: string) (right: string) =
        let leftBytes = Encoding.UTF8.GetBytes left
        let rightBytes = Encoding.UTF8.GetBytes right
        let mutable index = 0

        while index < leftBytes.Length
              && index < rightBytes.Length
              && leftBytes[index] = rightBytes[index] do
            index <- index + 1

        if index = leftBytes.Length && index = rightBytes.Length then
            0
        elif index = leftBytes.Length then
            -1
        elif index = rightBytes.Length then
            1
        elif leftBytes[index] < rightBytes[index] then
            -1
        else
            1

    let private compareSegment left right =
        match left, right with
        | Integer leftDigits, Integer rightDigits ->
            let lengthOrder = compare leftDigits.Length rightDigits.Length
            if lengthOrder <> 0 then lengthOrder else String.CompareOrdinal(leftDigits, rightDigits)
        | Text _, Integer _ -> -1
        | Integer _, Text _ -> 1
        | Text leftText, Text rightText -> compareUtf8 leftText rightText

    /// Matches the pinned useragent gem Version#<=>, including its asymmetric result for
    /// non-comparable left-hand strings and its first-six-segment limit.
    let compare (left: string) (right: string) =
        let left = if isNull left then "" else left
        let right = if isNull right then "" else right

        if isComparable left then
            let leftSegments = segments left
            let rightSegments = segments right
            let zero = Integer "0"
            let mutable result = 0
            let mutable index = 0

            while index < 6 && result = 0 do
                let leftSegment = if index < leftSegments.Length then leftSegments[index] else zero
                let rightSegment = if index < rightSegments.Length then rightSegments[index] else zero
                result <- compareSegment leftSegment rightSegment
                index <- index + 1

            result
        elif String.Equals(left, right, StringComparison.Ordinal) then
            0
        else
            -1

    /// Returns true when the pinned Ruby comparison says version is at least minimum.
    let isAtLeast version minimum =
        compare version minimum >= 0

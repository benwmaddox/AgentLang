namespace AgentLang

open System
open System.Globalization

/// Pure, deterministic value checks shared by trusted primitive operations.
/// This module deliberately depends only on .NET value types and never on a
/// business domain or host capability.
module TrustedValues =
    let private guidError = "INVALID_GUID"
    let private integerOverflow = "INT_OVERFLOW"
    let private instantError = "INVALID_INSTANT"
    let private instantRangeError = "INSTANT_RANGE"

    let private isAsciiLetterOrDigit (value: char) =
        (value >= 'A' && value <= 'Z')
        || (value >= 'a' && value <= 'z')
        || (value >= '0' && value <= '9')

    let guidCanonical (value: string) =
        if isNull value then false
        else
            match Guid.TryParseExact(value, "D") with
            | true, identifier -> String.Equals(identifier.ToString("D"), value, StringComparison.Ordinal)
            | false, _ -> false

    /// Accept exactly the forms accepted by Guid.TryParse and store lower-case D text.
    let guidNormalize (value: string) =
        if isNull value then Error guidError
        else
            match Guid.TryParse value with
            | true, identifier -> Ok(identifier.ToString("D"))
            | false, _ -> Error guidError

    /// Match the bounded ASCII policy documented by the conventional business fixture.
    /// The scan is linear and rejects overlong inputs before inspecting their contents.
    let emailAddressValid (value: string) =
        if isNull value || value.Length = 0 || value.Length > 254 then false
        else
            let atIndex = value.IndexOf('@')
            if atIndex <= 0 || atIndex <> value.LastIndexOf('@') || atIndex = value.Length - 1 then false
            else
                let localValid =
                    let mutable valid = true
                    let mutable previousWasDot = false
                    for index = 0 to atIndex - 1 do
                        let character = value[index]
                        if character = '.' then
                            if index = 0 || previousWasDot then valid <- false
                            previousWasDot <- true
                        else
                            let allowed =
                                isAsciiLetterOrDigit character
                                || character = '_'
                                || character = '%'
                                || character = '+'
                                || character = '-'
                            if not allowed then valid <- false
                            previousWasDot <- false
                    valid && not previousWasDot

                let domainValid =
                    let mutable valid = true
                    let mutable labelCount = 0
                    let mutable labelStart = atIndex + 1
                    for index = atIndex + 1 to value.Length do
                        if index = value.Length || value[index] = '.' then
                            let labelEnd = index - 1
                            if labelEnd < labelStart
                               || not (isAsciiLetterOrDigit value[labelStart])
                               || not (isAsciiLetterOrDigit value[labelEnd]) then
                                valid <- false
                            for labelIndex = labelStart to labelEnd do
                                let character = value[labelIndex]
                                if not (isAsciiLetterOrDigit character) && character <> '-' then
                                    valid <- false
                            labelCount <- labelCount + 1
                            labelStart <- index + 1
                    valid && labelCount >= 2

                localValid && domainValid

    let addChecked (left: int64) (right: int64) =
        try Ok(Checked.(+) left right)
        with :? OverflowException -> Error integerOverflow

    let multiplyChecked (left: int64) (right: int64) =
        try Ok(Checked.(*) left right)
        with :? OverflowException -> Error integerOverflow

    /// Scale a signed Int64 value by a rational factor without losing precision
    /// in the intermediate product. Integer division truncates toward zero.
    let scaleRatioTowardZero (value: int64) (numerator: int64) (denominator: int64) =
        if denominator = 0L then Error "DIVIDE_BY_ZERO"
        else
            // The product of any two Int64 values fits in Int128. Dividing only
            // after that exact multiplication preserves fractions and allows
            // large intermediate products whose final quotient still fits.
            let quotient =
                (Int128.CreateChecked value * Int128.CreateChecked numerator)
                / Int128.CreateChecked denominator
            if quotient < Int128.CreateChecked Int64.MinValue || quotient > Int128.CreateChecked Int64.MaxValue then
                Error integerOverflow
            else
                Ok(int64 quotient)

    let private parseInstant (value: string) : Result<DateTimeOffset, string> =
        let isAsciiDigit (character: char) = character >= '0' && character <= '9'
        let hasAsciiDigits (text: string) start count =
            let mutable valid = true
            for index = start to start + count - 1 do
                if not (isAsciiDigit text[index]) then valid <- false
            valid

        let hasExplicitZone (text: string) =
            text.EndsWith("Z", StringComparison.Ordinal)
            || text.EndsWith("z", StringComparison.Ordinal)
            || (text.Length >= 6
                && (text[text.Length - 6] = '+' || text[text.Length - 6] = '-')
                && hasAsciiDigits text (text.Length - 5) 2
                && text[text.Length - 3] = ':'
                && hasAsciiDigits text (text.Length - 2) 2)

        let hasIsoShape (text: string) =
            let length = text.Length
            let fixedShape =
                length >= 20
                && hasAsciiDigits text 0 4
                && text[4] = '-'
                && hasAsciiDigits text 5 2
                && text[7] = '-'
                && hasAsciiDigits text 8 2
                && text[10] = 'T'
                && hasAsciiDigits text 11 2
                && text[13] = ':'
                && hasAsciiDigits text 14 2
                && text[16] = ':'
                && hasAsciiDigits text 17 2

            if not fixedShape then false
            else
                let zoneStart =
                    if text.EndsWith("Z", StringComparison.Ordinal) || text.EndsWith("z", StringComparison.Ordinal) then length - 1
                    else length - 6
                if zoneStart = 19 then true
                elif zoneStart > 19 && text[19] = '.' then
                    let fractionLength = zoneStart - 20
                    fractionLength >= 1
                    && fractionLength <= 7
                    && hasAsciiDigits text 20 fractionLength
                else false

        if isNull value || value.Length < 20 || value.Length > 33 || not (hasExplicitZone value) || not (hasIsoShape value) then
            Error instantError
        else
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | false, _ -> Error instantError
            | true, instant ->
                try Ok(instant.ToUniversalTime())
                with :? ArgumentOutOfRangeException -> Error instantError

    let parseUtc (value: string) : Result<string, string> =
        parseInstant value
        |> Result.map (fun instant -> instant.ToString("O", CultureInfo.InvariantCulture))

    let instantIsCanonicalUtc (value: string) =
        match parseUtc value with
        | Ok normalized -> String.Equals(normalized, value, StringComparison.Ordinal)
        | Error _ -> false

    let instantBefore (left: string) (right: string) : Result<bool, string> =
        if not (instantIsCanonicalUtc left) || not (instantIsCanonicalUtc right) then Error instantError
        else
            match parseInstant left, parseInstant right with
            | Ok leftInstant, Ok rightInstant -> Ok(leftInstant < rightInstant)
            | _ -> Error instantError

    let instantAddDays (value: string) (days: int64) : Result<string, string> =
        if not (instantIsCanonicalUtc value) then Error instantError
        // DateTime's complete range is fewer than 2^22 days. This guard keeps
        // every accepted Int64 exactly representable before calling AddDays(double).
        elif days < -3_652_058L || days > 3_652_058L then Error instantRangeError
        else
            match parseInstant value with
            | Error _ -> Error instantError
            | Ok instant ->
                try
                    instant.AddDays(float days).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
                    |> Ok
                with :? ArgumentOutOfRangeException -> Error instantRangeError

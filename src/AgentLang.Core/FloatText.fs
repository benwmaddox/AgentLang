namespace AgentLang

open System
open System.Globalization
open System.Numerics

/// Invariant shortest-roundtrip text for binary64 values.
module FloatText =
    let private renderFinite (value: double) =
        let bits = BitConverter.DoubleToUInt64Bits value
        let negative = (bits &&& 0x8000000000000000UL) <> 0UL
        let magnitudeBits = bits &&& 0x7FFFFFFFFFFFFFFFUL

        if magnitudeBits = 0UL then
            if negative then "-0.0" else "0.0"
        else
            let fraction = magnitudeBits &&& 0x000FFFFFFFFFFFFFUL
            let exponentField = int ((magnitudeBits >>> 52) &&& 0x7FFUL)
            let mantissa, binaryExponent =
                if exponentField = 0 then
                    fraction, -1074
                else
                    0x0010000000000000UL ||| fraction, exponentField - 1075

            let mutable remainder = BigInteger(mantissa) <<< 2
            let mutable denominator = BigInteger(4)
            let binaryScale = max 0 binaryExponent
            let denominatorScale = max 0 (-binaryExponent)

            if binaryScale > 0 then
                remainder <- remainder <<< binaryScale
            if denominatorScale > 0 then
                denominator <- denominator <<< denominatorScale

            let inclusiveBoundaries = (mantissa &&& 1UL) = 0UL
            let mutable lowerMargin =
                BigInteger(if exponentField > 1 && fraction = 0UL then 1 else 2)
                <<< binaryScale
            let mutable upperMargin = BigInteger(2) <<< binaryScale
            let mutable decimalExponent = 0

            while remainder < denominator do
                remainder <- remainder * BigInteger(10)
                lowerMargin <- lowerMargin * BigInteger(10)
                upperMargin <- upperMargin * BigInteger(10)
                decimalExponent <- decimalExponent - 1

            while remainder >= denominator * BigInteger(10) do
                denominator <- denominator * BigInteger(10)
                decimalExponent <- decimalExponent + 1

            let digits = Array.zeroCreate<char> 17
            let mutable digitCount = 0
            let mutable stopped = false
            let mutable roundUp = false

            while digitCount < digits.Length && not stopped do
                let digit = int (remainder / denominator)
                digits[digitCount] <- char (int '0' + digit)
                digitCount <- digitCount + 1
                remainder <- remainder % denominator

                let lowerComparison = remainder.CompareTo(lowerMargin)
                let low = lowerComparison < 0 || (lowerComparison = 0 && inclusiveBoundaries)
                let upperComparison = (denominator - remainder).CompareTo(upperMargin)
                let high = upperComparison < 0 || (upperComparison = 0 && inclusiveBoundaries)

                if low || high then
                    stopped <- true
                    if low && high then
                        let midpointComparison = (remainder * BigInteger(2)).CompareTo(denominator)
                        roundUp <- midpointComparison > 0 || (midpointComparison = 0 && ((int digits[digitCount - 1] - int '0') % 2 <> 0))
                    else
                        roundUp <- high
                else
                    remainder <- remainder * BigInteger(10)
                    lowerMargin <- lowerMargin * BigInteger(10)
                    upperMargin <- upperMargin * BigInteger(10)

            if not stopped then
                invalidOp "A finite binary64 value did not fit in 17 shortest-roundtrip decimal digits."

            if roundUp then
                let mutable carryIndex = digitCount - 1
                while carryIndex >= 0 && digits[carryIndex] = '9' do
                    digits[carryIndex] <- '0'
                    carryIndex <- carryIndex - 1

                if carryIndex < 0 then
                    digits[0] <- '1'
                    decimalExponent <- decimalExponent + 1
                else
                    digits[carryIndex] <- char (int digits[carryIndex] + 1)

            while digitCount > 1 && digits[digitCount - 1] = '0' do
                digitCount <- digitCount - 1

            let significand = String(digits, 0, digitCount)
            let body =
                if decimalExponent >= -4 && decimalExponent <= 16 then
                    let decimalPoint = decimalExponent + 1
                    if decimalPoint <= 0 then
                        "0." + String('0', -decimalPoint) + significand
                    elif decimalPoint >= digitCount then
                        significand + String('0', decimalPoint - digitCount) + ".0"
                    else
                        significand.Substring(0, decimalPoint) + "." + significand.Substring(decimalPoint)
                else
                    let exponentSign = if decimalExponent < 0 then "-" else "+"
                    let exponentDigits = Math.Abs(decimalExponent).ToString("D2", CultureInfo.InvariantCulture)
                    let mantissaText =
                        if digitCount = 1 then significand
                        else significand.Substring(0, 1) + "." + significand.Substring(1)
                    mantissaText + "E" + exponentSign + exponentDigits

            if negative then "-" + body else body

    /// Render finite values canonically; retain explicit invariant names for non-finite diagnostics.
    let format (value: double) =
        if Double.IsNaN value then "NaN"
        elif Double.IsPositiveInfinity value then "Infinity"
        elif Double.IsNegativeInfinity value then "-Infinity"
        else renderFinite value

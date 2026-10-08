The isolated trial succeeded. I added `BoundedRange` with `start` and `finish` fields, a validator enforcing `start <= finish`, and `range.width`, which returns `finish - start`.

All five attached tests passed, including equal endpoints and rejection of reversed construction with `RECORD_VALIDATION_FAILED`. `range.width` and the generated record constructor/accessors were committed at library maturity; `range.is-valid` was committed at project maturity. Its tests can only observe `true`, since invalid ranges cannot be constructed.

The broker task committed successfully, and the broker closed with exit code 0.

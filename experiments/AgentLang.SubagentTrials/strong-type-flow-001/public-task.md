# Add an explicit speed conversion

Inspect the available project types and add a pure library word named
delivery.speed-kph with the signature:

    Delivery -> KilometersPerHour

It should convert the Delivery speed from meters per second to kilometers per
hour by multiplying by 3.6. Use the nominal value accessors and constructors
explicitly so the conversion between the two unit types is visible in the word.

Add meaningful tests for zero, positive, fractional, and signed speed inputs,
run them with complete own instruction and branch coverage, and commit the tested word durably through the language
protocol. Preserve the seeded types, validator, record, and existing tests.

The Email policy is illustrative: require an at sign and dot, reject spaces,
and reject leading or trailing at signs. It is not the full Internet email
standard. Both speed types are distinct Float wrappers with no range restriction;
zero, fractional, and signed speeds remain supported. Do not add implicit unit
conversion or change that policy to make a test pass.

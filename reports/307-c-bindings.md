# 307 — Item 7: calling C libraries, with declared effects

Item 7 lets a Flow word be a C function from an existing library, with its
effects declared on the word like any other word's. It works the way
crudspec's `= go "pkg".Func` does:

```flow
fn zlib.crc32-update(crc: Int, data: Bytes) -> Int = c "unsigned long crc32_z(unsigned long, bytes)" from "z"

fn zlib.crc32(data: Bytes) -> Int {
    doc "zlib's CRC-32 of data, from seed 0."
    zlib.crc32-update(0, data)
}
```

## The prototype is part of the binding

The agreed syntax was `= c "crc32" from "z"`. The quoted string is now the C
prototype, written with C type names, for two reasons:
- **Widths differ by platform.** zlib's `crc32_z` takes and returns
  `unsigned long`, which is 64-bit on Linux and 32-bit on Windows. A fixed
  "an Int is `int64_t`" rule would read garbage in the upper half on Windows.
- **The C shape is not the Flow shape.** zlib's CRC takes a seed and a pointer
  plus a length, while the word a caller wants takes only the data. So the
  binding names the real function (`crc32-update`), and `crc32` is a one-line
  Flow word on top.

**Types.** A prototype is `<return type> <symbol>(<parameter types>)`, using a
closed list of types:
- the fixed-width integers `int8_t` to `uint64_t`;
- `short`, `int`, `unsigned`, `long`, `unsigned long`, `long long`;
- `size_t` and `ptrdiff_t`;
- `bool` and `double`;
- `bytes` and `text`.

`bytes` and `text` each stand for a pointer and a `size_t` length: a Bytes
value, or a String's UTF-8 bytes. The pointer is read-only and valid only for
the call. It is never null, even for an empty value, because zlib, for
example, treats a null buffer specially.

**Flow types.** Each Flow parameter fits one prototype entry:
- an Int goes to any integer type;
- a Bool goes to `bool` or an integer;
- a Float goes to `double`;
- Bytes goes to `bytes`, and a String goes to `text`.

A binding returns an integer (as an Int), a `bool` or a `double`.

**Range checks.** An Int passed to a narrower C type is range-checked. Out of
range, the call fails with `RUNTIME_C_ARGUMENT_RANGE` on both backends; nothing
is silently truncated. A 64-bit unsigned parameter takes any Int and passes its
bits, so it round-trips with a `uint64_t` return.

**Effects.** A binding declares them after the library, for example
`… from "z" effects fs.read`, and has none by default. As with any word,
callers must declare whatever effects their callees have, so the capability
model holds. The C code itself is trusted to do what the effects say.

**Loading.** The library is loaded, and the symbol found, on the first call.
Defining a binding never fails because a library is missing; calling it fails
with one of these:
- `RUNTIME_C_LIBRARY_MISSING`;
- `RUNTIME_C_SYMBOL_MISSING`.

A library name such as `"z"` is tried as these files, and a name with a dot or
a slash is used as written:
- **Linux:** `libz.so`, `libz.so.1`;
- **Windows:** `z.dll`, `libz.dll` and the `…1.dll` forms.

## How it runs

**One word, nothing new to store.** A binding is an ordinary word whose body is
one call of a reserved intrinsic, holding the library and the prototype. So the
dictionary stores, fingerprints, describes and tests it like any other word,
and the formatter writes it back as `= c "…" from "…"`. The call lowers to one
non-call IR step, `ForeignCall`, which the verifier checks against the
prototype.

**Interpreter.** It loads the library with .NET's `NativeLibrary` and builds
one unmanaged `calli` stub per prototype, with this platform's widths. Bytes
and Strings are pinned for the call.

**Native.** The generated module holds, for each binding:
- the library and symbol names;
- a cached address;
- a small resolver: `dlopen`/`dlsym` on Linux, `LoadLibraryA`/`GetProcAddress`
  on Windows.

The resolver runs on the first call and publishes the address with an atomic
store. The call itself:
1. range-checks and narrows each Int to its C type;
2. asks the runtime for each Bytes or String pointer and length
   (`al_owning_foreign_bytes`, a pure helper that checks the value is in the
   live arena);
3. calls through the address, with `signext`/`zeroext` on narrow arguments;
4. widens the result to 64 bits.

Modules without bindings are unchanged. No build script needed new link
flags: `dlopen` is in glibc, and `kernel32.lib`, which the test DLLs' link now
includes, adds nothing to modules that don't use it.

## Checks

The new suite `AgentLang.ForeignCalls.Tests` builds a small C library,
`flowffi`, with the toolchain's clang. It has one function per ABI shape:
- signed and unsigned 8-, 16-, 32- and 64-bit integers;
- `long` and `unsigned long`, at each platform's width;
- `double` and `bool`;
- `bytes` first, in the middle and last;
- `text`, and `text` with `bytes`;
- a CRC-32 that takes a seed.

The suite then checks:
- **Rejections:** an unknown C type, a malformed prototype, a returned pointer,
  a String passed as bytes, the wrong number of parameters, an output type that
  the return does not give, and a missing library clause.
- **Formatting:** bindings, with and without effects, format as written and
  then format to themselves.
- **Commits:** every binding commits with its attached tests, which run in the
  interpreter. So do three bindings whose tests expect a missing library, a
  missing symbol and an out-of-range argument.
- **Interpreter values:** about 150 inputs around every range boundary, from 0
  and ±1 through 2³¹, 2³², 5·10⁹ and the Int64 extremes, against an F#
  reference. They include:
  - the C wrap-around of `int64_t` addition;
  - a nonzero seed with empty data;
  - a String with `é🚀`;
  - a value of 300 bytes.
- **Native = interpreter:** every binding over the same inputs, at O0 and
  trusted O2, including the range failures and the missing library and symbol.

That is 826 assertions.

- **Suites:** all 24 pass, including the new one.
- **Campfire:** it has no bindings, so its `module.ll` is byte-identical to the
  item-2c build. The build takes 38.4 s.

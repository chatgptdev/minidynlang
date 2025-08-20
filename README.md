# MiniDynLang

A tiny, expressive, dynamically-typed programming language implemented in C#. MiniDynLang features objects, arrays, first-class and arrow functions, destructuring (with defaults and rest), computed keys, named arguments, optional chaining and nullish operations, exceptions with try/catch/finally, a simple module system, and a compact standard library.

For the complete language specification, see the Language Reference:
- [LanguageReference.md](LanguageReference.md)

---

## Table of contents

- Highlights
- Install & build
- CLI & REPL
- Quick start
- Language tour
  - Values, truthiness, equality
  - Variables & scope
  - Expressions & operators
  - Strings & interpolation
  - Arrays & objects
  - Destructuring (decl & assign)
  - Functions & arrow functions
  - this & methods
  - Named arguments
  - Optional chaining & nullish
  - Control flow
  - Errors & exceptions
  - Modules
- Standard library (selected)
- Design and implementation notes
- Tests
- License

---

## Highlights

- Values: number (int64 / double / big integer), string, boolean, nil, array, object, function
- Variables: var (function/global-scoped), let/const (block-scoped with TDZ)
- First-class functions: defaults, rest, closures; arrow functions
- Computed object keys: { [expr]: value }
- Destructuring: in declarations and in assignment statements; defaults and ...rest; alias to lvalues
- Named arguments for user functions; mixing with positional; defaults and rest supported
- Optional chaining (. ?.[]) and nullish coalescing (??) plus nullish assignment (??=)
- Indexing: arrays (negative indices allowed), strings (1-char result; negatives allowed)
- Loops: for-of (values), for-in (keys/indices), classic for, while; break/continue
- Errors: throw any value; try/catch/finally; runtime errors as objects with name/message/stack
- Modules: require(...) with CommonJS-like exports/module.exports; caching and cycle support
- Comma operator: (a, b, c) sequences expressions; result is last

---

## Install & build

Prerequisite: .NET Framework 4.8 (or a compatible SDK for building/running on your system).

```bat
git clone https://github.com/chatgptdev/minidynlang.git
cd minidynlang
dotnet build -c Release
```

---

## CLI & REPL

Start a REPL:

```bat
bin\Release\minidynlang.exe
```

Run the demo program:

```bat
bin\Release\minidynlang.exe demo.mdl
```

Run the test suite:

```bat
bin\Release\minidynlang.exe tests\tests.mdl
```

---

## Quick start

```text
println("Hello, world");
let x = 41; x += 1;
println(x == 42 ? "ok" : "fail");

let o = { a: 1, b: 2, d: "str" };
o.a += 5; o["b"] = o["b"] * 10;
println(o.a, o.b); // 6 20
```

---

## Language tour

### Values, truthiness, equality

- Types: number, string, boolean, nil, array, object, function
- Truthiness:
  - false: nil, boolean false, numeric 0, empty string ""
  - true: non-0 numbers, non-empty strings, functions, arrays, objects
- Equality:
  - Same-type: numeric/string compare by value; arrays/objects/functions by reference
  - Cross-type: number == string is numeric if the string parses; otherwise false

### Variables & scope

- var: function-scoped (or global), reassignable
- let: block-scoped, reassignable, Temporal Dead Zone (TDZ)
- const: block-scoped, must be initialized, non-reassignable binding (object contents can still mutate)

### Expressions & operators

- Arithmetic: + - * / % (division is integer when exact; otherwise double)
- Unary: -x, +x, !x
- Comparison: == != < <= > >=
- Logical: && || ! (keywords and/or/not also supported)
- Ternary: cond ? a : b
- Nullish: a ?? b; a ??= b
- Optional chaining: obj?.prop, obj?.[key], obj?.method(args)
- Comma operator: (a, b, c) evaluates left-to-right, returns c
- Arrays + concatenate with + and += when both sides are arrays

Indexing:
- Arrays: arr[i] (negative indices allowed; -1 is last)
- Strings: s[i] returns a 1-character string; negative indices allowed; out-of-bounds is a runtime error
- Objects: obj[key] (key coerced to string)

### Strings & interpolation

- Normal strings: "hello", escapes: \n \r \t \" \\ \0 \xNN \uNNNN
- Interpolation: "Hello, ${name}!"
- Raw triple-quoted: """anything including " and \ and ${...}""" (no escapes, no interpolation)

### Arrays & objects

- Arrays: [1, 2, "x"] (holes allowed via elision: [1, , 3])
- Objects: { a: 1, b: 2 } with insertion-ordered keys
- Shorthand properties: let a=1; { a } → { a:1 }
- Computed keys: { ["k" + x]: v }

### Destructuring (decl & assign)

- Declarations: let/const/var patterns with defaults and ...rest
- Assignment statement form for swapping or assigning into lvalues
- Object aliases can target lvalues (including obj.prop or obj[key])

Examples:
```text
let [a, b=2, ...rest] = [1, , 3, 4, 5]; // a=1, b=2, rest=[3,4,5]
const { x, y: z=10, ...others } = { x:1 };
{ a: obj.a, b: obj.b } = { a: 7, b: 8 }; // assign into properties
```

### Functions & arrow functions

- Function literal: fn (params) { ... }
- Declaration: fn name(params) { ... }
- Arrow functions:
  - x => x + 1
  - (a, b=2, ...rest) => { return a + b; }
  - Expression body returns value; block body requires return

Parameters:
- Defaults: fn (x=10, y=2*x) { ... }
- Rest: fn (a, ...rest) { ... } (must be last)
- Unique parameter names enforced

Tail-call optimization: self-tail-calls in user functions are optimized (including named-arg variants).

### this & methods

- Method call target.prop(args) binds this to target for normal (non-arrow) user functions
- Arrow functions capture this lexically at creation; calling as a method does not rebind this

### Named arguments

- User functions support named args: f(x: 1, y: 2)
- You may mix positional and named; after the first named arg, remaining positionals go to rest (if present), otherwise error
- Defaults evaluate in the callee environment, left-to-right
- Built-ins do not support named args

### Optional chaining & nullish

- obj?.prop, obj?.[key], obj?.method(args) → nil if base is nil (no call/access)
- a ?? b returns a unless it is nil; otherwise b
- a ??= b assigns only if target is nil
- Optional chaining is supported on assignment targets; if the base is nil, the assignment short-circuits without evaluating RHS or index/key

### Control flow

- if (cond) stmt else stmt
- while (cond) stmt
- for (init; cond; inc) stmt (init can be decl or expression)
- for-of: iterates values of arrays/strings/objects; nil yields zero iterations
- for-in: iterates keys/indices (strings for arrays/strings); nil yields zero iterations
- break, continue

### Errors & exceptions

- throw expr (any value)
- try/catch/finally; catch with or without a binding
- Built-ins: error(msg) returns an error object; raise(msg) throws one
- Runtime errors (e.g., division by zero) are caught as objects with:
  - name: "RuntimeError"
  - message, at, stack (stack is an array of frames)

### Modules

- require(specifier)
  - Resolves absolute/relative paths; tries "", ".mdl", ".minidyn"; directories use index
  - Execution environment has exports and module objects; return value is module.exports if set; else exports
  - Caching by absolute path; cycles supported (temporary exports provided during load)

---

## Standard library (selected)

I/O and time
- print(...), println(...), gets()
- now_ms(), sleep_ms(ms)

Type & conversion
- type(x), to_number(x), to_string(x), length(x)

Math
- abs, floor, ceil, round, sqrt, pow, min, max
- random(), srand(seed)

Strings
- substring, index_of, contains, starts_with, ends_with
- to_upper, to_lower, trim, split, replace, repeat
- pad_start, pad_end

Parsing & JSON
- parse_int, parse_float
- json_stringify(value[, pretty=false]) (throws on cycles)
- json_parse(s) (JSON null → nil)

Arrays & objects
- array(...), push, pop, slice(arrOrStr, start[, end]) (negative indices)
- join, at(arr, idx), set_at, clone
- keys, values, entries, from_entries
- has_key, remove_key, merge

Functional & utility
- map, filter, reduce, sort([, comparator])
- unique, deep_equal
- range(n | start,end[,step])

See LanguageReference.md for the full list and details.

---

## Design and implementation notes

- Numbers automatically promote across Int64 / Double / BigInteger as needed
- Division is integer when exact; otherwise double
- Arrays/objects preserve insertion order for keys; equality is by identity
- Strings index to 1-character strings; assignment into string indices is an error
- let/const have TDZ checks; var is function/global-scoped and cannot redeclare a block-scoped name in the same function/global
- Named arguments are supported for user functions (not for built-ins)
- Comma operator is supported in expressions; result is the last subexpression
- Optional chaining applies to property/index access, calls, and assignment targets
- A simple bytecode compiler/VM accelerates a subset of functions; the interpreter handles full semantics (including defaults/rest/try/finally/optional chaining)

For full semantics and edge cases, see LanguageReference.md.

---

## Tests

Run the test suite:

```bat
bin\Release\minidynlang.exe tests\tests.mdl
```

The suite covers arithmetic and number literals (hex/binary/underscores/exponents/bigints), logical and ternary ops, the comma operator, strings (escapes, interpolation, raw), arrays/objects (including computed keys and insertion order), destructuring, functions/arrow/this/named args, loops (for-of/for-in/classic), errors and finally semantics, optional chaining with assignment, JSON, cycle-safe stringification, and tail-call optimization.

---

## License

MIT
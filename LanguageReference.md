MiniDynLang at a glance
- Dynamically typed, expression-oriented scripting language.
- First-class functions (function literals, arrow functions), closures, this-binding.
- Arrays and objects with insertion-ordered keys, destructuring, defaults, rest.
- Optional chaining, nullish coalescing, nullish assignment.
- for-of and for-in loops; classic for/while, break/continue.
- Exceptions with try/catch/finally, throw any value, error objects, stack traces.
- String interpolation with ${...}, triple-quoted raw strings.
- Numeric literals: decimal, hex 0x..., binary 0b..., underscores, floats, big integers.
- Simple module system via require(...) with CommonJS-like exports/module.exports.

1. Lexical elements
- Comments
  - // line comment
  - /* block comment */ (not nested)
- Identifiers: [A-Za-z_][A-Za-z0-9_]*
- Keywords: var, let, const, if, else, while, break, continue, try, catch, finally, throw, for, in, of, fn, return, true, false, nil, and, or, not
- Operators and punctuation
  - Arithmetic: + - * / %
  - Assignment: =, +=, -=, *=, /=, %=, ??=
  - Comparison: == != < <= > >=
  - Logical: && || (also and/or), ! (also not)
  - Ternary: ? :
  - Nullish coalescing: ??
  - Optional chaining: ?., ?.[] (optional property and index)
  - Indexing: [], Property: ., Spread/rest in patterns: ...
  - Grouping and sequencing: () , ; {} []

2. Types and values
- Types: number, string, boolean, nil, function, array, object
- Truthiness
  - false: nil, boolean false, numeric 0, empty string ""
  - true: non-0 numbers, non-empty strings, functions, arrays, objects
- Equality
  - Same-type equality uses natural equality:
    - numbers compare by numeric value with promotion across Int64/Double/BigInteger,
    - strings compare ordinal,
    - arrays/objects/functions compare by reference (identity).
  - Cross type equality:
    - number == string converts the string to a number if possible; else false.
    - otherwise false.

3. Literals
- Numbers
  - Decimal: 42, 1_000, 3.14, .5 (via 0.5), 1e-9, 1_2_3
  - Hex: 0xFF, 0Xdead_beef
  - Binary: 0b1010_1100
  - Internally: Int64, Double, BigInteger as needed.
  - Literal selection: decimals with a dot or exponent produce Double; otherwise choose the smallest integer kind that fits (Int64, else BigInteger). Hex/bin choose Int64 or BigInteger depending on size.
- Strings
  - Normal: "hello", escapes: \n \r \t \" \\ \0 \xNN \uNNNN
  - Interpolation: "Hello, ${name}!" — ${...} parses a full expression (nesting supported); syntax errors are reported at the interpolation site.
  - Raw triple-quoted: """anything including " and \ and ${...}""" (no escapes, no interpolation)
- Arrays: [1, 2, "x"] (holes allowed: [,,] inserts nils)
- Objects
  - { a: 1, b: 2 }
  - Shorthand properties: let a=1; let b=2; { a, b } -> { a:1, b:2 }
  - Computed keys: { ["k" + x]: value }

4. Variables and scope
- var: function-scoped (or global), reassignable; declared in nearest function/global environment (even when inside blocks).
- let: block-scoped, reassignable, with TDZ (cannot access before initialization).
- const: block-scoped, must be initialized, cannot be reassigned.
- Resolution: lexical, with runtime TDZ checks for let/const.

5. Expressions and operators
- Arithmetic
  - Operators: + - * / %
  - + also concatenates strings if either operand is a string, and concatenates arrays when both sides are arrays; otherwise numeric addition.
  - Numeric representation and promotion: numbers are Int64, Double, or BigInteger. Promotion rules: if either is Double → Double; else if either is BigInteger → BigInteger; else Int64.
  - Division: if evenly divisible in integer domain, result stays integer; otherwise a Double. Division or modulo by zero is a runtime error.
- Unary
  - -x, +x (coerces to number), !x
- Comparisons
  - Numeric or string relational (< <= > >=). Allowed only for two numbers or two strings (ordinal for strings). Other combinations throw a runtime error.
- Equality
  - ==, != follow the rules in section 2 (numeric promotion across number kinds; number==string tries numeric parse; other cross-type comparisons are false).
- Logical
  - a && b, a || b (short-circuit). They return one of the operands (no implicit boolean coercion of the result).
  - Also and/or keywords as aliases.
- Ternary
  - cond ? thenExpr : elseExpr
- Nullish
  - a ?? b returns a unless a is nil; otherwise b.
  - a ??= b assigns only if the target is nil (works for variables, properties, and indexes).
- Optional chaining
  - obj?.prop -> nil if obj is nil
  - obj?.[key] -> nil if obj is nil
  - obj?.method(args) -> nil if obj is nil; otherwise calls with this set appropriately (see section 7)
  - Optional LHS assignment: a?.b = x and a?.[k] = x are no-ops if a is nil and evaluate to nil; otherwise they behave like normal assignment. The same applies to a?.b ??= x.
- Comma operator
  - (a, b, c) evaluates left to right; value is the last.
- Assignments
  - Assignments yield the assigned value. Compound assignments (+=, -=, etc.) apply the operator using the above rules and then assign.

Indexing rules
- Arrays: arr[i], negative indices allowed (normalize: -1 means last). Out of bounds is a runtime error; use at(arr, i) to get nil instead (negatives supported).
- Strings: s[i] returns a 1-character string; negatives supported; out of bounds: runtime error.
- Objects: obj[key] coerces key to string; missing keys return nil (not an error).

6. Strings and interpolation
- Use ${...} inside normal strings to embed any expression; nested braces inside the expression are supported.
- Escaping works only in normal strings.
- Triple-quoted raw strings do not process escapes or interpolation.

Examples
````md
"line 1\nline 2"
"Hello ${firstName} ${lastName.toUpperCase()}"
"""C:\path\with\no\escapes\and "quotes" and ${literal}"""
````

7. Functions
- Function literal: fn (params) { ... }
- Declaration: fn name(params) { ... } (function-scoped; available via var-like rules)
- Arrow functions:
  - Single param: x => x + 1
  - Multiple: (a, b=2, ...rest) => { return a + b; }
  - Expression body returns its value; block body requires explicit return.
- Parameters
  - Defaults: fn (x=10, y=2*x) { ... }
  - Rest: fn (a, ...rest) { ... } (must be last)
  - Names must be unique.
- Named arguments (user functions)
  - Call with name: expr pairs: f(x: 1, y: 2)
  - You can mix positional and named; once a named arg is seen, remaining positionals go to rest (if rest exists), otherwise it’s an error.
  - Duplicate names are an error; missing required (non-defaulted) parameters are an error.
  - Built-ins do not support named arguments.
- this binding
  - Method call target.prop(args) passes this = target for normal (non-arrow) user functions.
  - Arrow functions capture this lexically upon creation; calling them as methods does not rebind this.
  - Inside functions you can access this as a constant.

Examples
````md
fn add(a, b) { return a + b; }
let inc = x => x + 1;

let obj = { n: 10, addTo: fn (x) { return this.n + x; } };
obj.addTo(5)  // 15, because this.n == 10

// Defaults, rest, named args
let f = fn (x=1, y=2, ...rest) { return x + y + length(rest); }
f(3, 4, 5, 6)        // 3 + 4 + 2 = 9
f(y: 10, x: 1)       // 12
````

8. Destructuring
- Works in variable declarations and in a special assignment statement form.
- Arrays: defaults and rest supported.
- Objects: property mapping with aliases and defaults; rest supported.
- Aliased object pattern targets can be lvalues (including a.b or a[b]) with assignment.

Declarations
````md
let [a, b=2, ...rest] = [1, 3, 4, 5];        // a=1, b=3, rest=[4,5]
const {x, y: z=10, ...others} = {x:1};       // x=1, z=10, others={}
var { p: obj.q } = { p: 42 };                // assigns obj.q = 42
````

Assignment statement (statement form; requires semicolon)
````md
[ a, b ] = [ b, a ];
{ k: obj[k] } = { k: "key", x: 1 };
````

9. Control flow
- if (cond) stmt else stmt
- while (cond) stmt
- for (init; cond; inc) stmt
  - init may be var/let/const declaration, destructuring declaration, or expression.
- for-in / for-of
  - for (pattern in expr) { ... } iterates keys/indices (strings for arrays/strings, object keys).
  - for (pattern of expr) { ... } iterates values (arrays, strings, object values).
  - Iterating over nil performs zero iterations.
  - The pattern may be a declaration (var/let/const) or an assignment target (including destructuring).
  - for-of on non-iterable (non-array/string/object/nil) is a runtime error; for-in on non-indexable (non-array/string/object/nil) is a runtime error.
- break, continue

Examples
````md
for (let i=0; i<10; i=i+1) { println(i); }

for (let v of [10,20]) { println(v); }

for (let k in {a:1,b:2}) { println(k); }  // "a", "b"

for ({k, v} of entries({a:1,b:2})) {
  println(k, v);
}
````

10. Error handling
- throw expr: throws any value. try/catch/finally supported.
- catch may have an identifier parameter or be parameterless.
- Builtins raise and error help construct/throw rich error objects.
- Runtime errors (e.g., division by zero) are caught by catch as error objects named "RuntimeError" with message, at, and stack fields.

Examples
````md
try {
  if (x == nil) throw error("x missing");
} catch (e) {
  println("caught:", e.name, e.message);
} finally {
  println("cleanup");
}
````

11. Optional chaining and nullish
- obj?.prop, obj?.[key], obj?.method(args) return nil if obj is nil.
- Optional method calls bind this = receiver for normal functions when the receiver is non-nil.
- a ?? b returns a unless it is nil; otherwise b.
- a ??= b assigns only if target is nil (works for variables, properties, indexes).
- Optional assignment targets short-circuit to nil without assignment when the base is nil (e.g., a?.b = x, a?.[k] ??= x).

12. Modules
- require(specifier) loads a module.
  - Resolution:
    - Absolute or relative paths resolved against caller file’s directory.
    - Directories use index, trying extensions: "", ".mdl", ".minidyn".
    - Files try extensions above if none provided.
  - Execution:
    - Module runs in its own environment with exports and module objects.
    - Start with: exports = {}; module = { exports }
    - Return value is module.exports if set; else exports.
  - Caching:
    - Modules are cached by absolute path.
    - Cycles supported: during load, require returns the temporary exports object.

Example
````md
// math.mdl
exports.add = fn (a, b) { return a + b; };
module.exports.sub = fn (a, b) { return a - b; };

// main.mdl
let m = require("./math");
println(m.add(2,3));  // 5
println(m.sub(5,2));  // 3
````

13. Standard library (built-ins)
Notes
- All built-ins are available in the global scope.
- Arity is shown as name(arg1, arg2=optional, ...varargs).
- Unless stated, built-ins do not accept named arguments.
- Nil is falsey; empty string is falsey; 0 is falsey; arrays/objects/functions are truthy.
- Indices are 0-based. Functions that mention “negative index allowed” accept negative indices as “from the end”.

## Core
- length(x): number
  - String length; array length; object property count.
  - Boolean/number/function => 1; nil => 0; otherwise 0.
- type(x): string
  - Returns "number", "string", "boolean", "nil", "function", "array", or "object".
- to_number(x): number|nil
  - String parsed as int/bigint/double; boolean => 1/0; nil => 0; other => 0.
- to_string(x): string
  - Stringifies value (arrays/objects printed with cycle-safe printer).
- print(...args): nil
  - Writes args joined by a single space, no newline.
- println(...args): nil
  - Writes args joined by a single space with trailing newline.
- gets(): string|nil
  - Reads a line from stdin (nil on EOF).

## Objects
- keys(obj): array<string>
- values(obj): array<any>
- entries(obj): array< [string, any] >
- from_entries(arrOfPairs): object
- has_key(obj, key): boolean
- remove_key(obj, key): boolean
- merge(a, b): object
  - Shallow: properties from b overwrite a.
- deep_merge(a, b): object|array|any
  - Objects merged recursively; arrays concatenated; otherwise b.

## Arrays
- array(...items): array<any>
- push(arr, ...items): number
  - Appends; returns new length.
- pop(arr): any|nil
  - Removes and returns last element or nil if empty.
- at(arr, index): any|nil
  - Negative index allowed; nil if OOB.
- set_at(arr, index, value): any
  - Negative index allowed; throws if OOB.
- slice(arrOrStr, start, endOptional): array<any>|string
  - Negative indices allowed; end is exclusive and optional.
- join(arr, sep): string
  - Joins by sep, stringifying elements.
- clone(x): any
  - Array/object shallow clone; other values returned as-is.
- map(arr, fn): array<any>
- filter(arr, fn): array<any>
- reduce(arr, fn, initialOptional): any
  - Without initial: uses first element and starts at index 1.
- sort(arr, compareFnOptional): array<any>
  - Returns sorted copy. Default compare sorts numbers numerically, strings ordinally, else by type name.
  - compareFn(a,b) returns negative/zero/positive like JS.
- unique(arr): array<any>
  - Dedup by value identity (numbers/strings by value; arrays/objects by reference).
- some(arr, fn): boolean
- every(arr, fn): boolean
- find(arr, fn): any|nil
- find_index(arr, fn): number
  - -1 when not found.
- range(end) → [0..end)
- range(start, end) → [start..end)
- range(start, end, step): array<number>
  - step cannot be 0; works for negative steps.

## Strings
- substring(str, start, lengthOptional): string
- index_of(str, search): number
  - -1 when not found.
- contains(str, search): boolean
- starts_with(str, prefix): boolean
- ends_with(str, suffix): boolean
- to_upper(str): string
- to_lower(str): string
- trim(str): string
- split(str, sep): array<string>
- replace(str, find, repl): string
- repeat(str, count): string
  - count < 0 treated as 0.
- pad_start(str, len, pad=" "): string
- pad_end(str, len, pad=" "): string

## Numbers and Math
- abs(x): number
- floor(x): number
- ceil(x): number
- round(x): number
- sqrt(x): number
- pow(x, y): number
- min(...values): number|nil
- max(...values): number|nil
- sin(x), cos(x), tan(x): number
- asin(x), acos(x), atan(x): number
- log(x), exp(x): number
- sign(x): number
  - Returns -1, 0, or 1 based on sign.
- clamp(x, lo, hi): number
  - If lo > hi, arguments are swapped.
- random(): number
  - Uniform in [0,1).
- random_int(min, max): number
  - Inclusive range; min/max order doesn’t matter.
- srand(seed): nil
  - Sets random() seed for this process.

## Time and Dates
- now_ms(): number
  - Unix time in milliseconds (UTC).
- sleep_ms(ms): nil
- format_date(ms, format): string
  - ms since epoch (UTC). Format uses .NET format strings with invariant culture.
- parse_date(text): number
  - Parses date/time; returns ms since epoch (UTC). Throws on invalid date.
- now_iso(): string
  - ISO 8601 UTC timestamp.

## JSON
- json_stringify(value, pretty=false): string
  - Throws on cyclic structures.
- json_parse(text): any
  - Parses JSON into MiniDyn values. Throws on invalid JSON.

## URL and Regex
- url_encode(text): string
- url_decode(text): string
- regex_match(text, pattern, flagsOptional): boolean
- regex_replace(text, pattern, repl, flagsOptional): string
- match(text, pattern, flagsOptional): array<string>
  - Returns all full-match substrings (no capture groups).
- Regex flags: i (ignore case), m (multiline), s (singleline). Culture-invariant.

## Encoding, Bytes and Crypto
Byte arrays are represented as arrays of numbers 0..255.

- read_bytes(path): array<number>
- write_bytes(path, bytes): nil
  - Throws if any element is outside 0..255.
- base64_encode(textUtf8): string
- base64_decode(b64): string
  - Throws "invalid base64" on error.
- base64_encode_bytes(bytes): string
- base64_decode_bytes(b64): array<number>
  - Throws "invalid base64" on error.
- hex_encode_bytes(bytes): string
  - Lowercase hex.
- hex_decode_bytes(hex): array<number>
  - Even-length hex required; throws on invalid hex.
- md5(text), sha1(text), sha256(text), sha512(text): string
  - Returns lowercase hex of hash (UTF-8 of text).
- md5_bytes(bytes), sha1_bytes(bytes), sha256_bytes(bytes), sha512_bytes(bytes): string
  - Returns lowercase hex of hash.

## UUID
- uuid_v4(): string
  - Lowercase 36-char canonical form.

## File System
- read_file(path): string
- write_file(path, text): nil
- exists(path): boolean
  - True for existing file or directory.
- copy_file(src, dst, overwrite=false): nil
- move(src, dst): nil
  - Works for files or directories. Throws if source does not exist.
- remove(path, recursive=false): boolean
  - Deletes file or directory; returns true if deleted, false if not found.
- mkdir(path, recursive=false): boolean
  - Non-recursive: returns false if already exists; true if created.
  - Recursive: always creates (returns true).
- list_dir(path, full=false): array<string>
  - Throws if directory doesn’t exist. full=true returns absolute paths.
- chdir(path): nil
- cwd(): string

## Paths
- path_join(...parts): string
- path_dirname(path): string
- path_basename(path): string
- path_extname(path): string
- path_change_ext(path, newExt): string
  - newExt may omit '.' (it will be added).
- path_normalize(path): string
  - Returns canonical full path.
- path_resolve(path): string
  - Alias of normalize.
- path_is_absolute(path): boolean

## Environment
- env_get(name): string|nil
- env_set(name, valueOrNil): nil
  - Set to nil to unset.

## Modules
- require(specifier): any
  - Resolves using the host module loader.
  - Resolution: absolute path; or relative to the caller’s directory; supports implicit extensions ["", ".mdl", ".minidyn"]; index files inside directories (index, index.mdl, index.minidyn).
  - Exposes CommonJS-like exports via `module.exports` or `exports`.

## HTTP (host-provided, disabled by default)
- http_get(url): string
- http_post(url, body, contentType="application/json"): string
  - Throws "HTTP is disabled" if no network client was configured by the host.

## Errors and Exceptions
- error(message): object
  - Constructs an Error object { name, message }.
- raise(message): never
  - Throws an Error object created with error(message).

## Equality and Utilities
- deep_equal(a, b): boolean
  - Structural deep equality with cycle detection.
- pprint(x): nil
  - Pretty-prints JSON when possible; falls back to string representation.

## Parsing Helpers
- parse_int(text): number|nil
  - Parses int/bigint; returns nil on failure.
- parse_float(text): number|nil
  - Parses floating point; returns nil on failure.

14. Semantics details and edge cases
- let TDZ: reading a let/const name before its first assignment throws “Cannot access 'name' before initialization”.
- var collisions: var cannot redeclare existing block-scoped name in the same function/global.
- Arrays: concatenation with + and +=; negative indices supported in many operations.
- Indexing: missing object properties return nil; array/string out-of-bounds is a runtime error (use at(...) to get nil instead for arrays); assignment into string indices is an error.
- Object property order is preserved by insertion order; object keys are strings.
- Optional calls: obj?.method(args) yields nil if obj is nil (no call performed) and binds this = receiver when called.
- Assignments are expressions and evaluate to the assigned value.
- Named arguments:
  - User functions: supported (default/rest handled).
  - Built-ins: not supported (throws).
  - Compiled functions (a subset, internally): may accept named args mapped to positional if they declare no defaults/rest.
- Iteration over nil in for-in/for-of performs zero iterations.

15. Examples

Basics
````md
println("Hello, world")
let x = 41
x += 1
println(x == 42 ? "ok" : "fail")
````

Strings
````md
let name = "Ada"
println("Hello, ${name}!")        // interpolation
println("""C:\no\escapes\${raw}""")
````

Objects and arrays
````md
let o = { a: 1, b: 2 }
println(keys(o))        // ["a","b"]
let a = [1,2,3]; println(a[-1])  // 3
````

Optional chaining and nullish
````md
let u = nil
println(u?.profile?.name)      // nil
let v = nil
v ??= 10
println(v)                     // 10
````

Destructuring
````md
let [a,b=2,...r] = [1,3,4,5]
{ k: o.key, m=0 } = { k: "name" };  // assigns o.key = "name"; declares m=0 if not present
````

Functions and this
````md
fn greet(name="world") { println("Hello", name) }
greet()
let obj = { n: 10, inc: fn (d=1) { this.n = this.n + d; return this.n } }
println(obj.inc())      // 11
````

Loops
````md
for (let i=0; i<3; i=i+1) println(i)
for (let x of [10,20]) println(x)
for (let k in {a:1,b:2}) println(k)   // "a", "b"
````

Errors
````md
try {
  throw error("boom")
} catch (e) {
  println(e.name, e.message)
} finally { println("done") }
````

Modules
````md
// math.mdl
exports.add = fn (a,b) { return a+b }

// main.mdl
let m = require("./math")
println(m.add(2,3))
````

16. Tooling notes
- The interpreter prints values via a safe stringifier (cycles print as [<cycle>] or {<cycle>}).
- Error objects from runtime errors include:
  - name = "RuntimeError"
  - message
  - at = "file:line:column" (when available)
  - stack = array of "at function <name> (file:line:column)" frames
- REPL supports evaluating single expressions directly; multi-statement input executes statements.

17. Implementation notes (for advanced users)
- Tail-call optimization is applied for self-tail-calls in user functions (positional and named-arg variants).
- A simple bytecode compiler/VM accelerates a subset of functions (no defaults/rest in compiled functions; may fall back to AST interpreter).
- Optional chaining, nullish coalescing/assignment, and try/catch/finally are implemented across both interpreter and VM.

This reference reflects the current language behavior as implemented. Use it as a guide when reading or writing MiniDynLang code.

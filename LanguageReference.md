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
  - Same-type equality uses natural equality (numbers with numeric promotion, strings ordinal, reference for objects/arrays/functions).
  - Cross type equality:
    - number == string converts string to number if possible; else false.
    - otherwise false.

3. Literals
- Numbers
  - Decimal: 42, 1_000, 3.14, .5 (via 0.5), 1e-9, 1_2_3
  - Hex: 0xFF, 0Xdead_beef
  - Binary: 0b1010_1100
  - Internally: Int64, Double, BigInteger as needed.
- Strings
  - Normal: "hello", escapes: \n \r \t \" \\ \0 \xNN \uNNNN
  - Interpolation: "Hello, ${name}!" (expression inside ${...})
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
- Arithmetic: + - * / % (numbers). + also concatenates strings and concatenates arrays when both sides are arrays.
- Unary: -x, +x (coerces to number), !x
- Comparisons: numeric or string relational (< <= > >=). Throws on invalid type combinations.
- Equality: ==, !=
- Logical: a && b, a || b (short-circuit). Also and/or keywords.
- Ternary: cond ? thenExpr : elseExpr
- Nullish: a ?? b (returns a unless a is nil), a ??= b (assign b if target is nil)
- Optional chaining:
  - obj?.prop -> nil if obj is nil
  - obj?.[key] -> nil if obj is nil
  - obj?.method(args) -> nil if obj is nil; otherwise call with this set (see below)
- Comma operator: (a, b, c) evaluates left to right; value is last.

Indexing rules
- Arrays: arr[i], negative indices allowed (normalize: -1 means last).
- Strings: s[i] returns 1-character string; negatives supported; out of bounds: runtime error.
- Objects: obj[key] coerces key to string.

6. Strings and interpolation
- Use ${...} inside normal strings to embed any expression.
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
  - The pattern may be a declaration (var/let/const) or an assignment target (including destructuring).
  - for-of on non-iterable (non-array/string/object/nil) is a runtime error; for-in on non-indexable is a runtime error.
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
- a ?? b returns a unless it is nil; otherwise b.
- a ??= b assigns only if target is nil (works for variables, properties, indexes; optional targets short-circuit to nil without assignment).

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
I/O and timing
- print(...args): writes without newline; returns nil.
- println(...args): writes with newline; returns nil.
- gets(): reads a line from stdin; returns string or nil at EOF.
- now_ms(): current UTC milliseconds since epoch.
- sleep_ms(ms): blocks the thread for ms.

Type and conversion
- type(x): "nil" | "boolean" | "number" | "string" | "function" | "array" | "object"
- to_number(x): number or nil (strings parsed to number; booleans to 0/1; nil -> 0)
- to_string(x): string

Numbers and math
- abs(x), floor(x), ceil(x), round(x), sqrt(x)
- pow(a, b)
- min(a, ...), max(a, ...)
- random(): [0,1)
- srand(seed): set RNG seed

Strings
- substring(s, start[, len])
- index_of(s, sub)
- contains(s, sub)
- starts_with(s, prefix), ends_with(s, suffix)
- to_upper(s), to_lower(s), trim(s)
- split(s, sep)
- replace(s, find, repl)
- repeat(s, count)
- pad_start(s, len[, pad=" "]), pad_end(s, len[, pad=" "])

Parsing and JSON
- parse_int(s): integer number or nil
- parse_float(s): floating number or nil
- json_stringify(value[, pretty=false]): JSON string; throws on cyclic structures.
- json_parse(s): parses JSON (null → nil; arrays/objects mapped accordingly)

Arrays and objects
- array(...items): constructs array
- length(x): string/array/object length; nil→0; scalars→1
- push(arr, ...values): append; returns new length
- pop(arr): remove last; returns value or nil
- slice(arrOrString, start[, end]): negative indices allowed
- join(arr, sep): string
- at(arr, index): returns element or nil (negative allowed)
- set_at(arr, index, value): assign; returns value
- clone(x): shallow clone for array/object; otherwise returns x
- keys(obj): array of keys (in insertion order)
- values(obj): array of values (in insertion order)
- entries(obj): array of [key, value]
- from_entries(arrayOfPairs): object
- has_key(obj, key): boolean
- remove_key(obj, key): boolean
- merge(obj1, obj2): shallow merge; obj2 overrides

Higher-order and utility
- map(arr, fn): array
- filter(arr, fn): array
- reduce(arr, fn[, initial]): value
- sort(arr[, comparator]): returns new sorted array (stable not guaranteed); comparator(a,b) returns negative/0/positive; default compares numbers/strings, then falls back to type name.
- unique(arr): returns new array with unique elements (by value equality)
- deep_equal(a, b): deep structural equality (handles cycles)
- range(n) | range(start, end) | range(start, end, step): array of ints

Errors
- error(msg): returns error object {name:"Error", message:msg}
- raise(msg): throws error(msg)
- require(specifier): loads module (see Modules)

14. Semantics details and edge cases
- let TDZ: reading a let/const name before its first assignment throws “Cannot access 'name' before initialization”.
- var collisions: var cannot redeclare existing block-scoped name in the same function/global.
- Arrays: concatenation with + and +=; negative indices supported in many operations.
- Strings: indexing returns 1-char string; assignment into string indices is an error.
- Object property order is preserved by insertion order; object keys are strings.
- Optional calls: obj?.method(args) yields nil if obj is nil (no call performed).
- Named arguments:
  - User functions: supported (default/rest handled).
  - Built-ins: not supported (throws).
  - Compiled functions (a subset, internally): may accept named args mapped to positional if they declare no defaults/rest.

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

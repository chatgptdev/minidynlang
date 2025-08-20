# MiniDynLang built-ins

General notes
- String comparisons are ordinal (case-sensitive, culture-invariant).
- Negative indices are allowed in functions that say “negative allowed” (they index from the end).
- length(x): numbers/booleans/functions report 1; nil reports 0; objects count keys (in insertion order).
- Equality:
  - Primitive equality is by value.
  - Arrays/objects/functions compare by reference identity in == and unique().
  - Use deep_equal(a,b) for structural comparison (handles cycles).
- All examples assume println prints the result.

I/O and timing
- print(...args): writes args without newline; returns nil.
- println(...args): writes args with newline; returns nil.
- gets(): reads a line from stdin; returns string or nil at EOF.
- now_ms(): number. UTC ms since Unix epoch.
- sleep_ms(ms): blocks current thread for ms (ms < 0 treated as 0).

Type and conversion
- type(x): string: "nil" | "boolean" | "number" | "string" | "function" | "array" | "object".
- to_number(x): number or nil.
  - string: parses decimal/float; boolean: 0/1; nil: 0; other non-numeric types: nil.
- to_string(x): string. Uses language printer (arrays/objects printed with cycle safety).

Numbers and math
- abs(x), floor(x), ceil(x), round(x), sqrt(x): number.
- pow(a, b): number (double).
- min(a, ...), max(a, ...): number. At least 1 arg; returns nil if called with 0 args.
- random(): number in [0,1).
- srand(seed): sets RNG seed. Seed is truncated to int.

Strings
- substring(s, start[, len]): string.
  - start < 0 clamped to 0; start > len clamped to len; len < 0 -> 0; out-of-range is clamped.
- index_of(s, sub): number (0-based) or -1 if not found.
- contains(s, sub): boolean.
- starts_with(s, prefix), ends_with(s, suffix): boolean.
- to_upper(s), to_lower(s), trim(s): string.
- split(s, sep): array of strings. Splits by literal substring sep (not regex). Keeps empty segments.
- replace(s, find, repl): string. Literal replace, case-sensitive, replaces all occurrences.
- repeat(s, count): string. count < 0 treated as 0.
- pad_start(s, len[, pad=" "]), pad_end(s, len[, pad=" "]): string. Empty pad treated as single space.

Parsing and JSON
- parse_int(s): integer number or nil. Parses decimal integers only (no decimals).
- parse_float(s): floating number or nil. Accepts decimal floats with optional thousands separators.
- json_stringify(value[, pretty=false]): JSON string. Throws on cyclic structures. nil → "null".
- json_parse(s): parses JSON to language values (null → nil). Numbers may be int, big-int, or double.

Arrays and objects
- array(...items): constructs array.
- length(x): number.
  - string: code units; array: elements; object: keys count; nil→0; number/boolean/function→1.
- push(arr, ...values): number. Appends; returns new length.
- pop(arr): value or nil if empty.
- slice(arrOrString, start[, end]): same-type as input. Negative indices allowed; clamps to bounds; does not throw.
- join(arr, sep): string. Elements are to_string’d.
- at(arr, index): value or nil if out of range. Negative index allowed.
- set_at(arr, index, value): value. Negative index allowed; throws if out of range.
- clone(x): shallow clone for arrays/objects; returns x for other types.
- keys(obj): array of string keys (in insertion order).
- values(obj): array of values (in insertion order).
- entries(obj): array of [key, value] pairs (arrays of length 2).
- from_entries(arrayOfPairs): object. Each pair must be [string, any]; throws otherwise.
- has_key(obj, key): boolean.
- remove_key(obj, key): boolean.
- merge(obj1, obj2): object. Shallow merge; obj2 entries override; preserves insertion order.

Higher-order and utility
- map(arr, fn): array. Calls fn(elem).
- filter(arr, fn): array. Keeps elem when fn(elem) is truthy.
- reduce(arr, fn[, initial]): value.
  - With initial, starts from it; otherwise uses first element (throws on empty array with no initial).
- sort(arr[, comparator]): array. Returns a sorted clone (original unchanged).
  - comparator(a,b) should return negative/0/positive; default compares numbers, then strings; otherwise by type name.
  - Stability not guaranteed.
- unique(arr): array. Keeps first occurrence of each unique value by language equality:
  - primitives by value; arrays/objects/functions by reference identity.
- deep_equal(a, b): boolean. Structural deep equality; detects cycles; arrays by order; objects by keys/values.
- range(n) | range(start, end) | range(start, end, step): array of ints. Half-open [start, end). step!=0 (throws if 0). Negative steps allowed.

Errors
- error(msg): object. { name: "Error", message: msg } plus optional context if built from runtime error.
- raise(msg): throws error(msg).

Modules
- require(specifier): value (the module’s exports).
  - Resolution:
    - Absolute path used as-is.
    - Relative/bare spec resolved relative to the caller file’s directory; falls back to current directory in REPL.
    - If a directory, tries index, index.mdl, index.minidyn.
    - If no extension, tries "", .mdl, .minidyn.
  - Caching: per-absolute-path, case-insensitive. On cyclic loads, a pre-seeded exports object is returned during evaluation.
  - Module scope:
    - Predefined: exports, module; module.exports initially aliases exports.
    - Return value is the final module.exports object.
  - Errors:
    - Throws if cannot resolve or load; cache is cleaned on failure.

Examples
````text
println(length("abc"));           // 3
println(length(array(1,2,3)));    // 3
println(length(nil));              // 0
println(to_number("3.14"));       // 3.14
println(to_number(true));         // 1

println(substring("hello", 1, 3));    // "ell"
println(slice("abcdef", -3));         // "def"
println(slice(array(1,2,3,4), 1, 3)); // [2, 3]
println(at(array(10,20,30), -1));     // 30

let xs = array(3,1,2);
println(join(sort(xs), ","));     // "1,2,3"
println(unique(array(1,1,"1")));  // [1, "1"]

// map / filter / reduce
let a = array(1,2,3);
println(map(a, n => n*n));            // [1,4,9]
println(filter(a, n => n % 2 == 1));  // [1,3]
println(reduce(a, (acc, n) => acc + n, 0)); // 6

// objects
let o = from_entries(array(array("a",1), array("b",2)));
println(keys(o));                     // ["a","b"]
println(values(o));                   // [1,2]
println(entries(merge(o, { b: 3 }))); // [["a",1],["b",3]]

// JSON
let s = json_stringify({a: array(1,2)}, true);
println(s);                           // pretty JSON
println(json_parse("""{"a":[1,2]}""").a[0]); // 1

// errors
let e = error("boom");
println(e.name + ": " + e.message);   // "Error: boom"
// raise("boom"); // throws
````

Edge cases and throws
- Division/modulo by zero: runtime error.
- set_at with out-of-range index: runtime error.
- from_entries with malformed pairs: runtime error.
- range with step == 0: runtime error.
- json_stringify on cyclic structures: runtime error.
- Built-ins do not accept named arguments.
# MiniDynLang

A tiny **dynamically-typed** programming language implemented in **C#**. MiniDynLang is a single-file reference interpreter with a friendly REPL, a compact but expressive syntax (objects, arrays, first-class/arrow functions, destructuring, computed keys, named arguments, ternary/logical operators, compound assignments), and a small standard library.

> If you like languages in the "small but sharp" vein (à la Lox / Little Lisp / JS-ish minimal cores), you’ll feel at home.

---

## Table of contents

* [Highlights](#highlights)
* [Install & build](#install--build)
* [CLI & REPL](#cli--repl)
* [Language tour](#language-tour)

  * [Values & variables](#values--variables)
  * [Control flow](#control-flow)
  * [Operators](#operators)
  * [Arrays](#arrays)
  * [Objects & computed keys](#objects--computed-keys)
  * [Destructuring (decl & assign)](#destructuring-decl--assign)
  * [Functions](#functions)
  * [Arrow functions](#arrow-functions)
  * [`this` & methods](#this--methods)
  * [Named arguments](#named-arguments)
  * [Ternary & logical](#ternary--logical)
* [Standard library (selected)](#standard-library-selected)
* [Design notes](#design-notes)
* [Errors](#errors)
* [Project structure](#project-structure)
* [Contributing](#contributing)
* [License](#license)

---

## Highlights

* **Values**: number (**int** / **double** / **bigint**), string, boolean, nil, array, object, function
* **Variables**: `var`, `let` (block-scoped), `const`
* **Control flow**: `if/else`, `while`, `break`, `continue`, `return`
* **Operators**: `+ - * / %` (numeric promotion), comparisons, logical `and/or/not`, unary `+ - !`, **ternary** `? :`
* **Arrays / Objects**: literals, indexing, dot/bracket property access, **computed object keys**, concat arrays via `+`
* **Destructuring**: in declarations **and** assignments; defaults and `...rest` for arrays/objects; alias to lvalues
* **Functions**: `fn` declarations/expressions, **default & rest params**, closures
* **Arrow functions**: `x => x*2`, `(x,y)=>{ ... }`, with expression or block bodies
* **Named arguments**: call user functions with `f(x: 1, y: 2)`; mix with positionals; works with defaults & rest
* **Methods & `this`**: `obj.method()` binds `this` to `obj`
* **REPL**: expression-result echo
* **Tiny runtime**: one C# file; easy to hack/extend

---

## Install & build

**Prerequisite**: .NET 6.0 SDK or later.

```bash
git clone https://github.com/chatgptdev/minidynlang.git
cd minidynlang
dotnet build -c Release
```

---

## CLI & REPL

Start REPL:

```bash
dotnet run
```

Run the built-in **demo** program showcasing most features:

```bash
dotnet run -- -demo
```

Run a file:

```bash
dotnet run -- path/to/file.mini
```

---

## Language tour

### Values & variables

```text
let x = 42;
const msg = "hello";
var y;           // defaults to nil

println(true, false, nil);  // true false nil
println(1 + 2 * 3);         // 7
println("Hello " + "World");// Hello World
```

### Control flow

```text
let n = 3;
while (n > 0) {
  println(n);
  n = n - 1;
}
if (n == 0) println("Done"); else println("Not done");
```

### Operators

* Arithmetic: `+ - * / %`
  Division yields an **integer** when exact, otherwise a **double**.
* Comparison: `== != < <= > >=`
  Numbers vs numeric strings compare numerically when the string parses as a number.
* Logical: `and`, `or`, `not`
* Unary: `+`, `-`, `!`
* **Ternary**: `cond ? a : b`
* **Compound assignment**: `+= -= *= /= %=`

```text
let a = 10;
a += 5;              // 15
println(1 < 2 ? "yes" : "no"); // yes
```

### Arrays

```text
let arr = [1, 2, 3];
println(arr[0]);     // 1
arr[0] += 41;        // compound index assignment
println(arr);        // [42, 2, 3]

// Concat
println([1,2] + [3,4]);  // [1, 2, 3, 4]
```

Helpers (see stdlib): `length`, `array`, `push`, `pop`, `slice(arrOrStr, start[, end])`, `join`, `at(arr, idx)`, `set_at`.

* **Negative indices** are supported by helpers like `slice` and `at` (Python-style from the end).

### Objects & computed keys

```text
let o = { a: 1, b: 2, d: "str" };
println(o.a, o["b"]);      // 1 2
o.a += 5;
o["b"] = o["b"] * 10;
println(o.a, o.b);         // 6 20

let k = "x";
let o2 = { [k + "1"]: 11, [k + "2"]: 22 };
println(o2["x1"], o2["x2"]); // 11 22
```

Object helpers: `keys`, `has_key`, `remove_key`, `merge`.

### Destructuring (decl & assign)

Array/object patterns in **declarations** and **assignments**, with defaults and `...rest`.
Object patterns support **aliasing to lvalues**, e.g. assign directly into properties.

```text
let [p, q = 20, ...rest] = [10, , 30, 40, 50];
println(p, q, rest);             // 10 20 [30, 40, 50]

const { a, b: bb, z = 99, ...other } = { a: 1, b: 2 };
println(a, bb, z, other);

[p, q] = [100, 200];
{ a: o.a, b: o.b } = { a: 7, b: 8 };  // assign into object properties
println(p, q, o.a, o.b);              // 100 200 7 8
```

### Functions

Declare with `fn`, both statements and expressions. Parameters support **defaults** and `...rest`. Closures are supported.

```text
fn add(x, y = 0) {
  return x + y;
}
println(add(2), add(2, 3)); // 2 5

let sum = fn (...nums) {
  let s = 0, i = 0;
  while (i < length(nums)) { s += nums[i]; i += 1; }
  return s;
};
println(sum(1,2,3,4)); // 10
```

### Arrow functions

Expression or block body; single-param arrows may omit parentheses.

```text
let double = x => x * 2;
let fact = n => { if (n <= 1) return 1; return n * fact(n-1); };

println(double(7));  // 14
println(fact(5));    // 120
```

> Note: Arrow functions **do not bind their own `this`**; see next section.

### `this` & methods

Calling a user function via property syntax **binds** `this` to the receiver.

```text
let counter = {
  value: 10,
  add: fn(n) { this.value = this.value + n; return this.value; },
  get: fn() { return this.value; }
};

println(counter.get());    // 10
println(counter.add(5));   // 15
```

Arrow functions don’t automatically bind `this`. If you need `this`, use `fn` for methods.

### Named arguments

User-defined functions support **named arguments**, mixing with positional arguments, plus defaults and rest. (Built-ins are positional only.)

```text
fn createUser(name, age = 25, city = "Unknown", active = true) {
  return { name: name, age: age, city: city, active: active };
}

let u1 = createUser("Alice", 28, "NY", true);           // positionals
let u2 = createUser(name: "Bob", city: "London", age: 35); // named
let u3 = createUser("Charlie", city: "Tokyo", active: false); // mixed

println(u1, u2, u3);
```

Named arguments that don’t match parameters are rejected unless a **rest** parameter exists—in which case the extras are collected.

### Ternary & logical

```text
println(true and false);        // false
println(false or true);         // true
println(not true);              // false
println(1 < 2 ? "yes" : "no");  // yes
```

---

## Standard library (selected)

Output & IO

* `print(...)`, `println(...)`
* `gets()` → read a line from stdin or `nil` on EOF

Type & conversion

* `to_number(x)`, `to_string(x)`, `type(x)`, `length(x)`

Math

* `abs`, `floor`, `ceil`, `round`, `sqrt`, `pow`, `min`, `max`
* `random()`, `srand(seed)`

Strings

* `substring(s, start[, len])`, `index_of(hay, needle)`, `contains`, `starts_with`, `ends_with`
* `to_upper`, `to_lower`, `trim`, `split(s, sep)`, `parse_int`, `parse_float`

Arrays

* `array(...)`, `push(arr, ...items)`, `pop(arr)`
* `slice(arrOrStr, start[, end])` *(supports negative indices)*
* `join(arr, sep)`, `at(arr, idx)`, `set_at(arr, idx, val)`, `clone(arrOrObj)`

Objects

* `keys(obj)`, `has_key(obj, key)`, `remove_key(obj, key)`, `merge(a, b)`

Time

* `now_ms()` → Unix time in milliseconds (UTC)

---

## Design notes

* **Numbers** promote across `int` / `bigint` / `double` as needed.
  Division is integer when exact, otherwise double. Modulo and division by zero throw runtime errors.
* **Equality**: numbers and numeric strings compare numerically when possible; arrays/objects/functions compare by **identity**.
* **Truthiness**:

  * `nil` is falsey
  * empty string/array/object are falsey
  * numbers are falsey only if `0`
  * everything else is truthy
* Arrays/objects are **reference types**.
* `let`/`const` are **block-scoped**; `const` prevents **reassignment of the binding** (but you can still mutate the object the binding points to).

---

## Errors

* **Lexing**: `MiniDynLexError`
* **Parsing**: `MiniDynParseError`
* **Runtime**: `MiniDynRuntimeError`

Messages generally include `line:column` where useful.

---

## Project structure

Single-file reference implementation (namespace `MiniDynLang`):

* **Lexer** → tokens & keywords
* **Parser** → AST for expressions/statements, including destructuring & arrow functions
* **Interpreter** → environments, `this` binding for method calls, named arguments resolution, built-ins
* **Program** → REPL / file runner / `-demo`

This layout is purposely compact so you can read it end-to-end and extend it.

---

## Contributing

Issues and PRs welcome! Possible directions:

* Modules / import system
* More stdlib (maps/sets, JSON, file IO, formatting)
* Better diagnostics & error recovery
* Performance (e.g., bytecode VM)
* Tests & CI
* Named-args support for built-ins

---

## License

MIT

---

## Quick snippets

```text
// Objects + computed keys + merge
let base = { a: 1, b: 2 };
let key = "x";
let extra = { [key+"1"]: 11, [key+"2"]: 22 };
println( merge(base, extra) );

// Destructuring assign directly into properties
let o = { a: 0, b: 0 };
{ a: o.a, b: o.b } = { a: 7, b: 8 };
println(o);  // {a: 7, b: 8}

// Named arguments + defaults + rest
fn log(level = "INFO", ...fields, msg = "") {
  println(level + ": " + msg, fields);
}
log("WARN", "u=42", "ip=1.2.3.4", msg: "rate limited");
```

---

> Run the **demo** to see many of these features in action:

```bash
dotnet run -- -demo
```

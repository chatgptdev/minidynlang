Notes and conventions
- Case-sensitive identifiers; reserved keywords: var, let, const, if, else, while, break, continue, try, catch, finally, throw, for, in, of, true, false, nil, and, or, not, fn, return.
- Comments: line  and block /* ... */ (no nesting).
- Semicolons are required after expression statements, return, throw, break, continue, and declaration lists.
- Numbers: decimal integers (with _ separators), decimals with fractional/exponent parts, hex 0x[0-9A-Fa-f_]+, binary 0b[01_]+.
- Strings:
  - Normal strings "...": escapes \n \r \t \" \\ \0 \xNN \uNNNN; allow interpolation ${ Expression } (nesting allowed).
  - Raw strings """...""": multiline, no escapes, no interpolation.
- Arrays: allow elisions (holes) via consecutive commas and allow a trailing comma.
- Objects: keys are identifiers, string literals, or computed keys [expr]; no trailing comma (by parser).
- Parameters: default values (expr without comma operator) and one rest param ...name (must be last).
- Calls: named args name: expr and positional args; mixing allowed with restrictions (no positional after named unless collected by a rest parameter).
- Operators and precedence (high to low, left-associative unless noted):
  - Member/call/index/optional chaining: ., [], (args), ?., ?.[]
  - Unary +, -, not, !
  - *, /, %
  - +, -
  - <, <=, >, >=
  - ==, !=
  - and (&&)
  - or (||)
  - ?? (nullish coalesce)
  - Assignment =, +=, -=, *=, /=, %=, ??= (right-associative; target must be lvalue)
  - Ternary ? :
  - Comma ,
- Optional chaining short-circuits without evaluating the RHS/key/arguments.

Formal grammar (EBNF)
````ebnf
Program   = { Declaration | Statement } EOF ;

Declaration =
    "fn" Identifier "(" [ ParamList ] ")" Block
  | "var"   ( Pattern "=" Expression | VarDeclList ) ";"
  | "let"   ( Pattern "=" Expression | VarDeclList ) ";"
  | "const" ( Pattern "=" Expression | ConstDeclList ) ";"
  ;

VarDeclList   = VarDeclarator   { "," VarDeclarator   } ;
ConstDeclList = ConstDeclarator { "," ConstDeclarator } ;

VarDeclarator   = Identifier [ "=" Ternary ] ;
ConstDeclarator = Identifier "=" Ternary ;

Statement =
    Block
  | IfStmt
  | WhileStmt
  | ForStmt
  | "break" ";"
  | "continue" ";"
  | "return" [ Expression ] ";"
  | "throw" Expression ";"
  | TryStmt
  | DestructuringAssignStmt
  | ExprStmt
  ;

Block     = "{" { Declaration | Statement } "}" ;
ExprStmt  = Expression ";" ;

DestructuringAssignStmt =
  ( ArrayPattern | ObjectPattern ) "=" Expression ";" ;

IfStmt    = "if" "(" Expression ")" Statement [ "else" Statement ] ;
WhileStmt = "while" "(" Expression ")" Statement ;

TryStmt =
  "try" Block
  [ "catch" [ "(" [ Identifier ] ")" ] Block ]
  [ "finally" Block ]
  ;

ForStmt =
  "for" "(" ClassicForHead ")" Statement
| "for" "(" ForEachHead    ")" Statement ;

ClassicForHead =
  [ ForInitializer ] ";" [ Expression ] ";" [ Expression ] ;

ForInitializer =
    VarForInit
  | LetForInit
  | ConstForInit
  | Expression
  ;

VarForInit   = "var"   ( Pattern "=" Expression | VarDeclList   ) ;
LetForInit   = "let"   ( Pattern "=" Expression | VarDeclList   ) ;
ConstForInit = "const" ( Pattern "=" Expression | ConstDeclList ) ;

ForEachHead =
  ( ForEachDecl | ForEachTarget ) ( "of" | "in" ) Expression ;

ForEachDecl   = ( "var" | "let" | "const" ) Pattern ;
ForEachTarget = Pattern | LValue ;

LValue = Identifier { "." Identifier | "[" Expression "]" } ;

Expression = CommaExpr ;
CommaExpr  = Ternary { "," Ternary } ;

Ternary    = Assignment [ "?" Assignment ":" Ternary ] ;

Assignment = Nullish [ AssignOp Assignment ] ;
AssignOp   = "=" | "+=" | "-=" | "*=" | "/=" | "%=" | "??=" ;

Nullish    = LogicalOr { "??" LogicalOr } ;
LogicalOr  = LogicalAnd { ("or" | "||") LogicalAnd } ;
LogicalAnd = Equality   { ("and" | "&&") Equality   } ;
Equality   = Relational { ("==" | "!=") Relational  } ;
Relational = Additive   { ("<" | "<=" | ">" | ">=") Additive } ;
Additive   = Multiplicative { ("+" | "-") Multiplicative } ;
Multiplicative = Unary { ("*" | "/" | "%") Unary } ;

Unary =
    ("+" | "-" | "!" | "not") Unary
  | Member
  ;

Member =
  Primary
  {   // left-associative chaining
      "?." Identifier               // optional property
    | "?." "[" Ternary "]"          // optional index
    | "(" [ ArgList ] ")"           // call
    | "." Identifier                // property
    | "[" Ternary "]"               // index
  }
  ;

ArgList = Arg { "," Arg } ;
Arg     = Identifier ":" Ternary | Ternary ;

Primary =
    Number
  | String
  | "true" | "false" | "nil"
  | ArrayLiteral
  | ObjectLiteral
  | "(" ( ArrowFunction | Expression ) ")"
  | Identifier "=>" ArrowBody         // single-parameter arrow
  | "fn" "(" [ ParamList ] ")" Block  // function expression
  | Identifier
  ;

ArrowFunction = [ ParamList ] "=>" ArrowBody ;
ArrowBody     = Block | Expression ;

ParamList = Param { "," Param } ;
Param     = "..." Identifier | Identifier [ "=" Ternary ] ;

ArrayLiteral =
  "[" [ ArrayElements ] "]" ;

ArrayElements =
  ArrayElement { "," ArrayElement } [ "," ] ;   // trailing comma allowed

ArrayElement =
  /* hole (elision) */   // written as ",," in source
| Ternary ;

ObjectLiteral =
  "{"
    [ ObjectEntry { "," ObjectEntry } ]
  "}" ;

ObjectEntry =
    Identifier ":" Ternary
  | Identifier                     // shorthand
  | String ":" Ternary
  | "[" Ternary "]" ":" Ternary
  ;

Pattern =
    ArrayPattern
  | ObjectPattern
  | Identifier
  ;

ArrayPattern =
  "["
    [ PatternElem { "," PatternElem } [ "," "..." Pattern ] ]
  "]" ;

PatternElem = Pattern [ "=" Ternary ] ;

ObjectPattern =
  "{"
    [ ObjPatProp { "," ObjPatProp } [ "," "..." Pattern ] ]
  "}" ;

ObjPatProp =
  (Identifier | String)
  [ ":" AliasTarget ]
  [ "=" Ternary ] ;

AliasTarget = Pattern | LValue ;
````

Lexical summary
- Identifier = letter | "_" , followed by { letter | digit | "_" } (Unicode letters allowed by C# char.IsLetter/IsLetterOrDigit).
- Number:
  - Decimal: digits ["." digits] [Exponent] | digits Exponent; digits may contain "_".
  - Hex: "0x" hexDigits (with "_").
  - Binary: "0b" (0|1)+ (with "_").
  - Exponent = ("e"|"E") [ "+" | "-" ] digits (with "_").
- String:
  - Normal: '"' { char | escape | interpolation } '"'
    - escape: \n \r \t \" \\ \0 \xNN \uNNNN
    - interpolation: "${" Expression "}"
  - Raw: '"""' { anyCharExcept('"""') } '"""'
- Punctuators/operators: ( ) { } [ ] . , ; : ? ... + - * / % += -= *= /= %= = == != < <= > >= && || ! and or not ?. ?? ??= =>

Syntactic constraints and behavior
- Assignment targets must be lvalues (Identifier, property, or index); destructuring assignment is a statement form at the start of a statement.
- Rest parameter: only one, last in the list.
- Named arguments:
  - Each name must match a non-rest parameter; duplicates are errors.
  - Positional args after a named arg are only allowed if the callee has a rest parameter (then they go into rest).
- Optional chaining:
  - a?.b, a?.[k], a?.m(args) return nil without evaluating b/k/args when a is nil.
- Defaults in parameters and destructuring use expressions parsed as Ternary (the comma operator is not part of these subexpressions).
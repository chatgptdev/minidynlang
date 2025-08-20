using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;

namespace MiniDynLang
{
    internal static class JsonUtil
    {
        public static string EscapeJson(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (char.IsControl(ch)) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }
    }
    internal static class RuntimeOps
    {
        // Conversion/coercion semantics used across interpreter and VM
        public static NumberValue ToNumber(Value v)
        {
            switch (v.Type)
            {
                case ValueType.Number: return v.AsNumber();
                case ValueType.Boolean: return NumberValue.FromBool(v.AsBoolean());
                case ValueType.Nil: return NumberValue.FromLong(0);
                case ValueType.String:
                    return NumberValue.TryFromString(v.AsString(), out var nv) ? nv : NumberValue.FromLong(0);
                default:
                    return NumberValue.FromLong(0);
            }
        }

        public static string ToStringValue(Value v) => v.ToString();

        public static int NormalizeIndex(int idx, int len)
        {
            if (idx < 0) idx = len + idx;
            return idx;
        }

        public static bool CompareEq(Value a, Value b)
        {
            if (a.Type == b.Type) return a.Equals(b);

            // numeric <-> string soft equality
            if (a.Type == ValueType.Number && b.Type == ValueType.String)
                return NumberValue.TryFromString(b.AsString(), out var nb) && NumberValue.Compare(a.AsNumber(), nb) == 0;

            if (a.Type == ValueType.String && b.Type == ValueType.Number)
                return NumberValue.TryFromString(a.AsString(), out var na) && NumberValue.Compare(na, b.AsNumber()) == 0;

            return false;
        }

        public static bool CompareRel(Value a, Value b, string op)
        {
            if (a.Type == ValueType.Number && b.Type == ValueType.Number)
            {
                int cmp = NumberValue.Compare(a.AsNumber(), b.AsNumber());
                switch (op)
                {
                    case "<": return cmp < 0;
                    case "<=": return cmp <= 0;
                    case ">": return cmp > 0;
                    case ">=": return cmp >= 0;
                }
            }
            if (a.Type == ValueType.String && b.Type == ValueType.String)
            {
                int cmp = string.CompareOrdinal(a.AsString(), b.AsString());
                switch (op)
                {
                    case "<": return cmp < 0;
                    case "<=": return cmp <= 0;
                    case ">": return cmp > 0;
                    case ">=": return cmp >= 0;
                }
            }
            throw new MiniDynRuntimeError("Relational comparison only on numbers or strings");
        }
    }

    // Exceptions
    public class MiniDynException : Exception
    {
        public int Line { get; }
        public int Column { get; }
        public MiniDynException(string message, int line = -1, int column = -1) : base(message)
        {
            Line = line; Column = column;
        }
        public override string ToString()
        {
            if (Line >= 0) return $"{Message} at {Line}:{Column}";
            return Message;
        }
    }

    // Source position + call frame
    public readonly struct SourceSpan
    {
        public string FileName { get; }
        public int Line { get; }
        public int Column { get; }
        public int EndLine { get; }
        public int EndColumn { get; }

        public bool IsEmpty => Line <= 0 || Column <= 0;

        public SourceSpan(string fileName, int line, int column, int endLine = -1, int endColumn = -1)
        {
            FileName = fileName ?? "<script>";
            Line = line;
            Column = column;
            EndLine = endLine <= 0 ? line : endLine;
            EndColumn = endColumn <= 0 ? column : endColumn;
        }

        public static SourceSpan FromToken(Token t)
            => t == null ? new SourceSpan("<script>", -1, -1) : new SourceSpan(t.FileName, t.Line, t.Column);

        public override string ToString()
        {
            if (string.IsNullOrEmpty(FileName)) return $"{Line}:{Column}";
            return $"{FileName}:{Line}:{Column}";
        }
    }

    public readonly struct CallFrame
    {
        public string FunctionName { get; }
        public SourceSpan CallSite { get; }

        public CallFrame(string functionName, SourceSpan callSite)
        {
            FunctionName = string.IsNullOrEmpty(functionName) ? "<anonymous>" : functionName;
            CallSite = callSite;
        }

        public override string ToString()
        {
            // Example: at function foo (script.mdl:12:5)
            var site = CallSite.IsEmpty ? "" : $" ({CallSite})";
            return $"at function {FunctionName}{site}";
        }
    }
    public sealed class MiniDynLexError : MiniDynException { public MiniDynLexError(string msg, int l, int c) : base(msg, l, c) { } }
    public sealed class MiniDynParseError : MiniDynException { public MiniDynParseError(string msg, int l, int c) : base(msg, l, c) { } }
    public sealed class MiniDynRuntimeError : MiniDynException
    {
        public SourceSpan Span { get; set; } // nearest source span
        public List<CallFrame> CallStack { get; set; } // call frames (top to bottom)

        public MiniDynRuntimeError(string msg) : base(msg) { }

        public MiniDynRuntimeError(string msg, SourceSpan span, List<CallFrame> stack = null) : base(msg)
        {
            Span = span;
            CallStack = stack;
        }

        public MiniDynRuntimeError WithContext(SourceSpan span, IEnumerable<CallFrame> frames)
        {
            if (Span.IsEmpty) Span = span;
            if (CallStack == null || CallStack.Count == 0)
                CallStack = frames?.ToList() ?? new List<CallFrame>();
            return this;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append(Message);
            if (!Span.IsEmpty)
                sb.Append(" at ").Append(Span.ToString());
            if (CallStack != null && CallStack.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Function stack:");
                foreach (var frame in CallStack)
                    sb.AppendLine(frame.ToString());
                sb.Append("--------------------------");
            }
            return sb.ToString();
        }
    }

    // Value system
    public enum ValueType { Number, String, Boolean, Nil, Function, Array, Object }

    public readonly struct NumberValue
    {
        public enum NumKind { Int, Double, BigInt }
        public NumKind Kind { get; }
        public long I64 { get; }
        public double Dbl { get; }
        public BigInteger BigInt { get; }

        private NumberValue(long i) { Kind = NumKind.Int; I64 = i; Dbl = 0; BigInt = default; }
        private NumberValue(double d) { Kind = NumKind.Double; Dbl = d; I64 = 0; BigInt = default; }
        private NumberValue(BigInteger b) { Kind = NumKind.BigInt; BigInt = b; I64 = 0; Dbl = 0; }

        public static NumberValue FromLong(long i) => new NumberValue(i);
        public static NumberValue FromDouble(double d) => new NumberValue(d);
        public static NumberValue FromBigInt(BigInteger b) => new NumberValue(b);

        public static bool TryFromString(string s, out NumberValue nv)
        {
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            {
                nv = FromLong(i); return true;
            }
            if (BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bi))
            {
                nv = FromBigInt(bi); return true;
            }
            if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d))
            {
                nv = FromDouble(d); return true;
            }
            nv = default; return false;
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case NumKind.Int: return I64.ToString(CultureInfo.InvariantCulture);
                case NumKind.Double: return Dbl.ToString("R", CultureInfo.InvariantCulture);
                case NumKind.BigInt: return BigInt.ToString(CultureInfo.InvariantCulture);
                default: return "0";
            }
        }

        private static bool IsIntegralDouble(double d) => Math.Floor(d) == d && !double.IsInfinity(d) && !double.IsNaN(d);

        public NumberValue ToDoubleNV()
        {
            switch (Kind)
            {
                case NumKind.Double: return this;
                case NumKind.Int: return FromDouble((double)I64);
                case NumKind.BigInt: return FromDouble((double)BigInt);
                default: return FromDouble(0);
            }
        }
        public NumberValue ToBigIntNV()
        {
            switch (Kind)
            {
                case NumKind.BigInt: return this;
                case NumKind.Int: return FromBigInt(new BigInteger(I64));
                case NumKind.Double: return IsIntegralDouble(Dbl) ? FromBigInt(new BigInteger(Dbl)) : FromDouble(Dbl);
                default: return FromBigInt(BigInteger.Zero);
            }
        }

        public static void Promote(NumberValue a, NumberValue b, out NumberValue A, out NumberValue B)
        {
            if (a.Kind == NumKind.Double || b.Kind == NumKind.Double)
            {
                A = a.ToDoubleNV();
                B = b.ToDoubleNV();
                return;
            }
            if (a.Kind == NumKind.BigInt || b.Kind == NumKind.BigInt)
            {
                A = a.ToBigIntNV();
                B = b.ToBigIntNV();
                return;
            }
            A = a; B = b;
        }

        public static NumberValue FromBool(bool b) => FromLong(b ? 1 : 0);
        public static NumberValue Neg(in NumberValue x)
        {
            switch (x.Kind)
            {
                case NumKind.Int: return FromLong(-x.I64);
                case NumKind.BigInt: return FromBigInt(-x.BigInt);
                case NumKind.Double: return FromDouble(-x.Dbl);
                default: return FromLong(0);
            }
        }
        public static NumberValue Add(in NumberValue a, in NumberValue b)
        {
            Promote(a, b, out var A, out var B);
            switch (A.Kind)
            {
                case NumKind.Int: return FromLong(A.I64 + B.I64);
                case NumKind.BigInt: return FromBigInt(A.BigInt + B.BigInt);
                case NumKind.Double: return FromDouble(A.Dbl + B.Dbl);
                default: return FromLong(0);
            }
        }
        public static NumberValue Sub(in NumberValue a, in NumberValue b)
        {
            Promote(a, b, out var A, out var B);
            switch (A.Kind)
            {
                case NumKind.Int: return FromLong(A.I64 - B.I64);
                case NumKind.BigInt: return FromBigInt(A.BigInt - B.BigInt);
                case NumKind.Double: return FromDouble(A.Dbl - B.Dbl);
                default: return FromLong(0);
            }
        }
        public static NumberValue Mul(in NumberValue a, in NumberValue b)
        {
            Promote(a, b, out var A, out var B);
            switch (A.Kind)
            {
                case NumKind.Int: return FromLong(A.I64 * B.I64);
                case NumKind.BigInt: return FromBigInt(A.BigInt * B.BigInt);
                case NumKind.Double: return FromDouble(A.Dbl * B.Dbl);
                default: return FromLong(0);
            }
        }
        public static NumberValue Div(in NumberValue a, in NumberValue b)
        {
            Promote(a, b, out var A, out var B);
            switch (A.Kind)
            {
                case NumKind.Double:
                    if (B.Dbl == 0) throw new MiniDynRuntimeError("Division by zero");
                    return FromDouble(A.Dbl / B.Dbl);
                case NumKind.BigInt:
                    if (B.BigInt.IsZero) throw new MiniDynRuntimeError("Division by zero");
                    return (A.BigInt % B.BigInt == 0) ? FromBigInt(A.BigInt / B.BigInt) : FromDouble((double)A.BigInt / (double)B.BigInt);
                case NumKind.Int:
                    if (B.I64 == 0) throw new MiniDynRuntimeError("Division by zero");
                    return (A.I64 % B.I64 == 0) ? FromLong(A.I64 / B.I64) : FromDouble((double)A.I64 / (double)B.I64);
                default:
                    return FromLong(0);
            }
        }
        public static NumberValue Mod(in NumberValue a, in NumberValue b)
        {
            Promote(a, b, out var A, out var B);
            switch (A.Kind)
            {
                case NumKind.Double:
                    if (B.Dbl == 0) throw new MiniDynRuntimeError("Division by zero");
                    return FromDouble(A.Dbl % B.Dbl);
                case NumKind.BigInt:
                    if (B.BigInt.IsZero) throw new MiniDynRuntimeError("Division by zero");
                    return FromBigInt(A.BigInt % B.BigInt);
                case NumKind.Int:
                    if (B.I64 == 0) throw new MiniDynRuntimeError("Division by zero");
                    return FromLong(A.I64 % B.I64);
                default:
                    return FromLong(0);
            }
        }
        public static int Compare(in NumberValue a, in NumberValue b)
        {
            Promote(a, b, out var A, out var B);
            switch (A.Kind)
            {
                case NumKind.Int: return A.I64.CompareTo(B.I64);
                case NumKind.BigInt: return A.BigInt.CompareTo(B.BigInt);
                case NumKind.Double: return A.Dbl.CompareTo(B.Dbl);
                default: return 0;
            }
        }

        public override int GetHashCode()
        {
            switch (Kind)
            {
                case NumKind.Int: return I64.GetHashCode();
                case NumKind.Double: return Dbl.GetHashCode();
                case NumKind.BigInt: return BigInt.GetHashCode();
                default: return 0;
            }
        }
        public override bool Equals(object obj)
        {
            var n = obj is NumberValue ? (NumberValue)obj : default(NumberValue);
            if (!(obj is NumberValue)) return false;
            if (Kind == n.Kind)
            {
                switch (Kind)
                {
                    case NumKind.Int: return I64 == n.I64;
                    case NumKind.Double: return Dbl.Equals(n.Dbl);
                    case NumKind.BigInt: return BigInt.Equals(n.BigInt);
                    default: return false;
                }
            }
            return Compare(this, n) == 0;
        }
    }

    public sealed class ArrayValue
    {
        public readonly List<Value> Items;
        public ArrayValue() { Items = new List<Value>(); }
        public ArrayValue(IEnumerable<Value> items) { Items = new List<Value>(items); }
        public int Length => Items.Count;
        public Value this[int idx]
        {
            get => Items[idx];
            set => Items[idx] = value;
        }
        public ArrayValue Clone() => new ArrayValue(Items);
        public override string ToString()
        {
            // Use cycle-aware printer
            return SafeStringifier.PrintArray(this);
        }
    }

    public sealed class ObjectValue
    {
        // Preserve insertion order deterministically across runtimes
        private readonly Dictionary<string, Value> _map;
        private readonly List<string> _order;

        public ObjectValue()
        {
            _map = new Dictionary<string, Value>(StringComparer.Ordinal);
            _order = new List<string>();
        }

        // Legacy convenience: builds an ordered object from an existing dictionary
        public ObjectValue(Dictionary<string, Value> dict)
        {
            _map = new Dictionary<string, Value>(StringComparer.Ordinal);
            _order = new List<string>();
            if (dict != null)
            {
                foreach (var kv in dict)
                    Set(kv.Key, kv.Value);
            }
        }

        public bool TryGet(string key, out Value v)
        {
            if (_map.TryGetValue(key, out v)) return true;
            v = Value.Nil(); // ensure callers get a defined nil value when missing
            return false;
        }
        public bool Contains(string key)
        {
            key = StringInterner.Intern(key);
            return _map.ContainsKey(key);
        }

        public void Set(string key, Value v)
        {
            key = StringInterner.Intern(key);
            if (!_map.ContainsKey(key))
                _order.Add(key);
            _map[key] = v;
        }

        public bool Remove(string key)
        {
            key = StringInterner.Intern(key);
            if (!_map.ContainsKey(key)) return false;
            _map.Remove(key);
            _order.Remove(key);
            return true;
        }

        public int Count => _map.Count;

        public IEnumerable<string> Keys => _order;

        public IEnumerable<KeyValuePair<string, Value>> Entries
        {
            get
            {
                foreach (var k in _order)
                    yield return new KeyValuePair<string, Value>(k, _map[k]);
            }
        }

        public ObjectValue CloneShallow()
        {
            var copy = new ObjectValue();
            foreach (var k in _order)
                copy.Set(k, _map[k]);
            return copy;
        }

        public override string ToString()
        {
            // Use cycle-aware printer
            return SafeStringifier.PrintObject(this);
        }
    }

    // Add a small, process-local string interner for object property names.
    internal static class StringInterner
    {
        private static readonly ConcurrentDictionary<string, string> _pool =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public static string Intern(string s)
        {
            if (s == null) return null;
            return _pool.GetOrAdd(s, k => k);
        }
    }

    // Cycle-safe pretty printer for Value, ArrayValue and ObjectValue.
    internal static class SafeStringifier
    {
        public static string Print(Value v)
        {
            var path = new HashSet<object>();
            return PrintValue(v, path);
        }

        public static string PrintArray(ArrayValue arr)
        {
            var path = new HashSet<object>();
            return PrintArray(arr, path);
        }

        public static string PrintObject(ObjectValue obj)
        {
            var path = new HashSet<object>();
            return PrintObject(obj, path);
        }

        private static string PrintValue(Value v, HashSet<object> path)
        {
            switch (v.Type)
            {
                case ValueType.Nil: return "nil";
                case ValueType.Boolean: return v.AsBoolean() ? "true" : "false";
                case ValueType.Number: return v.AsNumber().ToString();
                case ValueType.String: return v.AsString();
                case ValueType.Function: return v.AsFunction() != null ? v.AsFunction().ToString() : "<function>";
                case ValueType.Array: return PrintArray(v.AsArray(), path);
                case ValueType.Object: return PrintObject(v.AsObject(), path);
                default: return "nil";
            }
        }

        private static string PrintArray(ArrayValue arr, HashSet<object> path)
        {
            if (path.Contains(arr)) return "[<cycle>]";
            path.Add(arr);
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(PrintValue(arr[i], path));
            }
            sb.Append(']');
            path.Remove(arr);
            return sb.ToString();
        }

        private static string PrintObject(ObjectValue obj, HashSet<object> path)
        {
            if (path.Contains(obj)) return "{<cycle>}";
            path.Add(obj);
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in obj.Entries)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key);
                sb.Append(": ");
                sb.Append(PrintValue(kv.Value, path));
            }
            sb.Append('}');
            path.Remove(obj);
            return sb.ToString();
        }
    }

    public readonly struct Value : IEquatable<Value>
    {
        public ValueType Type { get; }
        private readonly NumberValue _num;
        private readonly string _str;
        private readonly bool _bool;
        private readonly ICallable _func;
        private readonly ArrayValue _arr;
        private readonly ObjectValue _obj;

        private static readonly Value TrueVal = new Value(ValueType.Boolean, default, null, true, null, null, null);
        private static readonly Value FalseVal = new Value(ValueType.Boolean, default, null, false, null, null, null);
        private static readonly Value NilVal = new Value(ValueType.Nil, default, null, false, null, null, null);

        private Value(ValueType t, NumberValue n, string s, bool b, ICallable f, ArrayValue a, ObjectValue o)
        {
            Type = t; _num = n; _str = s; _bool = b; _func = f; _arr = a; _obj = o;
        }

        public static Value Number(in NumberValue n) => new Value(ValueType.Number, n, null, false, null, null, null);
        public static Value String(string s) => new Value(ValueType.String, default, s ?? "", false, null, null, null);
        public static Value Boolean(bool b) => b ? TrueVal : FalseVal;
        public static Value Nil() => NilVal;
        public static Value Function(ICallable f) => new Value(ValueType.Function, default, null, false, f, null, null);
        public static Value Array(ArrayValue a) => new Value(ValueType.Array, default, null, false, null, a ?? new ArrayValue(), null);
        public static Value Object(ObjectValue o) => new Value(ValueType.Object, default, null, false, null, null, o ?? new ObjectValue());

        public NumberValue AsNumber()
            => Type == ValueType.Number ? _num : throw new MiniDynRuntimeError("Expected number");
        public string AsString()
            => Type == ValueType.String ? _str : throw new MiniDynRuntimeError("Expected string");
        public bool AsBoolean()
            => Type == ValueType.Boolean ? _bool : throw new MiniDynRuntimeError("Expected boolean");
        public ICallable AsFunction()
            => Type == ValueType.Function ? _func : throw new MiniDynRuntimeError("Expected function");
        public ArrayValue AsArray()
            => Type == ValueType.Array ? _arr : throw new MiniDynRuntimeError("Expected array");
        public ObjectValue AsObject()
            => Type == ValueType.Object ? _obj : throw new MiniDynRuntimeError("Expected object");

        public override string ToString()
        {
            // Delegate to cycle-aware printer
            return SafeStringifier.Print(this);
        }

        public static bool IsTruthy(in Value v)
        {
            switch (v.Type)
            {
                case ValueType.Nil: return false;
                case ValueType.Boolean: return v._bool;
                case ValueType.Number: return NumberValue.Compare(v._num, NumberValue.FromLong(0)) != 0;
                case ValueType.String: return !string.IsNullOrEmpty(v._str);
                case ValueType.Function: return true;
                case ValueType.Array: return true;   // arrays are always truthy (even when empty)
                case ValueType.Object: return true;  // objects are always truthy (even when empty)
                default: return false;
            }
        }

        public bool Equals(Value o)
        {
            if (o.Type != Type) return false;
            switch (Type)
            {
                case ValueType.Nil: return true;
                case ValueType.Boolean: return _bool == o._bool;
                case ValueType.Number: return NumberValue.Compare(_num, o._num) == 0;
                case ValueType.String: return _str == o._str;
                case ValueType.Function: return ReferenceEquals(_func, o._func);
                case ValueType.Array: return ReferenceEquals(_arr, o._arr);
                case ValueType.Object: return ReferenceEquals(_obj, o._obj);
                default: return false;
            }
        }
        public override bool Equals(object obj) => obj is Value v && Equals(v);

        public override int GetHashCode()
        {
            switch (Type)
            {
                case ValueType.Nil: return 0;
                case ValueType.Boolean: return _bool.GetHashCode();
                case ValueType.Number: return _num.GetHashCode();
                case ValueType.String: return _str != null ? _str.GetHashCode() : 0;
                case ValueType.Function: return _func != null ? _func.GetHashCode() : 0;
                case ValueType.Array: return _arr != null ? _arr.GetHashCode() : 0;
                case ValueType.Object: return _obj != null ? _obj.GetHashCode() : 0;
                default: return 0;
            }
        }
    }

    // Lexer
    public enum TokenType
    {
        EOF,
        Identifier,
        Number,
        String,

        Var,
        Let,
        Const,

        If,
        Else,
        While,
        Break,
        Continue,
        Try,
        Catch,
        Finally,
        Throw,

        True,
        False,
        Nil,
        And,
        Or,
        Not,

        Fn,
        Return,

        Plus, Minus, Star, Slash, Percent,
        PlusAssign, MinusAssign, StarAssign, SlashAssign, PercentAssign,

        LParen, RParen,
        LBrace, RBrace,
        LBracket, RBracket,
        Semicolon, Comma,
        Assign, // =
        Equal, NotEqual, Less, LessEq, Greater, GreaterEq,
        Question, Colon, // ternary
        Ellipsis, // ...
        Dot, // .
        Arrow, // =>
        QuestionDot,          // ?.
        NullishCoalesce,      // ??
        NullishAssign,        // ??=
        For,                  // for
        In,                   // in
        Of,                   // of
    }

    public class Token
    {
        public TokenType Type { get; }
        public string Lexeme { get; }
        public object Literal { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }
        public string FileName { get; }

        // Indicates this token represents a raw triple-quoted string ("""...""")
        public bool IsRawString { get; }

        // The starting line/column of the string's content (i.e., after the opening quote(s))
        public int StringContentStartLine { get; }
        public int StringContentStartColumn { get; }

        public Token(TokenType type, string lexeme, object literal, int pos, int line, int column)
        {
            Type = type;
            Lexeme = lexeme;
            Literal = literal;
            Position = pos;
            Line = line;
            Column = column;
            FileName = "<script>";
            IsRawString = false;
            StringContentStartLine = -1;
            StringContentStartColumn = -1;
        }

        public Token(TokenType type, string lexeme, object literal, int pos, int line, int column, string fileName)
        {
            Type = type;
            Lexeme = lexeme;
            Literal = literal;
            Position = pos;
            Line = line;
            Column = column;
            FileName = fileName ?? "<script>";
            IsRawString = false;
            StringContentStartLine = -1;
            StringContentStartColumn = -1;
        }

        // Constructor specialized for string tokens with raw flag and content start position
        public Token(TokenType type, string lexeme, object literal, int pos, int line, int column, string fileName,
                     bool isRawString, int contentStartLine, int contentStartColumn)
        {
            Type = type;
            Lexeme = lexeme;
            Literal = literal;
            Position = pos;
            Line = line;
            Column = column;
            FileName = fileName ?? "<script>";
            IsRawString = isRawString;
            StringContentStartLine = contentStartLine;
            StringContentStartColumn = contentStartColumn;
        }

        public override string ToString() => $"{Type} '{Lexeme}' @ {Line}:{Column}";
    }

    public class Lexer
    {
        private readonly string _src;
        private int _pos;
        private int _line = 1;
        private int _col = 1;
        private readonly string _fileName;

        private static readonly Dictionary<string, TokenType> Keywords = new Dictionary<string, TokenType>()
        {
            ["var"] = TokenType.Var,
            ["let"] = TokenType.Let,
            ["const"] = TokenType.Const,
            ["if"] = TokenType.If,
            ["else"] = TokenType.Else,
            ["while"] = TokenType.While,
            ["break"] = TokenType.Break,
            ["continue"] = TokenType.Continue,
            ["try"] = TokenType.Try,
            ["catch"] = TokenType.Catch,
            ["finally"] = TokenType.Finally,
            ["throw"] = TokenType.Throw,
            ["for"] = TokenType.For,
            ["in"] = TokenType.In,
            ["of"] = TokenType.Of,

            ["true"] = TokenType.True,
            ["false"] = TokenType.False,
            ["nil"] = TokenType.Nil,
            ["and"] = TokenType.And,
            ["or"] = TokenType.Or,
            ["not"] = TokenType.Not,
            ["fn"] = TokenType.Fn,
            ["return"] = TokenType.Return,
        };

        public Lexer(string src, string fileName = "<script>")
        {
            _src = src ?? "";
            _fileName = string.IsNullOrEmpty(fileName) ? "<script>" : fileName;
        }

        private bool IsAtEnd => _pos >= _src.Length;
        private char Peek() => IsAtEnd ? '\0' : _src[_pos];
        private char PeekNext() => (_pos + 1 < _src.Length) ? _src[_pos + 1] : '\0';
        private char PeekNext2() => (_pos + 2 < _src.Length) ? _src[_pos + 2] : '\0';

        private char Advance()
        {
            char c = _src[_pos++];
            if (c == '\n') { _line++; _col = 1; }
            else { _col++; }
            return c;
        }

        private void SkipWhitespaceAndComments()
        {
            while (!IsAtEnd)
            {
                char c = Peek();
                if (char.IsWhiteSpace(c)) { Advance(); }
                else if (c == '/' && PeekNext() == '/')
                {
                    while (!IsAtEnd && Peek() != '\n') Advance();
                }
                else if (c == '/' && PeekNext() == '*')
                {
                    // block comment /* ... */
                    Advance(); Advance();
                    while (!IsAtEnd && !(Peek() == '*' && PeekNext() == '/')) Advance();
                    if (IsAtEnd) throw new MiniDynLexError("Unterminated block comment", _line, _col);
                    Advance(); Advance(); // consume */
                }
                else
                {
                    break;
                }
            }
        }

        private Token MakeToken(TokenType t, string lexeme, object lit, int startPos, int startLine, int startCol)
            => new Token(t, lexeme, lit, startPos, startLine, startCol, _fileName);

        public Token NextToken()
        {
            SkipWhitespaceAndComments();
            int start = _pos;
            int startLine = _line;
            int startCol = _col;

            if (IsAtEnd) return MakeToken(TokenType.EOF, "", null, _pos, _line, _col);

            char c = Advance();
            switch (c)
            {
                case '(':
                    return MakeToken(TokenType.LParen, "(", null, start, startLine, startCol);
                case ')':
                    return MakeToken(TokenType.RParen, ")", null, start, startLine, startCol);
                case '{':
                    return MakeToken(TokenType.LBrace, "{", null, start, startLine, startCol);
                case '}':
                    return MakeToken(TokenType.RBrace, "}", null, start, startLine, startCol);
                case '[':
                    return MakeToken(TokenType.LBracket, "[", null, start, startLine, startCol);
                case ']':
                    return MakeToken(TokenType.RBracket, "]", null, start, startLine, startCol);
                case ';':
                    return MakeToken(TokenType.Semicolon, ";", null, start, startLine, startCol);
                case ',':
                    return MakeToken(TokenType.Comma, ",", null, start, startLine, startCol);
                case '+':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.PlusAssign, "+=", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Plus, "+", null, start, startLine, startCol);
                case '-':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.MinusAssign, "-=", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Minus, "-", null, start, startLine, startCol);
                case '*':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.StarAssign, "*=", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Star, "*", null, start, startLine, startCol);
                case '/':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.SlashAssign, "/=", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Slash, "/", null, start, startLine, startCol);
                case '%':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.PercentAssign, "%=", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Percent, "%", null, start, startLine, startCol);
                case '?':
                    // Optional chaining '?.', nullish coalescing '??', and ternary '?'
                    if (Peek() == '.')
                    {
                        Advance();
                        return MakeToken(TokenType.QuestionDot, "?.", null, start, startLine, startCol);
                    }
                    if (Peek() == '?')
                    {
                        Advance();
                        if (Peek() == '=')
                        {
                            Advance();
                            return MakeToken(TokenType.NullishAssign, "??=", null, start, startLine, startCol);
                        }
                        return MakeToken(TokenType.NullishCoalesce, "??", null, start, startLine, startCol);
                    }
                    return MakeToken(TokenType.Question, "?", null, start, startLine, startCol);
                case ':':
                    return MakeToken(TokenType.Colon, ":", null, start, startLine, startCol);
                case '.':
                    if (Peek() == '.' && PeekNext() == '.')
                    {
                        Advance(); Advance();
                        return MakeToken(TokenType.Ellipsis, "...", null, start, startLine, startCol);
                    }
                    return MakeToken(TokenType.Dot, ".", null, start, startLine, startCol);
                case '!':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.NotEqual, "!=", null, start, startLine, startCol); }
                    // Support '!' as alias for 'not'
                    return MakeToken(TokenType.Not, "!", null, start, startLine, startCol);
                case '&':
                    if (Peek() == '&') { Advance(); return MakeToken(TokenType.And, "&&", null, start, startLine, startCol); }
                    throw new MiniDynLexError("Unexpected '&'", startLine, startCol);
                case '|':
                    if (Peek() == '|') { Advance(); return MakeToken(TokenType.Or, "||", null, start, startLine, startCol); }
                    throw new MiniDynLexError("Unexpected '|'", startLine, startCol);
                case '=':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.Equal, "==", null, start, startLine, startCol); }
                    if (Peek() == '>') { Advance(); return MakeToken(TokenType.Arrow, "=>", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Assign, "=", null, start, startLine, startCol);
                case '<':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.LessEq, "<=", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Less, "<", null, start, startLine, startCol);
                case '>':
                    if (Peek() == '=') { Advance(); return MakeToken(TokenType.GreaterEq, ">=", null, start, startLine, startCol); }
                    return MakeToken(TokenType.Greater, ">", null, start, startLine, startCol);
                case '"':
                    // Raw triple-quote """...""" (multiline, no escapes)
                    if (Peek() == '"' && PeekNext() == '"')
                    {
                        Advance(); Advance(); // consume the two extra quotes
                        return RawStringToken(start, startLine, startCol);
                    }
                    return StringToken(start, startLine, startCol);
            }

            if (char.IsDigit(c))
            {
                return NumberToken(start, startLine, startCol);
            }

            if (char.IsLetter(c) || c == '_')
            {
                return IdentifierToken(start, startLine, startCol);
            }

            throw new MiniDynLexError($"Unexpected character '{c}'", startLine, startCol);
        }

        private static int HexDigit(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return 10 + (ch - 'a');
            if (ch >= 'A' && ch <= 'F') return 10 + (ch - 'A');
            return -1;
        }

        private Token StringToken(int start, int startLine, int startCol)
        {
            // Parse a standard quoted string with escapes, and record content start pos (after opening ")
            var sb = new StringBuilder();
            while (!IsAtEnd && Peek() != '"')
            {
                char c = Advance();
                if (c == '\\' && !IsAtEnd)
                {
                    char e = Advance();
                    switch (e)
                    {
                        case 'n': c = '\n'; break;
                        case 'r': c = '\r'; break;
                        case 't': c = '\t'; break;
                        case '"': c = '"'; break;
                        case '\\': c = '\\'; break;
                        case '0': c = '\0'; break;
                        case 'x':
                        {
                            // require two hex digits; do not advance unless both are valid
                            if (_pos + 1 >= _src.Length)
                                throw new MiniDynLexError("Invalid \\xNN escape", _line, _col);
                            int h1 = HexDigit(_src[_pos]);
                            int h2 = HexDigit(_src[_pos + 1]);
                            if (h1 < 0 || h2 < 0)
                                throw new MiniDynLexError("Invalid \\xNN escape", _line, _col);
                            Advance(); // first hex
                            Advance(); // second hex
                            c = (char)((h1 << 4) | h2);
                            break;
                        }
                        case 'u':
                        {
                            // require four hex digits; do not advance unless all four are valid
                            if (_pos + 3 >= _src.Length)
                                throw new MiniDynLexError("Invalid \\uNNNN escape", _line, _col);
                            int h1 = HexDigit(_src[_pos]);
                            int h2 = HexDigit(_src[_pos + 1]);
                            int h3 = HexDigit(_src[_pos + 2]);
                            int h4 = HexDigit(_src[_pos + 3]);
                            if (h1 < 0 || h2 < 0 || h3 < 0 || h4 < 0)
                                throw new MiniDynLexError("Invalid \\uNNNN escape", _line, _col);
                            Advance(); Advance(); Advance(); Advance();
                            c = (char)((h1 << 12) | (h2 << 8) | (h3 << 4) | h4);
                            break;
                        }
                        default:
                            // Unknown escape: treat as literal of escaped char
                            c = e;
                            break;
                    }
                }
                sb.Append(c);
            }
            if (IsAtEnd) throw new MiniDynLexError("Unterminated string", startLine, startCol);
            Advance(); // consume closing "
            var text = sb.ToString();
            // content starts right after the opening "
            return new Token(TokenType.String, text, text, start, startLine, startCol, _fileName,
                             isRawString: false,
                             contentStartLine: startLine,
                             contentStartColumn: startCol + 1);
        }


        private Token RawStringToken(int start, int startLine, int startCol)
        {
            // We are positioned right after consuming the opening """
            var sb = new StringBuilder();
            while (!IsAtEnd)
            {
                if (Peek() == '"' && PeekNext() == '"' && PeekNext2() == '"')
                {
                    Advance(); Advance(); Advance(); // consume closing """
                    var text = sb.ToString();
                    // content starts after the opening """
                    return new Token(TokenType.String, text, text, start, startLine, startCol, _fileName,
                                     isRawString: true,
                                     contentStartLine: startLine,
                                     contentStartColumn: startCol + 3);
                }
                sb.Append(Advance());
            }
            throw new MiniDynLexError("Unterminated raw string", startLine, startCol);
        }

        private Token NumberToken(int start, int startLine, int startCol)
        {
            // Base prefixes and underscores in digits
            // 0x... hex, 0b... binary
            if (_src[start] == '0' && (_pos < _src.Length))
            {
                char kind = _src[_pos];
                if (kind == 'x' || kind == 'X')
                {
                    Advance(); // consume x
                    while (true)
                    {
                        char p = Peek();
                        if (p == '_') { Advance(); continue; }
                        int hv = HexDigit(p);
                        if (hv >= 0) { Advance(); continue; }
                        break;
                    }
                    string hextext = _src.Substring(start, _pos - start);
                    string digits = hextext.Substring(2).Replace("_", "");
                    if (digits.Length == 0) throw new MiniDynLexError("Invalid hex literal", startLine, startCol);

                    // Parse as hex (two's complement). If negative, convert to unsigned by adding 2^(nibbles*4).
                    BigInteger bi = BigInteger.Parse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                    BigInteger unsigned = bi;
                    if (bi.Sign < 0)
                    {
                        int bitCount = digits.Length * 4;
                        unsigned = bi + (BigInteger.One << bitCount);
                    }

                    object lit = (unsigned <= long.MaxValue)
                        ? (object)NumberValue.FromLong((long)unsigned)
                        : NumberValue.FromBigInt(unsigned);
                    return MakeToken(TokenType.Number, hextext, lit, start, startLine, startCol);
                }
                if (kind == 'b' || kind == 'B')
                {
                    Advance(); // consume b
                    bool sawDigit = false;
                    BigInteger bi = BigInteger.Zero;
                    while (!IsAtEnd)
                    {
                        char p = Peek();
                        if (p == '_') { Advance(); continue; }
                        if (p == '0' || p == '1')
                        {
                            sawDigit = true;
                            bi = (bi << 1) + (p == '1' ? BigInteger.One : BigInteger.Zero);
                            Advance();
                            continue;
                        }
                        break;
                    }
                    if (!sawDigit) throw new MiniDynLexError("Invalid binary literal", startLine, startCol);
                    string bintext = _src.Substring(start, _pos - start);
                    object lit = (bi >= long.MinValue && bi <= long.MaxValue)
                        ? (object)NumberValue.FromLong((long)bi)
                        : NumberValue.FromBigInt(bi);
                    return MakeToken(TokenType.Number, bintext, lit, start, startLine, startCol);
                }
            }

            bool hasDot = false;
            bool hasExp = false;

            // Integer and optional fractional part (allow underscores)
            while (true)
            {
                char p = Peek();
                if (char.IsDigit(p) || p == '_') { Advance(); continue; }
                if (!hasDot && p == '.' && char.IsDigit(PeekNext()))
                {
                    hasDot = true; Advance(); continue;
                }
                break;
            }

            // Optional exponent part: e[+|-]?digits (allow underscores in digits)
            if (Peek() == 'e' || Peek() == 'E')
            {
                hasExp = true;
                Advance(); // consume 'e'/'E'

                if (Peek() == '+' || Peek() == '-') Advance();

                if (!char.IsDigit(Peek()))
                    throw new MiniDynLexError("Invalid exponent in number literal", startLine, startCol);
                while (char.IsDigit(Peek()) || Peek() == '_') Advance();
            }

            string textAll = _src.Substring(start, _pos - start);
            string text = textAll.Replace("_", "");
            object lit2;

            if (hasDot || hasExp)
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new MiniDynLexError("Invalid number literal", startLine, startCol);
                lit2 = NumberValue.FromDouble(d);
            }
            else
            {
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    lit2 = NumberValue.FromLong(i);
                else
                    lit2 = NumberValue.FromBigInt(BigInteger.Parse(text, CultureInfo.InvariantCulture));
            }
            return MakeToken(TokenType.Number, textAll, lit2, start, startLine, startCol);
        }

        private Token IdentifierToken(int start, int startLine, int startCol)
        {
            while (char.IsLetterOrDigit(Peek()) || Peek() == '_') Advance();
            string text = _src.Substring(start, _pos - start);
            if (Keywords.TryGetValue(text, out var kw))
                return MakeToken(kw, text, null, start, startLine, startCol);
            return MakeToken(TokenType.Identifier, text, text, start, startLine, startCol);
        }
    }

    // AST
    public abstract class Expr
    {
        public SourceSpan Span { get; set; } // populated by parser where convenient
        public interface IVisitor<T>
        {
            T VisitLiteral(Literal e);
            T VisitVariable(Variable e);
            T VisitAssign(Assign e);
            T VisitUnary(Unary e);
            T VisitBinary(Binary e);
            T VisitLogical(Logical e);
            T VisitCall(Call e);
            T VisitGrouping(Grouping e);
            T VisitFunction(Function e);
            T VisitTernary(Ternary e);
            T VisitIndex(Index e);
            T VisitArrayLiteral(ArrayLiteral e);
            T VisitObjectLiteral(ObjectLiteral e);
            T VisitProperty(Property e);
            T VisitDestructuringAssign(DestructuringAssign e);
            T VisitComma(Comma e);
        }
        public abstract T Accept<T>(IVisitor<T> v);

        public sealed class Literal : Expr
        {
            public Value Value;
            public Literal(Value v) { Value = v; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitLiteral(this);
        }
        public sealed class Variable : Expr
        {
            public string Name;
            public Variable(string name) { Name = name; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitVariable(this);
        }
        public sealed class Assign : Expr
        {
            public Expr Target; // Variable | Property | Index
            public Token Op;    // =, +=, -=, *=, /=, %=
            public Expr Value;
            public Assign(Expr target, Token op, Expr value) { Target = target; Op = op; Value = value; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitAssign(this);
        }
        public sealed class Unary : Expr
        {
            public Token Op; public Expr Right;
            public Unary(Token op, Expr right) { Op = op; Right = right; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitUnary(this);
        }
        public sealed class Binary : Expr
        {
            public Expr Left; public Token Op; public Expr Right;
            public Binary(Expr l, Token op, Expr r) { Left = l; Op = op; Right = r; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitBinary(this);
        }
        public sealed class Logical : Expr
        {
            public Expr Left; public Token Op; public Expr Right;
            public Logical(Expr l, Token op, Expr r) { Left = l; Op = op; Right = r; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitLogical(this);
        }
        public sealed class Call : Expr
        {
            public sealed class Argument
            {
                public string Name { get; }  // null for positional
                public Expr Value { get; }
                public bool IsNamed => Name != null;

                public Argument(string name, Expr value)
                {
                    Name = name;
                    Value = value;
                }
            }

            public Expr Callee { get; }
            public List<Argument> Args { get; }

            public Call(Expr callee, List<Argument> args)
            {
                Callee = callee;
                Args = args;
            }

            public override T Accept<T>(IVisitor<T> v) => v.VisitCall(this);
        }
        public sealed class Grouping : Expr
        {
            public Expr Inner;
            public Grouping(Expr inner) { Inner = inner; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitGrouping(this);
        }
        public sealed class Function : Expr
        {
            public List<Param> Parameters;
            public Stmt.Block Body;
            public bool IsArrow;

            public Function(List<Param> parameters, Stmt.Block body, bool isArrow = false)
            {
                Parameters = parameters; Body = body; IsArrow = isArrow;
            }
            public override T Accept<T>(IVisitor<T> v) => v.VisitFunction(this);
        }

        public sealed class Ternary : Expr
        {
            public Expr Cond, Then, Else;
            public Ternary(Expr cond, Expr thenE, Expr elseE) { Cond = cond; Then = thenE; Else = elseE; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitTernary(this);
        }
        public sealed class Index : Expr
        {
            public Expr Target;
            public Expr IndexExpr;
            public bool IsOptional; // via '?.['
            public Index(Expr t, Expr i, bool isOptional = false) { Target = t; IndexExpr = i; IsOptional = isOptional; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitIndex(this);
        }
        public sealed class ArrayLiteral : Expr
        {
            public List<Expr> Elements;
            public ArrayLiteral(List<Expr> elems) { Elements = elems; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitArrayLiteral(this);
        }
        public sealed class ObjectLiteral : Expr
        {
            public sealed class Entry
            {
                public string KeyName; // when identifier key or string literal
                public Expr KeyExpr;   // when computed key
                public Expr ValueExpr;
                public Entry(string keyName, Expr keyExpr, Expr valueExpr) { KeyName = keyName; KeyExpr = keyExpr; ValueExpr = valueExpr; }
            }
            public List<Entry> Entries;
            public ObjectLiteral(List<Entry> entries) { Entries = entries; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitObjectLiteral(this);
        }
        public sealed class Property : Expr
        {
            public Expr Target;
            public string Name;
            public bool IsOptional; // via '?.'
            public Property(Expr target, string name, bool isOptional = false) { Target = target; Name = name; IsOptional = isOptional; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitProperty(this);
        }

        // Destructuring assignment expression: pattern = value
        public sealed class DestructuringAssign : Expr
        {
            public Pattern Pat;
            public Expr Value;
            public DestructuringAssign(Pattern pat, Expr val) { Pat = pat; Value = val; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitDestructuringAssign(this);
        }

        // Patterns for destructuring
        public abstract class Pattern
        {
            public abstract void Bind(Interpreter interp, Value source, Action<string, Value> assignLocal, bool allowConstReassign);
        }

        public sealed class PatternIdentifier : Pattern
        {
            public string Name;
            public PatternIdentifier(string n) { Name = n; }
            public override void Bind(Interpreter interp, Value source, Action<string, Value> assignLocal, bool allowConstReassign)
            {
                assignLocal(Name, source);
            }
        }

        public sealed class PatternArray : Pattern
        {
            public sealed class Elem
            {
                public Pattern Inner;
                public Expr Default; // optional
                public Elem(Pattern inner, Expr def) { Inner = inner; Default = def; }
            }
            public List<Elem> Elements;
            public bool HasRest;
            public Pattern RestPattern; // PatternIdentifier or nested
            public PatternArray(List<Elem> elements, bool hasRest = false, Pattern rest = null)
            {
                Elements = elements; HasRest = hasRest; RestPattern = rest;
            }

            public override void Bind(Interpreter interp, Value source, Action<string, Value> assignLocal, bool allowConstReassign)
            {
                var arr = source.Type == ValueType.Array ? source.AsArray() : new ArrayValue();
                int idx = 0;
                foreach (var el in Elements)
                {
                    Value v;
                    if (idx < arr.Length)
                    {
                        v = arr[idx];
                        if (v.Type == ValueType.Nil && el.Default != null)
                            v = interp.EvaluateWithEnv(el.Default, interp.CurrentEnv);
                    }
                    else
                    {
                        v = el.Default != null
                            ? interp.EvaluateWithEnv(el.Default, interp.CurrentEnv)
                            : Value.Nil();
                    }
                    el.Inner.Bind(interp, v, assignLocal, allowConstReassign);
                    idx++;
                }
                if (HasRest && RestPattern != null)
                {
                    var rest = new ArrayValue();
                    for (; idx < arr.Length; idx++) rest.Items.Add(arr[idx]);
                    RestPattern.Bind(interp, Value.Array(rest), assignLocal, allowConstReassign);
                }
            }
        }

        public sealed class PatternObject : Pattern
        {
            public sealed class Prop
            {
                public string SourceKey;
                public Pattern TargetPattern; // often PatternIdentifier
                public Expr Default;
                public Prop(string key, Pattern pat, Expr def) { SourceKey = key; TargetPattern = pat; Default = def; }
            }

            public List<Prop> Props;
            public bool HasRest;
            public Pattern RestPattern; // binds remaining keys as object
            public PatternObject(List<Prop> props, bool hasRest = false, Pattern rest = null)
            {
                Props = props; HasRest = hasRest; RestPattern = rest;
            }

            public override void Bind(Interpreter interp, Value source, Action<string, Value> assignLocal, bool allowConstReassign)
            {
                var obj = source.Type == ValueType.Object ? source.AsObject() : new ObjectValue();
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in Props)
                {
                    Value v;
                    if (obj.TryGet(p.SourceKey, out var vv))
                    {
                        used.Add(p.SourceKey);
                        v = vv;
                    }
                    else if (p.Default != null)
                        v = interp.EvaluateWithEnv(p.Default, interp.CurrentEnv);
                    else
                        v = Value.Nil();
                    p.TargetPattern.Bind(interp, v, assignLocal, allowConstReassign);
                }
                if (HasRest && RestPattern != null)
                {
                    var rest = new ObjectValue();
                    foreach (var kv in obj.Entries)
                    {
                        if (!used.Contains(kv.Key)) rest.Set(kv.Key, kv.Value);
                    }
                    RestPattern.Bind(interp, Value.Object(rest), assignLocal, allowConstReassign);
                }
            }
        }

        public sealed class PatternLValue : Pattern
        {
            public Expr LValue;
            public PatternLValue(Expr lv) { LValue = lv; }
            public override void Bind(Interpreter interp, Value source, Action<string, Value> assignLocal, bool allowConstReassign)
            {
                // reuse the same assignment machinery as for := by creating a temporary Assign expr
                // However we need to perform direct assignment into the lvalue.
                // Build a fake Assign with '=' and evaluate it in current env:
                var assign = new Expr.Assign(LValue, new Token(TokenType.Assign, "=", null, 0, 0, 0), new Expr.Literal(source));
                interp.EvaluateWithEnv(assign, interp.CurrentEnv);
            }
        }
        public sealed class Param
        {
            public string Name;
            public Expr Default;
            public bool IsRest;
            public Param(string name, Expr def = null, bool isRest = false)
            {
                Name = name; Default = def; IsRest = isRest;
            }
        }

        // Comma expression (left, right) -> evaluates left then right, returns right
        public sealed class Comma : Expr
        {
            public Expr Left;
            public Expr Right;
            public Comma(Expr left, Expr right) { Left = left; Right = right; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitComma(this);
        }
    }

    public abstract class Stmt
    {
        public SourceSpan Span { get; set; } // populated by parser where convenient
        public interface IVisitor<T>
        {
            T VisitExpr(ExprStmt s);
            T VisitVar(Var s);
            T VisitLet(Let s);
            T VisitConst(Const s);
            T VisitBlock(Block s);
            T VisitIf(If s);
            T VisitWhile(While s);
            T VisitFunction(Function s);
            T VisitReturn(Return s);
            T VisitBreak(Break s);
            T VisitContinue(Continue s);
            T VisitDestructuringDecl(DestructuringDecl s);
            T VisitTryCatchFinally(TryCatchFinally s);
            T VisitThrow(Throw s);
            T VisitForEach(ForEach s);
            T VisitForClassic(ForClassic s);
            T VisitDeclList(DeclList s);
        }
        public abstract T Accept<T>(IVisitor<T> v);

       // A sequence of declarations that must run in the current scope (no new block scope).
       public sealed class DeclList : Stmt
       {
           public List<Stmt> Decls;
           public DeclList(List<Stmt> decls) { Decls = decls; }
           public override T Accept<T>(IVisitor<T> v) => v.VisitDeclList(this);
       }

        public sealed class ExprStmt : Stmt
        {
            public Expr Expression;
            public ExprStmt(Expr e) { Expression = e; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitExpr(this);
        }
        public sealed class Var : Stmt
        {
            public string Name;
            public Expr Initializer;
            public Var(string name, Expr init) { Name = name; Initializer = init; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitVar(this);
        }
        public sealed class Let : Stmt
        {
            public string Name;
            public Expr Initializer;
            public Let(string name, Expr init) { Name = name; Initializer = init; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitLet(this);
        }
        public sealed class Const : Stmt
        {
            public string Name; public Expr Initializer;
            public Const(string name, Expr init) { Name = name; Initializer = init; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitConst(this);
        }
        public sealed class DestructuringDecl : Stmt
        {
            public Expr.Pattern Pattern;
            public Expr Initializer;
            public enum Kind { Var, Let, Const }
            public Kind DeclKind;
            public DestructuringDecl(Expr.Pattern pat, Expr init, Kind kind) { Pattern = pat; Initializer = init; DeclKind = kind; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitDestructuringDecl(this);
        }
        public sealed class Block : Stmt
        {
            public List<Stmt> Statements;
            public Block(List<Stmt> st) { Statements = st; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitBlock(this);
        }
        public sealed class If : Stmt
        {
            public Expr Condition; public Stmt Then;
            public Stmt Else;
            public If(Expr cond, Stmt thenS, Stmt elseS) { Condition = cond; Then = thenS; Else = elseS; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitIf(this);
        }
        public sealed class While : Stmt
        {
            public Expr Condition; public Stmt Body;
            public While(Expr cond, Stmt body) { Condition = cond; Body = body; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitWhile(this);
        }
        public sealed class Function : Stmt
        {
            public string Name;
            public Expr.Function FuncExpr;
            public Function(string name, Expr.Function expr) { Name = name; FuncExpr = expr; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitFunction(this);
        }
        public sealed class Return : Stmt
        {
            public Expr Value;
            public Return(Expr value) { Value = value; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitReturn(this);
        }
        public sealed class Break : Stmt
        {
            public override T Accept<T>(IVisitor<T> v) => v.VisitBreak(this);
        }
        public sealed class Continue : Stmt
        {
            public override T Accept<T>(IVisitor<T> v) => v.VisitContinue(this);
        }
        public sealed class TryCatchFinally : Stmt
        {
            public Block Try;
            public string CatchName; // optional; null if no binding
            public Block Catch;      // optional
            public Block Finally;    // optional
            public override T Accept<T>(IVisitor<T> v) => v.VisitTryCatchFinally(this);
        }
        public sealed class Throw : Stmt
        {
            public Expr Value;
            public Throw(Expr value) { Value = value; }
            public override T Accept<T>(IVisitor<T> v) => v.VisitThrow(this);
        }

        // for ( ... ; ... ; ... ) { body }
        public sealed class ForClassic : Stmt
        {
            public Stmt Initializer; // null or Var/Let/Const/ExprStmt (without trailing ;)
            public Expr Condition;   // null => true
            public Expr Increment;   // null => no-op
            public Stmt Body;
            public override T Accept<T>(IVisitor<T> v) => v.VisitForClassic(this);
        }

        // for ( [decl] pattern in|of iterable ) { body }
        public sealed class ForEach : Stmt
        {
            public Expr.Pattern Pattern;
            public bool IsDeclaration;
            public DestructuringDecl.Kind DeclKind; // valid when IsDeclaration
            public bool IsOf;       // true => of; false => in
            public Expr Iterable;
            public Stmt Body;
            public override T Accept<T>(IVisitor<T> v) => v.VisitForEach(this);
        }
    }

    // Parser
    public class Parser
    {
        private readonly List<Token> _tokens;
        private int _current;

        public Parser(Lexer lexer)
        {
            _tokens = new List<Token>();
            Token t;
            do { t = lexer.NextToken(); _tokens.Add(t); } while (t.Type != TokenType.EOF);
        }

        private Expr.Pattern ParseAliasPatternOrLValue()
        {
            if (!Check(TokenType.Identifier))
                return ParseSinglePattern();

            var baseTok = Advance();
            var baseVar = new Expr.Variable((string)baseTok.Literal);
            baseVar.Span = SourceSpan.FromToken(baseTok);
            Expr expr = baseVar;

            bool hasAccessor = false;          // did we see '.' or '[' ?

            while (true)
            {
                if (Match(TokenType.Dot))
                {
                    var nameTok = Consume(TokenType.Identifier, "Expected property name after '.'");
                    var prop = new Expr.Property(expr, (string)nameTok.Literal);
                    prop.Span = SourceSpan.FromToken(nameTok);
                    expr = prop;
                    hasAccessor = true;
                }
                else if (Match(TokenType.LBracket))
                {
                    var lbrTok = Previous();
                    var idx = Expression();
                    Consume(TokenType.RBracket, "Expected ']'");
                    var index = new Expr.Index(expr, idx);
                    index.Span = SourceSpan.FromToken(lbrTok);
                    expr = index;
                    hasAccessor = true;
                }
                else break;
            }

            // simple alias like "bb" - declare a new variable
            if (!hasAccessor)
                return new Expr.PatternIdentifier((string)baseTok.Literal);

            return new Expr.PatternLValue(expr);
        }

        private Token Peek() => _tokens[_current];
        private Token Previous() => _tokens[_current - 1];
        private bool IsAtEnd => Peek().Type == TokenType.EOF;
        private bool Check(TokenType t) => !IsAtEnd && Peek().Type == t;
        private Token Advance()
        {
            if (!IsAtEnd) _current++;
            return Previous();
        }
        private bool Match(params TokenType[] types)
        {
            foreach (var t in types)
            {
                if (Check(t)) { Advance(); return true; }
            }
            return false;
        }
        private Token Consume(TokenType t, string msg)
        {
            if (Check(t)) return Advance();
            var p = Peek();
            throw new MiniDynParseError(msg, p.Line, p.Column);
        }

        public List<Stmt> Parse()
        {
            List<Stmt> stmts = new List<Stmt>();
            while (!IsAtEnd) stmts.Add(Declaration());
            return stmts;
        }

        private Stmt Declaration()
        {
            if (Match(TokenType.Fn)) { var fnTok = Previous(); return FunctionDecl(fnTok, "function"); }
            if (Match(TokenType.Const)) return ConstDeclOrDestructuring();
            if (Match(TokenType.Let)) return LetDeclOrDestructuring();
            if (Match(TokenType.Var)) return VarDeclOrDestructuring();
            return Statement();
        }

        private List<Expr.Param> ParseParamList()
        {
            var parameters = new List<Expr.Param>();
            if (!Check(TokenType.RParen))
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                bool sawRest = false;

                while (true)
                {
                    if (Match(TokenType.Ellipsis))
                    {
                        if (sawRest)
                            throw new MiniDynParseError("Only one rest parameter allowed", Peek().Line, Peek().Column);

                        var nameTok = Consume(TokenType.Identifier, "Expected rest parameter name after '...'");
                        var name = (string)nameTok.Literal;

                        if (!seen.Add(name))
                            throw new MiniDynParseError($"Duplicate parameter name '{name}'", nameTok.Line, nameTok.Column);

                        parameters.Add(new Expr.Param(name, null, isRest: true));
                        sawRest = true;

                        if (Match(TokenType.Comma))
                            throw new MiniDynParseError("Rest parameter must be last", Peek().Line, Peek().Column);

                        break;
                    }

                    var nameTok2 = Consume(TokenType.Identifier, "Expected parameter name");
                    var pname = (string)nameTok2.Literal;

                    if (!seen.Add(pname))
                        throw new MiniDynParseError($"Duplicate parameter name '{pname}'", nameTok2.Line, nameTok2.Column);

                    Expr def = null;
                    if (Match(TokenType.Assign))
                    {
                        // default value should allow ternary but not comma operator
                        def = Ternary();
                    }
                    parameters.Add(new Expr.Param(pname, def, isRest: false));

                    if (!Match(TokenType.Comma))
                        break;
                }
            }
            return parameters;
        }

        private Stmt FunctionDecl(Token fnTok, string kind)
        {
            var nameTok = Consume(TokenType.Identifier, $"Expected {kind} name");
            Consume(TokenType.LParen, "Expected '('");
            var parameters = ParseParamList();
            Consume(TokenType.RParen, "Expected ')'");
            Consume(TokenType.LBrace, "Expected '{' before function body");
            var body = BlockStatementInternal();
            var funcExpr = new Expr.Function(parameters, body, isArrow: false);
            funcExpr.Span = SourceSpan.FromToken(fnTok);
            var decl = new Stmt.Function((string)nameTok.Literal, funcExpr);
            decl.Span = SourceSpan.FromToken(fnTok);
            return decl;
        }

        // Destructuring patterns
        private Expr.Pattern ParsePattern()
        {
            if (Match(TokenType.LBracket))
            {
                var elements = new List<Expr.PatternArray.Elem>();
                bool hasRest = false;
                Expr.Pattern rest = null;
                if (!Check(TokenType.RBracket))
                {
                    do
                    {
                        if (Match(TokenType.Ellipsis))
                        {
                            rest = ParseSinglePattern();
                            hasRest = true;
                            break;
                        }
                        var pat = ParseSinglePattern();
                        Expr def = null;
                        if (Match(TokenType.Assign))
                        {
                            // default value: allow ternary but not comma operator
                            def = Ternary();
                        }
                        elements.Add(new Expr.PatternArray.Elem(pat, def));
                    } while (Match(TokenType.Comma));
                }
                Consume(TokenType.RBracket, "Expected ']'");
                return new Expr.PatternArray(elements, hasRest, rest);
            }
            else if (Match(TokenType.LBrace))
            {
                var props = new List<Expr.PatternObject.Prop>();
                bool hasRest = false;
                Expr.Pattern rest = null;
                if (!Check(TokenType.RBrace))
                {
                    do
                    {
                        if (Match(TokenType.Ellipsis))
                        {
                            rest = ParseSinglePattern();
                            hasRest = true;
                            break;
                        }
                        // key [: aliasPattern] [= default]
                        string key;
                        if (Match(TokenType.Identifier))
                            key = (string)Previous().Literal;
                        else if (Match(TokenType.String))
                            key = (string)Previous().Literal;
                        else
                            throw new MiniDynParseError("Expected property name in object pattern", Peek().Line, Peek().Column);

                        Expr.Pattern aliasPat;
                        if (Match(TokenType.Colon))
                        {
                            aliasPat = ParseAliasPatternOrLValue();
                        }
                        else
                        {
                            aliasPat = new Expr.PatternIdentifier(key);
                        }
                        Expr def = null;
                        if (Match(TokenType.Assign))
                        {
                            // default value: allow ternary but not comma operator
                            def = Ternary();
                        }
                        props.Add(new Expr.PatternObject.Prop(key, aliasPat, def));
                    } while (Match(TokenType.Comma));
                }
                Consume(TokenType.RBrace, "Expected '}'");
                return new Expr.PatternObject(props, hasRest, rest);
            }
            else
            {
                return ParseSinglePattern();
            }
        }

        private Expr.Pattern ParseSinglePattern()
        {
            if (Match(TokenType.Identifier))
            {
                return new Expr.PatternIdentifier((string)Previous().Literal);
            }
            else if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                return ParsePattern();
            }
            throw new MiniDynParseError("Invalid pattern: expected identifier, array pattern, or object pattern", Peek().Line, Peek().Column);
        }

        private Stmt VarDeclOrDestructuring()
        {
            var kwTok = Previous(); // 'var'
            // var <pattern or name> [= initializer] ;
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                Expr init;
                if (Match(TokenType.Assign)) init = Expression();
                else throw new MiniDynParseError("Destructuring declaration requires initializer", Peek().Line, Peek().Column);
                Consume(TokenType.Semicolon, "Expected ';'");
                var d = new Stmt.DestructuringDecl(pat, init, Stmt.DestructuringDecl.Kind.Var);
                d.Span = SourceSpan.FromToken(kwTok);
                return d;
            }
           // Multiple declarators: var a = ..., b = ... ;
           var decls = new List<Stmt>();
           do
           {
               var nameTok = Consume(TokenType.Identifier, "Expected variable name");
               Expr init2 = null;
               if (Match(TokenType.Assign))
                   init2 = Ternary(); // prevent comma from bleeding into initializer
               var s = new Stmt.Var((string)nameTok.Literal, init2) { Span = SourceSpan.FromToken(kwTok) };
               decls.Add(s);
           } while (Match(TokenType.Comma));
           Consume(TokenType.Semicolon, "Expected ';'");
           return (decls.Count == 1) ? decls[0] : new Stmt.DeclList(decls) { Span = SourceSpan.FromToken(kwTok) };
        }

        private Stmt LetDeclOrDestructuring()
        {
            var kwTok = Previous(); // 'let'
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                Expr init;
                if (Match(TokenType.Assign)) init = Expression();
                else throw new MiniDynParseError("Destructuring declaration requires initializer", Peek().Line, Peek().Column);
                Consume(TokenType.Semicolon, "Expected ';'");
                var d = new Stmt.DestructuringDecl(pat, init, Stmt.DestructuringDecl.Kind.Let);
                d.Span = SourceSpan.FromToken(kwTok);
                return d;
            }
            // Multiple declarators: let a = ..., b = ... ;
            var decls = new List<Stmt>();
            do
            {
                var nameTok = Consume(TokenType.Identifier, "Expected variable name");
                Expr init2 = null;
                if (Match(TokenType.Assign))
                   init2 = Ternary(); // prevent comma from bleeding into initializer
               var s = new Stmt.Let((string)nameTok.Literal, init2) { Span = SourceSpan.FromToken(kwTok) };
               decls.Add(s);
           } while (Match(TokenType.Comma));
           Consume(TokenType.Semicolon, "Expected ';'");
           return (decls.Count == 1) ? decls[0] : new Stmt.DeclList(decls) { Span = SourceSpan.FromToken(kwTok) };

        }

        private Stmt ConstDeclOrDestructuring()
        {
            var kwTok = Previous(); // 'const'
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                var init = ParseInitializerRequired();
                Consume(TokenType.Semicolon, "Expected ';'");
                var d = new Stmt.DestructuringDecl(pat, init, Stmt.DestructuringDecl.Kind.Const);
                d.Span = SourceSpan.FromToken(kwTok);
                return d;
            }
            // Multiple declarators: const a = ..., b = ... ; (each requires initializer)
            var decls = new List<Stmt>();
            do
            {
                var nameTok = Consume(TokenType.Identifier, "Expected constant name");
                Consume(TokenType.Assign, "Expected '=' after const name");
                var init2 = Ternary(); // prevent comma from bleeding into initializer
                var s = new Stmt.Const((string)nameTok.Literal, init2) { Span = SourceSpan.FromToken(kwTok) };
                decls.Add(s);
            } while (Match(TokenType.Comma));
            Consume(TokenType.Semicolon, "Expected ';'");
            return (decls.Count == 1) ? decls[0] : new Stmt.DeclList(decls) { Span = SourceSpan.FromToken(kwTok) };
        }

        private Expr ParseInitializerRequired()
        {
            Consume(TokenType.Assign, "Expected '=' for initializer");
            return Expression();
        }

        private Stmt Statement()
        {
            if (Match(TokenType.If)) { var ifTok = Previous(); return IfStatement(ifTok); }
            if (Match(TokenType.While)) { var whileTok = Previous(); return WhileStatement(whileTok); }
            if (Match(TokenType.Break)) { var brTok = Previous(); Consume(TokenType.Semicolon, "Expected ';' after break"); var s = new Stmt.Break(); s.Span = SourceSpan.FromToken(brTok); return s; }
            if (Match(TokenType.Continue)) { var ctTok = Previous(); Consume(TokenType.Semicolon, "Expected ';' after continue"); var s = new Stmt.Continue(); s.Span = SourceSpan.FromToken(ctTok); return s; }
            if (Match(TokenType.Return))
            {
                var retTok = Previous();
                Expr value = null;
                if (!Check(TokenType.Semicolon)) value = Expression();
                Consume(TokenType.Semicolon, "Expected ';' after return value");
                var s = new Stmt.Return(value);
                s.Span = SourceSpan.FromToken(retTok);
                return s;
            }
            if (Match(TokenType.Throw))
            {
                var thrTok = Previous();
                var val = Expression();
                Consume(TokenType.Semicolon, "Expected ';' after throw value");
                var s = new Stmt.Throw(val) { Span = SourceSpan.FromToken(thrTok) };
                return s;
            }
            if (Match(TokenType.Try))
            {
                var tryTok = Previous();
                Consume(TokenType.LBrace, "Expected '{' after try");
                var tryBlock = BlockStatementInternal();

                Stmt.Block catchBlock = null;
                string catchName = null;
                Stmt.Block finallyBlock = null;

                if (Match(TokenType.Catch))
                {
                    // Optional parameter: catch (name) { ... } or catch { ... }
                    if (Match(TokenType.LParen))
                    {
                        if (Check(TokenType.Identifier))
                            catchName = (string)Advance().Literal;
                        Consume(TokenType.RParen, "Expected ')' after catch parameter");
                    }
                    Consume(TokenType.LBrace, "Expected '{' after catch");
                    catchBlock = BlockStatementInternal();
                }

                if (Match(TokenType.Finally))
                {
                    Consume(TokenType.LBrace, "Expected '{' after finally");
                    finallyBlock = BlockStatementInternal();
                }

                if (catchBlock == null && finallyBlock == null)
                    throw new MiniDynParseError("Expected 'catch' or 'finally' after try block", Peek().Line, Peek().Column);

                var s = new Stmt.TryCatchFinally
                {
                    Try = tryBlock,
                    Catch = catchBlock,
                    CatchName = catchName,
                    Finally = finallyBlock,
                    Span = SourceSpan.FromToken(tryTok)
                };
                return s;
            }

            if (Match(TokenType.For))
            {
                var forTok = Previous();
                Consume(TokenType.LParen, "Expected '(' after 'for'");

                // Lookahead: classic if a ';' appears before matching ')'
                if (SeesSemicolonBeforeRParen())
                {
                    var fc = new Stmt.ForClassic();
                    // Initializer
                    if (Match(TokenType.Semicolon))
                    {
                        fc.Initializer = null;
                    }
                    else if (Match(TokenType.Var))
                    {
                        fc.Initializer = ParseForInitializerDecl(Stmt.DestructuringDecl.Kind.Var);
                        Consume(TokenType.Semicolon, "Expected ';' after for-initializer");
                    }
                    else if (Match(TokenType.Let))
                    {
                        fc.Initializer = ParseForInitializerDecl(Stmt.DestructuringDecl.Kind.Let);
                        Consume(TokenType.Semicolon, "Expected ';' after for-initializer");
                    }
                    else if (Match(TokenType.Const))
                    {
                        fc.Initializer = ParseForInitializerDecl(Stmt.DestructuringDecl.Kind.Const);
                        Consume(TokenType.Semicolon, "Expected ';' after for-initializer");
                    }
                    else
                    {
                        var initExpr = CommaExpr();
                        Consume(TokenType.Semicolon, "Expected ';' after for-initializer");
                        fc.Initializer = new Stmt.ExprStmt(initExpr) { Span = initExpr?.Span ?? default(SourceSpan) };
                    }

                    // Condition
                    if (Match(TokenType.Semicolon))
                        fc.Condition = null;
                    else
                    {
                        fc.Condition = Expression();
                        Consume(TokenType.Semicolon, "Expected ';' after for-condition");
                    }

                    // Increment
                    if (Match(TokenType.RParen))
                        fc.Increment = null;
                    else
                    {
                        fc.Increment = Expression();
                        Consume(TokenType.RParen, "Expected ')' after for-increment");
                    }

                    fc.Body = Statement();
                    fc.Span = SourceSpan.FromToken(forTok);
                    return fc;
                }
                else
                {
                    // for-in/of
                    var fe = new Stmt.ForEach();
                    fe.Span = SourceSpan.FromToken(forTok);

                    bool isDecl = false;
                    Stmt.DestructuringDecl.Kind declKind = Stmt.DestructuringDecl.Kind.Let; // default

                    if (Match(TokenType.Var))
                    {
                        isDecl = true; declKind = Stmt.DestructuringDecl.Kind.Var;
                        fe.Pattern = ParseForHeadPatternDeclaration();
                    }
                    else if (Match(TokenType.Let))
                    {
                        isDecl = true; declKind = Stmt.DestructuringDecl.Kind.Let;
                        fe.Pattern = ParseForHeadPatternDeclaration();
                    }
                    else if (Match(TokenType.Const))
                    {
                        isDecl = true; declKind = Stmt.DestructuringDecl.Kind.Const;
                        fe.Pattern = ParseForHeadPatternDeclaration();
                    }
                    else
                    {
                        // Not a declaration: allow destructuring or lvalue pattern
                        if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
                            fe.Pattern = ParsePattern();
                        else
                            fe.Pattern = ParseAliasPatternOrLValue();
                    }

                    if (Match(TokenType.Of))
                        fe.IsOf = true;
                    else if (Match(TokenType.In))
                        fe.IsOf = false;
                    else
                        throw new MiniDynParseError("Expected 'of' or 'in' in for-statement", Peek().Line, Peek().Column);

                    fe.IsDeclaration = isDecl;
                    fe.DeclKind = declKind;

                    fe.Iterable = Expression();
                    Consume(TokenType.RParen, "Expected ')' after for-head");
                    fe.Body = Statement();
                    return fe;
                }
            }

            // Array destructuring assignment: [a,b] = expr;
            if (Check(TokenType.LBracket))
            {
                var pat = ParsePattern();
                var assignTok = Consume(TokenType.Assign, "Expected '=' in destructuring assignment");
                var val = Expression();
                Consume(TokenType.Semicolon, "Expected ';'");
                var e = new Expr.DestructuringAssign(pat, val);
                e.Span = SourceSpan.FromToken(assignTok);
                var s = new Stmt.ExprStmt(e) { Span = e.Span };
                return s;
            }

            // For '{', we need to look ahead to determine if it's a block or destructuring
            if (Check(TokenType.LBrace))
            {
                // Try to determine if this is a destructuring pattern or a block
                int savePoint = _current;
                bool isDestructuring = false;

                try
                {
                    Advance(); // consume '{'

                    // Look for pattern-like syntax
                    if (!Check(TokenType.RBrace))
                    {
                        var nextToken = PeekAhead(1);
                        // Check if it looks like object destructuring
                        if (Check(TokenType.Ellipsis) ||
                            (Check(TokenType.Identifier) && (nextToken?.Type == TokenType.Colon || nextToken?.Type == TokenType.Comma || nextToken?.Type == TokenType.Assign)) ||
                            Check(TokenType.String))
                        {
                            // Skip to find '}'
                            int braceCount = 1;
                            while (!IsAtEnd && braceCount > 0)
                            {
                                if (Check(TokenType.LBrace)) braceCount++;
                                else if (Check(TokenType.RBrace)) braceCount--;
                                if (braceCount > 0) Advance();
                            }

                            if (braceCount == 0)
                            {
                                Advance(); // consume '}'
                                // Check if followed by '='
                                if (Check(TokenType.Assign))
                                {
                                    isDestructuring = true;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // If we hit an error, assume it's not destructuring
                }

                // Restore position
                _current = savePoint;

                if (isDestructuring)
                {
                    var pat = ParsePattern();
                    var assignTok2 = Consume(TokenType.Assign, "Expected '=' in destructuring assignment");
                    var val = Expression();
                    Consume(TokenType.Semicolon, "Expected ';'");
                    var e = new Expr.DestructuringAssign(pat, val);
                    e.Span = SourceSpan.FromToken(assignTok2);
                    var s = new Stmt.ExprStmt(e) { Span = e.Span };
                    return s;
                }
                else
                {
                    // Plain block statement
                    Advance(); // consume '{'
                    return BlockStatementInternal();
                }
            }

            return ExprStatement();
        }

        // Scan tokens from current position to find a ';' before the matching ')'
        private bool SeesSemicolonBeforeRParen()
        {
            int i = _current;
            int depth = 0; // nested parentheses
            while (i < _tokens.Count)
            {
                var t = _tokens[i];
                if (t.Type == TokenType.LParen) depth++;
                else if (t.Type == TokenType.RParen)
                {
                    if (depth == 0) return false;
                    depth--;
                }
                else if (t.Type == TokenType.Semicolon && depth == 0)
                {
                    return true;
                }
                i++;
            }
            return false;
        }

        // Parse an initializer declaration in classic for (without consuming the trailing ';')
        private Stmt ParseForInitializerDecl(Stmt.DestructuringDecl.Kind kind)
        {
            // Pattern or single name
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                // initializer required
                Consume(TokenType.Assign, "Destructuring declaration requires initializer");
                var init = Expression();
                return new Stmt.DestructuringDecl(pat, init, kind);
            }
            else
            {
                var nameTok = Consume(TokenType.Identifier, "Expected variable name");
                if (kind == Stmt.DestructuringDecl.Kind.Const)
                {
                    Consume(TokenType.Assign, "Expected '=' after const name");
                    var init = Expression();
                    return new Stmt.Const((string)nameTok.Literal, init);
                }
                else
                {
                    Expr init = null;
                    if (Match(TokenType.Assign)) init = Expression();
                    return (kind == Stmt.DestructuringDecl.Kind.Var)
                        ? (Stmt)new Stmt.Var((string)nameTok.Literal, init)
                        : new Stmt.Let((string)nameTok.Literal, init);
                }
            }
        }

        // Parse pattern after var/let/const in for-of/in head; initializers are NOT allowed
        private Expr.Pattern ParseForHeadPatternDeclaration()
        {
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                if (Check(TokenType.Assign))
                    throw new MiniDynParseError("Initializer not allowed in for-in/of declaration", Peek().Line, Peek().Column);
                return pat;
            }
            var nameTok = Consume(TokenType.Identifier, "Expected identifier");
            if (Check(TokenType.Assign))
                throw new MiniDynParseError("Initializer not allowed in for-in/of declaration", Peek().Line, Peek().Column);
            return new Expr.PatternIdentifier((string)nameTok.Literal);
        }

        private Stmt IfStatement(Token ifTok)
        {
            Consume(TokenType.LParen, "Expected '(' after if");
            var cond = Expression();
            Consume(TokenType.RParen, "Expected ')'");
            var thenS = Statement();
            Stmt elseS = null;
            if (Match(TokenType.Else)) elseS = Statement();
            var s = new Stmt.If(cond, thenS, elseS);
            s.Span = SourceSpan.FromToken(ifTok);
            return s;
        }

        private Stmt WhileStatement(Token whileTok)
        {
            Consume(TokenType.LParen, "Expected '(' after while");
            var cond = Expression();
            Consume(TokenType.RParen, "Expected ')'");
            var body = Statement();
            var s = new Stmt.While(cond, body);
            s.Span = SourceSpan.FromToken(whileTok);
            return s;
        }

        private Stmt.Block BlockStatementInternal()
        {
            var lbraceTok = Previous(); // caller just consumed '{'
            List<Stmt> stmts = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !IsAtEnd)
                stmts.Add(Declaration());
            Consume(TokenType.RBrace, "Expected '}'");
            var b = new Stmt.Block(stmts);
            b.Span = SourceSpan.FromToken(lbraceTok);
            return b;
        }

        private Stmt ExprStatement()
        {
            var expr = Expression();
            Consume(TokenType.Semicolon, "Expected ';'");
            return new Stmt.ExprStmt(expr) { Span = expr?.Span ?? default(SourceSpan) };
        }

        // expression -> comma
        private Expr Expression() => CommaExpr();

        // lowest precedence comma operator: expr , expr , expr ...
        private Expr CommaExpr()
        {
            var expr = Ternary();
            while (Match(TokenType.Comma))
            {
                var right = Ternary();
                expr = new Expr.Comma(expr, right);
            }
            return expr;
        }

        // ternary -> or ('?' expression ':' expression)?
        private Expr Ternary()
        {
            var cond = Assignment();
            if (Match(TokenType.Question))
            {
                var qTok = Previous();
                // Do not allow the comma operator to bleed across argument/element boundaries.
                // Use Assignment for 'then' and recurse to Ternary for right-associativity on 'else'.
                var thenE = Assignment();
                Consume(TokenType.Colon, "Expected ':' in ternary expression");
                var elseE = Ternary();
                var t = new Expr.Ternary(cond, thenE, elseE);
                t.Span = SourceSpan.FromToken(qTok);
                return t;
            }
            return cond;
        }

        // assignment -> lvalue ( '=' | op_assign ) assignment | logic_or
        // lvalue can be Variable, Property, Index, or Pattern (for destructuring)
        private Expr Assignment()
        {
            // NOTE: precedence change: assignment consumes from Nullish() (which sits between Or() and Ternary()).
            var expr = Nullish();

            if (Match(TokenType.Assign, TokenType.PlusAssign, TokenType.MinusAssign, TokenType.StarAssign, TokenType.SlashAssign, TokenType.PercentAssign, TokenType.NullishAssign))
            {
                Token op = Previous();
                // handle destructuring assign e.g. [a,b] = RHS
                if (expr is Expr.Index || expr is Expr.Property || expr is Expr.Variable)
                {
                    var value = Assignment();
                    var a = new Expr.Assign(expr, op, value);
                    a.Span = SourceSpan.FromToken(op);
                    return a;
                }
                else if (expr is Expr.Grouping g && (g.Inner is Expr.Index || g.Inner is Expr.Property || g.Inner is Expr.Variable))
                {
                    var value = Assignment();
                    var a = new Expr.Assign(g.Inner, op, value);
                    a.Span = SourceSpan.FromToken(op);
                    return a;
                }
                else
                {
                    throw new MiniDynParseError("Invalid assignment target", Previous().Line, Previous().Column);
                }
            }
            return expr;
        }

        private Expr Or()
        {
            var expr = And();
            while (Match(TokenType.Or))
            {
                var op = Previous();
                var right = And();
                var n = new Expr.Logical(expr, op, right) { Span = SourceSpan.FromToken(op) };
                expr = n;
            }
            return expr;
        }

        private Expr And()
        {
            var expr = Equality();
            while (Match(TokenType.And))
            {
                var op = Previous();
                var right = Equality();
                var n = new Expr.Logical(expr, op, right) { Span = SourceSpan.FromToken(op) };
                expr = n;
            }
            return expr;
        }

        private Expr Equality()
        {
            var expr = Comparison();
            while (Match(TokenType.Equal, TokenType.NotEqual))
            {
                var op = Previous();
                var right = Comparison();
                var n = new Expr.Binary(expr, op, right) { Span = SourceSpan.FromToken(op) };
                expr = n;
            }
            return expr;
        }

        private Expr Comparison()
        {
            var expr = Term();
            while (Match(TokenType.Less, TokenType.LessEq, TokenType.Greater, TokenType.GreaterEq))
            {
                var op = Previous();
                var right = Term();
                var n = new Expr.Binary(expr, op, right) { Span = SourceSpan.FromToken(op) };
                expr = n;
            }
            return expr;
        }

        private Expr Term()
        {
            var expr = Factor();
            while (Match(TokenType.Plus, TokenType.Minus))
            {
                var op = Previous();
                var right = Factor();
                var n = new Expr.Binary(expr, op, right) { Span = SourceSpan.FromToken(op) };
                expr = n;
            }
            return expr;
        }

        private Expr Factor()
        {
            var expr = Unary();
            while (Match(TokenType.Star, TokenType.Slash, TokenType.Percent))
            {
                var op = Previous();
                var right = Unary();
                var n = new Expr.Binary(expr, op, right) { Span = SourceSpan.FromToken(op) };
                expr = n;
            }
            return expr;
        }

        private Expr Unary()
        {
            if (Match(TokenType.Plus, TokenType.Minus, TokenType.Not))
            {
                var op = Previous();
                var right = Unary();
                var u = new Expr.Unary(op, right);
                u.Span = SourceSpan.FromToken(op);
                return u;
            }
            return Member();
        }

        // precedence level: nullish coalescing between Or() and Ternary()
        private Expr Nullish()
        {
            var expr = Or();
            while (Match(TokenType.NullishCoalesce))
            {
                var op = Previous();
                var right = Or();
                var n = new Expr.Binary(expr, op, right) { Span = SourceSpan.FromToken(op) };
                expr = n;
            }
            return expr;
        }

        private Expr Member()
        {
            Expr expr = Primary();

            while (true)
            {
                // Optional chaining: '?.' followed by identifier or '['
                if (Match(TokenType.QuestionDot))
                {
                    // optional index a?.[expr]
                    if (Match(TokenType.LBracket))
                    {
                        var lbrTok = Previous();
                        var indexExpr = Ternary(); // allow ternary, not comma
                        Consume(TokenType.RBracket, "Expected ']'");
                        var idx = new Expr.Index(expr, indexExpr, isOptional: true);
                        idx.Span = SourceSpan.FromToken(lbrTok);
                        expr = idx;
                        continue;
                    }
                    // optional property a?.prop
                    var nameTok = Consume(TokenType.Identifier, "Expected property name after '?.'");
                    var prop = new Expr.Property(expr, (string)nameTok.Literal, isOptional: true);
                    prop.Span = SourceSpan.FromToken(nameTok);
                    expr = prop;
                    continue;
                }

                // 1. function / method call
                if (Match(TokenType.LParen))
                {
                    var lparTok = Previous();
                    var args = new List<Expr.Call.Argument>();

                    if (!Check(TokenType.RParen))
                    {
                        do
                        {
                            var nextToken = PeekAhead(1);
                            if (Check(TokenType.Identifier) && nextToken?.Type == TokenType.Colon)
                            {
                                var nameTok = Advance();
                                Consume(TokenType.Colon, "Expected ':'");
                                args.Add(new Expr.Call.Argument(
                                    (string)nameTok.Literal,
                                    Ternary()));
                            }
                            else
                            {
                                args.Add(new Expr.Call.Argument(null, Ternary()));
                            }
                        } while (Match(TokenType.Comma));
                    }

                    Consume(TokenType.RParen, "Expected ')'");
                    var call = new Expr.Call(expr, args);
                    call.Span = SourceSpan.FromToken(lparTok);
                    expr = call;
                    continue;
                }

                // 2. property access '.'
                if (Match(TokenType.Dot))
                {
                    var nameTok = Consume(TokenType.Identifier, "Expected property name after '.'");
                    var prop = new Expr.Property(expr, (string)nameTok.Literal, isOptional: false);
                    prop.Span = SourceSpan.FromToken(nameTok);
                    expr = prop;
                    continue;
                }

                // 3. index access '['
                if (Match(TokenType.LBracket))
                {
                    var lbrTok = Previous();
                    var indexExpr = Ternary();
                    Consume(TokenType.RBracket, "Expected ']'");
                    var idx = new Expr.Index(expr, indexExpr, isOptional: false);
                    idx.Span = SourceSpan.FromToken(lbrTok);
                    expr = idx;
                    continue;
                }

                break;
            }

            return expr;
        }

        private Expr Primary()
        {
            if (Match(TokenType.Number))
            {
                var tok = Previous();
                var lit = new Expr.Literal(Value.Number((NumberValue)tok.Literal));
                lit.Span = SourceSpan.FromToken(tok);
                return lit;
            }
            if (Match(TokenType.String))
            {
                var tok = Previous();
                var expr = BuildStringExprFromToken(tok);
                return expr;
            }
            if (Match(TokenType.True))
            {
                var tok = Previous();
                var lit = new Expr.Literal(Value.Boolean(true));
                lit.Span = SourceSpan.FromToken(tok);
                return lit;
            }
            if (Match(TokenType.False))
            {
                var tok = Previous();
                var lit = new Expr.Literal(Value.Boolean(false));
                lit.Span = SourceSpan.FromToken(tok);
                return lit;
            }
            if (Match(TokenType.Nil))
            {
                var tok = Previous();
                var lit = new Expr.Literal(Value.Nil());
                lit.Span = SourceSpan.FromToken(tok);
                return lit;
            }
            if (Match(TokenType.LBracket))
            {
                var lbrTok = Previous();
                var elems = new List<Expr>();

                // We may be looking at "]" right away – empty literal "[]"
                while (!Check(TokenType.RBracket))
                {
                    // Support elisions:        [1, , 3]  or  [, 2]
                    if (Check(TokenType.Comma))
                    {
                        // a hole – treat it as explicit nil
                        elems.Add(new Expr.Literal(Value.Nil()));
                    }
                    else
                    {
                        // IMPORTANT: Allow ternary, but not comma operator (comma separates elements)
                        elems.Add(Ternary());
                    }

                    // If there is a comma, consume it and loop again.
                    // If the next token is ']', the loop will exit.
                    Match(TokenType.Comma);
                }

                Consume(TokenType.RBracket, "Expected ']'");
                var arr = new Expr.ArrayLiteral(elems);
                arr.Span = SourceSpan.FromToken(lbrTok);
                return arr;
            }
            if (Match(TokenType.LBrace))
            {
                var lbraceTok = Previous();
                var entries = new List<Expr.ObjectLiteral.Entry>();
                if (!Check(TokenType.RBrace))
                {
                    do
                    {
                        // key: value
                        if (Check(TokenType.Identifier))
                        {
                            var keyTok = Advance();
                            string keyName = (string)keyTok.Literal;
                            if (Match(TokenType.Colon))
                            {
                                // Allow ternary in value, not comma operator
                                var valExpr = Ternary();
                                entries.Add(new Expr.ObjectLiteral.Entry(keyName, null, valExpr));
                            }
                            else
                            {
                                // shorthand { a } -> { a: a }
                                entries.Add(new Expr.ObjectLiteral.Entry(keyName, null, new Expr.Variable(keyName)));
                            }
                        }
                        else if (Check(TokenType.String))
                        {
                            var keyTok = Advance();
                            string keyName = (string)keyTok.Literal;
                            Consume(TokenType.Colon, "Expected ':' after string key");
                            var valExpr = Ternary(); // allow ternary value
                            entries.Add(new Expr.ObjectLiteral.Entry(keyName, null, valExpr));
                        }
                        else if (Match(TokenType.LBracket))
                        {
                            // computed key [expr] : value
                            var keyExpr = Ternary(); // allow ternary in key
                            Consume(TokenType.RBracket, "Expected ']'");
                            Consume(TokenType.Colon, "Expected ':' after computed key");
                            var valExpr = Ternary(); // allow ternary in value
                            entries.Add(new Expr.ObjectLiteral.Entry(null, keyExpr, valExpr));
                        }
                        else
                        {
                            throw new MiniDynParseError("Invalid object literal entry", Peek().Line, Peek().Column);
                        }
                    } while (Match(TokenType.Comma));
                }
                Consume(TokenType.RBrace, "Expected '}'");
                var obj = new Expr.ObjectLiteral(entries);
                obj.Span = SourceSpan.FromToken(lbraceTok);
                return obj;
            }

            if (Match(TokenType.LParen))
            {
                var lparenTok = Previous();
                // Could be grouped expression or arrow function parameters
                int savePoint = _current;

                // Try to parse as arrow function parameters
                if (TryParseArrowFunction(out var arrowFunc))
                {
                    return arrowFunc;
                }

                // Not arrow function, restore and parse as grouping
                _current = savePoint; // we are already right after '('
                var e = Expression();
                Consume(TokenType.RParen, "Expected ')'");
                var g = new Expr.Grouping(e);
                g.Span = SourceSpan.FromToken(lparenTok);
                return g;
            }

            var nextTokenIsArrow = PeekAhead(1)?.Type == TokenType.Arrow;
            // ──────────────── 1. Single-parameter arrow function  x => expr ────────────────
            // Must be checked before we treat the identifier as an ordinary variable.
            if (Check(TokenType.Identifier) && nextTokenIsArrow)
            {
                var param = Advance();
                var arrowTok = Consume(TokenType.Arrow, "Expected '=>'");

                Stmt.Block bodyBlock;
                if (Check(TokenType.LBrace))
                {
                    // It's a block body, like: n => { ... }
                    Consume(TokenType.LBrace, "Expected '{' for arrow function block body.");
                    bodyBlock = BlockStatementInternal();
                }
                else
                {
                    // It's an expression body, like: n => n * 2
                    // Use Ternary() to avoid consuming commas that belong to the outer context (e.g. call args).
                    var body = Ternary();
                    bodyBlock = new Stmt.Block(new List<Stmt> { new Stmt.Return(body) });
                }

                var parameters = new List<Expr.Param> { new Expr.Param((string)param.Literal) };
                var fn = new Expr.Function(parameters, bodyBlock, isArrow: true);
                fn.Span = SourceSpan.FromToken(arrowTok);
                return fn;
            }

            if (Match(TokenType.Fn))
            {
                var fnTok = Previous();
                // anonymous/inline function expression
                Consume(TokenType.LParen, "Expected '(' after 'fn'");
                var parameters = ParseParamList();
                Consume(TokenType.RParen, "Expected ')'");
                Consume(TokenType.LBrace, "Expected '{' before function body");
                var body = BlockStatementInternal();
                var fn = new Expr.Function(parameters, body, isArrow: false);
                fn.Span = SourceSpan.FromToken(fnTok);
                return fn;
            }

            // Ordinary identifier
            if (Match(TokenType.Identifier))
            {
                var tok = Previous();
                var v = new Expr.Variable((string)tok.Literal);
                v.Span = SourceSpan.FromToken(tok);
                return v;
            }
            throw new MiniDynParseError("Expected expression", Peek().Line, Peek().Column);
        }

        // Build plain literal or interpolated string expression from a string token.
        private Expr BuildStringExprFromToken(Token tok)
        {
            var span = SourceSpan.FromToken(tok);
            var text = (string)tok.Literal ?? "";

            // Raw triple-quoted strings: no ${...} interpolation.
            if (tok != null && tok.IsRawString)
            {
                var lit = new Expr.Literal(Value.String(text));
                lit.Span = span;
                return lit;
            }

            const string marker = "${";
            if (text.IndexOf(marker, StringComparison.Ordinal) < 0)
            {
                var lit = new Expr.Literal(Value.String(text));
                lit.Span = span;
                return lit;
            }

            // Compute absolute source position within the string content
            (int line, int col) ComputePosInStringContent(int offsetInContent)
            {
                int line = tok.StringContentStartLine > 0 ? tok.StringContentStartLine : tok.Line;
                int col = tok.StringContentStartColumn > 0 ? tok.StringContentStartColumn : (tok.Column + 1);
                for (int k = 0; k < offsetInContent; k++)
                {
                    char ch = text[k];
                    if (ch == '\n') { line++; col = 1; }
                    else { col++; }
                }
                return (line, col);
            }

            // Split into parts: text and embedded expressions delimited by ${ ... }
            var parts = new List<object>(); // string or Expr
            int i = 0;
            while (i < text.Length)
            {
                int j = text.IndexOf(marker, i, StringComparison.Ordinal);
                if (j < 0)
                {
                    var tail = text.Substring(i);
                    if (tail.Length > 0) parts.Add(tail);
                    break;
                }
                // literal chunk before ${
                if (j > i) parts.Add(text.Substring(i, j - i));

                // Find matching }
                int endExpr = FindMatchingBrace(text, j + 1); // returns index of closing '}'
                if (endExpr < 0)
                {
                    // Unterminated interpolation: report at the start of '${'
                    var pos = ComputePosInStringContent(j);
                    throw new MiniDynParseError("Unterminated interpolation '${...}' in string", pos.line, pos.col);
                }

                int exprStart = j + 2; // after ${
                int exprLen = endExpr - exprStart;
                string exprSrc = exprLen > 0 ? text.Substring(exprStart, exprLen) : "";

                // Base position (absolute) of the interpolation expression start, for error mapping
                var basePos = ComputePosInStringContent(exprStart);

                try
                {
                    var exprNode = ParseExpressionFromString(exprSrc, tok.FileName);
                    parts.Add(exprNode);
                }
                catch (MiniDynParseError ex)
                {
                    // Map the error location inside exprSrc to the original file coordinates
                    int absLine = basePos.line + (ex.Line > 0 ? (ex.Line - 1) : 0);
                    int absCol = (ex.Line <= 1)
                        ? (basePos.col + Math.Max(0, ex.Column - 1))
                        : ex.Column;
                    throw new MiniDynParseError($"In string interpolation: {ex.Message}", absLine, absCol);
                }

                i = endExpr + 1; // continue after '}'
            }

            // Fold into left-associated additions for evaluation order
            Expr result = null;
            foreach (var part in parts)
            {
                Expr partExpr;
                if (part is string s)
                {
                    partExpr = new Expr.Literal(Value.String(s)) { Span = span };
                }
                else
                {
                    partExpr = (Expr)part;
                }
                if (result == null) result = partExpr;
                else result = new Expr.Binary(result, new Token(TokenType.Plus, "+", null, 0, 0, 0, tok.FileName), partExpr) { Span = span };
            }
            if (result == null) result = new Expr.Literal(Value.String("")) { Span = span };
            return result;
        }

        private static int FindMatchingBrace(string s, int openBraceIndex /* points at '{' */)
        {
            // openBraceIndex is index of '{' in the source (we pass j+1 from "${")
            int i = openBraceIndex + 1;
            int depth = 1;
            bool inString = false;
            while (i < s.Length)
            {
                char ch = s[i];
                if (inString)
                {
                    if (ch == '\\') { i += 2; continue; }
                    if (ch == '"') { inString = false; i++; continue; }
                    i++; continue;
                }
                if (ch == '"') { inString = true; i++; continue; }
                if (ch == '{') { depth++; i++; continue; }
                if (ch == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                    i++; continue;
                }
                i++;
            }
            return -1;
        }

        private Expr ParseExpressionFromString(string expr, string fileName)
        {
            var lx = new Lexer(expr, fileName ?? "<interp>");
            var p2 = new Parser(lx);
            var e = p2.Expression();
            // We don't require explicit EOF check here; trailing whitespace is fine.
            return e;
        }

        private Token PeekAhead(int distance)
        {
            if (_current + distance >= _tokens.Count) return null;
            return _tokens[_current + distance];
        }

        private bool TryParseArrowFunction(out Expr arrowFunc)
        {
            arrowFunc = null;

            // Save position
            int start = _current;

            try
            {
                var parameters = ParseParamList();
                Consume(TokenType.RParen, "Expected ')'");

                if (!Check(TokenType.Arrow))
                {
                    _current = start;
                    return false;
                }

                var arrowTok = Consume(TokenType.Arrow, "Expected '=>'");

                Stmt.Block bodyBlock;

                if (Check(TokenType.LBrace))
                {
                    // Block body
                    Advance(); // consume '{'
                    bodyBlock = BlockStatementInternal();
                }
                else
                {
                    // Expression body
                    // Use Ternary() so a trailing ', ...' is not captured into the arrow body.
                    var bodyExpr = Ternary();
                    bodyBlock = new Stmt.Block(new List<Stmt> { new Stmt.Return(bodyExpr) });
                }

                var fn = new Expr.Function(parameters, bodyBlock, isArrow: true);
                fn.Span = SourceSpan.FromToken(arrowTok);
                arrowFunc = fn;
                return true;
            }
            catch
            {
                _current = start;
                return false;
            }
        }
    }

    // Environment
    public class Environment
    {
        private readonly Dictionary<string, Value> _values = new Dictionary<string, Value>();
        private readonly HashSet<string> _consts = new HashSet<string>();
        private readonly HashSet<string> _lets = new HashSet<string>(); // tracks block-scoped let/const by name
        // track "uninitialized" for TDZ-like checks on let without initializer
        private readonly HashSet<string> _uninitialized = new HashSet<string>();

        public virtual bool IsFunction => false; // mark function/global boundaries
        public Environment Enclosing { get; }

        public Environment(Environment enclosing = null) { Enclosing = enclosing; }

        // helper – is name declared in this exact env (no parents)
        public virtual bool HasHere(string name) => _values.ContainsKey(name);

        // helper – get only from this env (throws if TDZ)
        public virtual bool TryGetHere(string name, out Value v)
        {
            if (_values.TryGetValue(name, out v))
            {
                if (_uninitialized.Contains(name))
                    throw new MiniDynRuntimeError($"Cannot access '{name}' before initialization");
                return true;
            }
            v = default(Value);
            return false;
        }

        // nearest function/global env (fallback to self if none)
        private Environment GetNearestFunctionEnv()
        {
            var e = this;
            while (e != null && !e.IsFunction) e = e.Enclosing;
            return e ?? this;
        }

        // var belongs to nearest function/global env; error if collides with block-scoped in that env
        public void DeclareVarInFunctionOrGlobal(string name, Value value)
        {
            var target = GetNearestFunctionEnv();
            // If that function env has a block-scoped declaration with same name, disallow var redeclare
            if (target._values.ContainsKey(name) && (target._lets.Contains(name) || target._consts.Contains(name)))
                throw new MiniDynRuntimeError($"Cannot redeclare block-scoped '{name}' with 'var'");
            target._values[name] = value;
            // Note: var can be redeclared; we don't track a separate _vars set.
        }

        public virtual void DefineVar(string name, Value value) => _values[name] = value;

        public virtual void DefineLet(string name, Value value)
        {
            if (HasHere(name))
                throw new MiniDynRuntimeError($"Cannot redeclare '{name}' in the same block scope");
            _values[name] = value;
            _lets.Add(name);
            // If it was previously marked uninitialized in this scope, clear it.
            _uninitialized.Remove(name);
        }

        // let without initializer => TDZ sentinel
        public virtual void DefineLetUninitialized(string name)
        {
            if (HasHere(name))
                throw new MiniDynRuntimeError($"Cannot redeclare '{name}' in the same block scope");
            _values[name] = Value.Nil(); // placeholder; real guard is in _uninitialized
            _lets.Add(name);
            _uninitialized.Add(name);
        }

        public virtual void DefineConst(string name, Value value)
        {
            if (HasHere(name))
                throw new MiniDynRuntimeError($"Cannot redeclare '{name}' in the same block scope");
            _values[name] = value;
            _lets.Add(name);
            _consts.Add(name);
        }

        public virtual void Assign(string name, Value value)
        {
            if (_values.ContainsKey(name))
            {
                if (_consts.Contains(name))
                    throw new MiniDynRuntimeError($"Cannot assign to const '{name}'");
                _values[name] = value;
                // TDZ lift: first write initializes a 'let'
                _uninitialized.Remove(name);
                return;
            }
            if (Enclosing != null) { Enclosing.Assign(name, value); return; }
            throw new MiniDynRuntimeError($"Undefined variable '{name}'");
        }

        public virtual Value Get(string name)
        {
            if (_values.TryGetValue(name, out var v))
            {
                if (_uninitialized.Contains(name))
                    throw new MiniDynRuntimeError($"Cannot access '{name}' before initialization");
                return v;
            }
            if (Enclosing != null) return Enclosing.Get(name);
            throw new MiniDynRuntimeError($"Undefined variable '{name}'");
        }

        public virtual bool TryGet(string name, out Value v)
        {
            if (_values.TryGetValue(name, out var local))
            {
                if (_uninitialized.Contains(name))
                    throw new MiniDynRuntimeError($"Cannot access '{name}' before initialization");
                v = local;
                return true;
            }
            if (Enclosing != null) return Enclosing.TryGet(name, out v);
            v = default(Value);
            return false;
        }


        public bool IsDeclaredHere(string name) => _values.ContainsKey(name) && _lets.Contains(name);
    }

    // Environment that exposes current bytecode function's params/locals (backed by the VM slots array)
    internal sealed class LocalsBackedEnvironment : Environment
    {
        private readonly Value[] _slots;
        private readonly Dictionary<string, int> _slotOf;

        public LocalsBackedEnvironment(Environment enclosing, Value[] slots, Dictionary<string, int> slotOf)
            : base(enclosing)
        {
            _slots = slots ?? Array.Empty<Value>();
            _slotOf = slotOf ?? new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public override Value Get(string name)
        {
            if (name != null && _slotOf.TryGetValue(name, out var idx))
            {
                if (idx >= 0 && idx < _slots.Length) return _slots[idx];
            }
            return base.Get(name);
        }

        public override bool TryGet(string name, out Value v)
        {
            if (name != null && _slotOf.TryGetValue(name, out var idx))
            {
                if (idx >= 0 && idx < _slots.Length)
                {
                    v = _slots[idx]; return true;
                }
            }
            return base.TryGet(name, out v);
        }

        public override void Assign(string name, Value value)
        {
            if (name != null && _slotOf.TryGetValue(name, out var idx))
            {
                if (idx >= 0 && idx < _slots.Length)
                {
                    _slots[idx] = value; return;
                }
            }
            base.Assign(name, value);
        }
    }

    // Marks function/global scopes (nearest target for 'var')
    public class FunctionEnvironment : Environment
    {
        public override bool IsFunction => true;
        public FunctionEnvironment(Environment enclosing = null) : base(enclosing) { }
    }

    public interface ICallable
    {
        int ArityMin { get; }
        int ArityMax { get; } // int.MaxValue for variadic
        Value Call(Interpreter interp, List<Value> args);
    }

    public class BuiltinFunction : ICallable
    {
        public string Name { get; }
        private readonly Func<Interpreter, List<Value>, Value> _fn;
        public int ArityMin { get; }
        public int ArityMax { get; }

        public BuiltinFunction(string name, int arityMin, int arityMax, Func<Interpreter, List<Value>, Value> fn)
        {
            Name = name; ArityMin = arityMin; ArityMax = arityMax; _fn = fn;
        }

        public Value Call(Interpreter interp, List<Value> args) => _fn(interp, args);
        public override string ToString() => $"<builtin {Name}>";
    }

    public class UserFunction : ICallable
    {
        public struct ParamSpec
        {
            public string Name;
            public Expr Default;
            public bool IsRest;
            public ParamSpec(string n, Expr d, bool r) { Name = n; Default = d; IsRest = r; }
        }

        // kind & captured 'this'
        public enum Kind { Normal, Arrow }
        public Kind FunctionKind { get; }
        private readonly Value? _capturedThis; // for arrows (nullable since Value is a struct)

        public List<ParamSpec> Params { get; }
        public Stmt.Block Body { get; }
        public Environment Closure { get; }
        public string Name { get; }
        public SourceSpan DefSpan { get; } // where function was defined

        private Value? _boundThis;

        // Stable identity shared across bound clones for tail-call detection
        private static int _nextId;
        public int FunctionId { get; }

        public int ArityMin
        {
            get
            {
                int count = 0;
                foreach (var p in Params)
                {
                    if (p.IsRest) continue;
                    if (p.Default == null) count++;
                }
                return count;
            }
        }
        public int ArityMax
        {
            get
            {
                foreach (var p in Params) if (p.IsRest) return int.MaxValue;
                return Params.Count;
            }
        }

        // kind + capturedThis parameters (default to Normal/null for old call sites)
        // functionId (optional) to preserve identity across clones/binds
        public UserFunction(string name, List<Expr.Param> parameters, Stmt.Block body, Environment closure,
                            Kind kind = Kind.Normal, Value? capturedThis = null, SourceSpan defSpan = default(SourceSpan),
                            int functionId = 0)
        {
            Name = name;
            Body = body;
            Closure = closure;
            Params = new List<ParamSpec>(parameters.Count);
            foreach (var p in parameters)
                Params.Add(new ParamSpec(p.Name, p.Default, p.IsRest));
            FunctionKind = kind;
            _capturedThis = capturedThis;
            DefSpan = defSpan;
            FunctionId = functionId != 0 ? functionId : ++_nextId;
        }

        public UserFunction BindThis(Value thisValue)
        {
            // arrows ignore rebinding
            if (FunctionKind == Kind.Arrow) return this;

            var bound = new UserFunction(Name, ToExprParams(), Body, Closure, FunctionKind, _capturedThis, DefSpan, FunctionId);
            bound._boundThis = thisValue;
            return bound;
        }

        public Value Call(Interpreter interp, List<Value> args)
        {
            // trampoline loop + function-current stack for tail-call detection
            interp.PushFunction(this);
            try
            {
                var currentArgs = args;
                Interpreter.ArgMapping? currentMap = null; // support mapping from tail-calls
                while (true)
                {
                    var env = new Environment(Closure);

                    // define 'this' according to kind (arrow uses captured lexical; normal uses bound call-site)
                    if (FunctionKind == Kind.Arrow)
                    {
                        if (_capturedThis.HasValue) env.DefineConst("this", _capturedThis.Value);
                    }
                    else if (_boundThis.HasValue)
                    {
                        env.DefineConst("this", _boundThis.Value);
                    }

                    // Bind parameters:
                    if (currentMap.HasValue)
                    {
                        // Mapping-based binding (named args): evaluate defaults in callee env
                        var m = currentMap.Value;
                        int restIndex = -1;
                        for (int pi = 0; pi < Params.Count; pi++)
                        {
                            var p = Params[pi];
                            if (p.IsRest) { restIndex = pi; continue; }
                            if (m.Filled.Length <= pi) throw new MiniDynRuntimeError("Invalid argument mapping");
                            if (m.Filled[pi]) env.DefineVar(p.Name, m.Values[pi]);
                            else if (p.Default != null) env.DefineVar(p.Name, interp.EvaluateWithEnv(p.Default, env));
                            else throw new MiniDynRuntimeError($"Missing required argument '{p.Name}' for function {ToString()}");
                        }
                        if (restIndex >= 0)
                        {
                            var rest = new ArrayValue();
                            foreach (var v in m.Rest) rest.Items.Add(v);
                            env.DefineVar(Params[restIndex].Name, Value.Array(rest));
                        }
                        else if (m.Rest != null && m.Rest.Count > 0)
                        {
                            throw new MiniDynRuntimeError($"Function {ToString()} expected at most {Params.Count} args, got more");
                        }
                    }
                    else
                    {
                        // Positional-only binding (existing logic)
                        int i = 0;
                        int argsCount = currentArgs.Count;
                        bool hasRest = false;
                        for (int pi = 0; pi < Params.Count; pi++)
                        {
                            var p = Params[pi];
                            if (p.IsRest)
                            {
                                hasRest = true;
                                var rest = new ArrayValue();
                                for (int ai = i; ai < argsCount; ai++)
                                {
                                    rest.Items.Add(currentArgs[ai]);
                                }
                                env.DefineVar(p.Name, Value.Array(rest));
                                i = argsCount;
                                continue;
                            }
                            if (i < argsCount)
                            {
                                env.DefineVar(p.Name, currentArgs[i++]);
                            }
                            else
                            {
                                if (p.Default != null)
                                {
                                    env.DefineVar(p.Name, interp.EvaluateWithEnv(p.Default, env));
                                }
                                else
                                {
                                    throw new MiniDynRuntimeError($"Missing required argument '{p.Name}' for function {ToString()}");
                                }
                            }
                        }
                        if (!hasRest && i < argsCount)
                            throw new MiniDynRuntimeError($"Function {ToString()} expected at most {ArityMax} args, got {argsCount}");
                    }

                    try
                    {
                        interp.ExecuteBlock(Body.Statements, env);
                    }
                    catch (Interpreter.TailCallSignal tcs)
                    {
                        // Only handle tail-calls to the same function identity (including bound clones)
                        var uf = tcs.Function;
                        if (uf != null && uf.FunctionId == this.FunctionId)
                        {
                            if (tcs.Mapping.HasValue)
                            {
                                currentMap = tcs.Mapping.Value;
                                currentArgs = null;
                            }
                            else
                            {
                                currentArgs = tcs.Args ?? new List<Value>();
                                currentMap = null;
                            }
                            // loop again with new args; do not grow C# stack
                            continue;
                        }
                        throw;
                    }
                    catch (Interpreter.ReturnSignal ret)
                    {
                        return ret.Value;
                    }

                    return Value.Nil();
                }
            }
            finally
            {
                interp.PopFunction();
            }
        }

        // Call variant that takes a mapping (used for named-arg calls)
        public Value CallWithMapping(Interpreter interp, Interpreter.ArgMapping mapping)
        {
            interp.PushFunction(this);
            try
            {
                var currentMap = (Interpreter.ArgMapping?)mapping;
                List<Value> currentArgs = null;

                while (true)
                {
                    var env = new Environment(Closure);

                    if (FunctionKind == Kind.Arrow)
                    {
                        if (_capturedThis.HasValue) env.DefineConst("this", _capturedThis.Value);
                    }
                    else if (_boundThis.HasValue)
                    {
                        env.DefineConst("this", _boundThis.Value);
                    }

                    // Bind from mapping
                    var m = currentMap.Value;
                    int restIndex = -1;
                    for (int pi = 0; pi < Params.Count; pi++)
                    {
                        var p = Params[pi];
                        if (p.IsRest) { restIndex = pi; continue; }
                        if (m.Filled.Length <= pi) throw new MiniDynRuntimeError("Invalid argument mapping");
                        if (m.Filled[pi]) env.DefineVar(p.Name, m.Values[pi]);
                        else if (p.Default != null) env.DefineVar(p.Name, interp.EvaluateWithEnv(p.Default, env));
                        else throw new MiniDynRuntimeError($"Missing required argument '{p.Name}' for function {ToString()}");
                    }
                    if (restIndex >= 0)
                    {
                        var rest = new ArrayValue();
                        foreach (var v in m.Rest) rest.Items.Add(v);
                        env.DefineVar(Params[restIndex].Name, Value.Array(rest));
                    }
                    else if (m.Rest != null && m.Rest.Count > 0)
                    {
                        throw new MiniDynRuntimeError($"Function {ToString()} expected at most {Params.Count} args, got more");
                    }

                    try
                    {
                        interp.ExecuteBlock(Body.Statements, env);
                    }
                    catch (Interpreter.TailCallSignal tcs)
                    {
                        var uf = tcs.Function;
                        if (uf != null && uf.FunctionId == this.FunctionId)
                        {
                            if (tcs.Mapping.HasValue)
                            {
                                currentMap = tcs.Mapping.Value;
                                currentArgs = null;
                            }
                            else
                            {
                                currentArgs = tcs.Args ?? new List<Value>();
                                currentMap = null;
                            }
                            // If we got positional-only args, switch to the positional Call path
                            if (currentMap == null)
                                return this.Call(interp, currentArgs);
                            continue;
                        }
                        throw;
                    }
                    catch (Interpreter.ReturnSignal ret)
                    {
                        return ret.Value;
                    }

                    return Value.Nil();
                }
            }
            finally
            {
                interp.PopFunction();
            }
        }

        public override string ToString()
        {
            return Name != null ? $"<fn {Name}>" : "<fn>";
        }

        public UserFunction Bind(Environment newClosure) =>
            new UserFunction(Name, ToExprParams(), Body, newClosure, FunctionKind, _capturedThis, DefSpan, FunctionId);

        private List<Expr.Param> ToExprParams()
        {
            var list = new List<Expr.Param>(Params.Count);
            foreach (var p in Params) list.Add(new Expr.Param(p.Name, p.Default, p.IsRest));
            return list;
        }
    }
    
    // Host-provided module loader abstraction + default FS loader
    public interface IModuleLoader
    {
        // Returns an absolute path for the specifier (or null if cannot resolve).
        string Resolve(string specifier, string baseDirectory);
        // Loads source for an absolute path. Returns true if found.
        bool TryLoad(string absolutePath, out string source);
    }

    public sealed class FileSystemModuleLoader : IModuleLoader
    {
        private static readonly string[] DefaultExtensions = new[] { "", ".mdl", ".minidyn" };

        public string Resolve(string specifier, string baseDirectory)
        {
            try
            {
                string candidate;
                if (Path.IsPathRooted(specifier))
                {
                    candidate = specifier;
                }
                else
                {
                    // Relative or bare specifier: resolve from baseDirectory
                    if (string.IsNullOrEmpty(baseDirectory))
                        baseDirectory = Directory.GetCurrentDirectory();
                    candidate = Path.Combine(baseDirectory, specifier);
                }

                // If candidate points to a directory, try index files
                if (Directory.Exists(candidate))
                {
                    foreach (var ext in DefaultExtensions)
                    {
                        var idx = Path.Combine(candidate, "index" + ext);
                        if (File.Exists(idx)) return Path.GetFullPath(idx);
                    }
                }

                // Try with default extensions if no extension given
                if (string.IsNullOrEmpty(Path.GetExtension(candidate)))
                {
                    foreach (var ext in DefaultExtensions)
                    {
                        var p = candidate + ext;
                        if (File.Exists(p)) return Path.GetFullPath(p);
                    }
                }

                // Otherwise, accept as-is if exists
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);

                return null;
            }
            catch
            {
                return null;
            }
        }

        public bool TryLoad(string absolutePath, out string source)
        {
            try
            {
                if (File.Exists(absolutePath))
                {
                    source = File.ReadAllText(absolutePath);
                    return true;
                }
            }
            catch
            {
                // fall through
            }
            source = null;
            return false;
        }
    }

    // Simple bytecode for expression-bodied functions
    internal enum OpCode
    {
        // stack: push/pop Value
        LoadConst,       // A = const index
        LoadName,        // O = string name
        StoreName,       // O = string name (pop value, assign)
        Pop,
        LoadParam,       // A = param index

        Neg, Not,
        Add, Sub, Mul, Div, Mod,

        CmpEq, CmpNe, CmpLt, CmpLe, CmpGt, CmpGe,

        // logical / nullish via control flow
        Jump,            // A = target ip
        JumpIfFalse,     // A = target ip (pop cond)
        JumpIfTruthy,    // A = target ip (pop cond)
        JumpIfNotNil,    // A = target ip (pop value, if not nil jump, else push back? see usage)
        Dup,             // duplicate top-of-stack
        Dup2,            // duplicate top two stack items ( ... a b -> ... a b a b )

        // property/method and calls
        GetProp,         // O = string name; pop target, push value (nil if missing)
        StoreProp,       // O = string name; pop value, pop target, set, push value
        Call,            // A = argCount; stack: ..., callee, arg1..argN -> pushes result
        CallMethod,      // A = argCount; stack: ..., receiver, callee, arg1..argN -> pushes result (binds receiver for normal UserFunction)

        Return,          // return top-of-stack
        Noop,

        // locals + index access
        LoadLocal,       // A = local slot index (params+locals array)
        StoreLocal,      // A = local slot index (pop value, store, push back)
        GetIndex,        // stack: ..., target, index -> pops both, pushes value (nil if missing)
        StoreIndex,       // stack: ..., target, index, value -> store, push value

        // literals
        NewArray,        // push Value.Array(new ArrayValue())
        ArrayAppend,     // stack: ..., array, value -> append; push array back
        NewObject,       // push Value.Object(new ObjectValue())

        // functions
        MakeFunction,    // O = FunctionProto; push Value.Function

        // === intrinsics for compiled semantics ===
        TypeOf,          // pop v -> push string type name (lowercase)
        Length,          // pop v -> push number length (semantics like builtin 'length')
        Keys,            // pop object -> push array of string keys
        Values,          // pop object -> push array of values
        ToStringOp,      // pop v -> push string conversion (Interpreter.ToStringValue)
        MakeError,       // pop message(string) -> push Error object via Interpreter.MakeError("Error", msg)

        // === exceptions / try-catch-finally ===
        TryEnter,        // A = catchIp (or -1), O = (int)finallyIp (or -1)
        TryLeave,        // pop current try frame; if a completion is pending, resume unwinding
        Throw,           // pop value and start unwinding through catch/finally
        EndFinally,      // marker inside finally; logic handled by TryLeave

        // === loop control with finally-unwinding ===
        BreakPending,    // A = target ip to jump to after unwinding finally blocks
        ContinuePending  // A = target ip to jump to after unwinding finally blocks
    }

    internal sealed class Instruction
    {
        public OpCode Op;
        public int A;
        public object O; // used for names (string) or future operands

        public Instruction(OpCode op, int a = 0, object o = null)
        {
            Op = op; A = a; O = o;
        }
    }

    internal sealed class Chunk
    {
        public readonly List<Instruction> Code = new List<Instruction>(64);
        public readonly List<Value> Constants = new List<Value>(32);

        // Metadata for closures: parameter and local names
        public List<string> ParamNames = new List<string>();
        public List<string> LocalNames = new List<string>();

        // Exception table metadata (not required for VM execution, but useful for tooling)
        public sealed class ExceptionRegion
        {
            public int TryStartIp;
            public int TryEndIp;
            public int CatchIp;   // -1 if none
            public int FinallyIp; // -1 if none
        }
        public readonly List<ExceptionRegion> ExceptionTable = new List<ExceptionRegion>();

        public int AddConst(Value v)
        {
            int idx = Constants.Count;
            Constants.Add(v);
            return idx;
        }

        public int Emit(OpCode op, int a = 0, object o = null)
        {
            Code.Add(new Instruction(op, a, o));
            return Code.Count - 1;
        }

        public void PatchJump(int at, int targetIp)
        {
            Code[at].A = targetIp;
        }

        // Helper to patch TryEnter operands post-emit
        public void PatchTryEnter(int at, int catchIp, int finallyIp)
        {
            Code[at].A = catchIp;
            Code[at].O = finallyIp;
        }

        // trivial peephole: remove Jump to next, collapse Noop
        public void Peephole()
        {
            for (int i = 0; i < Code.Count; i++)
            {
                var ins = Code[i];
                if (ins.Op == OpCode.Jump && ins.A == i + 1)
                    Code[i].Op = OpCode.Noop;
            }
            // No need to physically remove Noop; the VM will skip fast.
        }
    }

    // Descriptor for function literals/declarations emitted by the compiler.
    internal sealed class FunctionProto
    {
        public string Name;                 // null for anonymous expressions
        public List<Expr.Param> Params;     // parameter metadata (rest/defaults allowed for fallback)
        public Stmt.Block Body;             // original body for fallback UserFunction
        public bool IsArrow;
        public SourceSpan DefSpan;

        // Compiled form (if available); null => fallback to UserFunction at runtime
        public Chunk CompiledChunk;
        public int LocalCount;
    }

    // === exception/try state ===
    enum RegionState { InTry, InCatch, InFinally }
    struct TryFrame
    {
        public int CatchIp;
        public int FinallyIp;
        public RegionState State;
    }
    enum PendingKind { None, Throw, Return, ThrowRuntime, Break, Continue }
    internal sealed class BytecodeVM
    {
        private readonly Interpreter _interp;
        public BytecodeVM(Interpreter interp) { _interp = interp; }

        public Value Run(Chunk chunk, Environment env, Value[] locals = null)
        {
            var stack = new List<Value>(16);
            int ip = 0;

            var tryStack = new Stack<TryFrame>();
            PendingKind pending = PendingKind.None;
            Value pendingValue = Value.Nil();
            MiniDynRuntimeError pendingRuntime = null;
            int pendingTargetIp = -1;

            Value Pop()
            {
                if (stack.Count == 0) throw new MiniDynRuntimeError("VM stack underflow");
                var v = stack[stack.Count - 1];
                stack.RemoveAt(stack.Count - 1);
                return v;
            }
            void Push(Value v) => stack.Add(v);

            // Core unwinding routine: returns true if a handler/finally was found and ip updated.
            bool Unwind(bool acceptCatch)
            {
                while (tryStack.Count > 0)
                {
                    var fr = tryStack.Pop(); // examine this frame
                    // Re-push it if we're going to run its handler/finally
                    if (acceptCatch && fr.State == RegionState.InTry && fr.CatchIp >= 0)
                    {
                        fr.State = RegionState.InCatch;
                        tryStack.Push(fr);
                        // Deliver thrown value to catch landing via stack
                        if (pending == PendingKind.ThrowRuntime)
                        {
                            var errVal = _interp.MakeError("RuntimeError", pendingRuntime.Message, pendingRuntime);
                            Push(errVal);
                            pendingRuntime = null;
                            pending = PendingKind.None;
                        }
                        else
                        {
                            Push(pendingValue);
                            // Pending Throw is considered handled by catch
                            pending = PendingKind.None;
                        }
                        ip = fr.CatchIp;
                        return true;
                    }

                    if (fr.FinallyIp >= 0)
                    {
                        fr.State = RegionState.InFinally;
                        tryStack.Push(fr);
                        // For both throw and return we run finally; pending stays set
                        ip = fr.FinallyIp;
                        return true;
                    }

                    // No handlers in this frame; continue to next outer frame
                    // (frame already popped)
                }

                // Nothing left
                return false;
            }

            // On each iteration, if a pending completion should finish, do it now.
            void MaybeComplete()
            {
                if (pending == PendingKind.Return && tryStack.Count == 0)
                {
                    // No more finally frames to run -> return the value
                    throw new Interpreter.ReturnSignal(pendingValue);
                }
                if (pending == PendingKind.Throw && tryStack.Count == 0)
                {
                    // No handler -> bubble to interpreter
                    throw new Interpreter.ThrowSignal(pendingValue);
                }
                if (pending == PendingKind.ThrowRuntime && tryStack.Count == 0)
                {
                    // No catch anywhere -> rethrow original runtime error
                    throw pendingRuntime;
                }
                if ((pending == PendingKind.Break || pending == PendingKind.Continue) && tryStack.Count == 0)
                {
                    // Transfer control to the target ip after all finally frames have run.
                    if (pendingTargetIp < 0) throw new MiniDynRuntimeError("Invalid pending break/continue target");
                    ip = pendingTargetIp;
                    pending = PendingKind.None;
                    pendingTargetIp = -1;
                }
            }

            try
            {
                while (ip < chunk.Code.Count)
                {
                    // Pending completions finalize once we've unwound out of all frames
                    MaybeComplete();

                    var ins = chunk.Code[ip++];
                    try
                    {
                        switch (ins.Op)
                        {
                            case OpCode.Noop: break;

                            case OpCode.LoadConst:
                                Push(chunk.Constants[ins.A]); break;

                            case OpCode.LoadName:
                                Push(env.Get((string)ins.O)); break;

                            case OpCode.StoreName:
                                {
                                    var v = Pop();
                                    env.Assign((string)ins.O, v);
                                    Push(v);
                                    break;
                                }

                            case OpCode.Pop:
                                Pop(); break;

                            case OpCode.LoadParam:
                                {
                                    if (locals == null || ins.A < 0 || ins.A >= locals.Length)
                                        throw new MiniDynRuntimeError("Invalid parameter access");
                                    Push(locals[ins.A]);
                                    break;
                                }
                            // locals
                            case OpCode.LoadLocal:
                                {
                                    if (locals == null || ins.A < 0 || ins.A >= locals.Length)
                                        throw new MiniDynRuntimeError("Invalid local access");
                                    Push(locals[ins.A]);
                                    break;
                                }
                            case OpCode.StoreLocal:
                                {
                                    if (locals == null || ins.A < 0 || ins.A >= locals.Length)
                                        throw new MiniDynRuntimeError("Invalid local access");
                                    var v = Pop();
                                    locals[ins.A] = v;
                                    Push(v);
                                    break;
                                }
                            case OpCode.Neg:
                                {
                                    var r = Pop();
                                    Push(Value.Number(NumberValue.Neg(RuntimeOps.ToNumber(r))));
                                    break;
                                }
                            case OpCode.Not:
                                {
                                    var r = Pop();
                                    Push(Value.Boolean(!Value.IsTruthy(r)));
                                    break;
                                }

                            case OpCode.Add:
                                {
                                    var b = Pop(); var a = Pop();
                                    if (a.Type == ValueType.Number && b.Type == ValueType.Number)
                                        Push(Value.Number(NumberValue.Add(a.AsNumber(), b.AsNumber())));
                                    else if (a.Type == ValueType.String || b.Type == ValueType.String)
                                        Push(Value.String(_interp.ToStringValue(a) + _interp.ToStringValue(b)));
                                    else if (a.Type == ValueType.Array && b.Type == ValueType.Array)
                                    {
                                        var la = a.AsArray(); var rb = b.AsArray();
                                        var res = new ArrayValue();
                                        res.Items.AddRange(la.Items);
                                        res.Items.AddRange(rb.Items);
                                        Push(Value.Array(res));
                                    }
                                    else throw new MiniDynRuntimeError("Invalid '+' operands");
                                    break;
                                }
                            case OpCode.Sub:
                                {
                                    var b = Pop(); var a = Pop();
                                    Push(Value.Number(NumberValue.Sub(ToNum(a), ToNum(b))));
                                    break;
                                }
                            case OpCode.Mul:
                                {
                                    var b = Pop(); var a = Pop();
                                    Push(Value.Number(NumberValue.Mul(ToNum(a), ToNum(b))));
                                    break;
                                }
                            case OpCode.Div:
                                {
                                    var b = Pop(); var a = Pop();
                                    Push(Value.Number(NumberValue.Div(ToNum(a), ToNum(b))));
                                    break;
                                }
                            case OpCode.Mod:
                                {
                                    var b = Pop(); var a = Pop();
                                    Push(Value.Number(NumberValue.Mod(ToNum(a), ToNum(b))));
                                    break;
                                }

                            case OpCode.CmpEq:
                                {
                                    var b = Pop(); var a = Pop();
                                    Push(Value.Boolean(RuntimeOps.CompareEq(a, b)));
                                    break;
                                }
                            case OpCode.CmpNe:
                                {
                                    var b = Pop(); var a = Pop();
                                    Push(Value.Boolean(!RuntimeOps.CompareEq(a, b)));
                                    break;
                                }
                            case OpCode.CmpLt: { var b = Pop(); var a = Pop(); Push(Value.Boolean(RuntimeOps.CompareRel(a, b, "<"))); break; }
                            case OpCode.CmpLe: { var b = Pop(); var a = Pop(); Push(Value.Boolean(RuntimeOps.CompareRel(a, b, "<="))); break; }
                            case OpCode.CmpGt: { var b = Pop(); var a = Pop(); Push(Value.Boolean(RuntimeOps.CompareRel(a, b, ">"))); break; }
                            case OpCode.CmpGe: { var b = Pop(); var a = Pop(); Push(Value.Boolean(RuntimeOps.CompareRel(a, b, ">="))); break; }

                            case OpCode.Dup:
                                {
                                    var v = stack[stack.Count - 1];
                                    Push(v); break;
                                }
                            case OpCode.Dup2:
                                {
                                    if (stack.Count < 2) throw new MiniDynRuntimeError("VM stack underflow");
                                    var b = stack[stack.Count - 1];
                                    var a = stack[stack.Count - 2];
                                    Push(a);
                                    Push(b);
                                    break;
                                }
                            case OpCode.Jump:
                                ip = ins.A; break;

                            case OpCode.JumpIfFalse:
                                {
                                    var cnd = Pop();
                                    if (!Value.IsTruthy(cnd)) ip = ins.A;
                                    break;
                                }

                            case OpCode.JumpIfTruthy:
                                {
                                    var cnd = Pop();
                                    if (Value.IsTruthy(cnd)) ip = ins.A;
                                    break;
                                }

                            case OpCode.JumpIfNotNil:
                                {
                                    var v = Pop();
                                    if (v.Type != ValueType.Nil) ip = ins.A;
                                    else Push(v);
                                    break;
                                }

                            case OpCode.GetProp:
                                {
                                    var target = Pop();
                                    if (target.Type != ValueType.Object)
                                        throw new MiniDynRuntimeError("Property access target must be object");
                                    var obj = target.AsObject();
                                    if (obj.TryGet((string)ins.O, out var vv)) Push(vv);
                                    else Push(Value.Nil());
                                    break;
                                }
                            case OpCode.StoreProp:
                                {
                                    var value = Pop();
                                    var target = Pop();
                                    if (target.Type != ValueType.Object)
                                        throw new MiniDynRuntimeError("Property assignment target must be object");
                                    var obj = target.AsObject();
                                    obj.Set((string)ins.O, value);
                                    Push(value);
                                    break;
                                }
                    // index access
                            case OpCode.GetIndex:
                                {
                                    var index = Pop();
                                    var target = Pop();
                                    if (target.Type == ValueType.Array)
                                    {
                                        var arr = target.AsArray();
                                        int idx = (int)RuntimeOps.ToNumber(index).ToDoubleNV().Dbl;
                                        idx = RuntimeOps.NormalizeIndex(idx, arr.Length);
                                        if (idx < 0 || idx >= arr.Length)
                                            throw new MiniDynRuntimeError("Array index out of range");
                                        Push(arr[idx]);
                                    }
                                    else if (target.Type == ValueType.String)
                                    {
                                        var s = target.AsString();
                                        int idx = (int)RuntimeOps.ToNumber(index).ToDoubleNV().Dbl;
                                        idx = RuntimeOps.NormalizeIndex(idx, s.Length);
                                        if (idx < 0 || idx >= s.Length)
                                            throw new MiniDynRuntimeError("String index out of range");
                                        Push(Value.String(s[idx].ToString()));
                                    }
                                    else if (target.Type == ValueType.Object)
                                    {
                                        var key = _interp.ToStringValue(index);
                                        var obj = target.AsObject();
                                        if (obj.TryGet(key, out var vv)) Push(vv);
                                        else Push(Value.Nil());
                                    }
                                    else
                                    {
                                        throw new MiniDynRuntimeError("Indexing supported only on arrays, strings, or objects");
                                    }
                                    break;
                                }
                            case OpCode.StoreIndex:
                                {
                                    var value = Pop();
                                    var index = Pop();
                                    var target = Pop();
                                    if (target.Type == ValueType.Array)
                                    {
                                        var arr = target.AsArray();
                                        int idx = (int)RuntimeOps.ToNumber(index).ToDoubleNV().Dbl;
                                        idx = RuntimeOps.NormalizeIndex(idx, arr.Length);
                                        if (idx < 0 || idx >= arr.Length)
                                            throw new MiniDynRuntimeError("Array index out of range");
                                        arr[idx] = value;
                                        Push(value);
                                        break;
                                    }
                                    else if (target.Type == ValueType.Object)
                                    {
                                        var key = _interp.ToStringValue(index);
                                        var obj = target.AsObject();
                                        obj.Set(key, value);
                                        Push(value);
                                        break;
                                    }
                                    else if (target.Type == ValueType.String)
                                    {
                                        throw new MiniDynRuntimeError("Cannot assign into string by index");
                                    }
                                    throw new MiniDynRuntimeError("Index assignment target must be array or object");
                                }

                            case OpCode.Call:
                                {
                                    int argc = ins.A;
                                    var args = new List<Value>(argc);
                                    for (int k = 0; k < argc; k++) args.Add(Pop());
                                    args.Reverse();
                                    var callee = Pop();
                                    if (callee.Type != ValueType.Function)
                                        throw new MiniDynRuntimeError("Can only call functions");
                                    var fn = callee.AsFunction();
                                    var res = fn.Call(_interp, args);
                                    Push(res);
                                    break;
                                }

                            case OpCode.CallMethod:
                                {
                                    int argc = ins.A;
                                    var args = new List<Value>(argc);
                                    for (int k = 0; k < argc; k++) args.Add(Pop());
                                    args.Reverse();
                                    var callee = Pop();
                                    var receiver = Pop();
                                    if (callee.Type != ValueType.Function)
                                        throw new MiniDynRuntimeError("Can only call functions");
                                    var fn = callee.AsFunction();
                                    // Bind receiver for normal user functions
                                    if (fn is UserFunction uf && uf.FunctionKind == UserFunction.Kind.Normal)
                                        fn = uf.BindThis(receiver);
                                    else if (fn is BytecodeFunction bf && bf.FunctionKind == UserFunction.Kind.Normal)
                                        fn = bf.BindThis(receiver);
                                    var res = fn.Call(_interp, args);
                                    Push(res);
                                    break;
                                }

                            // literals
                            case OpCode.NewArray:
                                Push(Value.Array(new ArrayValue()));
                                break;

                            case OpCode.ArrayAppend:
                                {
                                    var val = Pop();
                                    var arrVal = Pop();
                                    if (arrVal.Type != ValueType.Array)
                                        throw new MiniDynRuntimeError("Array append target must be array");
                                    arrVal.AsArray().Items.Add(val);
                                    // keep building: push array back
                                    Push(arrVal);
                                    break;
                                }

                            case OpCode.NewObject:
                                Push(Value.Object(new ObjectValue()));
                                break;

                            case OpCode.MakeFunction:
                                {
                                    var proto = (FunctionProto)ins.O;
                                    var kind = proto.IsArrow ? UserFunction.Kind.Arrow : UserFunction.Kind.Normal;
                                    Value? capturedThis = null;
                            // Build a closure environment that exposes current params+locals to nested functions.
                            // Map: param names [0..P-1], local names [P..P+L-1].
                                    var nameToSlot = new Dictionary<string, int>(StringComparer.Ordinal);
                                    int paramCount = chunk.ParamNames?.Count ?? 0;
                                    for (int i = 0; i < paramCount; i++)
                                    {
                                        var n = chunk.ParamNames[i];
                                        if (!string.IsNullOrEmpty(n)) nameToSlot[n] = i;
                                    }
                                    if (chunk.LocalNames != null)
                                    {
                                        for (int i = 0; i < chunk.LocalNames.Count; i++)
                                        {
                                            var n = chunk.LocalNames[i];
                                            if (!string.IsNullOrEmpty(n)) nameToSlot[n] = paramCount + i;
                                        }
                                    }
                                    var closureEnv = new LocalsBackedEnvironment(env, locals ?? Array.Empty<Value>(), nameToSlot);
                                    if (proto.IsArrow)
                                    {
                                        Value t;
                                        if (closureEnv.TryGet("this", out t)) capturedThis = t;
                                    }

                            ICallable fn;
                            if (proto.CompiledChunk != null)
                            {
                                // Instantiate bytecode function with current env as closure
                                fn = new BytecodeFunction(
                                    proto.Name,
                                    proto.Params,
                                    closureEnv,
                                    proto.CompiledChunk,
                                    _interp,
                                    kind,
                                    capturedThis,
                                    null,
                                    proto.LocalCount
                                );
                            }
                            else
                            {
                                // Fallback to AST-based user function
                                fn = new UserFunction(
                                            proto.Name,
                                            proto.Params,
                                            proto.Body,
                                            closureEnv,
                                            kind,
                                            capturedThis,
                                            proto.DefSpan
                                        );
                                    }
                                    Push(Value.Function(fn));
                                    break;
                                }
                            // === intrinsics for compiled semantics ===
                            case OpCode.TypeOf:
                                {
                                    var v = Pop();
                                    // Mirror builtin 'type' behavior (lower-invariant names)
                                    Push(Value.String(v.Type.ToString().ToLowerInvariant()));
                                    break;
                                }
                            case OpCode.Length:
                                {
                                    var v = Pop();
                                    switch (v.Type)
                                    {
                                        case ValueType.String:
                                            Push(Value.Number(NumberValue.FromLong(v.AsString().Length)));
                                            break;
                                        case ValueType.Array:
                                            Push(Value.Number(NumberValue.FromLong(v.AsArray().Length)));
                                            break;
                                        case ValueType.Object:
                                            Push(Value.Number(NumberValue.FromLong(v.AsObject().Count)));
                                            break;
                                        case ValueType.Nil:
                                            Push(Value.Number(NumberValue.FromLong(0)));
                                            break;
                                        case ValueType.Boolean:
                                        case ValueType.Number:
                                        case ValueType.Function:
                                            Push(Value.Number(NumberValue.FromLong(1)));
                                            break;
                                        default:
                                            Push(Value.Number(NumberValue.FromLong(0)));
                                            break;
                                    }
                                    break;
                                }
                            case OpCode.Keys:
                                {
                                    var v = Pop();
                                    if (v.Type != ValueType.Object)
                                        throw new MiniDynRuntimeError("keys expects object");
                                    var o = v.AsObject();
                                    var arr = new ArrayValue();
                                    foreach (var k in o.Keys) arr.Items.Add(Value.String(k));
                                    Push(Value.Array(arr));
                                    break;
                                }
                            case OpCode.Values:
                                {
                                    var v = Pop();
                                    if (v.Type != ValueType.Object)
                                        throw new MiniDynRuntimeError("values expects object");
                                    var o = v.AsObject();
                                    var arr = new ArrayValue();
                                    foreach (var kv in o.Entries) arr.Items.Add(kv.Value);
                                    Push(Value.Array(arr));
                                    break;
                                }
                            case OpCode.ToStringOp:
                                {
                                    var v = Pop();
                                    Push(Value.String(_interp.ToStringValue(v)));
                                    break;
                                }
                            case OpCode.MakeError:
                                {
                                    var msgVal = Pop();
                                    var msg = _interp.ToStringValue(msgVal);
                                    var err = _interp.MakeError("Error", msg);
                                    Push(err);
                                    break;
                                }
                            // === exceptions / try-catch-finally ===
                            case OpCode.TryEnter:
                                {
                                    int catchIp = ins.A;
                                    int finallyIp = (ins.O is int fi) ? fi : -1;
                                    tryStack.Push(new TryFrame { CatchIp = catchIp, FinallyIp = finallyIp, State = RegionState.InTry });
                                    break;
                                }
                            case OpCode.EndFinally:
                                {
                                    // Marker; actual action happens in TryLeave
                                    break;
                                }
                            case OpCode.TryLeave:
                                {
                                    if (tryStack.Count == 0) throw new MiniDynRuntimeError("TryLeave with empty try stack");
                                    tryStack.Pop();
                                    if (pending != PendingKind.None)
                                    {
                                        bool acceptCatch =
                                            (pending == PendingKind.Throw) || (pending == PendingKind.ThrowRuntime);
                                        if (!Unwind(acceptCatch))
                                        {
                                            // Complete now outside any frame
                                            MaybeComplete();
                                        }
                                    }
                                    break;
                                }
                            case OpCode.Throw:
                                {
                                    var exVal = Pop();
                                    pending = PendingKind.Throw;
                                    pendingValue = exVal;
                                    if (!Unwind(acceptCatch: true))
                                    {
                                        // No handler in this compiled frame
                                        throw new Interpreter.ThrowSignal(exVal);
                                    }
                                    break;
                                }

                            case OpCode.Return:
                                {
                                    var rv = Pop();
                                    if (tryStack.Count == 0)
                                        return rv;

                                    // Ensure finally runs before returning
                                    pending = PendingKind.Return;
                                    pendingValue = rv;
                                    if (!Unwind(acceptCatch: false))
                                    {
                                        // No finally frames? then return now
                                        return rv;
                                    }
                                    break;
                                }

                            case OpCode.BreakPending:
                                {
                                    pending = PendingKind.Break;
                                    pendingTargetIp = ins.A;
                                    // Run through finally frames but do not enter catches
                                    if (!Unwind(acceptCatch: false))
                                    {
                                        // No frames; immediately transfer control
                                        MaybeComplete();
                                    }
                                    break;
                                }
                            case OpCode.ContinuePending:
                                {
                                    pending = PendingKind.Continue;
                                    pendingTargetIp = ins.A;
                                    // Run through finally frames but do not enter catches
                                    if (!Unwind(acceptCatch: false))
                                    {
                                        // No frames; immediately transfer control
                                        MaybeComplete();
                                    }
                                    break;
                                }

                            default:
                                throw new MiniDynRuntimeError("Unknown opcode");
                        }
                    }
                    catch (Interpreter.ThrowSignal ts)
                    {
                        // Convert host/interpreter throw into VM-level unwinding so bytecode try/catch can handle it
                        pending = PendingKind.Throw;
                        pendingValue = ts.Value;

                        if (!Unwind(acceptCatch: true))
                        {
                            // No handler in this compiled function; bubble out to caller/interpreter
                            throw;
                        }
                        // Handler or finally found; loop will continue at updated ip
                    }
                    catch (MiniDynRuntimeError ex)
                    {
                        // Start unwinding as a runtime throw; only convert to Error object if a catch is found.
                        pending = PendingKind.ThrowRuntime;
                        pendingRuntime = ex;
                        if (!Unwind(acceptCatch: true))
                        {
                            // No handler in this compiled function; bubble original runtime error
                            throw;
                        }
                        // Handler or finally found; loop will continue at updated ip
                    }
                }
            }
            catch (Interpreter.ReturnSignal r)
            {
                // Internal completion path from MaybeComplete()
                return r.Value;
            }
            return Value.Nil();

            NumberValue ToNum(Value v)
            {
                switch (v.Type)
                {
                    case ValueType.Number: return v.AsNumber();
                    case ValueType.Boolean: return NumberValue.FromBool(v.AsBoolean());
                    case ValueType.Nil: return NumberValue.FromLong(0);
                    case ValueType.String:
                        return NumberValue.TryFromString(v.AsString(), out var nv) ? nv : NumberValue.FromLong(0);
                    default: return NumberValue.FromLong(0);
                }
            }
        }
    }

    internal sealed class BytecodeFunction : ICallable
    {
        private readonly string _name;
        private readonly List<Expr.Param> _params;
        private readonly Environment _closure;
        private readonly Chunk _chunk;
        private readonly Interpreter _interp;
        private readonly UserFunction.Kind _kind;
        private readonly Value? _capturedThis;
        private readonly Value? _boundThis;

        // locals count
        private readonly int _localCount;

        public int ArityMin { get; }
        public int ArityMax { get; }

        public BytecodeFunction(string name, List<Expr.Param> ps, Environment closure, Chunk chunk, Interpreter interp,
                                UserFunction.Kind kind, Value? capturedThis, Value? boundThis = null,
                                int localCount = 0)
        {
            _name = name;
            _params = ps;
            _closure = closure;
            _chunk = chunk;
            _interp = interp;
            _kind = kind;
            _capturedThis = capturedThis;
            _boundThis = boundThis;
            _localCount = localCount;

            // Only support no-default, no-rest in first step
            int min = 0, max = ps.Count;
            foreach (var p in ps)
            {
                if (p.IsRest || p.Default != null) { min = 0; max = int.MaxValue; break; }
                min++;
            }
            ArityMin = min;
            ArityMax = max;
        }

       public UserFunction.Kind FunctionKind => _kind;
       
       // Expose parameter metadata so the interpreter can map named/default args.
       public IReadOnlyList<Expr.Param> Params => _params;

       public BytecodeFunction BindThis(Value thisValue)
        {
            // Arrow ignores rebinding
            if (_kind == UserFunction.Kind.Arrow) return this;
            return new BytecodeFunction(_name, _params, _closure, _chunk, _interp, _kind, _capturedThis, thisValue, _localCount);
        }
        public Value Call(Interpreter interp, List<Value> args)
        {
            // same env handling as user fn but simpler; no defaults/rest in this first step
            var env = new Environment(_closure);

            if (_kind == UserFunction.Kind.Arrow)
            {
                if (_capturedThis.HasValue) env.DefineConst("this", _capturedThis.Value);
            }
            else
            {
                if (_boundThis.HasValue) env.DefineConst("this", _boundThis.Value);
            }

            for (int i = 0; i < _params.Count; i++)
            {
                var p = _params[i];
                if (p.IsRest || p.Default != null)
                    throw new MiniDynRuntimeError("Bytecode function: rest/default params not supported yet");
                if (i < args.Count) env.DefineVar(p.Name, args[i]);
                else throw new MiniDynRuntimeError($"Missing required argument '{p.Name}' for function {_name ?? "<fn>"}");
            }
            if (args.Count > _params.Count)
                throw new MiniDynRuntimeError($"Function {_name ?? "<fn>"} expected {_params.Count} args, got {args.Count}");

            // params + locals array for VM
            int totalSlots = _params.Count + _localCount;
            var paramLocals = new Value[totalSlots];
            for (int i = 0; i < _params.Count && i < args.Count; i++) paramLocals[i] = args[i];
            for (int i = _params.Count; i < totalSlots; i++) paramLocals[i] = Value.Nil();

            var vm = new BytecodeVM(interp);
            return vm.Run(_chunk, env, paramLocals);
        }

        public override string ToString() => _name != null ? $"<fn {_name}>" : "<fn>";
    }

    internal sealed class BytecodeCompiler
    {
        private readonly Interpreter _interp;
        private Dictionary<string, int> _paramIndex; // name -> param slot
        // locals map
        private Dictionary<string, int> _localsIndex;
        private int _localCount;
        // simple loop context for break/continue
        private sealed class LoopContext
        {
            public List<int> BreakJumps = new List<int>();
            public List<int> ContinueJumps = new List<int>();
            public int LoopStartIp;
        }
        private readonly Stack<LoopContext> _loopStack = new Stack<LoopContext>();

        // Track nesting inside any try/catch/finally region for emitting break/continue as pending completions.
        private int _tryDepth = 0;

        public BytecodeCompiler(Interpreter interp) { _interp = interp; }

        public bool TryCompileFunction(Expr.Function fn, out BytecodeFunction bc)
        {
            bc = null;

            // First try the old "single return expr" fast-path
            if (TryExtractReturnExpr(fn.Body, out var expr))
            {
                // Only support params without defaults/rest for now
                if (fn.Parameters.Any(p => p.Default != null || p.IsRest)) return false;

                // Do not compile if the return expression contains a call (would defeat interpreter TCO)
                if (ContainsCallExpr(expr)) return false;

                // Prepare param index map
                _paramIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < fn.Parameters.Count; i++) _paramIndex[fn.Parameters[i].Name] = i;
                _localsIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                _localCount = 0;

                var chunk = new Chunk();
                if (!TryEmitExpr(expr, chunk)) return false;
                chunk.Emit(OpCode.Return);
                // Populate metadata for closures
                chunk.ParamNames = fn.Parameters.Select(p => p.Name).ToList();
                chunk.LocalNames = BuildLocalNamesList();
                chunk.Peephole();

                var kind = fn.IsArrow ? UserFunction.Kind.Arrow : UserFunction.Kind.Normal;
                Value? capturedThis = null;
                if (fn.IsArrow && _interp.CurrentEnv != null)
                {
                    Value t;
                    if (_interp.CurrentEnv.TryGet("this", out t))
                        capturedThis = t;
                }

                bc = new BytecodeFunction(null, fn.Parameters, _interp.CurrentEnv, chunk, _interp, kind, capturedThis, null, _localCount);
                return true;
            }

            // compile a subset of statement-bodied functions
            // Only support params without defaults/rest for now
            if (fn.Parameters.Any(p => p.Default != null || p.IsRest)) return false;

            _paramIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < fn.Parameters.Count; i++) _paramIndex[fn.Parameters[i].Name] = i;
            _localsIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            _localCount = 0;

            var chunk2 = new Chunk();
            foreach (var st in fn.Body.Statements)
            {
                if (!TryEmitStmt(st, chunk2)) return false;
            }
            // implicit return nil at end if not returned
            int nilIdx = chunk2.AddConst(Value.Nil());
            chunk2.Emit(OpCode.LoadConst, nilIdx);
            chunk2.Emit(OpCode.Return);
            // Populate metadata for closures
            chunk2.ParamNames = fn.Parameters.Select(p => p.Name).ToList();
            chunk2.LocalNames = BuildLocalNamesList();
            chunk2.Peephole();

            var kind2 = fn.IsArrow ? UserFunction.Kind.Arrow : UserFunction.Kind.Normal;
            Value? capturedThis2 = null;
            if (fn.IsArrow && _interp.CurrentEnv != null)
            {
                Value t;
                if (_interp.CurrentEnv.TryGet("this", out t))
                    capturedThis2 = t;
            }

            bc = new BytecodeFunction(null, fn.Parameters, _interp.CurrentEnv, chunk2, _interp, kind2, capturedThis2, null, _localCount);
            return true;
        }

        // Build a proto for a nested function: try to compile to chunk; otherwise keep AST for fallback.
        private FunctionProto BuildFunctionProto(Expr.Function fn, string name)
        {
            var proto = new FunctionProto
            {
                Name = name,
                Params = fn.Parameters,
                Body = fn.Body,
                IsArrow = fn.IsArrow,
                DefSpan = fn.Span
            };

            // Use a fresh compiler to avoid clobbering current locals/params state
            var childCompiler = new BytecodeCompiler(_interp);
            if (childCompiler.TryCompileFunctionToChunk(fn, out var childChunk, out var localCount))
            {
                proto.CompiledChunk = childChunk;
                proto.LocalCount = localCount;
            }
            // else leave compiled members null -> VM will fallback to UserFunction
            return proto;
        }

        // Internal: try compiling a function into a chunk (no instantiation), for nested MakeFunction.
        private bool TryCompileFunctionToChunk(Expr.Function fn, out Chunk chunk, out int localCount)
        {
            chunk = null;
            localCount = 0;

            // Do not support defaults/rest in bytecode subset
            if (fn.Parameters.Any(p => p.Default != null || p.IsRest)) return false;

            // Fast single-expression return
            if (TryExtractReturnExpr(fn.Body, out var rexpr))
            {
                if (ContainsCallExpr(rexpr)) return false;
                _paramIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < fn.Parameters.Count; i++) _paramIndex[fn.Parameters[i].Name] = i;
                _localsIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                _localCount = 0;

                var c = new Chunk();
                if (!TryEmitExpr(rexpr, c)) return false;
                c.Emit(OpCode.Return);
                c.ParamNames = fn.Parameters.Select(p => p.Name).ToList();
                c.LocalNames = BuildLocalNamesList();
                c.Peephole();
                chunk = c;
                localCount = _localCount;
                return true;
            }

            // Statement-bodied subset
            _paramIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < fn.Parameters.Count; i++) _paramIndex[fn.Parameters[i].Name] = i;
            _localsIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            _localCount = 0;

            var c2 = new Chunk();
            foreach (var st in fn.Body.Statements)
            {
                if (!TryEmitStmt(st, c2)) return false;
            }
            int nilIdx = c2.AddConst(Value.Nil());
            c2.Emit(OpCode.LoadConst, nilIdx);
            c2.Emit(OpCode.Return);
            c2.ParamNames = fn.Parameters.Select(p => p.Name).ToList();
            c2.LocalNames = BuildLocalNamesList();
            c2.Peephole();

            chunk = c2;
            localCount = _localCount;
            return true;
        }

        // Build local names list indexed by local slot (0.._localCount-1)
        private List<string> BuildLocalNamesList()
        {
            var arr = new string[_localCount];
            foreach (var kv in _localsIndex)
                arr[kv.Value] = kv.Key;
            return arr.ToList();
        }

        // === Helpers for lowering foreach ===

        // Support only simple identifier pattern targets in the bytecode subset.
        private bool TryPreparePatternTarget(Expr.Pattern pat, bool isDeclaration, Stmt.DestructuringDecl.Kind declKind,
                                             out string name, out int? targetLocalSlot)
        {
            name = null; targetLocalSlot = null;
            if (pat is Expr.PatternIdentifier id)
            {
                name = id.Name;
                if (isDeclaration)
                {
                    // declare as a local slot
                    int slot = EnsureLocal(name);
                    targetLocalSlot = ParamCount + slot;
                    return true;
                }
                else
                {
                    // assigning into an existing binding
                    if (TryResolveVarSlot(name, out var slotIdx))
                    {
                        targetLocalSlot = slotIdx;
                        return true;
                    }
                    // will fall back to StoreName at runtime
                    targetLocalSlot = null;
                    return true;
                }
            }
            // destructuring or lvalue patterns are not supported in this bytecode path
            return false;
        }

        // Store the value currently on TOS into the prepared pattern target, then pop the result.
        private void EmitStoreIntoPatternFromStack(Chunk c, string name, int? targetLocalSlot)
        {
            if (targetLocalSlot.HasValue)
                c.Emit(OpCode.StoreLocal, targetLocalSlot.Value);
            else
                c.Emit(OpCode.StoreName, 0, name);
            // Store* leaves the value on stack; pop it
            c.Emit(OpCode.Pop);
        }

        // statement emitter for a supported subset
        private bool TryEmitStmt(Stmt s, Chunk c)
        {
            switch (s)
            {
                case Stmt.Block b:
                    foreach (var st in b.Statements)
                        if (!TryEmitStmt(st, c)) return false;
                    return true;

                case Stmt.Return r:
                {
                    // If the return value contains a call, bail to interpreter so TCO works.
                    if (r.Value != null && ContainsCallExpr(r.Value)) return false;

                    if (r.Value != null)
                    {
                        if (!TryEmitExpr(r.Value, c)) return false;
                    }
                    else
                    {
                        c.Emit(OpCode.LoadConst, c.AddConst(Value.Nil()));
                    }
                    c.Emit(OpCode.Return);
                    return true;
                }

                // === throw ===
                case Stmt.Throw thr:
                {
                    if (!TryEmitExpr(thr.Value, c)) return false; // push exception value
                    c.Emit(OpCode.Throw);
                    // Unreachable normally; allow following code for structure
                    return true;
                }

                // === try/catch/finally ===
                case Stmt.TryCatchFinally tcf:
                {
                    int tryEnterIdx = c.Emit(OpCode.TryEnter, 0, 0); // patch later
                    int tryStart = c.Code.Count;

                    // Mark: emitting inside try/catch/finally region
                    _tryDepth++;
                    // try block
                    if (!TryEmitStmt(tcf.Try, c)) { _tryDepth--; return false; }

                    // Normal exit from try:
                    // - if finally exists -> jump to finally
                    // - else if only catch exists -> jump after catch
                    int jAfterTry = c.Emit(OpCode.Jump, 0);

                    // Catch landing (optional)
                    int catchIp = -1;
                    int afterCatchIpLabel = -1;
                    if (tcf.Catch != null)
                    {
                        catchIp = c.Code.Count;
                        // At catch landing, VM pushed the thrown value on stack
                        if (!string.IsNullOrEmpty(tcf.CatchName))
                        {
                            int catchSlot = EnsureLocal(tcf.CatchName);
                            c.Emit(OpCode.StoreLocal, ParamCount + catchSlot); // stores and pushes back
                            c.Emit(OpCode.Pop); // remove the duplicate
                        }
                        else
                        {
                            c.Emit(OpCode.Pop); // discard the thrown value
                        }

                        if (!TryEmitStmt(tcf.Catch, c)) { _tryDepth--; return false; }
                        // After catch, always proceed to finally (if present) or after-all
                        afterCatchIpLabel = c.Emit(OpCode.Jump, 0);
                    }

                    // Finally landing (optional)
                    int finallyIp = -1;
                    if (tcf.Finally != null)
                    {
                        finallyIp = c.Code.Count;
                        if (!TryEmitStmt(tcf.Finally, c)) { _tryDepth--; return false; }
                        // EndFinally marker, then TryLeave pops frame and continues/unwinds
                        c.Emit(OpCode.EndFinally);
                        c.Emit(OpCode.TryLeave);
                    }
                    else
                    {
                        // No finally: one TryLeave after try/catch
                        // We'll place TryLeave at the unified after-all location below
                    }

                    // After-all continuation
                    int afterAll = c.Code.Count;

                    // Patch normal try exit
                    if (tcf.Finally != null)
                        c.PatchJump(jAfterTry, finallyIp);
                    else if (tcf.Catch != null)
                        c.PatchJump(jAfterTry, afterAll); // skip catch region
                    else
                        c.PatchJump(jAfterTry, afterAll);

                    // Patch after-catch to go to finally or after-all
                    if (tcf.Catch != null)
                    {
                        if (tcf.Finally != null)
                            c.PatchJump(afterCatchIpLabel, finallyIp);
                        else
                            c.PatchJump(afterCatchIpLabel, afterAll);
                    }

                    // If there was no finally, put TryLeave at after-all
                    if (tcf.Finally == null)
                    {
                        c.Emit(OpCode.TryLeave);
                    }

                    // Leave try/catch/finally region
                    _tryDepth--;

                    // Patch TryEnter with handler IPs
                    c.PatchTryEnter(tryEnterIdx, catchIp, finallyIp);

                    // Exception metadata for tooling
                    c.ExceptionTable.Add(new Chunk.ExceptionRegion
                    {
                        TryStartIp = tryStart,
                        TryEndIp   = (tcf.Catch != null ? catchIp : (tcf.Finally != null ? finallyIp : afterAll)),
                        CatchIp    = catchIp,
                        FinallyIp  = finallyIp
                    });

                    return true;
                }

                case Stmt.ExprStmt es:
                    {
                        if (!TryEmitExpr(es.Expression, c)) return false;
                        c.Emit(OpCode.Pop);
                        return true;
                    }

                case Stmt.DeclList dl:
                    {
                        foreach (var d in dl.Decls)
                            if (!TryEmitStmt(d, c)) return false;
                        return true;
                    }

                case Stmt.Var v:
                    {
                        int slot = EnsureLocal(v.Name);
                        if (v.Initializer != null)
                        {
                            if (!TryEmitExpr(v.Initializer, c)) return false;
                        }
                        else
                        {
                            c.Emit(OpCode.LoadConst, c.AddConst(Value.Nil()));
                        }
                        c.Emit(OpCode.StoreLocal, ParamCount + slot);
                        return true;
                    }

                case Stmt.Let l:
                    {
                        int slot = EnsureLocal(l.Name);
                        if (l.Initializer != null)
                        {
                            if (!TryEmitExpr(l.Initializer, c)) return false;
                        }
                        else
                        {
                            c.Emit(OpCode.LoadConst, c.AddConst(Value.Nil()));
                        }
                        c.Emit(OpCode.StoreLocal, ParamCount + slot);
                        return true;
                    }

                case Stmt.Const cn:
                    {
                        int slot = EnsureLocal(cn.Name);
                        if (cn.Initializer == null) return false; // require initializer
                        if (!TryEmitExpr(cn.Initializer, c)) return false;
                        c.Emit(OpCode.StoreLocal, ParamCount + slot);
                        return true;
                    }

                case Stmt.If iff:
                    {
                        if (!TryEmitExpr(iff.Condition, c)) return false;
                        int jf = c.Emit(OpCode.JumpIfFalse, 0);
                        if (!TryEmitStmt(iff.Then, c)) return false;
                        int jend = c.Emit(OpCode.Jump, 0);
                        c.PatchJump(jf, c.Code.Count);
                        if (iff.Else != null)
                        {
                            if (!TryEmitStmt(iff.Else, c)) return false;
                        }
                        c.PatchJump(jend, c.Code.Count);
                        return true;
                    }
                case Stmt.While w:
                    {
                        // loopStart:
                        var ctx = new LoopContext();
                        ctx.LoopStartIp = c.Code.Count;
                        _loopStack.Push(ctx);

                        // condition
                        if (!TryEmitExpr(w.Condition, c)) { _loopStack.Pop(); return false; }
                        int jf = c.Emit(OpCode.JumpIfFalse, 0);

                        // body
                        if (!TryEmitStmt(w.Body, c)) { _loopStack.Pop(); return false; }

                        // continue patches jump to loopStart
                        foreach (var jp in ctx.ContinueJumps) c.PatchJump(jp, ctx.LoopStartIp);

                        c.Emit(OpCode.Jump, ctx.LoopStartIp);
                        int exitIp = c.Code.Count;
                        c.PatchJump(jf, exitIp);
                        // break patches jump to exit
                        foreach (var jp in ctx.BreakJumps) c.PatchJump(jp, exitIp);
                        _loopStack.Pop();
                        return true;
                    }

                case Stmt.ForClassic fc:
                    {
                        // Optional initializer
                        if (fc.Initializer != null)
                        {
                            if (!TryEmitStmt(fc.Initializer, c)) return false;
                        }

                        var ctx = new LoopContext();
                        ctx.LoopStartIp = c.Code.Count;
                        _loopStack.Push(ctx);

                        // Optional condition
                        int jf = -1;
                        if (fc.Condition != null)
                        {
                            if (!TryEmitExpr(fc.Condition, c)) { _loopStack.Pop(); return false; }
                            jf = c.Emit(OpCode.JumpIfFalse, 0);
                        }

                        // Body
                        if (!TryEmitStmt(fc.Body, c)) { _loopStack.Pop(); return false; }

                        // Continue targets go to increment section
                        int incStart = c.Code.Count;
                        foreach (var jp in ctx.ContinueJumps) c.PatchJump(jp, incStart);

                        // Optional increment expression; discard its value
                        if (fc.Increment != null)
                        {
                            if (!TryEmitExpr(fc.Increment, c)) { _loopStack.Pop(); return false; }
                            c.Emit(OpCode.Pop);
                        }

                        // Loop back
                        c.Emit(OpCode.Jump, ctx.LoopStartIp);
                        int exitIp = c.Code.Count;

                        // Patch condition false -> exit
                        if (jf >= 0) c.PatchJump(jf, exitIp);

                        // Break -> exit
                        foreach (var jp in ctx.BreakJumps) c.PatchJump(jp, exitIp);

                        _loopStack.Pop();
                        return true;
                    }
                case Stmt.Break _:
                    {
                        if (_loopStack.Count == 0) return false;
                        var ctx = _loopStack.Peek();
                        int j;
                        if (_tryDepth > 0)
                        {
                            // Ensure finally blocks execute when breaking out of try regions
                            j = c.Emit(OpCode.BreakPending, 0);
                        }
                        else
                        {
                            j = c.Emit(OpCode.Jump, 0);
                        }
                        ctx.BreakJumps.Add(j);
                        return true;
                    }
                case Stmt.Continue _:
                    {
                        if (_loopStack.Count == 0) return false;
                        var ctx = _loopStack.Peek();
                        int j;
                        if (_tryDepth > 0)
                        {
                            // Ensure finally blocks execute when continuing out of try regions
                            j = c.Emit(OpCode.ContinuePending, 0);
                        }
                        else
                        {
                            j = c.Emit(OpCode.Jump, 0);
                        }
                        ctx.ContinueJumps.Add(j);
                        return true;
                    }

                case Stmt.Function fdecl:
                    {
                        // Treat like 'var name = <function>' within the compiled function's locals
                        int slot = EnsureLocal(fdecl.Name);
                        var proto = BuildFunctionProto(fdecl.FuncExpr, fdecl.Name);
                        c.Emit(OpCode.MakeFunction, 0, proto);
                        c.Emit(OpCode.StoreLocal, ParamCount + slot);
                        return true;
                    }

                // === Lowered foreach ===
                case Stmt.ForEach fe:
                    {
                        // Only simple identifier pattern in the compiled path
                        if (!TryPreparePatternTarget(fe.Pattern, fe.IsDeclaration, fe.DeclKind,
                                                     out var bindName, out var bindLocalSlot))
                            return false;

                        // it = <iterable>
                        int itSlot = NewTempSlot();
                        if (!TryEmitExpr(fe.Iterable, c)) return false;
                        c.Emit(OpCode.StoreLocal, ParamCount + itSlot);
                        c.Emit(OpCode.Pop);

                        // t = type(it) via intrinsic
                        int typeSlot = NewTempSlot();
                        c.Emit(OpCode.LoadLocal, ParamCount + itSlot);
                        c.Emit(OpCode.TypeOf);
                        c.Emit(OpCode.StoreLocal, ParamCount + typeSlot);
                        c.Emit(OpCode.Pop);

                        // Constants
                        int kArray = c.AddConst(Value.String("array"));
                        int kString = c.AddConst(Value.String("string"));
                        int kObject = c.AddConst(Value.String("object"));
                        int kNil = c.AddConst(Value.String("nil"));
                        int kZero = c.AddConst(Value.Number(NumberValue.FromLong(0)));
                        int kOne = c.AddConst(Value.Number(NumberValue.FromLong(1)));

                        var endJumps = new List<int>();

                        // Utility to emit i=0..len-1 loop.
                        int EmitIndexLoop(int lenSlot, Func<Chunk, int, bool> emitElementOrKey, out int endJumpOut)
                        {
                            endJumpOut = -1;

                            // idx local
                            int idxSlot = NewTempSlot();

                            // idx = 0
                            c.Emit(OpCode.LoadConst, kZero);
                            c.Emit(OpCode.StoreLocal, ParamCount + idxSlot);
                            c.Emit(OpCode.Pop);

                            // Loop context
                            var ctx = new LoopContext();
                            ctx.LoopStartIp = c.Code.Count;
                            _loopStack.Push(ctx);

                            // condition: idx < len
                            c.Emit(OpCode.LoadLocal, ParamCount + idxSlot);
                            c.Emit(OpCode.LoadLocal, ParamCount + lenSlot);
                            c.Emit(OpCode.CmpLt);
                            int jf = c.Emit(OpCode.JumpIfFalse, 0);

                            // Body: push current element/key onto stack
                            if (!emitElementOrKey(c, idxSlot)) { _loopStack.Pop(); return -1; }

                            // Bind into target pattern
                            EmitStoreIntoPatternFromStack(c, bindName, bindLocalSlot);

                            // Emit body
                            if (!TryEmitStmt(fe.Body, c)) { _loopStack.Pop(); return -1; }

                            // continue -> inc
                            int incStart = c.Code.Count;
                            foreach (var jp in ctx.ContinueJumps) c.PatchJump(jp, incStart);

                            // idx = idx + 1
                            c.Emit(OpCode.LoadLocal, ParamCount + idxSlot);
                            c.Emit(OpCode.LoadConst, kOne);
                            c.Emit(OpCode.Add);
                            c.Emit(OpCode.StoreLocal, ParamCount + idxSlot);
                            c.Emit(OpCode.Pop);

                            // backedge
                            c.Emit(OpCode.Jump, ctx.LoopStartIp);

                            // exit, patch breaks
                            int exitIp = c.Code.Count;
                            c.PatchJump(jf, exitIp);
                            foreach (var jp in ctx.BreakJumps) c.PatchJump(jp, exitIp);
                            _loopStack.Pop();

                            // jump to end of foreach after finishing matched branch
                            endJumpOut = c.Emit(OpCode.Jump, 0);
                            return idxSlot;
                        }

                        // Branch: array (type == "array")
                        c.Emit(OpCode.LoadLocal, ParamCount + typeSlot);
                        c.Emit(OpCode.LoadConst, kArray);
                        c.Emit(OpCode.CmpEq);
                        int jNotArray = c.Emit(OpCode.JumpIfFalse, 0);
                        {
                            // len = length(it) via intrinsic
                            int lenSlot = NewTempSlot();
                            c.Emit(OpCode.LoadLocal, ParamCount + itSlot);
                            c.Emit(OpCode.Length);
                            c.Emit(OpCode.StoreLocal, ParamCount + lenSlot);
                            c.Emit(OpCode.Pop);

                            // for (i=0; i<len; i++) ...
                            int endJump;
                            int dummy = EmitIndexLoop(lenSlot,
                                emitElementOrKey: (cc, idxSlot) =>
                                {
                                    if (fe.IsOf)
                                    {
                                        // value = it[idx]
                                        cc.Emit(OpCode.LoadLocal, ParamCount + itSlot);
                                        cc.Emit(OpCode.LoadLocal, ParamCount + idxSlot);
                                        cc.Emit(OpCode.GetIndex);
                                    }
                                    else
                                    {
                                        // key = to_string(idx) via intrinsic
                                        cc.Emit(OpCode.LoadLocal, ParamCount + idxSlot);
                                        cc.Emit(OpCode.ToStringOp);
                                    }
                                    return true;
                                },
                                out endJump);
                            if (dummy < 0) return false;
                            endJumps.Add(endJump);
                        }
                        c.PatchJump(jNotArray, c.Code.Count);

                        // Branch: string (type == "string")
                        c.Emit(OpCode.LoadLocal, ParamCount + typeSlot);
                        c.Emit(OpCode.LoadConst, kString);
                        c.Emit(OpCode.CmpEq);
                        int jNotString = c.Emit(OpCode.JumpIfFalse, 0);
                        {
                            // len = length(it) via intrinsic
                            int lenSlot = NewTempSlot();
                            c.Emit(OpCode.LoadLocal, ParamCount + itSlot);
                            c.Emit(OpCode.Length);
                            c.Emit(OpCode.StoreLocal, ParamCount + lenSlot);
                            c.Emit(OpCode.Pop);

                            int endJump;
                            int dummy = EmitIndexLoop(lenSlot,
                                emitElementOrKey: (cc, idxSlot) =>
                                {
                                    if (fe.IsOf)
                                    {
                                        // ch = it[idx]
                                        cc.Emit(OpCode.LoadLocal, ParamCount + itSlot);
                                        cc.Emit(OpCode.LoadLocal, ParamCount + idxSlot);
                                        cc.Emit(OpCode.GetIndex);
                                    }
                                    else
                                    {
                                        // key = to_string(idx) via intrinsic
                                        cc.Emit(OpCode.LoadLocal, ParamCount + idxSlot);
                                        cc.Emit(OpCode.ToStringOp);
                                    }
                                    return true;
                                },
                                out endJump);
                            if (dummy < 0) return false;
                            endJumps.Add(endJump);
                        }
                        c.PatchJump(jNotString, c.Code.Count);

                        // Branch: object (type == "object")
                        c.Emit(OpCode.LoadLocal, ParamCount + typeSlot);
                        c.Emit(OpCode.LoadConst, kObject);
                        c.Emit(OpCode.CmpEq);
                        int jNotObject = c.Emit(OpCode.JumpIfFalse, 0);
                        {
                            // arr = (fe.IsOf) ? values(it) : keys(it) via intrinsics
                            int arrSlot = NewTempSlot();
                            c.Emit(OpCode.LoadLocal, ParamCount + itSlot);
                            c.Emit(fe.IsOf ? OpCode.Values : OpCode.Keys);
                            c.Emit(OpCode.StoreLocal, ParamCount + arrSlot);
                            c.Emit(OpCode.Pop);

                            // len = length(arr)
                            int lenSlot = NewTempSlot();
                            c.Emit(OpCode.LoadLocal, ParamCount + arrSlot);
                            c.Emit(OpCode.Length);
                            c.Emit(OpCode.StoreLocal, ParamCount + lenSlot);
                            c.Emit(OpCode.Pop);

                            int endJump;
                            int dummy = EmitIndexLoop(lenSlot,
                                emitElementOrKey: (cc, idxSlot) =>
                                {
                                    // elem = arr[idx]
                                    cc.Emit(OpCode.LoadLocal, ParamCount + arrSlot);
                                    cc.Emit(OpCode.LoadLocal, ParamCount + idxSlot);
                                    cc.Emit(OpCode.GetIndex);
                                    return true;
                                },
                                out endJump);
                            if (dummy < 0) return false;
                            endJumps.Add(endJump);
                        }
                        c.PatchJump(jNotObject, c.Code.Count);

                        // Branch: nil (type == "nil") -> empty loop (do nothing)
                        c.Emit(OpCode.LoadLocal, ParamCount + typeSlot);
                        c.Emit(OpCode.LoadConst, kNil);
                        c.Emit(OpCode.CmpEq);
                        int jNotNil = c.Emit(OpCode.JumpIfFalse, 0);
                        {
                            // nothing; just jump to end
                            int j = c.Emit(OpCode.Jump, 0);
                            endJumps.Add(j);
                        }
                        c.PatchJump(jNotNil, c.Code.Count);

                        // For other types: raise a runtime error to match interpreter behavior using intrinsics.
                        {
                            int msgIdx = c.AddConst(
                                Value.String(fe.IsOf
                                    ? "Value is not iterable for 'for ... of'"
                                    : "Value is not indexable for 'for ... in'"));

                            // MakeError + Throw
                            c.Emit(OpCode.LoadConst, msgIdx);
                            c.Emit(OpCode.MakeError);

                            // Throw the error value so VM try/catch can handle it
                            c.Emit(OpCode.Throw);
                        }

                        // For other types: fallback to interpreter by failing compilation
                        // (keeps behavior consistent with runtime errors on unsupported types)
                        if (endJumps.Count == 0) return false;

                        // Patch all end jumps to here
                        int endIp = c.Code.Count;
                        foreach (var jp in endJumps) c.PatchJump(jp, endIp);
                        return true;
                    }
                default:
                    return false;
            }
        }

        private int ParamCount => _paramIndex?.Count ?? 0;

        private int EnsureLocal(string name)
        {
            if (_localsIndex.TryGetValue(name, out var idx)) return idx;
            idx = _localCount++;
            _localsIndex[name] = idx;
            return idx;
        }
        
        // Allocate an unnamed temporary local slot (for internal compiler use).
        private int NewTempSlot() => _localCount++;

        private bool TryEmitExpr(Expr e, Chunk c)
        {
            // Constant folding for nested binary/unary where both sides literal
            if (TryFold(e, out var constVal))
            {
                c.Emit(OpCode.LoadConst, c.AddConst(constVal));
                return true;
            }

            switch (e)
            {
                case Expr.Comma cm:
                    {
                        if (!TryEmitExpr(cm.Left, c)) return false;
                        c.Emit(OpCode.Pop);
                        if (!TryEmitExpr(cm.Right, c)) return false;
                        return true;
                    }
                case Expr.Literal lit:
                    c.Emit(OpCode.LoadConst, c.AddConst(lit.Value));
                    return true;

                case Expr.Assign asg:
                   {
                       // Helper to map token -> opcode for compound
                       OpCode BinOp(TokenType t)
                       {
                           switch (t)
                           {
                               case TokenType.PlusAssign: return OpCode.Add;
                               case TokenType.MinusAssign: return OpCode.Sub;
                               case TokenType.StarAssign: return OpCode.Mul;
                               case TokenType.SlashAssign: return OpCode.Div;
                               case TokenType.PercentAssign: return OpCode.Mod;
                               default: return (OpCode)(-1);
                           }
                       }

                       // variable target
                       if (asg.Target is Expr.Variable v)
                       {
                           int slot;
                           bool isCompiledVar = TryResolveVarSlot(v.Name, out slot);
                           if (asg.Op.Type == TokenType.Assign)
                           {
                               if (!TryEmitExpr(asg.Value, c)) return false;
                               if (isCompiledVar) c.Emit(OpCode.StoreLocal, slot);
                               else c.Emit(OpCode.StoreName, 0, v.Name);
                               return true;
                           }

                           if (asg.Op.Type == TokenType.NullishAssign)
                           {
                               // cur ??= rhs
                               if (isCompiledVar) c.Emit(OpCode.LoadLocal, slot);
                               else c.Emit(OpCode.LoadName, 0, v.Name);
                               c.Emit(OpCode.Dup);
                               int jHave = c.Emit(OpCode.JumpIfNotNil, 0); // if not nil -> leave cur
                               c.Emit(OpCode.Pop); // drop nil
                               if (!TryEmitExpr(asg.Value, c)) return false;
                               if (isCompiledVar) c.Emit(OpCode.StoreLocal, slot);
                               else c.Emit(OpCode.StoreName, 0, v.Name);
                               int jEnd = c.Emit(OpCode.Jump, 0);
                               c.PatchJump(jHave, c.Code.Count);
                               // stack already has cur (non-nil)
                               c.PatchJump(jEnd, c.Code.Count);
                               return true;
                           }

                           // Compound op
                           if (isCompiledVar) c.Emit(OpCode.LoadLocal, slot);
                           else c.Emit(OpCode.LoadName, 0, v.Name);
                           if (!TryEmitExpr(asg.Value, c)) return false;
                           var bop = BinOp(asg.Op.Type);
                           if ((int)bop == -1) return false;
                           c.Emit(bop);
                           if (isCompiledVar) c.Emit(OpCode.StoreLocal, slot);
                           else c.Emit(OpCode.StoreName, 0, v.Name);
                           return true;
                       }

                       // property target
                       if (asg.Target is Expr.Property p && !p.IsOptional)
                       {
                           if (asg.Op.Type == TokenType.Assign)
                           {
                               if (!TryEmitExpr(p.Target, c)) return false;    // T
                               if (!TryEmitExpr(asg.Value, c)) return false;   // T, rhs
                               c.Emit(OpCode.StoreProp, 0, p.Name);            // -> rhs
                               return true;
                           }

                           if (asg.Op.Type == TokenType.NullishAssign)
                           {
                               // T; dup; getprop -> T cur; dup; if not nil => pop T and keep cur
                               if (!TryEmitExpr(p.Target, c)) return false;    // T
                               c.Emit(OpCode.Dup);                              // T T
                               c.Emit(OpCode.GetProp, 0, p.Name);              // T cur
                               c.Emit(OpCode.Dup);                              // T cur cur
                               int jHave = c.Emit(OpCode.JumpIfNotNil, 0);     // if non-nil -> T cur
                               c.Emit(OpCode.Pop);                              // drop nil -> T
                               if (!TryEmitExpr(asg.Value, c)) return false;   // T rhs
                               c.Emit(OpCode.StoreProp, 0, p.Name);            // rhs
                               int jEnd = c.Emit(OpCode.Jump, 0);
                               c.PatchJump(jHave, c.Code.Count);               // T cur
                               c.Emit(OpCode.Pop);                              // cur
                               c.PatchJump(jEnd, c.Code.Count);
                               return true;
                           }

                           // Compound op
                           if (!TryEmitExpr(p.Target, c)) return false;    // T
                           c.Emit(OpCode.Dup);                              // T, T
                           c.Emit(OpCode.GetProp, 0, p.Name);              // T, cur
                           if (!TryEmitExpr(asg.Value, c)) return false;   // T, cur, rhs
                           var bop2 = BinOp(asg.Op.Type);
                           if ((int)bop2 == -1) return false;
                           c.Emit(bop2);                                    // T, new
                           c.Emit(OpCode.StoreProp, 0, p.Name);            // new
                           return true;
                       }

                       // index target
                       if (asg.Target is Expr.Index ix && !ix.IsOptional)
                       {
                           if (asg.Op.Type == TokenType.Assign)
                           {
                               if (!TryEmitExpr(ix.Target, c)) return false;       // T
                               if (!TryEmitExpr(ix.IndexExpr, c)) return false;    // T, I
                               if (!TryEmitExpr(asg.Value, c)) return false;       // T, I, rhs
                               c.Emit(OpCode.StoreIndex);                          // rhs
                               return true;
                           }

                           if (asg.Op.Type == TokenType.NullishAssign)
                           {
                               // Preserve T and I in temp locals, compute cur once, short-circuit if non-nil
                               int tSlot = NewTempSlot();
                               int iSlot = NewTempSlot();

                               if (!TryEmitExpr(ix.Target, c)) return false;       // T
                               c.Emit(OpCode.StoreLocal, ParamCount + tSlot);      // T
                               c.Emit(OpCode.Pop);

                               if (!TryEmitExpr(ix.IndexExpr, c)) return false;    // I
                               c.Emit(OpCode.StoreLocal, ParamCount + iSlot);      // I
                               c.Emit(OpCode.Pop);

                               // cur = t[i]
                               c.Emit(OpCode.LoadLocal, ParamCount + tSlot);       // T
                               c.Emit(OpCode.LoadLocal, ParamCount + iSlot);       // T, I
                               c.Emit(OpCode.GetIndex);                            // cur
                               c.Emit(OpCode.Dup);
                               int jHave = c.Emit(OpCode.JumpIfNotNil, 0);        // if non-nil -> cur
                               c.Emit(OpCode.Pop);                                 // drop nil

                               // nil path: T, I, rhs -> StoreIndex
                               c.Emit(OpCode.LoadLocal, ParamCount + tSlot);       // T
                               c.Emit(OpCode.LoadLocal, ParamCount + iSlot);       // T, I
                               if (!TryEmitExpr(asg.Value, c)) return false;       // T, I, rhs
                               c.Emit(OpCode.StoreIndex);                          // rhs
                               int jEnd = c.Emit(OpCode.Jump, 0);

                               // non-nil path: leave cur
                               c.PatchJump(jHave, c.Code.Count);                   // cur

                               c.PatchJump(jEnd, c.Code.Count);
                               return true;
                           }

                           // Compound op
                           if (!TryEmitExpr(ix.Target, c)) return false;       // T
                           if (!TryEmitExpr(ix.IndexExpr, c)) return false;    // T, I
                           c.Emit(OpCode.Dup2);                                 // T, I, T, I
                           c.Emit(OpCode.GetIndex);                             // T, I, cur
                           if (!TryEmitExpr(asg.Value, c)) return false;        // T, I, cur, rhs
                           var bop3 = BinOp(asg.Op.Type);
                           if ((int)bop3 == -1) return false;
                           c.Emit(bop3);                                        // T, I, new
                           c.Emit(OpCode.StoreIndex);                           // new
                           return true;
                       }

                       // optional-chained or unsupported assignment target -> fallback
                       return false;
                   }

                case Expr.Variable v:
                    {
                        if (_localsIndex != null && _localsIndex.TryGetValue(v.Name, out var lidx))
                        {
                            c.Emit(OpCode.LoadLocal, ParamCount + lidx);
                            return true;
                        }
                        if (_paramIndex != null && _paramIndex.TryGetValue(v.Name, out var pidx))
                        {
                            c.Emit(OpCode.LoadParam, pidx);
                            return true;
                        }
                        c.Emit(OpCode.LoadName, 0, v.Name);
                        return true;
                    }

                case Expr.Grouping g:
                    return TryEmitExpr(g.Inner, c);

                case Expr.Unary u:
                    if (!TryEmitExpr(u.Right, c)) return false;
                    if (u.Op.Type == TokenType.Minus) c.Emit(OpCode.Neg);
                    else if (u.Op.Type == TokenType.Not) c.Emit(OpCode.Not);
                    else if (u.Op.Type == TokenType.Plus) { /* no-op */ }
                    else return false;
                    return true;

                case Expr.Binary b:
                    {
                        if (!TryEmitExpr(b.Left, c)) return false;
                        if (!TryEmitExpr(b.Right, c)) return false;
                        switch (b.Op.Type)
                        {
                            case TokenType.Plus: c.Emit(OpCode.Add); return true;
                            case TokenType.Minus: c.Emit(OpCode.Sub); return true;
                            case TokenType.Star: c.Emit(OpCode.Mul); return true;
                            case TokenType.Slash: c.Emit(OpCode.Div); return true;
                            case TokenType.Percent: c.Emit(OpCode.Mod); return true;

                            case TokenType.Equal: c.Emit(OpCode.CmpEq); return true;
                            case TokenType.NotEqual: c.Emit(OpCode.CmpNe); return true;
                            case TokenType.Less: c.Emit(OpCode.CmpLt); return true;
                            case TokenType.LessEq: c.Emit(OpCode.CmpLe); return true;
                            case TokenType.Greater: c.Emit(OpCode.CmpGt); return true;
                            case TokenType.GreaterEq: c.Emit(OpCode.CmpGe); return true;

                            case TokenType.NullishCoalesce:
                                {
                                    c.Emit(OpCode.Dup);
                                    int j = c.Emit(OpCode.JumpIfNotNil, 0);
                                    c.Emit(OpCode.Pop);
                                    if (!TryEmitExpr(b.Right, c)) return false;
                                    c.PatchJump(j, c.Code.Count);
                                    return true;
                                }
                            default:
                                return false;
                        }
                    }

                case Expr.Logical l:
                    {
                        if (l.Op.Type == TokenType.And)
                        {
                            // left && right:
                            // stack flow:
                            //   push left
                            //   dup                         -> [left, left]
                            //   jump_if_false L_end         (pops top copy; if false, jumps with one left on stack as result)
                            //   pop                         (remove remaining left before computing right)
                            //   emit right                  (right becomes result)
                            // L_end:
                            if (!TryEmitExpr(l.Left, c)) return false;
                            c.Emit(OpCode.Dup);
                            int jf = c.Emit(OpCode.JumpIfFalse, 0);
                            c.Emit(OpCode.Pop);
                            if (!TryEmitExpr(l.Right, c)) return false;
                            c.PatchJump(jf, c.Code.Count);
                            return true;
                        }
                        else if (l.Op.Type == TokenType.Or)
                        {
                            // left || right:
                            //   push left
                            //   dup
                            //   jump_if_truthy L_end        (pops top copy; if true, jumps with one left on stack as result)
                            //   pop                         (remove remaining left before computing right)
                            //   emit right
                            // L_end:
                            if (!TryEmitExpr(l.Left, c)) return false;
                            c.Emit(OpCode.Dup);
                            int jt = c.Emit(OpCode.JumpIfTruthy, 0);
                            c.Emit(OpCode.Pop);
                            if (!TryEmitExpr(l.Right, c)) return false;
                            c.PatchJump(jt, c.Code.Count);
                            return true;
                        }
                        return false;
                    }

                case Expr.Ternary t:
                    {
                        if (!TryEmitExpr(t.Cond, c)) return false;
                        int jf = c.Emit(OpCode.JumpIfFalse, 0);
                        c.Emit(OpCode.Pop);
                        if (!TryEmitExpr(t.Then, c)) return false;
                        int j = c.Emit(OpCode.Jump, 0);
                        c.PatchJump(jf, c.Code.Count);
                        if (!TryEmitExpr(t.Else, c)) return false;
                        c.PatchJump(j, c.Code.Count);
                        return true;
                    }

                // property with optional chaining
                case Expr.Property prop:
                    {
                        if (prop.IsOptional)
                        {
                            // target
                            if (!TryEmitExpr(prop.Target, c)) return false;
                            c.Emit(OpCode.Dup);
                            int jNotNil = c.Emit(OpCode.JumpIfNotNil, 0);
                            c.Emit(OpCode.Pop);
                            // when nil -> leave nil as result
                            int jEnd = c.Emit(OpCode.Jump, 0);
                            c.PatchJump(jNotNil, c.Code.Count);
                            // non-nil: get prop
                            c.Emit(OpCode.GetProp, 0, prop.Name);
                            c.PatchJump(jEnd, c.Code.Count);
                            return true;
                        }
                        // normal
                        if (!TryEmitExpr(prop.Target, c)) return false;
                        c.Emit(OpCode.GetProp, 0, prop.Name);
                        return true;
                    }

                // index access (with optional chain)
                case Expr.Index idx:
                    {
                        if (!TryEmitExpr(idx.Target, c)) return false;
                        if (idx.IsOptional)
                        {
                            c.Emit(OpCode.Dup);
                            int jNotNil = c.Emit(OpCode.JumpIfNotNil, 0);
                            c.Emit(OpCode.Pop);
                            int jEnd = c.Emit(OpCode.Jump, 0);
                            c.PatchJump(jNotNil, c.Code.Count);
                            if (!TryEmitExpr(idx.IndexExpr, c)) return false;
                            c.Emit(OpCode.GetIndex);
                            c.PatchJump(jEnd, c.Code.Count);
                            return true;
                        }
                        if (!TryEmitExpr(idx.IndexExpr, c)) return false;
                        c.Emit(OpCode.GetIndex);
                        return true;
                    }

                case Expr.Call call:
                    {
                        // Named args unsupported for bytecode path
                        if (call.Args.Any(a => a.IsNamed)) return false;

                        // Optional method call: a?.f(args) or a?.[k](args)
                        if (call.Callee is Expr.Property pcal && pcal.IsOptional)
                        {
                            // receiver
                            if (!TryEmitExpr(pcal.Target, c)) return false;      // stack: [recv]
                            c.Emit(OpCode.Dup);                                   // [recv, recv]
                            int jNotNil = c.Emit(OpCode.JumpIfNotNil, 0);        // pop top; if non-nil -> L1; else push back nil
                            c.Emit(OpCode.Pop);                                   // drop duplicate/nil
                            // short-circuit to nil result (skip args)
                            c.Emit(OpCode.LoadConst, c.AddConst(Value.Nil()));
                            int jEnd = c.Emit(OpCode.Jump, 0);

                            // L1: non-nil path: [recv]
                            c.PatchJump(jNotNil, c.Code.Count);
                            c.Emit(OpCode.Dup);                                   // [recv, recv]
                            c.Emit(OpCode.GetProp, 0, pcal.Name);                 // [recv, callee]
                            foreach (var a in call.Args)
                                if (!TryEmitExpr(a.Value, c)) return false;
                            c.Emit(OpCode.CallMethod, call.Args.Count);
                            c.PatchJump(jEnd, c.Code.Count);
                            return true;
                        }

                        if (call.Callee is Expr.Index ical && ical.IsOptional)
                        {
                            // receiver
                            if (!TryEmitExpr(ical.Target, c)) return false;       // [recv]
                            c.Emit(OpCode.Dup);                                    // [recv, recv]
                            int jNotNil = c.Emit(OpCode.JumpIfNotNil, 0);
                            c.Emit(OpCode.Pop);                                    // drop duplicate/nil
                            c.Emit(OpCode.LoadConst, c.AddConst(Value.Nil()));
                            int jEnd = c.Emit(OpCode.Jump, 0);

                            c.PatchJump(jNotNil, c.Code.Count);                   // [recv]
                            c.Emit(OpCode.Dup);                                    // [recv, recv]
                            if (!TryEmitExpr(ical.IndexExpr, c)) return false;    // [recv, recv, key]
                            c.Emit(OpCode.GetIndex);                              // [recv, callee]
                            foreach (var a in call.Args)
                                if (!TryEmitExpr(a.Value, c)) return false;
                            c.Emit(OpCode.CallMethod, call.Args.Count);
                            c.PatchJump(jEnd, c.Code.Count);
                            return true;
                        }

                        // Method call via property (non-optional)
                        if (call.Callee is Expr.Property p && !p.IsOptional)
                        {
                            if (!TryEmitExpr(p.Target, c)) return false;  // push receiver
                            c.Emit(OpCode.Dup);
                            c.Emit(OpCode.GetProp, 0, p.Name);            // consumes dup, pushes callee
                            foreach (var a in call.Args)
                                if (!TryEmitExpr(a.Value, c)) return false;
                            c.Emit(OpCode.CallMethod, call.Args.Count);
                            return true;
                        }

                        // Method call via index (non-optional)
                        if (call.Callee is Expr.Index ic && !ic.IsOptional)
                        {
                            if (!TryEmitExpr(ic.Target, c)) return false;         // [recv]
                            c.Emit(OpCode.Dup);                                    // [recv, recv]
                            if (!TryEmitExpr(ic.IndexExpr, c)) return false;      // [recv, recv, key]
                            c.Emit(OpCode.GetIndex);                              // [recv, callee]
                            foreach (var a in call.Args)
                                if (!TryEmitExpr(a.Value, c)) return false;
                            c.Emit(OpCode.CallMethod, call.Args.Count);
                            return true;
                        }

                        // Simple call
                        if (!TryEmitExpr(call.Callee, c)) return false;
                        foreach (var a in call.Args)
                            if (!TryEmitExpr(a.Value, c)) return false;
                        c.Emit(OpCode.Call, call.Args.Count);
                        return true;
                    }
                // array literal
                case Expr.ArrayLiteral arrLit:
                    {
                        c.Emit(OpCode.NewArray);                  // [array]
                        foreach (var el in arrLit.Elements)
                        {
                            c.Emit(OpCode.Dup);                    // [array, array]
                            if (!TryEmitExpr(el, c)) return false; // [array, array, value]
                            c.Emit(OpCode.ArrayAppend);            // [array]
                        }
                        return true;
                    }

                // object literal
                case Expr.ObjectLiteral objLit:
                    {
                        c.Emit(OpCode.NewObject); // [obj]
                        foreach (var entry in objLit.Entries)
                        {
                            if (entry.KeyName != null)
                            {
                                // obj.key = value
                                c.Emit(OpCode.Dup);                                // [obj, obj]
                                if (!TryEmitExpr(entry.ValueExpr, c)) return false;// [obj, obj, value]
                                c.Emit(OpCode.StoreProp, 0, entry.KeyName);        // [obj, value]
                                c.Emit(OpCode.Pop);                                 // [obj]
                            }
                            else
                            {
                                // obj[keyExpr] = valueExpr
                                c.Emit(OpCode.Dup);                                // [obj, obj]
                                if (!TryEmitExpr(entry.KeyExpr, c)) return false;  // [obj, obj, key]
                                if (!TryEmitExpr(entry.ValueExpr, c)) return false;// [obj, obj, key, value]
                                c.Emit(OpCode.StoreIndex);                          // [obj, value]
                                c.Emit(OpCode.Pop);                                 // [obj]
                            }
                        }
                        return true;
                    }

                case Expr.Function fnExpr:
                    {
                        var proto = BuildFunctionProto(fnExpr, null);
                        c.Emit(OpCode.MakeFunction, 0, proto);
                        return true;
                    }

                default:
                    return false;
            }
        }
        private bool TryResolveVarSlot(string name, out int slotIndex)
        {
            if (_localsIndex != null && _localsIndex.TryGetValue(name, out var lidx))
            {
                slotIndex = ParamCount + lidx;
                return true;
            }
            if (_paramIndex != null && _paramIndex.TryGetValue(name, out var pidx))
            {
                slotIndex = pidx;
                return true;
            }
            slotIndex = -1;
            return false;
        }

        private static bool TryExtractReturnExpr(Stmt.Block body, out Expr expr)
        {
            expr = null;
            if (body?.Statements == null || body.Statements.Count != 1) return false;
            if (body.Statements[0] is Stmt.Return r && r.Value != null)
            {
                expr = r.Value; return true;
            }
            return false;
        }

        // conservative detector for any call within an expression
        private static bool ContainsCallExpr(Expr e)
        {
            if (e == null) return false;
            switch (e)
            {
                case Expr.Call _: return true;
                case Expr.Grouping g: return ContainsCallExpr(g.Inner);
                case Expr.Unary u: return ContainsCallExpr(u.Right);
                case Expr.Binary b: return ContainsCallExpr(b.Left) || ContainsCallExpr(b.Right);
                case Expr.Logical l: return ContainsCallExpr(l.Left) || ContainsCallExpr(l.Right);
                case Expr.Ternary t: return ContainsCallExpr(t.Cond) || ContainsCallExpr(t.Then) || ContainsCallExpr(t.Else);
                case Expr.Index i: return ContainsCallExpr(i.Target) || ContainsCallExpr(i.IndexExpr);
                case Expr.Property p: return ContainsCallExpr(p.Target);
                case Expr.ArrayLiteral a:
                    foreach (var el in a.Elements) if (ContainsCallExpr(el)) return true;
                    return false;
                case Expr.ObjectLiteral o:
                    foreach (var ent in o.Entries)
                    {
                        if (ent.KeyExpr != null && ContainsCallExpr(ent.KeyExpr)) return true;
                        if (ContainsCallExpr(ent.ValueExpr)) return true;
                    }
                    return false;
                case Expr.Function _: return false; // function literal itself is not a call
                case Expr.Variable _: return false;
                case Expr.Literal _: return false;
                case Expr.Assign asg:
                    return ContainsCallExpr(asg.Target) || ContainsCallExpr(asg.Value);
                case Expr.DestructuringAssign da:
                    return ContainsCallExpr(da.Value);
                case Expr.Comma c:
                    return ContainsCallExpr(c.Left) || ContainsCallExpr(c.Right);
                default:
                    return false;
            }
        }

        private static bool TryFold(Expr e, out Value v)
        {
            v = default(Value);
            switch (e)
            {
                case Expr.Literal lit:
                    v = lit.Value; return true;

                case Expr.Unary u:
                    if (TryFold(u.Right, out var rv))
                    {
                        switch (u.Op.Type)
                        {
                            case TokenType.Minus:
                                if (rv.Type == ValueType.Number)
                                { v = Value.Number(NumberValue.Neg(rv.AsNumber())); return true; }
                                break;
                            case TokenType.Not:
                                v = Value.Boolean(!Value.IsTruthy(rv)); return true;
                            case TokenType.Plus:
                                v = rv; return true;
                        }
                    }
                    return false;

                case Expr.Binary b:
                    if (TryFold(b.Left, out var lv) && TryFold(b.Right, out var rv2))
                    {
                        switch (b.Op.Type)
                        {
                            case TokenType.Plus:
                                if (lv.Type == ValueType.Number && rv2.Type == ValueType.Number)
                                { v = Value.Number(NumberValue.Add(lv.AsNumber(), rv2.AsNumber())); return true; }
                                if (lv.Type == ValueType.String && rv2.Type == ValueType.String)
                                { v = Value.String(lv.AsString() + rv2.AsString()); return true; }
                                return false;
                            case TokenType.Minus:
                                if (lv.Type == ValueType.Number && rv2.Type == ValueType.Number)
                                { v = Value.Number(NumberValue.Sub(lv.AsNumber(), rv2.AsNumber())); return true; }
                                return false;
                            case TokenType.Star:
                                if (lv.Type == ValueType.Number && rv2.Type == ValueType.Number)
                                { v = Value.Number(NumberValue.Mul(lv.AsNumber(), rv2.AsNumber())); return true; }
                                return false;
                            case TokenType.Slash:
                                if (lv.Type == ValueType.Number && rv2.Type == ValueType.Number)
                                {
                                    // Avoid compile-time exceptions (e.g., division by zero) so runtime try/catch can handle them.
                                    var div = rv2.AsNumber();
                                    if (NumberValue.Compare(div, NumberValue.FromLong(0)) == 0) return false;
                                    try
                                    {
                                        v = Value.Number(NumberValue.Div(lv.AsNumber(), div));
                                        return true;
                                    }
                                    catch (MiniDynRuntimeError)
                                    {
                                        // Defer to runtime
                                        return false;
                                    }
                                }
                                return false;
                            case TokenType.Percent:
                                if (lv.Type == ValueType.Number && rv2.Type == ValueType.Number)
                                {
                                    // Avoid compile-time exceptions (e.g., modulo by zero).
                                    var mod = rv2.AsNumber();
                                    if (NumberValue.Compare(mod, NumberValue.FromLong(0)) == 0) return false;
                                    try
                                    {
                                        v = Value.Number(NumberValue.Mod(lv.AsNumber(), mod));
                                        return true;
                                    }
                                    catch (MiniDynRuntimeError)
                                    {
                                        // Defer to runtime
                                        return false;
                                    }
                                }
                                return false;
                            case TokenType.Equal:
                                v = Value.Boolean(lv.Equals(rv2)); return true;
                            case TokenType.NotEqual:
                                v = Value.Boolean(!lv.Equals(rv2)); return true;
                            case TokenType.Less:
                            case TokenType.LessEq:
                            case TokenType.Greater:
                            case TokenType.GreaterEq:
                                if (lv.Type == ValueType.Number && rv2.Type == ValueType.Number)
                                {
                                    int cmp = NumberValue.Compare(lv.AsNumber(), rv2.AsNumber());
                                    bool res = b.Op.Type == TokenType.Less ? (cmp < 0)
                                        : b.Op.Type == TokenType.LessEq ? (cmp <= 0)
                                        : b.Op.Type == TokenType.Greater ? (cmp > 0) : (cmp >= 0);
                                    v = Value.Boolean(res); return true;
                                }
                                if (lv.Type == ValueType.String && rv2.Type == ValueType.String)
                                {
                                    int cmp = string.CompareOrdinal(lv.AsString(), rv2.AsString());
                                    bool res = b.Op.Type == TokenType.Less ? (cmp < 0)
                                        : b.Op.Type == TokenType.LessEq ? (cmp <= 0)
                                        : b.Op.Type == TokenType.Greater ? (cmp > 0) : (cmp >= 0);
                                    v = Value.Boolean(res); return true;
                                }
                                return false;
                            case TokenType.NullishCoalesce:
                                v = (lv.Type != ValueType.Nil) ? lv : rv2; return true;
                        }
                    }
                    return false;

                case Expr.Logical l:
                    if (TryFold(l.Left, out var lv2))
                    {
                        if (l.Op.Type == TokenType.And)
                        {
                            if (!Value.IsTruthy(lv2)) { v = lv2; return true; }
                            if (TryFold(l.Right, out var rv3)) { v = rv3; return true; }
                        }
                        else if (l.Op.Type == TokenType.Or)
                        {
                            if (Value.IsTruthy(lv2)) { v = lv2; return true; }
                            if (TryFold(l.Right, out var rv3)) { v = rv3; return true; }
                        }
                    }
                    return false;

                case Expr.Ternary t:
                    if (TryFold(t.Cond, out var c))
                    {
                        if (Value.IsTruthy(c) && TryFold(t.Then, out var tv)) { v = tv; return true; }
                        if (!Value.IsTruthy(c) && TryFold(t.Else, out var ev)) { v = ev; return true; }
                    }
                    return false;

                default:
                    return false;
            }
        }
    }

    // Interpreter
    public class Interpreter : Expr.IVisitor<Value>, Stmt.IVisitor<object>
    {
        public class ReturnSignal : Exception
        {
            public Value Value;
            public ReturnSignal(Value v) { Value = v; }
        }
        public class BreakSignal : Exception { }
        public class ContinueSignal : Exception { }
        public class ThrowSignal : Exception
        {
            public Value Value;
            public ThrowSignal(Value v) { Value = v; }
        }

        // Tail-call trampoline signal
        public class TailCallSignal : Exception
        {
            public UserFunction Function { get; }
            public List<Value> Args { get; }
            // carry an argument mapping for named-args/defaults
            public ArgMapping? Mapping { get; }

            public TailCallSignal(UserFunction fn, List<Value> args)
            {
                Function = fn;
                Args = args;
                Mapping = null;
            }

            public TailCallSignal(UserFunction fn, ArgMapping mapping)
            {
                Function = fn;
                Mapping = mapping;
                Args = null;
            }
        }

        // mapping used for named arguments (no default evaluation here)
        public struct ArgMapping
        {
            public Value[] Values; // length == parameter count; slots for non-rest params
            public bool[] Filled;  // which non-rest param slots are provided
            public List<Value> Rest; // extra positional args for rest param (if any)
        }

        public readonly Environment Globals;
        private Environment _env;
        public Environment CurrentEnv => _env;

        // Track whether we're inside a loop to validate break/continue usage
        private int _loopDepth = 0;

        private readonly Dictionary<string, ICallable> _builtins = new Dictionary<string, ICallable>();
        private readonly Stack<SourceSpan> _nodeSpanStack = new Stack<SourceSpan>();
        private readonly Stack<CallFrame> _callStack = new Stack<CallFrame>();
        private readonly IModuleLoader _moduleLoader;
        private sealed class ModuleCacheEntry
        {
            // Final exports available after successful execution
            public Value? Exports;

            // Loading state support for cyclic dependencies
            public bool IsLoading;
            // Pre-seeded exports object used during loading (for cycles)
            public Value? TempExports;
        }
        private readonly Dictionary<string, ModuleCacheEntry> _moduleCache =
            new Dictionary<string, ModuleCacheEntry>(StringComparer.OrdinalIgnoreCase);

        // stack of currently executing user functions (for tail-call detection)
        private readonly Stack<UserFunction> _fnExecStack = new Stack<UserFunction>();
        internal void PushFunction(UserFunction fn) => _fnExecStack.Push(fn);
        internal void PopFunction() { if (_fnExecStack.Count > 0) _fnExecStack.Pop(); }
        internal UserFunction CurrentFunction => _fnExecStack.Count > 0 ? _fnExecStack.Peek() : null;

        public Interpreter(IModuleLoader moduleLoader = null)
        {
            _moduleLoader = moduleLoader ?? new FileSystemModuleLoader();

            // Make top-level a function env so 'var' is truly global/function scoped
            Globals = new FunctionEnvironment();
            _env = Globals;

            // Builtins
            DefineBuiltin("length", 1, 1, (i, a) =>
            {
                var v = a[0];
                switch (v.Type)
                {
                    case ValueType.String: return Value.Number(NumberValue.FromLong(v.AsString().Length));
                    case ValueType.Array: return Value.Number(NumberValue.FromLong(v.AsArray().Length));
                    case ValueType.Object: return Value.Number(NumberValue.FromLong(v.AsObject().Count));
                    case ValueType.Nil: return Value.Number(NumberValue.FromLong(0));
                    case ValueType.Boolean: return Value.Number(NumberValue.FromLong(1));
                    case ValueType.Number: return Value.Number(NumberValue.FromLong(1));
                    case ValueType.Function: return Value.Number(NumberValue.FromLong(1));
                    default: return Value.Number(NumberValue.FromLong(0));
                }
            });

            DefineBuiltin("print", 1, int.MaxValue, (i, a) =>
            {
                var sb = new StringBuilder();
                for (int idx = 0; idx < a.Count; idx++)
                {
                    sb.Append(i.ToStringValue(a[idx]));
                    if (idx + 1 < a.Count) sb.Append(' ');
                }
                Console.Write(sb.ToString());
                return Value.Nil();
            });

            DefineBuiltin("println", 0, int.MaxValue, (i, a) =>
            {
                if (a.Count == 0) { Console.WriteLine(); return Value.Nil(); }
                var sb = new StringBuilder();
                for (int idx = 0; idx < a.Count; idx++)
                {
                    sb.Append(i.ToStringValue(a[idx]));
                    if (idx + 1 < a.Count) sb.Append(' ');
                }
                Console.WriteLine(sb.ToString());
                return Value.Nil();
            });

            DefineBuiltin("gets", 0, 0, (i, a) =>
            {
                string line = Console.ReadLine();
                return Value.String(line);
            });

            DefineBuiltin("to_number", 1, 1, (i, a) =>
            {
                var v = a[0];
                switch (v.Type)
                {
                    case ValueType.Number: return v;
                    case ValueType.String:
                        return NumberValue.TryFromString(v.AsString(), out var nv) ? Value.Number(nv) : Value.Nil();
                    case ValueType.Boolean: return Value.Number(NumberValue.FromBool(v.AsBoolean()));
                    case ValueType.Nil: return Value.Number(NumberValue.FromLong(0));
                    default: return Value.Nil();
                }
            });

            DefineBuiltin("to_string", 1, 1, (i, a) => Value.String(i.ToStringValue(a[0])));

            DefineBuiltin("type", 1, 1, (i, a) => Value.String(a[0].Type.ToString().ToLowerInvariant()));

            // Object builtins
            DefineBuiltin("keys", 1, 1, (i, a) =>
            {
                var o = a[0].AsObject();
                var arr = new ArrayValue();
                foreach (var k in o.Keys) arr.Items.Add(Value.String(k));
                return Value.Array(arr);
            });

            DefineBuiltin("has_key", 2, 2, (i, a) =>
            {
                var o = a[0].AsObject();
                var k = a[1].AsString();
                return Value.Boolean(o.Contains(k));
            });

            DefineBuiltin("remove_key", 2, 2, (i, a) =>
            {
                var o = a[0].AsObject();
                var k = a[1].AsString();
                return Value.Boolean(o.Remove(k));
            });

            DefineBuiltin("merge", 2, 2, (i, a) =>
            {
                var o1 = a[0].AsObject();
                var o2 = a[1].AsObject();
                var res = o1.CloneShallow();
                foreach (var kv in o2.Entries) res.Set(kv.Key, kv.Value);
                return Value.Object(res);
            });

            // Math
            DefineBuiltin("abs", 1, 1, (i, a) =>
            {
                var n = ToNumber(a[0]);
                switch (n.Kind)
                {
                    case NumberValue.NumKind.Int: return Value.Number(NumberValue.FromLong(Math.Abs(n.I64)));
                    case NumberValue.NumKind.BigInt: return Value.Number(NumberValue.FromBigInt(BigInteger.Abs(n.BigInt)));
                    case NumberValue.NumKind.Double: return Value.Number(NumberValue.FromDouble(Math.Abs(n.Dbl)));
                    default: return Value.Number(NumberValue.FromLong(0));
                }
            });
            DefineBuiltin("floor", 1, 1, (i, a) => Value.Number(NumberValue.FromDouble(Math.Floor(ToNumber(a[0]).ToDoubleNV().Dbl))));
            DefineBuiltin("ceil", 1, 1, (i, a) => Value.Number(NumberValue.FromDouble(Math.Ceiling(ToNumber(a[0]).ToDoubleNV().Dbl))));
            DefineBuiltin("round", 1, 1, (i, a) => Value.Number(NumberValue.FromDouble(Math.Round(ToNumber(a[0]).ToDoubleNV().Dbl))));
            DefineBuiltin("sqrt", 1, 1, (i, a) => Value.Number(NumberValue.FromDouble(Math.Sqrt(ToNumber(a[0]).ToDoubleNV().Dbl))));
            DefineBuiltin("pow", 2, 2, (i, a) => Value.Number(NumberValue.FromDouble(Math.Pow(ToNumber(a[0]).ToDoubleNV().Dbl, ToNumber(a[1]).ToDoubleNV().Dbl))));
            DefineBuiltin("min", 1, int.MaxValue, (i, a) =>
            {
                if (a.Count == 0) return Value.Nil();
                var cur = ToNumber(a[0]);
                for (int k = 1; k < a.Count; k++)
                    if (NumberValue.Compare(ToNumber(a[k]), cur) < 0) cur = ToNumber(a[k]);
                return Value.Number(cur);
            });
            DefineBuiltin("max", 1, int.MaxValue, (i, a) =>
            {
                if (a.Count == 0) return Value.Nil();
                var cur = ToNumber(a[0]);
                for (int k = 1; k < a.Count; k++)
                    if (NumberValue.Compare(ToNumber(a[k]), cur) > 0) cur = ToNumber(a[k]);
                return Value.Number(cur);
            });

            var rng = new Random();
            DefineBuiltin("random", 0, 0, (i, a) => Value.Number(NumberValue.FromDouble(rng.NextDouble())));
            DefineBuiltin("srand", 1, 1, (i, a) => { rng = new Random((int)(ToNumber(a[0]).ToDoubleNV().Dbl)); return Value.Nil(); });

            // String utilities
            DefineBuiltin("substring", 2, 3, (i, a) =>
            {
                var s = a[0].AsString();
                var start = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                if (start < 0) start = 0;
                if (start > s.Length) start = s.Length;
                if (a.Count == 2)
                {
                    return Value.String(s.Substring(start));
                }
                var len = (int)ToNumber(a[2]).ToDoubleNV().Dbl;
                if (len < 0) len = 0;
                if (start + len > s.Length) len = s.Length - start;
                return Value.String(s.Substring(start, len));
            });
            DefineBuiltin("index_of", 2, 2, (i, a) => Value.Number(NumberValue.FromLong(a[0].AsString().IndexOf(a[1].AsString(), StringComparison.Ordinal))));
            DefineBuiltin("contains", 2, 2, (i, a) => Value.Boolean(a[0].AsString().IndexOf(a[1].AsString(), StringComparison.Ordinal) >= 0));
            DefineBuiltin("starts_with", 2, 2, (i, a) => Value.Boolean(a[0].AsString().StartsWith(a[1].AsString(), StringComparison.Ordinal)));
            DefineBuiltin("ends_with", 2, 2, (i, a) => Value.Boolean(a[0].AsString().EndsWith(a[1].AsString(), StringComparison.Ordinal)));
            DefineBuiltin("to_upper", 1, 1, (i, a) => Value.String(a[0].AsString().ToUpperInvariant()));
            DefineBuiltin("to_lower", 1, 1, (i, a) => Value.String(a[0].AsString().ToLowerInvariant()));
            DefineBuiltin("trim", 1, 1, (i, a) => Value.String(a[0].AsString().Trim()));
            DefineBuiltin("split", 2, 2, (i, a) =>
            {
                var parts = a[0].AsString().Split(new string[] { a[1].AsString() }, StringSplitOptions.None);
                var arr = new ArrayValue();
                foreach (var p in parts) arr.Items.Add(Value.String(p));
                return Value.Array(arr);
            });
            DefineBuiltin("parse_int", 1, 1, (i, a) =>
            {
                var s = a[0].AsString();
                if (NumberValue.TryFromString(s, out var nv) && nv.Kind != NumberValue.NumKind.Double)
                    return Value.Number(nv);
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return Value.Number(NumberValue.FromLong(l));
                if (BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bi))
                    return Value.Number(NumberValue.FromBigInt(bi));
                return Value.Nil();
            });
            DefineBuiltin("parse_float", 1, 1, (i, a) =>
            {
                var s = a[0].AsString();
                if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d))
                    return Value.Number(NumberValue.FromDouble(d));
                return Value.Nil();
            });

            // Array helpers
            DefineBuiltin("array", 0, int.MaxValue, (i, a) => Value.Array(new ArrayValue(a)));
            DefineBuiltin("push", 2, int.MaxValue, (i, a) =>
            {
                var arr = a[0].AsArray();
                for (int k = 1; k < a.Count; k++) arr.Items.Add(a[k]);
                return Value.Number(NumberValue.FromLong(arr.Length));
            });
            DefineBuiltin("pop", 1, 1, (i, a) =>
            {
                var arr = a[0].AsArray();
                if (arr.Length == 0) return Value.Nil();
                // Remove index-from-end operator (^1) for .NET Framework
                var v = arr.Items[arr.Length - 1];
                arr.Items.RemoveAt(arr.Length - 1);
                return v;
            });
            DefineBuiltin("slice", 2, 3, (i, a) =>
            {
                if (a[0].Type == ValueType.Array)
                {
                    var arr = a[0].AsArray();
                    int start = (int)RuntimeOps.ToNumber(a[1]).ToDoubleNV().Dbl;
                    start = RuntimeOps.NormalizeIndex(start, arr.Length);
                    int end = arr.Length;
                    if (a.Count == 3)
                    {
                        end = (int)RuntimeOps.ToNumber(a[2]).ToDoubleNV().Dbl;
                        end = RuntimeOps.NormalizeIndex(end, arr.Length);
                    }
                    if (start < 0) start = 0;
                    if (end < start) end = start;
                    if (end > arr.Length) end = arr.Length;
                    var res = new ArrayValue();
                    for (int k = start; k < end; k++) res.Items.Add(arr[k]);
                    return Value.Array(res);
                }
                else if (a[0].Type == ValueType.String)
                {
                    var s = a[0].AsString();
                    int start = (int)RuntimeOps.ToNumber(a[1]).ToDoubleNV().Dbl;
                    start = RuntimeOps.NormalizeIndex(start, s.Length);
                    int end = s.Length;
                    if (a.Count == 3)
                    {
                        end = (int)RuntimeOps.ToNumber(a[2]).ToDoubleNV().Dbl;
                        end = RuntimeOps.NormalizeIndex(end, s.Length);
                    }
                    if (start < 0) start = 0;
                    if (end < start) end = start;
                    if (end > s.Length) end = s.Length;
                    return Value.String(s.Substring(start, end - start));
                }
                throw new MiniDynRuntimeError("slice expects array or string");
            });
            DefineBuiltin("join", 2, 2, (i, a) =>
            {
                var arr = a[0].AsArray();
                var sep = a[1].AsString();
                var sb = new StringBuilder();
                for (int k = 0; k < arr.Length; k++)
                {
                    if (k > 0) sb.Append(sep);
                    sb.Append(i.ToStringValue(arr[k]));
                }
                return Value.String(sb.ToString());
            });
            DefineBuiltin("at", 2, 2, (i, a) =>
            {
                var arr = a[0].AsArray();
                int idx = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                idx = NormalizeIndex(idx, arr.Length);
                if (idx < 0 || idx >= arr.Length) return Value.Nil();
                return arr[idx];
            });
            DefineBuiltin("set_at", 3, 3, (i, a) =>
            {
                var arr = a[0].AsArray();
                int idx = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                idx = NormalizeIndex(idx, arr.Length);
                if (idx < 0 || idx >= arr.Length) throw new MiniDynRuntimeError("Array index out of range");
                arr[idx] = a[2];
                return a[2];
            });
            DefineBuiltin("clone", 1, 1, (i, a) =>
            {
                if (a[0].Type == ValueType.Array) return Value.Array(a[0].AsArray().Clone());
                if (a[0].Type == ValueType.Object) return Value.Object(a[0].AsObject().CloneShallow());
                return a[0];
            });

            // Time
            DefineBuiltin("now_ms", 0, 0, (i, a) => Value.Number(NumberValue.FromLong(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));

            // === Array/object helpers ===
            DefineBuiltin("values", 1, 1, (i, a) =>
            {
                var o = a[0].AsObject();
                var arr = new ArrayValue();
                foreach (var kv in o.Entries) arr.Items.Add(kv.Value);
                return Value.Array(arr);
            });

            DefineBuiltin("entries", 1, 1, (i, a) =>
            {
                var o = a[0].AsObject();
                var arr = new ArrayValue();
                foreach (var kv in o.Entries)
                {
                    var pair = new ArrayValue(new[] { Value.String(kv.Key), kv.Value });
                    arr.Items.Add(Value.Array(pair));
                }
                return Value.Array(arr);
            });

            DefineBuiltin("from_entries", 1, 1, (i, a) =>
            {
                var src = a[0].AsArray();
                var ov = new ObjectValue();
                for (int k = 0; k < src.Length; k++)
                {
                    var entry = src[k];
                    if (entry.Type != ValueType.Array) throw new MiniDynRuntimeError("from_entries expects array of [key, value] pairs");
                    var tup = entry.AsArray();
                    if (tup.Length != 2 || tup[0].Type != ValueType.String) throw new MiniDynRuntimeError("from_entries expects [string, any] pairs");
                    ov.Set(tup[0].AsString(), tup[1]);
                }
                return Value.Object(ov);
            });

            DefineBuiltin("map", 2, 2, (i, a) =>
            {
                var arr = a[0].AsArray();
                var fn = a[1].AsFunction();
                var res = new ArrayValue();
                for (int idx = 0; idx < arr.Length; idx++)
                    res.Items.Add(fn.Call(i, new List<Value> { arr[idx] }));
                return Value.Array(res);
            });

            DefineBuiltin("filter", 2, 2, (i, a) =>
            {
                var arr = a[0].AsArray();
                var fn = a[1].AsFunction();
                var res = new ArrayValue();
                for (int idx = 0; idx < arr.Length; idx++)
                {
                    var keep = fn.Call(i, new List<Value> { arr[idx] });
                    if (Value.IsTruthy(keep)) res.Items.Add(arr[idx]);
                }
                return Value.Array(res);
            });

            DefineBuiltin("reduce", 2, 3, (i, a) =>
            {
                var arr = a[0].AsArray();
                var fn = a[1].AsFunction();
                int start = 0;
                Value acc;
                if (a.Count == 3) { acc = a[2]; }
                else
                {
                    if (arr.Length == 0) throw new MiniDynRuntimeError("reduce of empty array with no initial value");
                    acc = arr[0]; start = 1;
                }
                for (int idx = start; idx < arr.Length; idx++)
                    acc = fn.Call(i, new List<Value> { acc, arr[idx] });
                return acc;
            });

            DefineBuiltin("sort", 1, 2, (i, a) =>
            {
                var src = a[0].AsArray();
                var copy = src.Clone();
                Comparison<Value> cmp;
                if (a.Count == 2)
                {
                    var fn = a[1].AsFunction();
                    cmp = (x, y) =>
                    {
                        var r = fn.Call(i, new List<Value> { x, y });
                        var n = r.Type == ValueType.Number ? r.AsNumber() : NumberValue.FromLong(0);
                        var d = n.ToDoubleNV().Dbl;
                        return d < 0 ? -1 : d > 0 ? 1 : 0;
                    };
                }
                else
                {
                    cmp = (x, y) =>
                    {
                        if (x.Type == ValueType.Number && y.Type == ValueType.Number)
                            return NumberValue.Compare(x.AsNumber(), y.AsNumber());
                        if (x.Type == ValueType.String && y.Type == ValueType.String)
                            return string.CompareOrdinal(x.AsString(), y.AsString());
                        // Fallback: by type name
                        return string.CompareOrdinal(x.Type.ToString(), y.Type.ToString());
                    };
                }
                copy.Items.Sort(cmp);
                return Value.Array(copy);
            });

            DefineBuiltin("unique", 1, 1, (i, a) =>
            {
                var arr = a[0].AsArray();
                var seen = new HashSet<Value>();
                var res = new ArrayValue();
                for (int k = 0; k < arr.Length; k++)
                    if (seen.Add(arr[k])) res.Items.Add(arr[k]);
                return Value.Array(res);
            });

            // Deep structural equality
            DefineBuiltin("deep_equal", 2, 2, (i, a) => Value.Boolean(i.DeepEqual(a[0], a[1])));

            DefineBuiltin("range", 1, 3, (i, a) =>
            {
                long start, end, step;
                if (a.Count == 1) { start = 0; end = (long)RuntimeOps.ToNumber(a[0]).ToDoubleNV().Dbl; step = 1; }
                else if (a.Count == 2) { start = (long)RuntimeOps.ToNumber(a[0]).ToDoubleNV().Dbl; end = (long)RuntimeOps.ToNumber(a[1]).ToDoubleNV().Dbl; step = 1; }
                else { start = (long)RuntimeOps.ToNumber(a[0]).ToDoubleNV().Dbl; end = (long)RuntimeOps.ToNumber(a[1]).ToDoubleNV().Dbl; step = (long)RuntimeOps.ToNumber(a[2]).ToDoubleNV().Dbl; }
                if (step == 0) throw new MiniDynRuntimeError("range step cannot be 0");
                var res = new ArrayValue();
                if (step > 0) for (long v = start; v < end; v += step) res.Items.Add(Value.Number(NumberValue.FromLong(v)));
                else for (long v = start; v > end; v += step) res.Items.Add(Value.Number(NumberValue.FromLong(v)));
                return Value.Array(res);
            });

            // === String helpers ===
            DefineBuiltin("replace", 3, 3, (i, a) =>
            {
                var s = a[0].AsString(); var find = a[1].AsString(); var repl = a[2].AsString();
                // .NET Framework doesn't have Replace with StringComparison; default Replace is ordinal and case-sensitive.
                return Value.String(s.Replace(find, repl));
            });
            DefineBuiltin("repeat", 2, 2, (i, a) =>
            {
                var s = a[0].AsString(); int n = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                if (n < 0) n = 0;
                var sb = new StringBuilder(s.Length * n);
                for (int k = 0; k < n; k++) sb.Append(s);
                return Value.String(sb.ToString());
            });
            DefineBuiltin("pad_start", 2, 3, (i, a) =>
            {
                var s = a[0].AsString(); int len = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                var pad = a.Count == 3 ? a[2].AsString() : " ";
                if (pad.Length == 0) pad = " ";
                if (s.Length >= len) return Value.String(s);
                var sb = new StringBuilder(len);
                while (sb.Length + s.Length < len) sb.Append(pad);
                var padCut = sb.ToString().Substring(0, len - s.Length);
                return Value.String(padCut + s);
            });
            DefineBuiltin("pad_end", 2, 3, (i, a) =>
            {
                var s = a[0].AsString(); int len = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                var pad = a.Count == 3 ? a[2].AsString() : " ";
                if (pad.Length == 0) pad = " ";
                if (s.Length >= len) return Value.String(s);
                var sb = new StringBuilder(len);
                sb.Append(s);
                while (sb.Length < len) sb.Append(pad);
                return Value.String(sb.ToString().Substring(0, len));
            });

            // === Time ===
            DefineBuiltin("sleep_ms", 1, 1, (i, a) =>
            {
                var ms = (int)ToNumber(a[0]).ToDoubleNV().Dbl;
                System.Threading.Thread.Sleep(ms < 0 ? 0 : ms);
                return Value.Nil();
            });

            // === JSON ===
            DefineBuiltin("json_stringify", 1, 2, (i, a) =>
            {
                bool pretty = a.Count == 2 && Value.IsTruthy(a[1]);
                return Value.String(i.JsonStringifyValue(a[0], pretty));
            });
            DefineBuiltin("json_parse", 1, 1, (i, a) =>
            {
                var s = a[0].AsString();
                // Use Newtonsoft.Json for .NET Framework
                var token = JToken.Parse(s);
                return JsonToValue(token);
            });
            // Simple error/raise builtins
            DefineBuiltin("error", 1, 1, (i, a) =>
            {
                var msg = i.ToStringValue(a[0]);
                return i.MakeError("Error", msg);
            });
            DefineBuiltin("raise", 1, 1, (i, a) =>
            {
                var msg = i.ToStringValue(a[0]);
                var err = i.MakeError("Error", msg);
                throw new ThrowSignal(err);
            });
            // --- Modules: require(path) ---
            DefineBuiltin("require", 1, 1, (i, a) =>
            {
                var spec = a[0].AsString();
                var baseDir = i.GetCallerDirectory();

                var abs = i._moduleLoader.Resolve(spec, baseDir);
                if (string.IsNullOrEmpty(abs))
                    throw new MiniDynRuntimeError($"Cannot resolve module '{spec}' from '{baseDir}'");

                // Cache lookup with loading-state handling
                if (i._moduleCache.TryGetValue(abs, out var cached))
                {
                    // Fully loaded -> return final exports
                    if (!cached.IsLoading && cached.Exports.HasValue)
                        return cached.Exports.Value;

                    // In-progress (cyclic) -> return the pre-seeded temp exports
                    if (cached.IsLoading && cached.TempExports.HasValue)
                        return cached.TempExports.Value;
                }

                // Create or reuse cache entry and mark loading
                if (!i._moduleCache.TryGetValue(abs, out cached))
                {
                    cached = new ModuleCacheEntry();
                    i._moduleCache[abs] = cached;
                }

                string src;
                if (!i._moduleLoader.TryLoad(abs, out src))
                {
                    // Cleanup on load failure
                    i._moduleCache.Remove(abs);
                    throw new MiniDynRuntimeError($"Cannot load module '{abs}'");
                }

                try
                {
                    // Parse
                    var lexer = new Lexer(src, abs);
                    var parser = new Parser(lexer);
                    var stmts = parser.Parse();

                    // Module environment: child of Globals; mark as FunctionEnvironment so 'var' is module-local
                    var moduleEnv = new FunctionEnvironment(i.Globals);

                    // Predefine exports and module
                    var initialExportsObj = new ObjectValue();
                    moduleEnv.DefineVar("exports", Value.Object(initialExportsObj));
                    var moduleObj = new ObjectValue();
                    moduleObj.Set("exports", Value.Object(initialExportsObj));
                    moduleEnv.DefineVar("module", Value.Object(moduleObj));

                    // Seed cache for cyclic dependencies (but do not finalize Exports yet)
                    cached.IsLoading = true;
                    cached.TempExports = Value.Object(initialExportsObj);

                    // Execute program in this environment
                    i.InterpretInEnv(stmts, moduleEnv);

                    // Read final module.exports
                    Value moduleVal;
                    if (!moduleEnv.TryGetHere("module", out moduleVal))
                        moduleVal = Value.Object(moduleObj);

                    Value finalExports = Value.Nil();
                    if (moduleVal.Type == ValueType.Object)
                    {
                        var mo = moduleVal.AsObject();
                        if (!mo.TryGet("exports", out finalExports))
                            finalExports = Value.Object(initialExportsObj);
                    }
                    else
                    {
                        finalExports = Value.Object(initialExportsObj);
                    }

                    // Commit success: finalize cache
                    cached.Exports = finalExports;
                    cached.IsLoading = false;
                    cached.TempExports = null;

                    return finalExports;
                }
                catch
                {
                    // On any failure, clear the cache entry so future require() retries the load
                    i._moduleCache.Remove(abs);
                    throw;
                }
            });
        }

        private string GetCallerDirectory()
        {
            try
            {
                if (_nodeSpanStack.Count > 0)
                {
                    var span = _nodeSpanStack.Peek();
                    var fn = span.FileName;
                    if (!string.IsNullOrEmpty(fn) && fn != "<script>" && fn != "<repl>")
                    {
                        var dir = Path.GetDirectoryName(fn);
                        if (!string.IsNullOrEmpty(dir)) return dir;
                    }
                }
            }
            catch { }
            return Directory.GetCurrentDirectory();
        }

        public void InterpretInEnv(List<Stmt> statements, Environment env)
        {
            var prev = _env;
            try
            {
                _env = env;
                foreach (var s in statements) Execute(s);
            }
            finally { _env = prev; }
        }

        private void DefineBuiltin(string name, int arityMin, int arityMax, Func<Interpreter, List<Value>, Value> fn)
        {
            var f = new BuiltinFunction(name, arityMin, arityMax, fn);
            _builtins[name] = f;
            Globals.DefineVar(name, Value.Function(f));
        }

        public string ToStringValue(Value v) => v.ToString();

        public void Interpret(List<Stmt> statements)
        {
            foreach (var s in statements) Execute(s);
        }

        private void Execute(Stmt s)
        {
            if (s != null && !s.Span.IsEmpty) _nodeSpanStack.Push(s.Span);
            try
            {
                s.Accept(this);
            }
            catch (MiniDynRuntimeError ex)
            {
                AttachErrorContext(ex);
                throw;
            }
            finally
            {
                if (s != null && !s.Span.IsEmpty && _nodeSpanStack.Count > 0) _nodeSpanStack.Pop();
            }
        }

        private Value Evaluate(Expr e)
        {
            if (e != null && !e.Span.IsEmpty) _nodeSpanStack.Push(e.Span);
            try
            {
                return e.Accept(this);
            }
            catch (MiniDynRuntimeError ex)
            {
                AttachErrorContext(ex);
                throw;
            }
            finally
            {
                if (e != null && !e.Span.IsEmpty && _nodeSpanStack.Count > 0) _nodeSpanStack.Pop();
            }
        }

        private void AttachErrorContext(MiniDynRuntimeError ex)
        {
            var span = _nodeSpanStack.Count > 0 ? _nodeSpanStack.Peek() : default(SourceSpan);
            // Materialize frames from top to bottom (current call first)
            var frames = _callStack.Reverse().ToList();
            ex.WithContext(span, frames);
        }
        public Value EvaluateWithEnv(Expr e, Environment env)
        {
            var prev = _env;
            try { _env = env; return Evaluate(e); }
            finally { _env = prev; }
        }

        // Stmt visitor
        public object VisitExpr(Stmt.ExprStmt s)
        {
            Evaluate(s.Expression);
            return null;
        }

        public object VisitVar(Stmt.Var s)
        {
            Value val = s.Initializer != null ? Evaluate(s.Initializer) : Value.Nil();
            // var goes to nearest function/global
            _env.DeclareVarInFunctionOrGlobal(s.Name, val);
            return null;
        }

        public object VisitLet(Stmt.Let s)
        {
            if (s.Initializer != null)
            {
                Value val = Evaluate(s.Initializer);
                _env.DefineLet(s.Name, val);
            }
            else
            {
                // TDZ-like behavior
                _env.DefineLetUninitialized(s.Name);
            }
            return null;
        }

        public object VisitConst(Stmt.Const s)
        {
            Value val = Evaluate(s.Initializer);
            _env.DefineConst(s.Name, val);
            return null;
        }

        public object VisitDeclList(Stmt.DeclList s)
        {
            // Execute declarations sequentially in the current scope (no new block environment).
            foreach (var d in s.Decls) Execute(d);
            return null;
        }

        public object VisitDestructuringDecl(Stmt.DestructuringDecl s)
        {
            var src = Evaluate(s.Initializer);
            // Declare bindings then assign
            void declare(string name, Value v)
            {
                switch (s.DeclKind)
                {
                    case Stmt.DestructuringDecl.Kind.Var:
                        // var pattern bindings go to function/global env
                        _env.DeclareVarInFunctionOrGlobal(name, v);
                        break;
                    case Stmt.DestructuringDecl.Kind.Let:
                        _env.DefineLet(name, v);
                        break;
                    case Stmt.DestructuringDecl.Kind.Const:
                        _env.DefineConst(name, v);
                        break;
                }
            }
            s.Pattern.Bind(this, src, declare, allowConstReassign: false);
            return null;
        }

        public object VisitBlock(Stmt.Block s)
        {
            ExecuteBlock(s.Statements, new Environment(_env));
            return null;
        }

        public void ExecuteBlock(List<Stmt> stmts, Environment env)
        {
            var prev = _env;
            try
            {
                _env = env;
                foreach (var st in stmts) Execute(st);
            }
            finally { _env = prev; }
        }

        public object VisitIf(Stmt.If s)
        {
            if (Value.IsTruthy(Evaluate(s.Condition))) Execute(s.Then);
            else if (s.Else != null) Execute(s.Else);
            return null;
        }

        public object VisitWhile(Stmt.While s)
        {
            _loopDepth++;
            try
            {
                while (Value.IsTruthy(Evaluate(s.Condition)))
                {
                    try
                    {
                        Execute(s.Body);
                    }
                    catch (ContinueSignal)
                    {
                        continue;
                    }
                    catch (BreakSignal)
                    {
                        break;
                    }
                }
                return null;
            }
            finally
            {
                _loopDepth--;
            }
        }

        public object VisitForClassic(Stmt.ForClassic s)
        {
            _loopDepth++;
            try
            {
                // Create a loop-local lexical environment when the initializer declares let/const.
                bool hasBlockScopedInit =
                    s.Initializer is Stmt.Let ||
                    s.Initializer is Stmt.Const ||
                    (s.Initializer is Stmt.DestructuringDecl dd &&
                    (dd.DeclKind == Stmt.DestructuringDecl.Kind.Let || dd.DeclKind == Stmt.DestructuringDecl.Kind.Const));

                var loopEnv = hasBlockScopedInit ? new Environment(_env) : _env;

                if (s.Initializer != null)
                {
                    var prev = _env;
                    try { _env = loopEnv; Execute(s.Initializer); }
                    finally { _env = prev; }
                }

                while (true)
                {
                    if (s.Condition != null)
                    {
                        var prev = _env;
                        try
                        {
                            _env = loopEnv;
                            var c = Evaluate(s.Condition);
                            if (!Value.IsTruthy(c)) break;
                        }
                        finally { _env = prev; }
                    }

                    try
                    {
                        var prev = _env;
                        try { _env = loopEnv; Execute(s.Body); }
                        finally { _env = prev; }
                    }
                    catch (ContinueSignal)
                    {
                        // fallthrough to increment
                    }
                    catch (BreakSignal)
                    {
                        break;
                    }

                    if (s.Increment != null)
                    {
                        var prev = _env;
                        try { _env = loopEnv; Evaluate(s.Increment); }
                        finally { _env = prev; }
                    }
                }
                return null;
            }
            finally
            {
                _loopDepth--;
            }
        }

        // foreach (for-in/of)
        public object VisitForEach(Stmt.ForEach s)
        {
            _loopDepth++;
            try
            {
                var iterable = Evaluate(s.Iterable);

                if (s.IsOf)
                {
                    var values = MaterializeForOf(iterable);
                    foreach (var item in values)
                    {
                        try
                        {
                            ExecuteForEachIteration(s, item);
                        }
                        catch (ContinueSignal)
                        {
                            continue;
                        }
                        catch (BreakSignal)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    var keys = MaterializeForIn(iterable);
                    foreach (var key in keys)
                    {
                        try
                        {
                            ExecuteForEachIteration(s, Value.String(key));
                        }
                        catch (ContinueSignal)
                        {
                            continue;
                        }
                        catch (BreakSignal)
                        {
                            break;
                        }
                    }
                }
                return null;
            }
            finally
            {
                _loopDepth--;
            }
        }

        public object VisitBreak(Stmt.Break s)
        {
            if (_loopDepth == 0)
                throw new MiniDynRuntimeError("break used outside of a loop");
            throw new BreakSignal();
        }

        public object VisitContinue(Stmt.Continue s)
        {
            if (_loopDepth == 0)
                throw new MiniDynRuntimeError("continue used outside of a loop");
            throw new ContinueSignal();
        }

        private void ExecuteForEachIteration(Stmt.ForEach s, Value iterValue)
        {
            // Declaration vs assignment
            if (s.IsDeclaration)
            {
                if (s.DeclKind == Stmt.DestructuringDecl.Kind.Var)
                {
                    // var: function/global scope, same binding reused; redeclare allowed
                    void dec(string name, Value v) => _env.DeclareVarInFunctionOrGlobal(name, v);
                    s.Pattern.Bind(this, iterValue, dec, allowConstReassign: false);
                    // body executes in current env
                    try { Execute(s.Body); }
                    catch (ContinueSignal) { return; }
                    catch (BreakSignal) { throw; }
                }
                else
                {
                    // let/const: fresh block environment per iteration
                    var loopEnv = new Environment(_env);
                    void declare(string name, Value v)
                    {
                        if (s.DeclKind == Stmt.DestructuringDecl.Kind.Let) loopEnv.DefineLet(name, v);
                        else loopEnv.DefineConst(name, v);
                    }
                    s.Pattern.Bind(this, iterValue, declare, allowConstReassign: false);
                    try { ExecuteBlock((s.Body as Stmt.Block)?.Statements ?? new List<Stmt> { s.Body }, loopEnv); }
                    catch (ContinueSignal) { return; }
                    catch (BreakSignal) { throw; }
                }
            }
            else
            {
                // Assignment into existing lvalues/bindings
                void assignLocal(string name, Value v) => _env.Assign(name, v);
                s.Pattern.Bind(this, iterValue, assignLocal, allowConstReassign: true);
                try { Execute(s.Body); }
                catch (ContinueSignal) { return; }
                catch (BreakSignal) { throw; }
            }
        }

        private List<Value> MaterializeForOf(Value v)
        {
            switch (v.Type)
            {
                case ValueType.Array:
                    return new List<Value>(v.AsArray().Items);
                case ValueType.String:
                    {
                        var s = v.AsString();
                        var list = new List<Value>(s.Length);
                        for (int i = 0; i < s.Length; i++)
                            list.Add(Value.String(s[i].ToString()));
                        return list;
                    }
                case ValueType.Object:
                    {
                        var list = new List<Value>();
                        foreach (var kv in v.AsObject().Entries)
                            list.Add(kv.Value);
                        return list;
                    }
                case ValueType.Nil:
                    return new List<Value>();
                default:
                    throw new MiniDynRuntimeError("Value is not iterable for 'for ... of'");
            }
        }

        private List<string> MaterializeForIn(Value v)
        {
            switch (v.Type)
            {
                case ValueType.Object:
                    return v.AsObject().Keys.ToList();
                case ValueType.Array:
                    {
                        var arr = v.AsArray();
                        var list = new List<string>(arr.Length);
                        for (int i = 0; i < arr.Length; i++) list.Add(i.ToString(CultureInfo.InvariantCulture));
                        return list;
                    }
                case ValueType.String:
                    {
                        var s = v.AsString();
                        var list = new List<string>(s.Length);
                        for (int i = 0; i < s.Length; i++) list.Add(i.ToString(CultureInfo.InvariantCulture));
                        return list;
                    }
                case ValueType.Nil:
                    return new List<string>();
                default:
                    throw new MiniDynRuntimeError("Value is not indexable for 'for ... in'");
            }
        }

        // Evaluate an expression that is in tail position of a return.
        // If it is (or reduces to) a direct self tail-call, this will throw TailCallSignal to trampoline.
        // Otherwise it evaluates and returns the value.
        private Value EvaluateTailPosition(Expr e)
        {
            // Unwrap groupings
            if (e is Expr.Grouping g)
                return EvaluateTailPosition(g.Inner);

            // Comma: evaluate left for side-effects, then tail-eval right
            if (e is Expr.Comma cm)
            {
                Evaluate(cm.Left);
                return EvaluateTailPosition(cm.Right);
            }

            // Ternary: short-circuit which branch to evaluate in tail position
            if (e is Expr.Ternary t)
            {
                var cond = Evaluate(t.Cond);
                if (Value.IsTruthy(cond)) return EvaluateTailPosition(t.Then);
                return EvaluateTailPosition(t.Else);
            }

            // Logical AND/OR: short-circuit and tail-eval the right side if needed
            if (e is Expr.Logical l)
            {
                var left = Evaluate(l.Left);
                if (l.Op.Type == TokenType.Or)
                {
                    if (Value.IsTruthy(left)) return left;
                    return EvaluateTailPosition(l.Right);
                }
                else
                {
                    if (!Value.IsTruthy(left)) return left;
                    return EvaluateTailPosition(l.Right);
                }
            }

            // Nullish coalescing in AST as Binary with NullishCoalesce
            if (e is Expr.Binary b && b.Op.Type == TokenType.NullishCoalesce)
            {
                var left = Evaluate(b.Left);
                if (left.Type != ValueType.Nil) return left;
                return EvaluateTailPosition(b.Right);
            }

            // Direct call in tail position -> use TCO path (including method/optional call cases)
            if (e is Expr.Call callExpr)
            {
                return EvaluateTailCallOrInvoke(callExpr);
            }

            // Fallback: just evaluate normally
            return Evaluate(e);
        }

        // Execute a call in tail position:
        // - If it's a self tail-call to the current UserFunction, throw TailCallSignal (positional or mapping).
        // - Otherwise, perform the call and return its result.
        private Value EvaluateTailCallOrInvoke(Expr.Call callExpr)
        {
            var currentFn = CurrentFunction;

            // Evaluate callee once, capture receiver for method-call binding (and optional chaining short-circuit)
            Value calleeVal; Value receiver; bool shortCircuitToNil;
            TryPrepareCalleeForCall(callExpr.Callee, out calleeVal, out receiver, out shortCircuitToNil);

            if (shortCircuitToNil)
                return Value.Nil();

            if (calleeVal.Type != ValueType.Function)
                throw new MiniDynRuntimeError("Can only call functions");

            var fn = calleeVal.AsFunction();

            // Self tail-call to same UserFunction -> trampoline
            if (currentFn != null && fn is UserFunction targetFn && targetFn.FunctionId == currentFn.FunctionId)
            {
                if (callExpr.Args.Any(a => a.IsNamed))
                {
                    var mapping = BuildArgMapping(ToParamViews(currentFn.Params), callExpr.Args);
                    throw new TailCallSignal(currentFn, mapping);
                }
                else
                {
                    var finalArgs = new List<Value>();
                    foreach (var a in callExpr.Args) finalArgs.Add(Evaluate(a.Value));
                    throw new TailCallSignal(currentFn, finalArgs);
                }
            }

            // Not a self tail-call: perform the call and return its result, preserving receiver binding
            if (fn is UserFunction userFn2)
            {
                if (callExpr.Args.Any(a => a.IsNamed))
                {
                    var mapping = BuildArgMapping(ToParamViews(userFn2.Params), callExpr.Args);
                    if (receiver.Type != ValueType.Nil && userFn2.FunctionKind == UserFunction.Kind.Normal)
                        fn = userFn2.BindThis(receiver);

                    string fnName = !string.IsNullOrEmpty(userFn2.Name) ? userFn2.Name : "<anonymous>";
                    var callSite = callExpr?.Span ?? default(SourceSpan);
                    _callStack.Push(new CallFrame(fnName, callSite));
                    try
                    {
                        return ((UserFunction)fn).CallWithMapping(this, mapping);
                    }
                    catch (MiniDynRuntimeError ex)
                    {
                        AttachErrorContext(ex);
                        throw;
                    }
                    finally
                    {
                        _callStack.Pop();
                    }
                }
                else
                {
                    var processedArgs = new List<Value>();
                    foreach (var a in callExpr.Args) processedArgs.Add(Evaluate(a.Value));

                    if (receiver.Type != ValueType.Nil && userFn2.FunctionKind == UserFunction.Kind.Normal)
                        fn = userFn2.BindThis(receiver);

                    if (processedArgs.Count < fn.ArityMin || processedArgs.Count > fn.ArityMax)
                        throw new MiniDynRuntimeError($"Function {fn} expected {fn.ArityMin}..{(fn.ArityMax == int.MaxValue ? "∞" : fn.ArityMax.ToString())} args, got {processedArgs.Count}");

                    string fnName = !string.IsNullOrEmpty(userFn2.Name) ? userFn2.Name : "<anonymous>";
                    var callSite = callExpr?.Span ?? default(SourceSpan);
                    _callStack.Push(new CallFrame(fnName, callSite));
                    try
                    {
                        return fn.Call(this, processedArgs);
                    }
                    catch (MiniDynRuntimeError ex)
                    {
                        AttachErrorContext(ex);
                        throw;
                    }
                    finally
                    {
                        _callStack.Pop();
                    }
                }
            }
            else if (fn is BytecodeFunction byteFn2)
            {
                List<Value> processedArgs;
                if (callExpr.Args.Any(a => a.IsNamed) || (byteFn2.Params?.Any(p => p.Default != null || p.IsRest) ?? false))
                    processedArgs = BuildPositionalArgsForBytecode(byteFn2.Params, callExpr.Args);
                else
                {
                    processedArgs = new List<Value>();
                    foreach (var a in callExpr.Args) processedArgs.Add(Evaluate(a.Value));
                }

                if (receiver.Type != ValueType.Nil && byteFn2.FunctionKind == UserFunction.Kind.Normal)
                    fn = byteFn2.BindThis(receiver);

                if (processedArgs.Count < fn.ArityMin || processedArgs.Count > fn.ArityMax)
                    throw new MiniDynRuntimeError($"Function {fn} expected {fn.ArityMin}..{(fn.ArityMax == int.MaxValue ? "∞" : fn.ArityMax.ToString())} args, got {processedArgs.Count}");

                string fnName = "<anonymous>";
                var callSite = callExpr?.Span ?? default(SourceSpan);
                _callStack.Push(new CallFrame(fnName, callSite));
                try
                {
                    return fn.Call(this, processedArgs);
                }
                catch (MiniDynRuntimeError ex)
                {
                    AttachErrorContext(ex);
                    throw;
                }
                finally
                {
                    _callStack.Pop();
                }
            }
            else
            {
                if (callExpr.Args.Any(a => a.IsNamed))
                    throw new MiniDynRuntimeError("Built-in functions do not support named arguments");

                var processedArgs = new List<Value>();
                foreach (var a in callExpr.Args) processedArgs.Add(Evaluate(a.Value));

                if (processedArgs.Count < fn.ArityMin || processedArgs.Count > fn.ArityMax)
                    throw new MiniDynRuntimeError($"Function {fn} expected {fn.ArityMin}..{(fn.ArityMax == int.MaxValue ? "∞" : fn.ArityMax.ToString())} args, got {processedArgs.Count}");

                string fnName = fn is BuiltinFunction bf ? bf.Name : "<anonymous>";
                var callSite = callExpr?.Span ?? default(SourceSpan);
                _callStack.Push(new CallFrame(fnName, callSite));
                try
                {
                    return fn.Call(this, processedArgs);
                }
                catch (MiniDynRuntimeError ex)
                {
                    AttachErrorContext(ex);
                    throw;
                }
                finally
                {
                    _callStack.Pop();
                }
            }
        }

        public object VisitFunction(Stmt.Function s)
        {
            var kind = s.FuncExpr.IsArrow ? UserFunction.Kind.Arrow : UserFunction.Kind.Normal;
            var fn = new UserFunction(s.Name, s.FuncExpr.Parameters, s.FuncExpr.Body, _env, kind, null, s.FuncExpr.Span);
            // function statements bind like 'var' in the nearest function/global env
            _env.DeclareVarInFunctionOrGlobal(s.Name, Value.Function(fn));
            return null;
        }

        public object VisitReturn(Stmt.Return s)
        {
            // Tail-position evaluation supporting calls nested in ternary/logical/nullish/comma/grouping.
            if (s.Value == null)
                throw new ReturnSignal(Value.Nil());

            var result = EvaluateTailPosition(s.Value);
            throw new ReturnSignal(result);
        }
        public object VisitThrow(Stmt.Throw s)
        {
            var v = Evaluate(s.Value);
            throw new ThrowSignal(v);
        }
        public object VisitTryCatchFinally(Stmt.TryCatchFinally s)
        {
            Value caught = Value.Nil();
            try
            {
                // Execute the try as a block (keeps normal block scoping)
                Execute(s.Try);
            }
            catch (ThrowSignal ts)
            {
                if (s.Catch != null)
                {
                    caught = ts.Value;
                    var catchEnv = new Environment(_env);
                    if (!string.IsNullOrEmpty(s.CatchName))
                        catchEnv.DefineLet(s.CatchName, caught);
                    ExecuteBlock(s.Catch.Statements, catchEnv);
                }
                else
                {
                    throw;
                }
            }
            catch (MiniDynRuntimeError ex)
            {
                // Bubble runtime errors as values into catch as a simple Error object
                if (s.Catch != null)
                {
                    var errVal = MakeError("RuntimeError", ex.Message, ex);
                    caught = errVal;
                    var catchEnv = new Environment(_env);
                    if (!string.IsNullOrEmpty(s.CatchName))
                        catchEnv.DefineLet(s.CatchName, errVal);
                    ExecuteBlock(s.Catch.Statements, catchEnv);
                }
                else
                {
                    throw;
                }
            }
            finally
            {
                if (s.Finally != null)
                {
                    Execute(s.Finally);
                }
            }
            return null;
        }

        // Expr visitor
        public Value VisitLiteral(Expr.Literal e) => e.Value;

        public Value VisitVariable(Expr.Variable e)
        {
            return _env.Get(e.Name);
        }

        public Value VisitAssign(Expr.Assign e)
        {
            // Helper to apply compound op (non-nullish-assign)
            Value ApplyOp(Value cur, TokenType op, Value rhsVal)
            {
                switch (op)
                {
                    case TokenType.Assign: return rhsVal;
                    case TokenType.PlusAssign:
                        if (cur.Type == ValueType.Number && rhsVal.Type == ValueType.Number)
                            return Value.Number(NumberValue.Add(cur.AsNumber(), rhsVal.AsNumber()));
                        if (cur.Type == ValueType.String || rhsVal.Type == ValueType.String)
                            return Value.String(ToStringValue(cur) + ToStringValue(rhsVal));
                        if (cur.Type == ValueType.Array && rhsVal.Type == ValueType.Array)
                        {
                            var la = cur.AsArray();
                            var ra = rhsVal.AsArray();
                            var res = new ArrayValue();
                            res.Items.AddRange(la.Items);
                            res.Items.AddRange(ra.Items);
                            return Value.Array(res);
                        }
                        throw new MiniDynRuntimeError("Invalid '+=' operands");
                    case TokenType.MinusAssign:
                        return Value.Number(NumberValue.Sub(ToNumber(cur), ToNumber(rhsVal)));
                    case TokenType.StarAssign:
                        return Value.Number(NumberValue.Mul(ToNumber(cur), ToNumber(rhsVal)));
                    case TokenType.SlashAssign:
                        return Value.Number(NumberValue.Div(ToNumber(cur), ToNumber(rhsVal)));
                    case TokenType.PercentAssign:
                        return Value.Number(NumberValue.Mod(ToNumber(cur), ToNumber(rhsVal)));
                    default:
                        throw new MiniDynRuntimeError("Unknown compound assignment");
                }
            }

            // Variable
            if (e.Target is Expr.Variable v)
            {
                var cur = _env.Get(v.Name);
                Value newVal;
                if (e.Op.Type == TokenType.NullishAssign)
                {
                    if (cur.Type == ValueType.Nil)
                    {
                        var rhs = Evaluate(e.Value);
                        newVal = rhs;
                        _env.Assign(v.Name, newVal);
                    }
                    else
                    {
                        newVal = cur; // no-op
                    }
                    return newVal;
                }
                else
                {
                    var rhs = Evaluate(e.Value);
                    newVal = ApplyOp(cur, e.Op.Type, rhs);
                    _env.Assign(v.Name, newVal);
                    return newVal;
                }
            }
            // Property (supports optional chaining short-circuit)
            else if (e.Target is Expr.Property p)
            {
                var objVal = Evaluate(p.Target);

                if (p.IsOptional && objVal.Type == ValueType.Nil)
                {
                    // Short-circuit: do not evaluate RHS; no assignment
                    return Value.Nil();
                }

                if (objVal.Type != ValueType.Object)
                    throw new MiniDynRuntimeError("Property assignment target must be object");

                var obj = objVal.AsObject();
                obj.TryGet(p.Name, out var cur); // cur = nil if missing

                Value newVal;
                if (e.Op.Type == TokenType.NullishAssign)
                {
                    if (cur.Type == ValueType.Nil)
                    {
                        var rhs = Evaluate(e.Value);
                        newVal = rhs;
                        obj.Set(p.Name, newVal);
                    }
                    else
                    {
                        newVal = cur; // no-op
                    }
                    return newVal;
                }
                else
                {
                    var rhs = Evaluate(e.Value);
                    newVal = ApplyOp(cur, e.Op.Type, rhs);
                    obj.Set(p.Name, newVal);
                    return newVal;
                }
            }
            // Index (supports optional chaining short-circuit)
            else if (e.Target is Expr.Index idx)
            {
                var target = Evaluate(idx.Target);

                if (idx.IsOptional && target.Type == ValueType.Nil)
                {
                    // Short-circuit: do not evaluate index or RHS; no assignment
                    return Value.Nil();
                }

                var idxV = Evaluate(idx.IndexExpr);

                if (target.Type == ValueType.Array)
                {
                    var arr = target.AsArray();
                    int i = (int)ToNumber(idxV).ToDoubleNV().Dbl;
                    i = NormalizeIndex(i, arr.Length);
                    if (i < 0 || i >= arr.Length) throw new MiniDynRuntimeError("Array index out of range");
                    var cur = arr[i];

                    Value newVal;
                    if (e.Op.Type == TokenType.NullishAssign)
                    {
                        if (cur.Type == ValueType.Nil)
                        {
                            var rhs = Evaluate(e.Value);
                            newVal = rhs;
                            arr[i] = newVal;
                        }
                        else
                        {
                            newVal = cur;
                        }
                        return newVal;
                    }
                    else
                    {
                        var rhs = Evaluate(e.Value);
                        newVal = ApplyOp(cur, e.Op.Type, rhs);
                        arr[i] = newVal;
                        return newVal;
                    }
                }
                else if (target.Type == ValueType.Object)
                {
                    var obj = target.AsObject();
                    var key = ToStringValue(idxV);
                    obj.TryGet(key, out var cur); // cur = nil if missing

                    Value newVal;
                    if (e.Op.Type == TokenType.NullishAssign)
                    {
                        if (cur.Type == ValueType.Nil)
                        {
                            var rhs = Evaluate(e.Value);
                            newVal = rhs;
                            obj.Set(key, newVal);
                        }
                        else
                        {
                            newVal = cur;
                        }
                        return newVal;
                    }
                    else
                    {
                        var rhs = Evaluate(e.Value);
                        newVal = ApplyOp(cur, e.Op.Type, rhs);
                        obj.Set(key, newVal);
                        return newVal;
                    }
                }
                else if (target.Type == ValueType.String)
                {
                    throw new MiniDynRuntimeError("Cannot assign into string by index");
                }
                throw new MiniDynRuntimeError("Index assignment target must be array or object");
            }
            else
            {
                throw new MiniDynRuntimeError("Invalid assignment target");
            }
        }

        public Value VisitUnary(Expr.Unary e)
        {
            var r = Evaluate(e.Right);
            switch (e.Op.Type)
            {
                case TokenType.Minus: return Value.Number(NumberValue.Neg(RuntimeOps.ToNumber(r)));
                case TokenType.Plus: return r.Type == ValueType.Number ? r : Value.Number(RuntimeOps.ToNumber(r));
                case TokenType.Not: return Value.Boolean(!Value.IsTruthy(r));
                default: throw new MiniDynRuntimeError("Unknown unary");
            }
        }

        public Value VisitBinary(Expr.Binary e)
        {
            // Evaluate with per-op short-circuit where needed
            switch (e.Op.Type)
            {
                case TokenType.Plus:
                case TokenType.Minus:
                case TokenType.Star:
                case TokenType.Slash:
                case TokenType.Percent:
                case TokenType.Equal:
                case TokenType.NotEqual:
                case TokenType.Less:
                case TokenType.LessEq:
                case TokenType.Greater:
                case TokenType.GreaterEq:
                    {
                        var l = Evaluate(e.Left);
                        var r = Evaluate(e.Right);
                        switch (e.Op.Type)
                        {
                            case TokenType.Plus:
                                if (l.Type == ValueType.Number && r.Type == ValueType.Number)
                                    return Value.Number(NumberValue.Add(l.AsNumber(), r.AsNumber()));
                                if (l.Type == ValueType.String || r.Type == ValueType.String)
                                    return Value.String(ToStringValue(l) + ToStringValue(r));
                                if (l.Type == ValueType.Array && r.Type == ValueType.Array)
                                {
                                    var la = l.AsArray();
                                    var ra = r.AsArray();
                                    var res = new ArrayValue();
                                    res.Items.AddRange(la.Items);
                                    res.Items.AddRange(ra.Items);
                                    return Value.Array(res);
                                }
                                throw new MiniDynRuntimeError("Invalid '+' operands");
                            case TokenType.Minus: return Value.Number(NumberValue.Sub(ToNumber(l), ToNumber(r)));
                            case TokenType.Star: return Value.Number(NumberValue.Mul(ToNumber(l), ToNumber(r)));
                            case TokenType.Slash: return Value.Number(NumberValue.Div(ToNumber(l), ToNumber(r)));
                            case TokenType.Percent: return Value.Number(NumberValue.Mod(ToNumber(l), ToNumber(r)));
                            case TokenType.Equal: return Value.Boolean(RuntimeOps.CompareEq(l, r));
                            case TokenType.NotEqual: return Value.Boolean(!RuntimeOps.CompareEq(l, r));
                            case TokenType.Less: return Value.Boolean(RuntimeOps.CompareRel(l, r, "<"));
                            case TokenType.LessEq: return Value.Boolean(RuntimeOps.CompareRel(l, r, "<="));
                            case TokenType.Greater: return Value.Boolean(RuntimeOps.CompareRel(l, r, ">"));
                            case TokenType.GreaterEq: return Value.Boolean(RuntimeOps.CompareRel(l, r, ">="));
                        }
                        break;
                    }
                case TokenType.NullishCoalesce:
                    {
                        var l = Evaluate(e.Left);
                        if (l.Type != ValueType.Nil) return l;
                        return Evaluate(e.Right);
                    }
                default:
                    throw new MiniDynRuntimeError("Unknown binary op");
            }
            throw new MiniDynRuntimeError("Unknown binary op");
        }

        public Value VisitLogical(Expr.Logical e)
        {
            var left = Evaluate(e.Left);
            if (e.Op.Type == TokenType.Or)
            {
                if (Value.IsTruthy(left)) return left;
                return Evaluate(e.Right);
            }
            else
            {
                if (!Value.IsTruthy(left)) return left;
                return Evaluate(e.Right);
            }
        }

        public Value VisitCall(Expr.Call e)
        {
            Value receiver = Value.Nil();
            Value calleeVal;

            // Detect optional-chain short-circuit on callee (property/index)
            if (e.Callee is Expr.Property prop)
            {
                var objVal = Evaluate(prop.Target);
                if (prop.IsOptional && objVal.Type == ValueType.Nil) return Value.Nil(); // skip args, return nil
                if (objVal.Type != ValueType.Object) throw new MiniDynRuntimeError("Property access target must be object");
                var obj = objVal.AsObject();
                obj.TryGet(prop.Name, out calleeVal);
                receiver = objVal;
            }
            else if (e.Callee is Expr.Index idx)
            {
                var objVal = Evaluate(idx.Target);
                if (idx.IsOptional && objVal.Type == ValueType.Nil) return Value.Nil(); // skip args, return nil
                if (objVal.Type != ValueType.Object) throw new MiniDynRuntimeError("Index access target must be object for method call");
                var key = ToStringValue(Evaluate(idx.IndexExpr));
                var obj = objVal.AsObject();
                obj.TryGet(key, out calleeVal);
                receiver = objVal;
            }
            else
            {
                calleeVal = Evaluate(e.Callee);
            }

            if (calleeVal.Type != ValueType.Function)
                throw new MiniDynRuntimeError("Can only call functions");

            var fn = calleeVal.AsFunction();

            // UserFunction: use mapping for named-arg calls (defaults evaluated in callee env)
            if (fn is UserFunction userFn)
            {
                if (e.Args.Any(a => a.IsNamed))
                {
                    var mapping = BuildArgMapping(ToParamViews(userFn.Params), e.Args);

                    // Bind 'this' if receiver provided and normal function
                    var callTarget = (receiver.Type != ValueType.Nil && userFn.FunctionKind == UserFunction.Kind.Normal)
                        ? userFn.BindThis(receiver)
                        : userFn;

                    string fnName = !string.IsNullOrEmpty(userFn.Name) ? userFn.Name : "<anonymous>";
                    var callSite = e?.Span ?? default(SourceSpan);
                    _callStack.Push(new CallFrame(fnName, callSite));
                    try
                    {
                        return callTarget.CallWithMapping(this, mapping);
                    }
                    catch (MiniDynRuntimeError ex)
                    {
                        AttachErrorContext(ex);
                        throw;
                    }
                    finally
                    {
                        _callStack.Pop();
                    }
                }
                else
                {
                    // positional-only path unchanged
                    var argsList = new List<Value>();
                    foreach (var arg in e.Args) argsList.Add(Evaluate(arg.Value));

                    // Bind 'this' if needed
                    if (receiver.Type != ValueType.Nil && userFn.FunctionKind == UserFunction.Kind.Normal)
                        fn = userFn.BindThis(receiver);

                    if (argsList.Count < fn.ArityMin || argsList.Count > fn.ArityMax)
                        throw new MiniDynRuntimeError($"Function {fn} expected {fn.ArityMin}..{(fn.ArityMax == int.MaxValue ? "∞" : fn.ArityMax.ToString())} args, got {argsList.Count}");

                    string fnName =
                        !string.IsNullOrEmpty(userFn.Name) ? userFn.Name : "<anonymous>";
                    var callSite = e?.Span ?? default(SourceSpan);
                    _callStack.Push(new CallFrame(fnName, callSite));
                    try
                    {
                        return fn.Call(this, argsList);
                    }
                    catch (MiniDynRuntimeError ex)
                    {
                        AttachErrorContext(ex);
                        throw;
                    }
                    finally
                    {
                        _callStack.Pop();
                    }
                }
            }

            // BytecodeFunction: keep named-arg support by mapping to positional (no defaults/rest)
            if (fn is BytecodeFunction byteFn)
            {
                List<Value> processedArgs;
                if (e.Args.Any(a => a.IsNamed) || (byteFn.Params?.Any(p => p.Default != null || p.IsRest) ?? false))
                {
                    processedArgs = BuildPositionalArgsForBytecode(byteFn.Params, e.Args);
                }
                else
                {
                    processedArgs = new List<Value>();
                    foreach (var arg in e.Args) processedArgs.Add(Evaluate(arg.Value));
                }

                if (receiver.Type != ValueType.Nil && byteFn.FunctionKind == UserFunction.Kind.Normal)
                    fn = byteFn.BindThis(receiver);

                if (processedArgs.Count < fn.ArityMin || processedArgs.Count > fn.ArityMax)
                    throw new MiniDynRuntimeError($"Function {fn} expected {fn.ArityMin}..{(fn.ArityMax == int.MaxValue ? "∞" : fn.ArityMax.ToString())} args, got {processedArgs.Count}");

                string fnName =
                    fn is BuiltinFunction bf ? bf.Name :
                    "<anonymous>";
                var callSite = e?.Span ?? default(SourceSpan);
                _callStack.Push(new CallFrame(fnName, callSite));
                try
                {
                    return fn.Call(this, processedArgs);
                }
                catch (MiniDynRuntimeError ex)
                {
                    AttachErrorContext(ex);
                    throw;
                }
                finally
                {
                    _callStack.Pop();
                }
            }

            // Builtins: do not support named args
            if (e.Args.Any(a => a.IsNamed))
                throw new MiniDynRuntimeError("Built-in functions do not support named arguments");

            var processedArgs2 = new List<Value>();
            foreach (var arg in e.Args) processedArgs2.Add(Evaluate(arg.Value));

            string fnName2 =
                fn is BuiltinFunction bf2 ? bf2.Name :
                "<anonymous>";
            var callSite2 = e?.Span ?? default(SourceSpan);
            _callStack.Push(new CallFrame(fnName2, callSite2));
            try
            {
                return fn.Call(this, processedArgs2);
            }
            catch (MiniDynRuntimeError ex)
            {
                AttachErrorContext(ex);
                throw;
            }
            finally
            {
                _callStack.Pop();
            }
        }

        // Helper used by VisitReturn to evaluate a call's callee once,
        // capturing receiver for method calls and honoring optional chaining short-circuit.
        private bool TryPrepareCalleeForCall(Expr calleeExpr, out Value calleeVal, out Value receiver, out bool shortCircuitToNil)
        {
            receiver = Value.Nil();
            shortCircuitToNil = false;

            if (calleeExpr is Expr.Property prop)
            {
                var objVal = Evaluate(prop.Target);
                if (prop.IsOptional && objVal.Type == ValueType.Nil)
                {
                    calleeVal = Value.Nil();
                    shortCircuitToNil = true;
                    return true;
                }
                if (objVal.Type != ValueType.Object) throw new MiniDynRuntimeError("Property access target must be object");
                var obj = objVal.AsObject();
                obj.TryGet(prop.Name, out calleeVal);
                receiver = objVal;
                return true;
            }
            else if (calleeExpr is Expr.Index idx)
            {
                var objVal = Evaluate(idx.Target);
                if (idx.IsOptional && objVal.Type == ValueType.Nil)
                {
                    calleeVal = Value.Nil();
                    shortCircuitToNil = true;
                    return true;
                }
                if (objVal.Type != ValueType.Object) throw new MiniDynRuntimeError("Index access target must be object for method call");
                var key = ToStringValue(Evaluate(idx.IndexExpr));
                var obj = objVal.AsObject();
                obj.TryGet(key, out calleeVal);
                receiver = objVal;
                return true;
            }
            else
            {
                calleeVal = Evaluate(calleeExpr);
                return true;
            }
        }

        // Unified parameter view over UserFunction.ParamSpec and Expr.Param
        private struct ParamView
        {
            public string Name;
            public Expr Default;
            public bool IsRest;
        }

        private static List<ParamView> ToParamViews(IReadOnlyList<UserFunction.ParamSpec> ps)
        {
            var list = new List<ParamView>(ps.Count);
            for (int i = 0; i < ps.Count; i++)
                list.Add(new ParamView { Name = ps[i].Name, Default = ps[i].Default, IsRest = ps[i].IsRest });
            return list;
        }

        private static List<ParamView> ToParamViews(IReadOnlyList<Expr.Param> ps)
        {
            var list = new List<ParamView>(ps.Count);
            for (int i = 0; i < ps.Count; i++)
                list.Add(new ParamView { Name = ps[i].Name, Default = ps[i].Default, IsRest = ps[i].IsRest });
            return list;
        }

        // Build mapping for named/positional args WITHOUT evaluating defaults.
        private ArgMapping BuildArgMapping(IReadOnlyList<ParamView> parameters, List<Expr.Call.Argument> args)
        {
            int paramCount = parameters?.Count ?? 0;
            var map = new ArgMapping
            {
                Values = new Value[paramCount],
                Filled = new bool[paramCount],
                Rest = new List<Value>()
            };

            // name->index only for non-rest params
            var nameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            int restIndex = -1;
            string restName = null;
            for (int i = 0; i < paramCount; i++)
            {
                if (parameters[i].IsRest) { restIndex = i; restName = parameters[i].Name; continue; }
                nameToIndex[parameters[i].Name] = i;
            }

            // helper: find next non-rest, unfilled parameter slot starting at cursor
            int cursor = 0;
            int NextNonRestUnfilled()
            {
                for (int i = cursor; i < paramCount; i++)
                {
                    if (!parameters[i].IsRest && !map.Filled[i]) { cursor = i + 1; return i; }
                }
                // also scan earlier ones in case cursor skipped something (e.g., named filled ahead)
                for (int i = 0; i < cursor; i++)
                {
                    if (!parameters[i].IsRest && !map.Filled[i]) { cursor = i + 1; return i; }
                }
                return -1;
            }

            bool seenNamed = false;

            // Evaluate and assign in left-to-right order
            foreach (var arg in args)
            {
                if (arg.IsNamed)
                {
                    seenNamed = true;

                    // Disallow binding rest parameter by name
                    if (restIndex >= 0 && string.Equals(arg.Name, restName, StringComparison.Ordinal))
                        throw new MiniDynRuntimeError($"Cannot bind rest parameter '{arg.Name}' by name");

                    if (!nameToIndex.TryGetValue(arg.Name, out int idx))
                        throw new MiniDynRuntimeError($"Unknown parameter '{arg.Name}'");

                    if (map.Filled[idx])
                        throw new MiniDynRuntimeError($"Argument '{arg.Name}' specified multiple times");

                    map.Values[idx] = Evaluate(arg.Value);
                    map.Filled[idx] = true;
                }
                else
                {
                    var val = Evaluate(arg.Value);

                    if (seenNamed)
                    {
                        if (restIndex >= 0)
                        {
                            map.Rest.Add(val);
                        }
                        else
                        {
                            throw new MiniDynRuntimeError("Positional arguments after named are not allowed");
                        }
                    }
                    else
                    {
                        int idx = NextNonRestUnfilled();
                        if (idx >= 0)
                        {
                            map.Values[idx] = val;
                            map.Filled[idx] = true;
                        }
                        else if (restIndex >= 0)
                        {
                            map.Rest.Add(val);
                        }
                        else
                        {
                            // too many positional args and no rest
                            int totalProvided = args.Count;
                            throw new MiniDynRuntimeError($"Function expected at most {paramCount} args, got {totalProvided}");
                        }
                    }
                }
            }

            return map;
        }

        // Build a positional list for bytecode fns (no defaults/rest supported).
        private List<Value> BuildPositionalArgsForBytecode(IReadOnlyList<Expr.Param> parameters, List<Expr.Call.Argument> args)
        {
            var map = BuildArgMapping(ToParamViews(parameters), args);
            // Ensure no rest and all non-rest filled
            if (parameters.Any(p => p.IsRest))
                throw new MiniDynRuntimeError("Bytecode function: rest params are not supported");
            for (int i = 0; i < parameters.Count; i++)
            {
                if (!map.Filled[i])
                    throw new MiniDynRuntimeError($"Missing required argument '{parameters[i].Name}'");
            }
            if (map.Rest.Count != 0)
                throw new MiniDynRuntimeError($"Function expected at most {parameters.Count} args, got more");

            var list = new List<Value>(parameters.Count);
            for (int i = 0; i < parameters.Count; i++) list.Add(map.Values[i]);
            return list;
        }

        public Value VisitGrouping(Expr.Grouping e) => Evaluate(e.Inner);

        public Value VisitFunction(Expr.Function e)
        {
            // Try fast bytecode path for simple expression-bodied functions
            var compiler = new BytecodeCompiler(this);
            if (compiler.TryCompileFunction(e, out var bcFn))
                return Value.Function(bcFn);

            // Fallback to existing user function
            Value? capturedThis = null;
            Value t;
            if (e.IsArrow && _env.TryGet("this", out t)) capturedThis = t;
            var kind = e.IsArrow ? UserFunction.Kind.Arrow : UserFunction.Kind.Normal;
            var uf = new UserFunction(null, e.Parameters, e.Body, _env, kind, capturedThis, e.Span);
            return Value.Function(uf);
        }

        public Value VisitTernary(Expr.Ternary e)
        {
            var c = Evaluate(e.Cond);
            if (Value.IsTruthy(c)) return Evaluate(e.Then);
            return Evaluate(e.Else);
        }

        public Value VisitIndex(Expr.Index e)
        {
            var target = Evaluate(e.Target);
            if (e.IsOptional && target.Type == ValueType.Nil) return Value.Nil();

            if (target.Type == ValueType.Array)
            {
                int idx = (int)RuntimeOps.ToNumber(Evaluate(e.IndexExpr)).ToDoubleNV().Dbl;
                var arr = target.AsArray();
                idx = RuntimeOps.NormalizeIndex(idx, arr.Length);
                if (idx < 0 || idx >= arr.Length) throw new MiniDynRuntimeError("Array index out of range");
                return arr[idx];
            }
            if (target.Type == ValueType.String)
            {
                var s = target.AsString();
                int idx = (int)RuntimeOps.ToNumber(Evaluate(e.IndexExpr)).ToDoubleNV().Dbl;
                idx = RuntimeOps.NormalizeIndex(idx, s.Length);
                if (idx < 0 || idx >= s.Length) throw new MiniDynRuntimeError("String index out of range");
                return Value.String(s[idx].ToString());
            }
            if (target.Type == ValueType.Object)
            {
                var keyVal = Evaluate(e.IndexExpr);
                var key = ToStringValue(keyVal);
                var obj = target.AsObject();
                if (obj.TryGet(key, out var val)) return val;
                return Value.Nil();
            }
            throw new MiniDynRuntimeError("Indexing supported only on arrays, strings, or objects");
        }

        public Value VisitArrayLiteral(Expr.ArrayLiteral e)
        {
            var arr = new ArrayValue();
            foreach (var el in e.Elements) arr.Items.Add(Evaluate(el));
            return Value.Array(arr);
        }

        public Value VisitObjectLiteral(Expr.ObjectLiteral e)
        {
            var objv = new ObjectValue();
            foreach (var entry in e.Entries)
            {
                string key;
                if (entry.KeyName != null)
                {
                    key = entry.KeyName;
                }
                else
                {
                    var kVal = Evaluate(entry.KeyExpr);
                    key = ToStringValue(kVal);
                }
                var val = Evaluate(entry.ValueExpr);
                objv.Set(key, val);
            }
            return Value.Object(objv);
        }

        public Value VisitProperty(Expr.Property e)
        {
            var objv = Evaluate(e.Target);
            if (e.IsOptional && objv.Type == ValueType.Nil) return Value.Nil();
            if (objv.Type != ValueType.Object) throw new MiniDynRuntimeError("Property access target must be object");
            var obj = objv.AsObject();
            if (obj.TryGet(e.Name, out var v)) return v;
            return Value.Nil();
        }

        public Value VisitDestructuringAssign(Expr.DestructuringAssign e)
        {
            var src = Evaluate(e.Value);
            void assignLocal(string name, Value v)
            {
                _env.Assign(name, v);
            }
            e.Pat.Bind(this, src, assignLocal, allowConstReassign: true);
            return Value.Nil();
        }

        // Evaluate left, then right; return right
        public Value VisitComma(Expr.Comma e)
        {
            Evaluate(e.Left);
            return Evaluate(e.Right);
        }

        private static int NormalizeIndex(int idx, int len)
        {
            if (idx < 0) idx = len + idx;
            return idx;
        }

        // Deep structural equality with cycle detection
        private bool DeepEqual(Value a, Value b)
        {
            var seen = new HashSet<(object, object)>(new RefPairEq());
            return DeepEqualCore(a, b, seen);
        }
        private sealed class RefPairEq : IEqualityComparer<(object, object)>
        {
            public bool Equals((object, object) x, (object, object) y)
                => ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);
            public int GetHashCode((object, object) obj)
                => (RuntimeHelpers.GetHashCode(obj.Item1) * 397) ^ RuntimeHelpers.GetHashCode(obj.Item2);
        }
        private bool DeepEqualCore(Value a, Value b, HashSet<(object, object)> seen)
        {
            if (a.Type != b.Type)
            {
                // Allow numeric coercion equality like normal '==' does
                if (a.Type == ValueType.Number && b.Type == ValueType.Number)
                    return NumberValue.Compare(a.AsNumber(), b.AsNumber()) == 0;
                return false;
            }
            switch (a.Type)
            {
                case ValueType.Nil: return true;
                case ValueType.Boolean: return a.AsBoolean() == b.AsBoolean();
                case ValueType.Number: return NumberValue.Compare(a.AsNumber(), b.AsNumber()) == 0;
                case ValueType.String: return string.Equals(a.AsString(), b.AsString(), StringComparison.Ordinal);
                case ValueType.Function: return ReferenceEquals(a.AsFunction(), b.AsFunction());
                case ValueType.Array:
                    {
                        var aa = a.AsArray();
                        var ab = b.AsArray();
                        if (ReferenceEquals(aa, ab)) return true;
                        if (aa.Length != ab.Length) return false;
                        if (!seen.Add((aa, ab))) return true; // already compared
                        for (int i = 0; i < aa.Length; i++)
                            if (!DeepEqualCore(aa[i], ab[i], seen)) return false;
                        return true;
                    }
                case ValueType.Object:
                    {
                        var oa = a.AsObject();
                        var ob = b.AsObject();
                        if (ReferenceEquals(oa, ob)) return true;
                        if (oa.Count != ob.Count) return false;
                        if (!seen.Add((oa, ob))) return true;
                        // Compare key sets
                        foreach (var kv in oa.Entries)
                        {
                            if (!ob.TryGet(kv.Key, out var bv)) return false;
                            if (!DeepEqualCore(kv.Value, bv, seen)) return false;
                        }
                        return true;
                    }
                default:
                    return false;
            }
        }

        private static NumberValue ToNumber(Value v)
        {
            switch (v.Type)
            {
                case ValueType.Number: return v.AsNumber();
                case ValueType.Boolean: return NumberValue.FromBool(v.AsBoolean());
                case ValueType.Nil: return NumberValue.FromLong(0);
                case ValueType.String:
                    return NumberValue.TryFromString(v.AsString(), out var nv) ? nv : NumberValue.FromLong(0);
                case ValueType.Array: return NumberValue.FromLong(0);
                case ValueType.Object: return NumberValue.FromLong(0);
                default: return NumberValue.FromLong(0);
            }
        }

        // Build a simple Error object from name/message and optional runtime error details
        internal Value MakeError(string name, string message, MiniDynRuntimeError ex = null)
        {
            var ov = new ObjectValue();
            ov.Set("name", Value.String(name ?? "Error"));
            ov.Set("message", Value.String(message ?? ""));
            if (ex != null)
            {
                if (!ex.Span.IsEmpty)
                {
                    ov.Set("at", Value.String(ex.Span.ToString()));
                }
                if (ex.CallStack != null && ex.CallStack.Count > 0)
                {
                    var arr = new ArrayValue();
                    foreach (var frame in ex.CallStack)
                        arr.Items.Add(Value.String(frame.ToString()));
                    ov.Set("stack", Value.Array(arr));
                }
            }
            return Value.Object(ov);
        }

        private string JsonStringifyValue(Value v, bool pretty = false, int indent = 0)
        {
            // Use an object path to detect cycles along the current traversal
            var path = new HashSet<object>();
            return JsonStringifyValueInternal(v, pretty, indent, path);
        }

        private string JsonStringifyValueInternal(Value v, bool pretty, int indent, HashSet<object> path)
        {
            string Ind() => new string(' ', indent);
            string Ind2() => new string(' ', indent + 2);

            switch (v.Type)
            {
                case ValueType.Nil: return "null";
                case ValueType.Boolean: return v.AsBoolean() ? "true" : "false";
                case ValueType.Number: return v.AsNumber().ToString();
                case ValueType.String: return "\"" + JsonUtil.EscapeJson(v.AsString()) + "\"";
                case ValueType.Array:
                    {
                        var arr = v.AsArray();
                        if (path.Contains(arr))
                            throw new MiniDynRuntimeError("Cannot stringify cyclic structure");
                        path.Add(arr);

                        if (arr.Length == 0) { path.Remove(arr); return "[]"; }
                        var parts = new List<string>(arr.Length);
                        for (int k = 0; k < arr.Length; k++)
                            parts.Add(JsonStringifyValueInternal(arr[k], pretty, indent + 2, path));
                        var result = !pretty
                            ? "[" + string.Join(",", parts) + "]"
                            : "[\n" + string.Join(",\n", parts.Select(p => Ind2() + p)) + "\n" + Ind() + "]";
                        path.Remove(arr);
                        return result;
                    }
                case ValueType.Object:
                    {
                        var obj = v.AsObject();
                        if (path.Contains(obj))
                            throw new MiniDynRuntimeError("Cannot stringify cyclic structure");
                        path.Add(obj);

                        if (obj.Count == 0) { path.Remove(obj); return "{}"; }
                        var parts = new List<string>(obj.Count);
                        foreach (var kv in obj.Entries)
                        {
                            var key = "\"" + JsonUtil.EscapeJson(kv.Key) + "\"";
                            var val = JsonStringifyValueInternal(kv.Value, pretty, indent + 2, path);
                            parts.Add(pretty ? (Ind2() + key + ": " + val) : (key + ":" + val));
                        }
                        var result = !pretty
                            ? "{" + string.Join(",", parts) + "}"
                            : "{\n" + string.Join(",\n", parts) + "\n" + Ind() + "}";
                        path.Remove(obj);
                        return result;
                    }
                default:
                    return "null";
            }
        }

        // Newtonsoft.Json JToken -> Value
        private static Value JsonToValue(JToken t)
        {
            switch (t.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return Value.Nil();

                case JTokenType.Boolean:
                    return Value.Boolean(t.Value<bool>());

                case JTokenType.Integer:
                    {
                        var jv = (JValue)t;
                        var raw = jv.Value;
                        if (raw is long l) return Value.Number(NumberValue.FromLong(l));
                        if (raw is int i) return Value.Number(NumberValue.FromLong(i));
                        if (raw is short s) return Value.Number(NumberValue.FromLong(s));
                        if (raw is byte b) return Value.Number(NumberValue.FromLong(b));
                        if (raw is ulong ul) return Value.Number(NumberValue.FromBigInt(new BigInteger(ul)));
                        if (raw is uint ui) return Value.Number(NumberValue.FromLong(ui));
                        if (raw is ushort us) return Value.Number(NumberValue.FromLong(us));
                        if (raw is sbyte sb) return Value.Number(NumberValue.FromLong(sb));
                        if (raw is BigInteger bi) return Value.Number(NumberValue.FromBigInt(bi));
                        // Fallback via string to preserve big values
                        return NumberValue.TryFromString(jv.ToString(CultureInfo.InvariantCulture), out var nv)
                            ? Value.Number(nv)
                            : Value.Number(NumberValue.FromDouble(Convert.ToDouble(raw, CultureInfo.InvariantCulture)));
                    }

                case JTokenType.Float:
                    return Value.Number(NumberValue.FromDouble(t.Value<double>()));

                case JTokenType.String:
                    return Value.String(t.Value<string>());

                case JTokenType.Array:
                    {
                        var arr = new ArrayValue();
                        foreach (var item in (JArray)t)
                            arr.Items.Add(JsonToValue(item));
                        return Value.Array(arr);
                    }

                case JTokenType.Object:
                    {
                        var ov = new ObjectValue();
                        foreach (var prop in (JObject)t)
                            ov.Set(prop.Key, JsonToValue(prop.Value));
                        return Value.Object(ov);
                    }

                default:
                    return Value.Nil();
            }
        }
    }

    class Program
    {
        static void RunFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File not found: {filePath}");
                return;
            }
            var source = File.ReadAllText(filePath);
            var lexer = new Lexer(source, filePath);
            var parser = new Parser(lexer);
            var program = parser.Parse();
            var loader = new FileSystemModuleLoader();
            var interp = new Interpreter(loader);
            interp.Interpret(program);
        }

        static void Repl()
        {
            Console.WriteLine("MiniDynLang REPL. Ctrl+C to exit.");
            var loader = new FileSystemModuleLoader();
            var interp = new Interpreter(loader);
            while (true)
            {
                Console.Write("> ");
                string line = Console.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var lexer = new Lexer(line, "<repl>");
                    var parser = new Parser(lexer);
                    var stmts = parser.Parse();

                    if (stmts.Count == 1 && stmts[0] is Stmt.ExprStmt es)
                    {
                        var val = interp.EvaluateWithEnv(es.Expression, interp.Globals);
                        Console.WriteLine(interp.ToStringValue(val));
                    }
                    else
                    {
                        interp.Interpret(stmts);
                    }
                }
                catch (Interpreter.ThrowSignal ex)
                {
                    Console.WriteLine("Uncaught exception: " + interp.ToStringValue(ex.Value));
                }
                catch (MiniDynException ex)
                {
                    Console.WriteLine("Error: " + ex.ToString());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error: " + ex.Message);
                }
            }
        }

        static void Main(string[] args)
        {
            try
            {
                if (args.Length == 0)
                {
                    Repl();
                }
                else
                {
                    RunFile(args[0]);
                }
            }
            catch (Interpreter.ThrowSignal ex)
            {
                Console.WriteLine("Runtime error: Uncaught exception -> " + ex.Value.ToString());
            }
            catch (MiniDynException ex)
            {
                Console.WriteLine("Runtime error: " + ex.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Runtime error: " + ex.Message);
            }
        }
    }
}

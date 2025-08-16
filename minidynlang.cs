using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace MiniDynLang
{
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
    public sealed class MiniDynLexError : MiniDynException { public MiniDynLexError(string msg, int l, int c) : base(msg, l, c) { } }
    public sealed class MiniDynParseError : MiniDynException { public MiniDynParseError(string msg, int l, int c) : base(msg, l, c) { } }
    public sealed class MiniDynRuntimeError : MiniDynException { public MiniDynRuntimeError(string msg) : base(msg) { } }

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
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < Items.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Items[i].ToString());
            }
            sb.Append(']');
            return sb.ToString();
        }
    }

    public sealed class ObjectValue
    {
        public readonly Dictionary<string, Value> Props;
        public ObjectValue() { Props = new Dictionary<string, Value>(); }
        public ObjectValue(Dictionary<string, Value> dict) { Props = dict ?? new Dictionary<string, Value>(); }
        public bool TryGet(string key, out Value v) => Props.TryGetValue(key, out v);
        public void Set(string key, Value v) => Props[key] = v;
        public bool Remove(string key) => Props.Remove(key);
        public int Count => Props.Count;
        public ObjectValue CloneShallow()
        {
            return new ObjectValue(new Dictionary<string, Value>(Props));
        }
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append('{');
            bool first = true;
            foreach (var kv in Props)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key);
                sb.Append(": ");
                sb.Append(kv.Value.ToString());
            }
            sb.Append('}');
            return sb.ToString();
        }
    }

    public sealed class Value
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

        public static Value Number(NumberValue n) => new Value(ValueType.Number, n, null, false, null, null, null);
        public static Value String(string s) => new Value(ValueType.String, default, s ?? "", false, null, null, null);
        public static Value Boolean(bool b) => b ? TrueVal : FalseVal;
        public static Value Nil() => NilVal;
        public static Value Function(ICallable f) => new Value(ValueType.Function, default, null, false, f, null, null);
        public static Value Array(ArrayValue a) => new Value(ValueType.Array, default, null, false, null, a ?? new ArrayValue(), null);
        public static Value Object(ObjectValue o) => new Value(ValueType.Object, default, null, false, null, null, o ?? new ObjectValue());

        public NumberValue AsNumber() => Type == ValueType.Number ? _num : throw new MiniDynRuntimeError("Expected number");
        public string AsString() => Type == ValueType.String ? _str : throw new MiniDynRuntimeError("Expected string");
        public bool AsBoolean() => Type == ValueType.Boolean ? _bool : throw new MiniDynRuntimeError("Expected boolean");
        public ICallable AsFunction() => Type == ValueType.Function ? _func : throw new MiniDynRuntimeError("Expected function");
        public ArrayValue AsArray() => Type == ValueType.Array ? _arr : throw new MiniDynRuntimeError("Expected array");
        public ObjectValue AsObject() => Type == ValueType.Object ? _obj : throw new MiniDynRuntimeError("Expected object");

        public override string ToString()
        {
            switch (Type)
            {
                case ValueType.Number: return _num.ToString();
                case ValueType.String: return _str;
                case ValueType.Boolean: return _bool ? "true" : "false";
                case ValueType.Function: return _func != null ? _func.ToString() : "<function>";
                case ValueType.Array: return _arr != null ? _arr.ToString() : "[]";
                case ValueType.Object: return _obj != null ? _obj.ToString() : "{}";
                default: return "nil";
            }
        }

        public static bool IsTruthy(Value v)
        {
            switch (v.Type)
            {
                case ValueType.Nil: return false;
                case ValueType.Boolean: return v._bool;
                case ValueType.Number: return NumberValue.Compare(v._num, NumberValue.FromLong(0)) != 0;
                case ValueType.String: return !string.IsNullOrEmpty(v._str);
                case ValueType.Function: return true;
                case ValueType.Array: return v._arr != null && v._arr.Length != 0;
                case ValueType.Object: return v._obj != null && v._obj.Count != 0;
                default: return false;
            }
        }

        public override bool Equals(object obj)
        {
            var o = obj as Value;
            if (o == null || o.Type != Type) return false;
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
    }

    public class Token
    {
        public TokenType Type { get; }
        public string Lexeme { get; }
        public object Literal { get; }
        public int Position { get; }
        public int Line { get; }
        public int Column { get; }

        public Token(TokenType type, string lexeme, object literal, int pos, int line, int column)
        {
            Type = type; Lexeme = lexeme; Literal = literal; Position = pos; Line = line; Column = column;
        }
        public override string ToString() => $"{Type} '{Lexeme}' @ {Line}:{Column}";
    }

    public class Lexer
    {
        private readonly string _src;
        private int _pos;
        private int _line = 1;
        private int _col = 1;

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
            ["true"] = TokenType.True,
            ["false"] = TokenType.False,
            ["nil"] = TokenType.Nil,
            ["and"] = TokenType.And,
            ["or"] = TokenType.Or,
            ["not"] = TokenType.Not,
            ["fn"] = TokenType.Fn,
            ["return"] = TokenType.Return,
        };

        public Lexer(string src) { _src = src ?? ""; }

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
            => new Token(t, lexeme, lit, startPos, startLine, startCol);

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
                    throw new MiniDynLexError("Unexpected '!'", startLine, startCol);
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

        private Token StringToken(int start, int startLine, int startCol)
        {
            StringBuilder sb = new StringBuilder();
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
                        default: c = e; break;
                    };
                }
                sb.Append(c);
            }
            if (IsAtEnd) throw new MiniDynLexError("Unterminated string", startLine, startCol);
            Advance(); // closing "
            string text = sb.ToString();
            return MakeToken(TokenType.String, text, text, start, startLine, startCol);
        }

        private Token NumberToken(int start, int startLine, int startCol)
        {
            bool hasDot = false;
            bool hasExp = false;

            // Integer and optional fractional part
            while (true)
            {
                char p = Peek();
                if (char.IsDigit(p)) { Advance(); continue; }
                if (!hasDot && p == '.' && char.IsDigit(PeekNext()))
                {
                    hasDot = true; Advance(); continue;
                }
                break;
            }

            // Optional exponent part: e[+|-]?digits
            if (Peek() == 'e' || Peek() == 'E')
            {
                hasExp = true;
                Advance(); // consume 'e'/'E'

                if (Peek() == '+' || Peek() == '-') Advance();

                if (!char.IsDigit(Peek()))
                    throw new MiniDynLexError("Invalid exponent in number literal", startLine, startCol);

                while (char.IsDigit(Peek())) Advance();
            }

            string text = _src.Substring(start, _pos - start);
            object lit;

            if (hasDot || hasExp)
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new MiniDynLexError("Invalid number literal", startLine, startCol);
                lit = NumberValue.FromDouble(d);
            }
            else
            {
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    lit = NumberValue.FromLong(i);
                else
                    lit = NumberValue.FromBigInt(BigInteger.Parse(text, CultureInfo.InvariantCulture));
            }
            return MakeToken(TokenType.Number, text, lit, start, startLine, startCol);
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
            public Index(Expr t, Expr i) { Target = t; IndexExpr = i; }
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
            public Property(Expr target, string name) { Target = target; Name = name; }
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
                    foreach (var kv in obj.Props)
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
        }
        public abstract T Accept<T>(IVisitor<T> v);

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
            Expr expr = new Expr.Variable((string)baseTok.Literal);

            bool hasAccessor = false;          // did we see '.' or '[' ?

            while (true)
            {
                if (Match(TokenType.Dot))
                {
                    var nameTok = Consume(TokenType.Identifier, "Expected property name after '.'");
                    expr = new Expr.Property(expr, (string)nameTok.Literal);
                    hasAccessor = true;
                }
                else if (Match(TokenType.LBracket))
                {
                    var idx = Expression();
                    Consume(TokenType.RBracket, "Expected ']'");
                    expr = new Expr.Index(expr, idx);
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
            if (Match(TokenType.Fn)) return FunctionDecl("function");
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

        private Stmt FunctionDecl(string kind)
        {
            var nameTok = Consume(TokenType.Identifier, $"Expected {kind} name");
            Consume(TokenType.LParen, "Expected '('");
            var parameters = ParseParamList();
            Consume(TokenType.RParen, "Expected ')'");
            Consume(TokenType.LBrace, "Expected '{' before function body");
            var body = BlockStatementInternal();
            return new Stmt.Function((string)nameTok.Literal, new Expr.Function(parameters, body, isArrow: false));
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
            // var <pattern or name> [= initializer] ;
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                Expr init;
                if (Match(TokenType.Assign)) init = Expression();
                else throw new MiniDynParseError("Destructuring declaration requires initializer", Peek().Line, Peek().Column);
                Consume(TokenType.Semicolon, "Expected ';'");
                return new Stmt.DestructuringDecl(pat, init, Stmt.DestructuringDecl.Kind.Var);
            }
            var nameTok = Consume(TokenType.Identifier, "Expected variable name");
            Expr init2 = null;
            if (Match(TokenType.Assign))
                init2 = Expression();
            Consume(TokenType.Semicolon, "Expected ';'");
            return new Stmt.Var((string)nameTok.Literal, init2);
        }

        private Stmt LetDeclOrDestructuring()
        {
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                Expr init;
                if (Match(TokenType.Assign)) init = Expression();
                else throw new MiniDynParseError("Destructuring declaration requires initializer", Peek().Line, Peek().Column);
                Consume(TokenType.Semicolon, "Expected ';'");
                return new Stmt.DestructuringDecl(pat, init, Stmt.DestructuringDecl.Kind.Let);
            }
            var nameTok = Consume(TokenType.Identifier, "Expected variable name");
            Expr init2 = null;
            if (Match(TokenType.Assign))
                init2 = Expression();
            Consume(TokenType.Semicolon, "Expected ';'");
            return new Stmt.Let((string)nameTok.Literal, init2);
        }

        private Stmt ConstDeclOrDestructuring()
        {
            if (Check(TokenType.LBracket) || Check(TokenType.LBrace))
            {
                var pat = ParsePattern();
                var init = ParseInitializerRequired();
                Consume(TokenType.Semicolon, "Expected ';'");
                return new Stmt.DestructuringDecl(pat, init, Stmt.DestructuringDecl.Kind.Const);
            }
            var nameTok = Consume(TokenType.Identifier, "Expected constant name");
            Consume(TokenType.Assign, "Expected '=' after const name");
            var init2 = Expression();
            Consume(TokenType.Semicolon, "Expected ';'");
            return new Stmt.Const((string)nameTok.Literal, init2);
        }

        private Expr ParseInitializerRequired()
        {
            Consume(TokenType.Assign, "Expected '=' for initializer");
            return Expression();
        }

        private Stmt Statement()
        {
            if (Match(TokenType.If)) return IfStatement();
            if (Match(TokenType.While)) return WhileStatement();
            if (Match(TokenType.Break)) { Consume(TokenType.Semicolon, "Expected ';' after break"); return new Stmt.Break(); }
            if (Match(TokenType.Continue)) { Consume(TokenType.Semicolon, "Expected ';' after continue"); return new Stmt.Continue(); }
            if (Match(TokenType.Return))
            {
                Expr value = null;
                if (!Check(TokenType.Semicolon)) value = Expression();
                Consume(TokenType.Semicolon, "Expected ';' after return value");
                return new Stmt.Return(value);
            }

            // Array destructuring assignment: [a,b] = expr;
            if (Check(TokenType.LBracket))
            {
                var pat = ParsePattern();
                Consume(TokenType.Assign, "Expected '=' in destructuring assignment");
                var val = Expression();
                Consume(TokenType.Semicolon, "Expected ';'");
                return new Stmt.ExprStmt(new Expr.DestructuringAssign(pat, val));
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
                    Consume(TokenType.Assign, "Expected '=' in destructuring assignment");
                    var val = Expression();
                    Consume(TokenType.Semicolon, "Expected ';'");
                    return new Stmt.ExprStmt(new Expr.DestructuringAssign(pat, val));
                }
                else
                {
                    // Plain block statement
                    Advance(); // consume '{'
                    return new Stmt.Block(BlockStatementInternal().Statements);
                }
            }

            return ExprStatement();
        }

        private Stmt IfStatement()
        {
            Consume(TokenType.LParen, "Expected '(' after if");
            var cond = Expression();
            Consume(TokenType.RParen, "Expected ')'");
            var thenS = Statement();
            Stmt elseS = null;
            if (Match(TokenType.Else)) elseS = Statement();
            return new Stmt.If(cond, thenS, elseS);
        }

        private Stmt WhileStatement()
        {
            Consume(TokenType.LParen, "Expected '(' after while");
            var cond = Expression();
            Consume(TokenType.RParen, "Expected ')'");
            var body = Statement();
            return new Stmt.While(cond, body);
        }

        private Stmt.Block BlockStatementInternal()
        {
            List<Stmt> stmts = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !IsAtEnd)
                stmts.Add(Declaration());
            Consume(TokenType.RBrace, "Expected '}'");
            return new Stmt.Block(stmts);
        }

        private Stmt ExprStatement()
        {
            var expr = Expression();
            Consume(TokenType.Semicolon, "Expected ';'");
            return new Stmt.ExprStmt(expr);
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
                // Do not allow the comma operator to bleed across argument/element boundaries.
                // Use Assignment for 'then' and recurse to Ternary for right-associativity on 'else'.
                var thenE = Assignment();
                Consume(TokenType.Colon, "Expected ':' in ternary expression");
                var elseE = Ternary();
                return new Expr.Ternary(cond, thenE, elseE);
            }
            return cond;
        }

        // assignment -> lvalue ( '=' | op_assign ) assignment | logic_or
        // lvalue can be Variable, Property, Index, or Pattern (for destructuring)
        private Expr Assignment()
        {
            var expr = Or();

            if (Match(TokenType.Assign, TokenType.PlusAssign, TokenType.MinusAssign, TokenType.StarAssign, TokenType.SlashAssign, TokenType.PercentAssign))
            {
                Token op = Previous();
                // handle destructuring assign e.g. [a,b] = RHS
                if (expr is Expr.Index || expr is Expr.Property || expr is Expr.Variable)
                {
                    var value = Assignment();
                    return new Expr.Assign(expr, op, value);
                }
                else if (expr is Expr.Grouping g && (g.Inner is Expr.Index || g.Inner is Expr.Property || g.Inner is Expr.Variable))
                {
                    var value = Assignment();
                    return new Expr.Assign(g.Inner, op, value);
                }
                else
                {
                    // allow [a,b] = ...
                    if (expr is Expr.ArrayLiteral || expr is Expr.ObjectLiteral)
                    {
                        // Convert literals-as-patterns? We only allow explicit patterns in statements; here support [a,b] or {x:y} in expressions by treating them as patterns-like is complex.
                        throw new MiniDynParseError("Invalid assignment target", Previous().Line, Previous().Column);
                    }
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
                expr = new Expr.Logical(expr, op, right);
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
                expr = new Expr.Logical(expr, op, right);
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
                expr = new Expr.Binary(expr, op, right);
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
                expr = new Expr.Binary(expr, op, right);
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
                expr = new Expr.Binary(expr, op, right);
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
                expr = new Expr.Binary(expr, op, right);
            }
            return expr;
        }

        private Expr Unary()
        {
            if (Match(TokenType.Plus, TokenType.Minus, TokenType.Not))
            {
                var op = Previous();
                var right = Unary();
                return new Expr.Unary(op, right);
            }
            return Member();
        }

        private Expr Member()
        {
            Expr expr = Primary();

            while (true)
            {
                // 1. function / method call  
                if (Match(TokenType.LParen))
                {
                    var args = new List<Expr.Call.Argument>();

                    if (!Check(TokenType.RParen))
                    {
                        do
                        {
                            var nextToken = PeekAhead(1);
                            // named argument?  
                            if (Check(TokenType.Identifier) && nextToken?.Type == TokenType.Colon)
                            {
                                var nameTok = Advance();          // identifier  
                                Consume(TokenType.Colon, "Expected ':'");
                                // allow ternary in argument value, but not comma operator
                                args.Add(new Expr.Call.Argument(
                                    (string)nameTok.Literal,
                                    Ternary()));
                            }
                            else
                            {
                                // positional (allow ternary, not comma operator)
                                args.Add(new Expr.Call.Argument(null, Ternary()));
                            }
                        } while (Match(TokenType.Comma));
                    }

                    Consume(TokenType.RParen, "Expected ')'");
                    expr = new Expr.Call(expr, args);
                    continue;
                }

                // 2. property access  
                if (Match(TokenType.Dot))
                {
                    var nameTok = Consume(TokenType.Identifier, "Expected property name after '.'");
                    expr = new Expr.Property(expr, (string)nameTok.Literal);
                    continue;
                }

                // 3. index access  
                if (Match(TokenType.LBracket))
                {
                    // allow ternary in index, not comma operator
                    var indexExpr = Ternary();
                    Consume(TokenType.RBracket, "Expected ']'");
                    expr = new Expr.Index(expr, indexExpr);
                    continue;
                }

                break; // nothing more to fold  
            }

            return expr;
        }

        private Expr Primary()
        {
            if (Match(TokenType.Number))
                return new Expr.Literal(Value.Number((NumberValue)Previous().Literal));
            if (Match(TokenType.String))
                return new Expr.Literal(Value.String((string)Previous().Literal));
            if (Match(TokenType.True))
                return new Expr.Literal(Value.Boolean(true));
            if (Match(TokenType.False))
                return new Expr.Literal(Value.Boolean(false));
            if (Match(TokenType.Nil))
                return new Expr.Literal(Value.Nil());
            if (Match(TokenType.LBracket))
            {
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
                return new Expr.ArrayLiteral(elems);
            }
            if (Match(TokenType.LBrace))
            {
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
                return new Expr.ObjectLiteral(entries);
            }

            if (Match(TokenType.LParen))
            {
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
                return new Expr.Grouping(e);
            }

            var nextTokenIsArrow = PeekAhead(1)?.Type == TokenType.Arrow;
            // ──────────────── 1. Single-parameter arrow function  x => expr ────────────────
            // Must be checked before we treat the identifier as an ordinary variable.
            if (Check(TokenType.Identifier) && nextTokenIsArrow)
            {
                var param = Advance();
                Consume(TokenType.Arrow, "Expected '=>'");

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
                return new Expr.Function(parameters, bodyBlock, isArrow: true);
            }

            if (Match(TokenType.Fn))
            {
                // anonymous/inline function expression
                Consume(TokenType.LParen, "Expected '(' after 'fn'");
                var parameters = ParseParamList();
                Consume(TokenType.RParen, "Expected ')'");
                Consume(TokenType.LBrace, "Expected '{' before function body");
                var body = BlockStatementInternal();
                return new Expr.Function(parameters, body, isArrow: false);
            }

            // Ordinary identifier
            if (Match(TokenType.Identifier))
                return new Expr.Variable((string)Previous().Literal);
            throw new MiniDynParseError("Expected expression", Peek().Line, Peek().Column);
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

                Consume(TokenType.Arrow, "Expected '=>'");

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

                arrowFunc = new Expr.Function(parameters, bodyBlock, isArrow: true);
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

        public Environment Enclosing { get; }

        public Environment(Environment enclosing = null) { Enclosing = enclosing; }

        public void DefineVar(string name, Value value) => _values[name] = value;
        public void DefineLet(string name, Value value)
        {
            _values[name] = value;
            _lets.Add(name);
        }
        public void DefineConst(string name, Value value)
        {
            _values[name] = value;
            _lets.Add(name);
            _consts.Add(name);
        }

        public void Assign(string name, Value value)
        {
            if (_values.ContainsKey(name))
            {
                if (_consts.Contains(name))
                    throw new MiniDynRuntimeError($"Cannot assign to const '{name}'");
                _values[name] = value; return;
            }
            if (Enclosing != null) { Enclosing.Assign(name, value); return; }
            throw new MiniDynRuntimeError($"Undefined variable '{name}'");
        }

        public Value Get(string name)
        {
            if (_values.TryGetValue(name, out var v)) return v;
            if (Enclosing != null) return Enclosing.Get(name);
            throw new MiniDynRuntimeError($"Undefined variable '{name}'");
        }

        public bool TryGet(string name, out Value v)
        {
            if (_values.TryGetValue(name, out var local))
            {
                v = local;
                return true;
            }
            if (Enclosing != null) return Enclosing.TryGet(name, out v);
            v = null;
            return false;
        }


        public bool IsDeclaredHere(string name) => _values.ContainsKey(name) && _lets.Contains(name);
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
        private readonly Value _capturedThis; // for arrows

        public List<ParamSpec> Params { get; }
        public Stmt.Block Body { get; }
        public Environment Closure { get; }
        public string Name { get; }

        private Value _boundThis;

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
        public UserFunction(string name, List<Expr.Param> parameters, Stmt.Block body, Environment closure,
                            Kind kind = Kind.Normal, Value capturedThis = null)
        {
            Name = name;
            Body = body;
            Closure = closure;
            Params = new List<ParamSpec>(parameters.Count);
            foreach (var p in parameters)
                Params.Add(new ParamSpec(p.Name, p.Default, p.IsRest));
            FunctionKind = kind;
            _capturedThis = capturedThis;
        }

        public UserFunction BindThis(Value thisValue)
        {
            // arrows ignore rebinding
            if (FunctionKind == Kind.Arrow) return this;

            var bound = new UserFunction(Name, ToExprParams(), Body, Closure, FunctionKind, _capturedThis);
            bound._boundThis = thisValue;
            return bound;
        }

        public Value Call(Interpreter interp, List<Value> args)
        {
            var env = new Environment(Closure);

            // define 'this' according to kind (arrow uses captured lexical; normal uses bound call-site)
            if (FunctionKind == Kind.Arrow)
            {
                if (_capturedThis != null) env.DefineConst("this", _capturedThis);
            }
            else if (_boundThis != null)
            {
                env.DefineConst("this", _boundThis);
            }

            // Bind parameters with defaults and rest
            int i = 0;
            int argsCount = args.Count;
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
                        rest.Items.Add(args[ai]);
                    }
                    env.DefineVar(p.Name, Value.Array(rest));
                    i = argsCount;
                    continue;     // << keep looping so later parameters get defaults / named values
                }
                if (i < argsCount)
                {
                    env.DefineVar(p.Name, args[i++]);
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

            try
            {
                interp.ExecuteBlock(Body.Statements, env);
            }
            catch (Interpreter.ReturnSignal ret)
            {
                return ret.Value ?? Value.Nil();
            }
            return Value.Nil();
        }

        public override string ToString()
        {
            return Name != null ? $"<fn {Name}>" : "<fn>";
        }

        public UserFunction Bind(Environment newClosure) =>
            new UserFunction(Name, ToExprParams(), Body, newClosure, FunctionKind, _capturedThis);

        private List<Expr.Param> ToExprParams()
        {
            var list = new List<Expr.Param>(Params.Count);
            foreach (var p in Params) list.Add(new Expr.Param(p.Name, p.Default, p.IsRest));
            return list;
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

        public readonly Environment Globals = new Environment();
        private Environment _env;
        public Environment CurrentEnv => _env;

        private readonly Dictionary<string, ICallable> _builtins = new Dictionary<string, ICallable>();

        public Interpreter()
        {
            _env = Globals;

            // Builtins
            DefineBuiltin("length", 1, 1, (i, a) =>
            {
                var v = a[0];
                switch (v.Type)
                {
                    case ValueType.String: return Value.Number(NumberValue.FromLong(v.AsString().Length));
                    case ValueType.Array:  return Value.Number(NumberValue.FromLong(v.AsArray().Length));
                    case ValueType.Object: return Value.Number(NumberValue.FromLong(v.AsObject().Count));
                    case ValueType.Nil:    return Value.Number(NumberValue.FromLong(0));
                    case ValueType.Boolean:return Value.Number(NumberValue.FromLong(1));
                    case ValueType.Number: return Value.Number(NumberValue.FromLong(1));
                    case ValueType.Function:return Value.Number(NumberValue.FromLong(1));
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
                foreach (var k in o.Props.Keys) arr.Items.Add(Value.String(k));
                return Value.Array(arr);
            });

            DefineBuiltin("has_key", 2, 2, (i, a) =>
            {
                var o = a[0].AsObject();
                var k = a[1].AsString();
                return Value.Boolean(o.Props.ContainsKey(k));
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
                foreach (var kv in o2.Props) res.Set(kv.Key, kv.Value);
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
                    int start = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                    start = NormalizeIndex(start, arr.Length);
                    int end = arr.Length;
                    if (a.Count == 3)
                    {
                        end = (int)ToNumber(a[2]).ToDoubleNV().Dbl;
                        end = NormalizeIndex(end, arr.Length);
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
                    int start = (int)ToNumber(a[1]).ToDoubleNV().Dbl;
                    start = NormalizeIndex(start, s.Length);
                    int end = s.Length;
                    if (a.Count == 3)
                    {
                        end = (int)ToNumber(a[2]).ToDoubleNV().Dbl;
                        end = NormalizeIndex(end, s.Length);
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
                foreach (var v in o.Props.Values) arr.Items.Add(v);
                return Value.Array(arr);
            });

            DefineBuiltin("entries", 1, 1, (i, a) =>
            {
                var o = a[0].AsObject();
                var arr = new ArrayValue();
                foreach (var kv in o.Props)
                {
                    var pair = new ArrayValue(new[] { Value.String(kv.Key), kv.Value });
                    arr.Items.Add(Value.Array(pair));
                }
                return Value.Array(arr);
            });

            DefineBuiltin("from_entries", 1, 1, (i, a) =>
            {
                var src = a[0].AsArray();
                var dict = new Dictionary<string, Value>(StringComparer.Ordinal);
                for (int k = 0; k < src.Length; k++)
                {
                    var entry = src[k];
                    if (entry.Type != ValueType.Array) throw new MiniDynRuntimeError("from_entries expects array of [key, value] pairs");
                    var tup = entry.AsArray();
                    if (tup.Length != 2 || tup[0].Type != ValueType.String) throw new MiniDynRuntimeError("from_entries expects [string, any] pairs");
                    dict[tup[0].AsString()] = tup[1];
                }
                return Value.Object(new ObjectValue(dict));
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

            DefineBuiltin("range", 1, 3, (i, a) =>
            {
                long start, end, step;
                if (a.Count == 1) { start = 0; end = (long)ToNumber(a[0]).ToDoubleNV().Dbl; step = 1; }
                else if (a.Count == 2) { start = (long)ToNumber(a[0]).ToDoubleNV().Dbl; end = (long)ToNumber(a[1]).ToDoubleNV().Dbl; step = 1; }
                else { start = (long)ToNumber(a[0]).ToDoubleNV().Dbl; end = (long)ToNumber(a[1]).ToDoubleNV().Dbl; step = (long)ToNumber(a[2]).ToDoubleNV().Dbl; }
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

            // === New: Time ===
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

        private void Execute(Stmt s) => s.Accept(this);

        private Value Evaluate(Expr e) => e.Accept(this);
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
            _env.DefineVar(s.Name, val);
            return null;
        }

        public object VisitLet(Stmt.Let s)
        {
            Value val = s.Initializer != null ? Evaluate(s.Initializer) : Value.Nil();
            _env.DefineLet(s.Name, val);
            return null;
        }

        public object VisitConst(Stmt.Const s)
        {
            Value val = Evaluate(s.Initializer);
            _env.DefineConst(s.Name, val);
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
                    case Stmt.DestructuringDecl.Kind.Var: _env.DefineVar(name, v); break;
                    case Stmt.DestructuringDecl.Kind.Let: _env.DefineLet(name, v); break;
                    case Stmt.DestructuringDecl.Kind.Const: _env.DefineConst(name, v); break;
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

        public object VisitFunction(Stmt.Function s)
        {
            var kind = s.FuncExpr.IsArrow ? UserFunction.Kind.Arrow : UserFunction.Kind.Normal;
            var fn = new UserFunction(s.Name, s.FuncExpr.Parameters, s.FuncExpr.Body, _env, kind, null);
            _env.DefineVar(s.Name, Value.Function(fn));
            return null;
        }

        public object VisitReturn(Stmt.Return s)
        {
            Value v = s.Value != null ? Evaluate(s.Value) : Value.Nil();
            throw new ReturnSignal(v);
        }

        public object VisitBreak(Stmt.Break s) { throw new BreakSignal(); }
        public object VisitContinue(Stmt.Continue s) { throw new ContinueSignal(); }

        // Expr visitor
        public Value VisitLiteral(Expr.Literal e) => e.Value;

        public Value VisitVariable(Expr.Variable e)
        {
            return _env.Get(e.Name);
        }

        public Value VisitAssign(Expr.Assign e)
        {
            // Compute RHS
            Value rhs = Evaluate(e.Value);

            // Helper to apply compound op
            Value ApplyOp(Value cur, TokenType op, Value rhsVal)
            {
                if (cur == null) cur = Value.Nil(); // Treat uninitialized/null values as nil for compound assignments
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

            if (e.Target is Expr.Variable v)
            {
                var cur = _env.Get(v.Name);
                var newVal = ApplyOp(cur, e.Op.Type, rhs);
                _env.Assign(v.Name, newVal);
                return newVal;
            }
            else if (e.Target is Expr.Property p)
            {
                var objVal = Evaluate(p.Target);
                if (objVal.Type != ValueType.Object) throw new MiniDynRuntimeError("Property assignment target must be object");
                var obj = objVal.AsObject();
                obj.Props.TryGetValue(p.Name, out var cur);
                var newVal = ApplyOp(cur, e.Op.Type, rhs);
                obj.Set(p.Name, newVal);
                return newVal;
            }
            else if (e.Target is Expr.Index idx)
            {
                var target = Evaluate(idx.Target);
                var idxV = Evaluate(idx.IndexExpr);
                if (target.Type == ValueType.Array)
                {
                    var arr = target.AsArray();
                    int i = (int)ToNumber(idxV).ToDoubleNV().Dbl;
                    i = NormalizeIndex(i, arr.Length);
                    if (i < 0 || i >= arr.Length) throw new MiniDynRuntimeError("Array index out of range");
                    var cur = arr[i];
                    var newVal = ApplyOp(cur, e.Op.Type, rhs);
                    arr[i] = newVal;
                    return newVal;
                }
                else if (target.Type == ValueType.Object)
                {
                    var obj = target.AsObject();
                    var key = ToStringValue(idxV);
                    obj.Props.TryGetValue(key, out var cur);
                    var newVal = ApplyOp(cur, e.Op.Type, rhs);
                    obj.Set(key, newVal);
                    return newVal;
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
                case TokenType.Minus: return Value.Number(NumberValue.Neg(ToNumber(r)));
                case TokenType.Plus: return r.Type == ValueType.Number ? r : Value.Number(ToNumber(r));
                case TokenType.Not: return Value.Boolean(!Value.IsTruthy(r));
                default: throw new MiniDynRuntimeError("Unknown unary");
            }
        }

        public Value VisitBinary(Expr.Binary e)
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
                        // concatenate arrays
                        var la = l.AsArray();
                        var ra = r.AsArray();
                        var res = new ArrayValue();
                        res.Items.AddRange(la.Items);
                        res.Items.AddRange(ra.Items);
                        return Value.Array(res);
                    }
                    throw new MiniDynRuntimeError("Invalid '+' operands");
                case TokenType.Minus:
                    return Value.Number(NumberValue.Sub(ToNumber(l), ToNumber(r)));
                case TokenType.Star:
                    return Value.Number(NumberValue.Mul(ToNumber(l), ToNumber(r)));
                case TokenType.Slash:
                    return Value.Number(NumberValue.Div(ToNumber(l), ToNumber(r)));
                case TokenType.Percent:
                    return Value.Number(NumberValue.Mod(ToNumber(l), ToNumber(r)));
                case TokenType.Equal:
                    return Value.Boolean(CompareEqual(l, r));
                case TokenType.NotEqual:
                    return Value.Boolean(!CompareEqual(l, r));
                case TokenType.Less:
                    return Value.Boolean(CompareRel(l, r, "<"));
                case TokenType.LessEq:
                    return Value.Boolean(CompareRel(l, r, "<="));
                case TokenType.Greater:
                    return Value.Boolean(CompareRel(l, r, ">"));
                case TokenType.GreaterEq:
                    return Value.Boolean(CompareRel(l, r, ">="));
                default:
                    throw new MiniDynRuntimeError("Unknown binary op");
            }
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
            Value receiver = null;
            Value calleeVal;

            // Check if this is a method call (obj.method())
            if (e.Callee is Expr.Property prop)
            {
                receiver = Evaluate(prop.Target);
                calleeVal = VisitProperty(prop);
            }
            else
            {
                calleeVal = Evaluate(e.Callee);
            }

            if (calleeVal.Type != ValueType.Function)
                throw new MiniDynRuntimeError("Can only call functions");

            var fn = calleeVal.AsFunction();

            // Process arguments
            List<Value> processedArgs;

            if (fn is UserFunction userFn)
            {
                processedArgs = ProcessNamedArguments(userFn, e.Args);

                // Bind 'this' for method calls
                if (receiver != null && userFn.FunctionKind == UserFunction.Kind.Normal)
                {
                    fn = userFn.BindThis(receiver);
                }
            }
            else
            {
                // Built-in functions only support positional arguments
                if (e.Args.Any(a => a.IsNamed))
                    throw new MiniDynRuntimeError("Built-in functions do not support named arguments");

                processedArgs = new List<Value>();
                foreach (var arg in e.Args)
                    processedArgs.Add(Evaluate(arg.Value));
            }

            if (processedArgs.Count < fn.ArityMin || processedArgs.Count > fn.ArityMax)
                throw new MiniDynRuntimeError($"Function {fn} expected {fn.ArityMin}..{(fn.ArityMax == int.MaxValue ? "∞" : fn.ArityMax.ToString())} args, got {processedArgs.Count}");

            return fn.Call(this, processedArgs);
        }

        private List<Value> ProcessNamedArguments(UserFunction fn, List<Expr.Call.Argument> args)
        {
            var result = new List<Value>(new Value[fn.Params.Count]);
            var filled = new bool[fn.Params.Count];
            var namedArgs = new Dictionary<string, Value>(StringComparer.Ordinal);
            var positionalArgs = new List<Value>();
            var restArgs = new List<Value>();

            // Evaluate all arguments first
            foreach (var arg in args)
            {
                if (arg.IsNamed)
                    namedArgs[arg.Name] = Evaluate(arg.Value);
                else
                    positionalArgs.Add(Evaluate(arg.Value));
            }

            // Index of rest param, if present
            int restParamIndex = -1;
            for (int i = 0; i < fn.Params.Count; i++)
            {
                if (fn.Params[i].IsRest) { restParamIndex = i; break; }
            }

            // Apply named arguments to matching parameters (non-rest only)
            foreach (var kv in namedArgs)
            {
                bool found = false;
                for (int i = 0; i < fn.Params.Count; i++)
                {
                    if (!fn.Params[i].IsRest && fn.Params[i].Name == kv.Key)
                    {
                        if (filled[i])
                            throw new MiniDynRuntimeError($"Argument '{kv.Key}' specified multiple times");
                        result[i] = kv.Value;
                        filled[i] = true;
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    // Do NOT push unknown named args into rest; this is an error.
                    throw new MiniDynRuntimeError($"Unknown parameter '{kv.Key}'");
                }
            }

            // Fill remaining non-rest parameters with positional args, left to right
            int posIndex = 0;
            for (int i = 0; i < fn.Params.Count && posIndex < positionalArgs.Count; i++)
            {
                if (!filled[i] && !fn.Params[i].IsRest)
                {
                    result[i] = positionalArgs[posIndex++];
                    filled[i] = true;
                }
            }

            // Remaining positional args go to rest (if present)
            while (posIndex < positionalArgs.Count)
                restArgs.Add(positionalArgs[posIndex++]);

            // Apply defaults for any still-unfilled non-rest parameters
            for (int i = 0; i < fn.Params.Count; i++)
            {
                if (!filled[i] && !fn.Params[i].IsRest)
                {
                    if (fn.Params[i].Default != null)
                    {
                        result[i] = EvaluateWithEnv(fn.Params[i].Default, CurrentEnv);
                        filled[i] = true;
                    }
                    else
                    {
                        throw new MiniDynRuntimeError($"Missing required argument '{fn.Params[i].Name}'");
                    }
                }
            }

            // Build the final positional list to call into UserFunction.Call
            // Place rest args exactly at the rest param position (and rest MUST be last after parser patch).
            var finalArgs = new List<Value>();
            for (int i = 0; i < fn.Params.Count; i++)
            {
                if (fn.Params[i].IsRest)
                {
                    finalArgs.AddRange(restArgs);
                    continue;
                }
                finalArgs.Add(result[i]);
            }
            return finalArgs;
        }


        public Value VisitGrouping(Expr.Grouping e) => Evaluate(e.Inner);

        public Value VisitFunction(Expr.Function e)
        {
            Value capturedThis = null;
            Value t;
            if (e.IsArrow && _env.TryGet("this", out t)) capturedThis = t;
            var kind = e.IsArrow ? UserFunction.Kind.Arrow : UserFunction.Kind.Normal;
            var uf = new UserFunction(null, e.Parameters, e.Body, _env, kind, capturedThis);
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
            var idxV = Evaluate(e.IndexExpr);
            if (target.Type == ValueType.Array)
            {
                int idx = (int)ToNumber(idxV).ToDoubleNV().Dbl;
                var arr = target.AsArray();
                idx = NormalizeIndex(idx, arr.Length);
                if (idx < 0 || idx >= arr.Length) throw new MiniDynRuntimeError("Array index out of range");
                return arr[idx];
            }
            if (target.Type == ValueType.String)
            {
                var s = target.AsString();
                int idx = (int)ToNumber(idxV).ToDoubleNV().Dbl;
                idx = NormalizeIndex(idx, s.Length);
                if (idx < 0 || idx >= s.Length) throw new MiniDynRuntimeError("String index out of range");
                return Value.String(s[idx].ToString());
            }
            if (target.Type == ValueType.Object)
            {
                var obj = target.AsObject();
                var key = ToStringValue(idxV);
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
            var dict = new Dictionary<string, Value>(StringComparer.Ordinal);
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
                dict[key] = val;
            }
            return Value.Object(new ObjectValue(dict));
        }

        public Value VisitProperty(Expr.Property e)
        {
            var objv = Evaluate(e.Target);
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

        private static bool CompareEqual(Value a, Value b)
        {
            if (a.Type == b.Type)
            {
                return a.Equals(b);
            }
            // Optional numeric-string numeric equality
            if (a.Type == ValueType.Number && b.Type == ValueType.String)
            {
                if (NumberValue.TryFromString(b.AsString(), out var nb)) return NumberValue.Compare(a.AsNumber(), nb) == 0;
                return false;
            }
            if (a.Type == ValueType.String && b.Type == ValueType.Number)
            {
                if (NumberValue.TryFromString(a.AsString(), out var na)) return NumberValue.Compare(na, b.AsNumber()) == 0;
                return false;
            }
            return false;
        }

        private static bool CompareRel(Value a, Value b, string op)
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
                    default: return false;
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
                    default: return false;
                }
            }
            throw new MiniDynRuntimeError("Relational comparison only on numbers or strings");
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

        // Helpers for JSON
        private static string EscapeJson(string s)
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

        private string JsonStringifyValue(Value v, bool pretty = false, int indent = 0)
        {
            string Ind() => new string(' ', indent);
            string Ind2() => new string(' ', indent + 2);

            switch (v.Type)
            {
                case ValueType.Nil: return "null";
                case ValueType.Boolean: return v.AsBoolean() ? "true" : "false";
                case ValueType.Number: return v.AsNumber().ToString();
                case ValueType.String: return "\"" + EscapeJson(v.AsString()) + "\"";
                case ValueType.Array:
                    {
                        var arr = v.AsArray();
                        if (arr.Length == 0) return "[]";
                        var parts = new List<string>(arr.Length);
                        for (int k = 0; k < arr.Length; k++)
                            parts.Add(JsonStringifyValue(arr[k], pretty, indent + 2));
                        if (!pretty) return "[" + string.Join(",", parts) + "]";
                        var lines = string.Join(",\n", parts.Select(p => Ind2() + p));
                        return "[\n" + lines + "\n" + Ind() + "]";
                    }
                case ValueType.Object:
                    {
                        var obj = v.AsObject();
                        if (obj.Count == 0) return "{}";
                        var parts = new List<string>(obj.Count);
                        foreach (var kv in obj.Props)
                        {
                            var key = "\"" + EscapeJson(kv.Key) + "\"";
                            var val = JsonStringifyValue(kv.Value, pretty, indent + 2);
                            parts.Add(pretty ? (Ind2() + key + ": " + val) : (key + ":" + val));
                        }
                        if (!pretty) return "{" + string.Join(",", parts) + "}";
                        var lines = string.Join(",\n", parts);
                        return "{\n" + lines + "\n" + Ind() + "}";
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
                        var dict = new Dictionary<string, Value>(StringComparer.Ordinal);
                        foreach (var prop in (JObject)t)
                            dict[prop.Key] = JsonToValue(prop.Value);
                        return Value.Object(new ObjectValue(dict));
                    }

                default:
                    return Value.Nil();
            }
        }
    }

    class Program
    {
        static void Run(string source)
        {
            var lexer = new Lexer(source);
            var parser = new Parser(lexer);
            var program = parser.Parse();
            var interp = new Interpreter();
            interp.Interpret(program);
        }

        static void RunFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File not found: {filePath}");
                return;
            }
            var source = File.ReadAllText(filePath);
            Run(source);
        }

        static void Repl()
        {
            Console.WriteLine("MiniDynLang REPL. Ctrl+C to exit.");
            var interp = new Interpreter();
            while (true)
            {
                Console.Write("> ");
                string line = Console.ReadLine();
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var lexer = new Lexer(line);
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

        static void RunExpectParseError(string source, string label)
        {
            try
            {
                Run(source);
                Console.WriteLine($"FAIL: expected parse error not thrown: {label}");
            }
            catch (MiniDynParseError ex)
            {
                Console.WriteLine($"OK (parse error: {label}) -> {ex.Message}");
            }
        }

        static void RunExpectRuntimeError(string source, string label)
        {
            try
            {
                Run(source);
                Console.WriteLine($"FAIL: expected runtime error not thrown: {label}");
            }
            catch (MiniDynRuntimeError ex)
            {
                Console.WriteLine($"OK (runtime error: {label}) -> {ex.Message}");
            }
        }

        static void Main(string[] args)
        {
            string demo = @"
                    // Patch 1 demo: Objects, property/index assignment, compound ops, destructuring
                    // Patch 2 demo: Arrow functions, this-binding, and named arguments

                    println(""=== ORIGINAL PATCH 1 FEATURES ==="");
                    
                    // Objects and property access
                    let o = { a: 1, b: 2, c: { nested: true }, d: ""str"" };
                    println(o.a, o[""b""], o.c.nested, o[""d""]);
                    o.a += 5;
                    o[""b""] = o[""b""] * 10;
                    println(o.a, o.b);

                    // Computed keys and merge
                    let k = ""x"";
                    let o2 = { [k + ""1""]: 11, [k+""2""]: 22 };
                    println(o2[""x1""], o2[""x2""]);
                    let o3 = merge(o, o2);
                    println(keys(o3));

                    // Arrays, index assignment and compound ops
                    let arr = [1,2,3];
                    arr[0] += 41;
                    arr[2] = arr[2] * 3;
                    println(arr);

                    // Destructuring declarations
                    let [p, q = 20, ...rest] = [10, , 30, 40, 50];
                    println(p, q, rest);

                    const { a, b: bb, z = 99, ...other } = o3;
                    println(a, bb, z, other);

                    // Destructuring assignment
                    [p, q] = [100, 200];
                    { a: o.a, b: o.b } = { a: 7, b: 8 }; // assign into object properties
                    println(p, q, o.a, o.b);

                    // Object index assignment
                    o[""newKey""] = 123;
                    println(has_key(o, ""newKey""), o[""newKey""]);

                    // Strings and numbers still work
                    println(""Hello "" + ""World"", 1 + 2 + 3);

                    println();
                    println(""=== EXTRA FEATURES ==="");
                    
                    // Arrow functions - expression body
                    println(""--- Arrow Functions ---"");
                    let add = (x, y) => x + y;
                    println(""add(5, 3) ="", add(5, 3));
                    
                    // Single parameter arrow function (no parentheses)
                    let double = x => x * 2;
                    println(""double(7) ="", double(7));
                    
                    // Arrow function with no parameters
                    let getMessage = () => ""Hello from arrow function!"";
                    println(getMessage());
                    
                    // Arrow function with block body
                    let factorial = n => {
                        if (n <= 1) return 1;
                        return n * factorial(n - 1);
                    };
                    println(""factorial(5) ="", factorial(5));
                    
                    // Arrow functions with default parameters
                    let greet = (name, greeting = ""Hello"") => greeting + "", "" + name + ""!"";
                    println(greet(""World""));
                    println(greet(""Alice"", ""Hi""));
                    
                    // Arrow function with rest parameters
                    let sum = (...nums) => {
                        let total = 0;
                        let i = 0;
                        while (i < length(nums)) {
                            total = total + nums[i];
                            i = i + 1;
   }
                        return total;
                    };
                    println(""sum(1, 2, 3, 4, 5) ="", sum(1, 2, 3, 4, 5));
                    
                    // Using arrow functions with higher-order functions
                    let numbers = [1, 2, 3, 4, 5];
                    let doubled = [];
                    let i = 0;
                    while (i < length(numbers)) {
                        push(doubled, double(numbers[i]));
                        i = i + 1;
                    }
                    println(""doubled:"", doubled);
                    
                    // This-binding and methods
                    println();
                    println(""--- This-binding and Methods ---"");
                    
                    let person = {
                        name: ""John"",
                        age: 30,
                        greet: fn() {
                            return ""Hello, I'm "" + this.name + "" and I'm "" + this.age + "" years old."";
                        },
                        haveBirthday: fn() {
                            this.age = this.age + 1;
                            return ""Happy birthday! Now "" + this.age + "" years old."";
                        }
                    };
                    
                    println(person.greet());
                    println(person.haveBirthday());
                    println(person.greet());
                    
                    // Method with arrow function - note: arrow functions don't bind their own 'this'
                    let calculator = {
                        value: 10,
                        add: fn(n) { this.value = this.value + n; return this.value; },
                        multiply: fn(n) { this.value = this.value * n; return this.value; },
                        getValue: fn() { return this.value; },
                        // Test nested this access
                        getDoubler: fn() {
                            return fn() { return this.value * 2; };
                        }
                    };
                    
                    println(""Initial value:"", calculator.getValue());
                    println(""After add(5):"", calculator.add(5));
                    println(""After multiply(2):"", calculator.multiply(2));
                    let doubler = calculator.getDoubler();
                    println(""Doubled value:"", doubler());
                    
                    // Named arguments
                    println();
                    println(""--- Named Arguments ---"");
                    
                    // Function with default parameters
                    fn createUser(name, age = 25, city = ""Unknown"", active = true) {
                        return {
                            name: name,
                            age: age,
                            city: city,
                            active: active
                        };
                    }
                    
                    // Call with positional arguments
                    let user1 = createUser(""Alice"", 28, ""New York"", true);
                    println(""user1:"", user1);
                    
                    // Call with named arguments
                    let user2 = createUser(name: ""Bob"", city: ""London"", age: 35);
                    println(""user2:"", user2);
                    
                    // Mixed positional and named arguments
                    let user3 = createUser(""Charlie"", city: ""Tokyo"", active: false);
                    println(""user3:"", user3);
                    
                    // All named arguments in different order
                    let user4 = createUser(active: false, name: ""David"", age: 40, city: ""Paris"");
                    println(""user4:"", user4);
                    
                    // Function with rest parameters and named arguments
                    fn formatMessage(template, prefix = ""MSG: "", suffix = ""!"", ...values) {
                        let result = prefix + template;
                        let i = 0;
                        while (i < length(values)) {
                            result = result + "" ["" + values[i] + ""]"";
                            i = i + 1;
                        }
                        return result + suffix;
                    }

                    // Pass explicit prefix/suffix when you also want extra trailing values:
                    println(formatMessage(""Error"", ""MSG: "", ""!"", ""404"", ""Not Found""));
                    println(formatMessage(""Info"", prefix: ""INFO: "", suffix: "" [OK]""));
                    println(formatMessage(template: ""Warning"", prefix: ""WARN: ""));
                    
                    // Arrow function with named arguments
                    let configure = (host = ""localhost"", port = 8080, ssl = false) => {
                        return {
                            url: (ssl ? ""https://"" : ""http://"") + host + "":"" + port
                        };
                    };
                    
                    println(configure());
                    println(configure(port: 443, ssl: true));
                    println(configure(""example.com"", ssl: true, port: 9000));

                    println();
                    println(""=== REST-LAST TESTS ==="");

                    fn restLastDemo(a, b = 2, ...r) {
                        println(""a="", a, "" b="", b, "" rest="", r);
                    }
                    restLastDemo(1);                  // a=1 b=2 rest=[]
                    restLastDemo(1, 3, 4, 5);         // a=1 b=3 rest=[4, 5]
                    restLastDemo(b: 10, a: 7, 8, 9);  // a=7 b=10 rest=[8, 9]

                    // Complex example combining all features
                    println();
                    println(""--- Combined Features Example ---"");
                    
                    let team = {
                        name: ""Development Team"",
                        members: [],
                        addMember: fn(name, role = ""Developer"", skills = []) {
                            let member = {
                                name: name,
                                role: role,
                                skills: skills,
                                introduce: fn() {
                                    return ""Hi, I'm "" + this.name + "", a "" + this.role + 
                                        "" with skills: "" + join(this.skills, "", "");
                                }
                            };
                            push(this.members, member);
                            return member;
                        },
                        listMembers: fn() {
                            println(""Team: "" + this.name);
                            let i = 0;
                            while (i < length(this.members)) {
                                println(""  - "" + this.members[i].introduce());
                                i = i + 1;
                            }
                        }
                    };
                    
                    // Add members using different argument styles
                    team.addMember(""Alice"", ""Lead Developer"", [""JavaScript"", ""Python""]);
                    team.addMember(name: ""Bob"", skills: [""Java"", ""C++""], role: ""Senior Developer"");
                    team.addMember(""Charlie"", skills: [""Go"", ""Rust""]);
                    
                    // Arrow function to find members by skill
                    let findBySkill = skill => {
                        let found = [];
                        let i = 0;
                        while (i < length(team.members)) {
                            let member = team.members[i];
                            let j = 0;
                            while (j < length(member.skills)) {
                                if (member.skills[j] == skill) {
                                    push(found, member.name);
                                    break;
                                }
                                j = j + 1;
                            }
                            i = i + 1;
                        }
                        return found;
                    };
                    
                    team.listMembers();
                    println(""Members who know Python:"", findBySkill(""Python""));
                    println(""Members who know Go:"", findBySkill(""Go""));
                    println();
                    println(""=== ARROW LEXICAL 'this' TESTS ==="");

                    // A counter object to test 'this' semantics.
                    let counter = {
                        n: 0,
                        inc: fn() { this.n = this.n + 1; return this.n; },

                        // Returns an ARROW that uses 'this' lexically (captured at creation in inc/maker).
                        makeArrowGetter: fn() {
                            // lexical 'this' is the counter when this method is called
                            return () => this.n;
                        },

                        // Returns a NORMAL function; when called as a method of another object,
                        // it should use that object's 'this'.
                        makeNormalGetter: fn() {
                            return fn() { return this.n; };
                        }
                    };

                    // bump to a known state
                    counter.inc(); // 1
                    counter.inc(); // 2

                    let getArrow = counter.makeArrowGetter();
                    let getNormal = counter.makeNormalGetter();

                    println(""counter.n ="", counter.n);
                    println(""getArrow() -> expect 2:"", getArrow()); // lexical this = counter

                    // Borrow the functions onto another object and call as methods.
                    let other = { n: 100 };
                    other.get = getArrow;
                    println(""borrowed arrow via other.get() -> expect 2:"", other.get()); // still 2 (arrow ignores call-site 'this')

                    other.get = getNormal;
                    println(""borrowed normal via other.get() -> expect 100:"", other.get()); // dynamic this = other

                    // Re-check that counter still unchanged by above calls other than our earlier incs.
                    println(""counter.n (still 2) ="", counter.n);

                    // Another object that exposes an arrow to its own 'this'
                    let holder = {
                        v: 7,
                        make: fn() { return () => this.v; },   // arrow captures holder at creation
                        norm: fn() { return this.v; }          // normal uses dynamic 'this'
                    };

                    let a = holder.make();
                    println(""a() -> expect 7:"", a());
                    let bobj = { v: 123, call: a };
                    println(""bobj.call() borrowed arrow -> expect 7:"", bobj.call()); // arrow ignores rebinding

                    let borrowNorm = { v: 123, m: holder.norm };
                    println(""borrowed normal m() -> expect 123:"", borrowNorm.m()); // normal respects call-site

                    // Ensure property-call on an arrow returned from a method does not rebind:
                    let wrapper = { m: getArrow, n: 999 };
                    println(""wrapper.m() (arrow) -> expect 2:"", wrapper.m());

                    println();
                    println(""=== BUILTIN SHOWCASE (non-interactive) ==="");
                    // gets() is interactive; omitted to keep demo non-blocking

                    // Basics
                    print(""print without newline -> ""); println(""done"");
                    println(""to_number('42.5') ="", to_number(""42.5""));
                    println(""to_string(123) ="", to_string(123));
                    println(""type([1,2,3]) ="", type([1,2,3]));

                    // Object key ops
                    let objDemo = { k1: 1, k2: 2 };
                    println(""remove_key k2 ="", remove_key(objDemo, ""k2""), "" keys="", keys(objDemo));

                    println();
                    println(""--- Math ---"");
                    println(""abs(-5) ="", abs(-5));
                    println(""floor(3.7) ="", floor(3.7), "" ceil(3.1) ="", ceil(3.1), "" round(3.5) ="", round(3.5));
                    println(""sqrt(9) ="", sqrt(9), "" pow(2,10) ="", pow(2,10));
                    println(""min(3,1,2) ="", min(3,1,2), "" max(3,1,2) ="", max(3,1,2));
                    srand(123);
                    println(""random seeded -> "", random(), "" "", random());

                    println();
                    println(""--- String ---"");
                    let s = ""  Hello, MiniDyn!  "";
                    println(""substring(s, 2, 5) ="", substring(s, 2, 5));
                    println(""index_of(s, 'Mini') ="", index_of(s, ""Mini""));
                    println(""contains(s, 'Dyn') ="", contains(s, ""Dyn""));
                    println(""starts_with(s, '  He') ="", starts_with(s, ""  He""));
                    println(""ends_with(s, '!  ') ="", ends_with(s, ""!  ""));
                    println(""to_upper(s) ="", to_upper(s));
                    println(""to_lower(s) ="", to_lower(s));
                    println(""trim(s) ="", trim(s));
                    let parts = split(""a,b,c"", "",""); println(""split -> "", parts);
                    println(""parse_int('123') ="", parse_int(""123""), "" parse_float('3.14') ="", parse_float(""3.14""));

                    println();
                    println(""--- Array ---"");
                    let arr2 = array(10, 20, 30);
                    println(""array -> "", arr2);
                    println(""push -> length ="", push(arr2, 40, 50), "" arr2="", arr2);
                    println(""pop -> "", pop(arr2), "" arr2="", arr2);
                    println(""at(arr2, -1) ="", at(arr2, -1));
                    println(""slice(arr2, 1) ="", slice(arr2, 1));
                    set_at(arr2, 0, 99); println(""set_at -> "", arr2);
                    let arrClone = clone(arr2); println(""clone(arr2) ="", arrClone);

                    println(""now_ms() ="", now_ms());

                    println();
                    println(""--- Object helpers ---"");
                    let obj2 = { a: 1, b: 2, c: 3 };
                    println(""values(obj2) ="", values(obj2));
                    let ents = entries(obj2); println(""entries(obj2) ="", ents);
                    let obj3 = from_entries(ents); println(""from_entries(entries(obj2)) ="", obj3);

                    println();
                    println(""--- Functional array helpers ---"");
                    let nums = [1,2,3,4,5,2,3];
                    println(""map x*2 -> "", map(nums, x => x * 2));
                    println(""filter x>2 -> "", filter(nums, x => x > 2));
                    println(""reduce sum -> "", reduce(nums, (a, b) => a + b, 0));
                    println(""sort default -> "", sort(nums));
                    println(""sort desc -> "", sort(nums, (a, b) => b - a));
                    println(""unique(nums) -> "", unique(nums));
                    println(""range(5) -> "", range(5));
                    println(""range(2, 8, 2) -> "", range(2, 8, 2));

                    println();
                    println(""--- More strings ---"");
                    println(""replace('foo bar','bar','baz') -> "", replace(""foo bar"", ""bar"", ""baz""));
                    println(""repeat('ab',3) -> "", repeat(""ab"", 3));
                    println(""pad_start('7',3,'0') -> "", pad_start(""7"", 3, ""0""));
                    println(""pad_end('7',3,'0') -> "", pad_end(""7"", 3, ""0""));

                    println(""sleeping 10ms...""); sleep_ms(10); println(""awake"");

                    println();
                    println(""--- JSON ---"");
                    let j = json_stringify({ msg: ""ok"", data: [1,2,3] }, true);
                    println(j);
                    let parsed = json_parse(j);
                    println(parsed);

                ";
            try
            {
                if (args.Length == 0)
                {
                    Repl();
                }
                else if (args[0] == "-demo")
                {
                    Run(demo);

                    Console.WriteLine();
                    Console.WriteLine("=== NEGATIVE TESTS (should error) ===");

                    // 1) Rest not last -> parse error
                    RunExpectParseError(@"fn bad1(a, ...r, b) { return 0; }", "rest must be last");

                    // 2) Duplicate parameter names -> parse error
                    RunExpectParseError(@"fn bad2(x, y, x) { return 0; }", "duplicate parameter name");

                    // 3) Unknown named argument -> runtime error
                    RunExpectRuntimeError(@"fn f(a) { println(a); } f(b: 1);", "unknown named argument");
                }
                else
                {
                    RunFile(args[0]);
                }
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

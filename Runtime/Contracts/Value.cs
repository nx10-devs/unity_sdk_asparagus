using System;
using System.Globalization;

namespace Asparagus.Sdk.Contracts
{
    public enum ValueKind : byte
    {
        None = 0,
        Text = 1,
        Number = 2,
        Flag = 3
    }

    /// <summary>
    /// Unboxed tagged union representing <c>asparagus_contract.v1.Value</c>
    /// (<c>oneof kind { string text = 1; double number = 2; bool flag = 3; }</c>).
    /// </summary>
    public readonly struct Value : IEquatable<Value>
    {
        public static readonly Value Empty = default;

        private readonly ValueKind _kind;
        private readonly string _text;
        private readonly double _number;
        private readonly bool _flag;

        private Value(ValueKind kind, string text, double number, bool flag)
        {
            _kind = kind;
            _text = text;
            _number = number;
            _flag = flag;
        }

        public ValueKind Kind
        {
            get { return _kind; }
        }

        public bool IsEmpty
        {
            get { return _kind == ValueKind.None; }
        }

        public string TextValue
        {
            get { return _kind == ValueKind.Text ? (_text ?? string.Empty) : string.Empty; }
        }

        public double NumberValue
        {
            get { return _kind == ValueKind.Number ? _number : 0d; }
        }

        public bool FlagValue
        {
            get { return _kind == ValueKind.Flag && _flag; }
        }

        public static Value FromText(string value)
        {
            return new Value(ValueKind.Text, value ?? string.Empty, 0d, false);
        }

        public static Value FromNumber(double value)
        {
            return new Value(ValueKind.Number, null, value, false);
        }

        public static Value FromFlag(bool value)
        {
            return new Value(ValueKind.Flag, null, 0d, value);
        }

        public bool TryGetText(out string value)
        {
            if (_kind == ValueKind.Text)
            {
                value = _text ?? string.Empty;
                return true;
            }

            value = string.Empty;
            return false;
        }

        public bool TryGetNumber(out double value)
        {
            if (_kind == ValueKind.Number)
            {
                value = _number;
                return true;
            }

            value = 0d;
            return false;
        }

        public bool TryGetFlag(out bool value)
        {
            if (_kind == ValueKind.Flag)
            {
                value = _flag;
                return true;
            }

            value = false;
            return false;
        }

        public static implicit operator Value(string value)
        {
            return value == null ? Empty : FromText(value);
        }

        public static implicit operator Value(double value)
        {
            return FromNumber(value);
        }

        public static implicit operator Value(float value)
        {
            return FromNumber(value);
        }

        public static implicit operator Value(int value)
        {
            return FromNumber(value);
        }

        public static implicit operator Value(long value)
        {
            return FromNumber(value);
        }

        public static implicit operator Value(bool value)
        {
            return FromFlag(value);
        }

        public bool Equals(Value other)
        {
            if (_kind != other._kind)
            {
                return false;
            }

            switch (_kind)
            {
                case ValueKind.None:
                    return true;
                case ValueKind.Text:
                    return string.Equals(_text ?? string.Empty, other._text ?? string.Empty, StringComparison.Ordinal);
                case ValueKind.Number:
                    return _number.Equals(other._number);
                case ValueKind.Flag:
                    return _flag == other._flag;
                default:
                    return false;
            }
        }

        public override bool Equals(object obj)
        {
            return obj is Value other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)_kind;
                switch (_kind)
                {
                    case ValueKind.Text:
                        hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(_text ?? string.Empty);
                        break;
                    case ValueKind.Number:
                        hash = (hash * 397) ^ _number.GetHashCode();
                        break;
                    case ValueKind.Flag:
                        hash = (hash * 397) ^ (_flag ? 1 : 0);
                        break;
                }

                return hash;
            }
        }

        public static bool operator ==(Value left, Value right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Value left, Value right)
        {
            return !left.Equals(right);
        }

        public override string ToString()
        {
            switch (_kind)
            {
                case ValueKind.Text:
                    return _text ?? string.Empty;
                case ValueKind.Number:
                    return _number.ToString("R", CultureInfo.InvariantCulture);
                case ValueKind.Flag:
                    return _flag ? "true" : "false";
                default:
                    return string.Empty;
            }
        }
    }
}

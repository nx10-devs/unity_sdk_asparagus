using System;
using System.Collections.Generic;
using System.IO;

namespace Asparagus.Sdk.Internal.Encoding
{
    /// <summary>
    /// Reusable, allocation-free Protobuf wire-format writer supporting Proto3 primitives,
    /// ZigZag sint32/sint64, packed repeated sint32, and in-place length-delimited submessages.
    /// </summary>
    public sealed class ProtobufWireWriter
    {
        public const int WireTypeVarint = 0;
        public const int WireTypeFixed64 = 1;
        public const int WireTypeLengthDelimited = 2;
        public const int WireTypeFixed32 = 5;

        private const int DefaultInitialCapacity = 16384;

        private byte[] _buffer;
        private int _position;

        public ProtobufWireWriter(int initialCapacity = DefaultInitialCapacity)
        {
            if (initialCapacity < 64)
            {
                initialCapacity = 64;
            }

            _buffer = new byte[initialCapacity];
            _position = 0;
        }

        public byte[] Buffer
        {
            get { return _buffer; }
        }

        public int Length
        {
            get { return _position; }
        }

        public void Reset()
        {
            _position = 0;
        }

        public byte[] ToArray()
        {
            byte[] result = new byte[_position];
            if (_position > 0)
            {
                System.Buffer.BlockCopy(_buffer, 0, result, 0, _position);
            }

            return result;
        }

        public void WriteTo(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (_position > 0)
            {
                stream.Write(_buffer, 0, _position);
            }
        }

        public static uint EncodeZigZag32(int value)
        {
            unchecked
            {
                return (uint)((value << 1) ^ (value >> 31));
            }
        }

        public static ulong EncodeZigZag64(long value)
        {
            unchecked
            {
                return (ulong)((value << 1) ^ (value >> 63));
            }
        }

        public static int ComputeVarint32Size(uint value)
        {
            if ((value & (0xffffffffu << 7)) == 0) return 1;
            if ((value & (0xffffffffu << 14)) == 0) return 2;
            if ((value & (0xffffffffu << 21)) == 0) return 3;
            if ((value & (0xffffffffu << 28)) == 0) return 4;
            return 5;
        }

        public void WriteTag(int fieldNumber, int wireType)
        {
            WriteRawVarint32((uint)((fieldNumber << 3) | wireType));
        }

        public void WriteRawVarint32(uint value)
        {
            EnsureCapacity(5);
            while (value >= 0x80u)
            {
                _buffer[_position++] = (byte)((value & 0x7fu) | 0x80u);
                value >>= 7;
            }

            _buffer[_position++] = (byte)value;
        }

        public void WriteRawVarint64(ulong value)
        {
            EnsureCapacity(10);
            while (value >= 0x80ul)
            {
                _buffer[_position++] = (byte)((value & 0x7ful) | 0x80ul);
                value >>= 7;
            }

            _buffer[_position++] = (byte)value;
        }

        public void WriteUInt32(int fieldNumber, uint value)
        {
            if (value == 0)
            {
                return;
            }

            WriteTag(fieldNumber, WireTypeVarint);
            WriteRawVarint32(value);
        }

        public void WriteUInt64(int fieldNumber, ulong value)
        {
            if (value == 0)
            {
                return;
            }

            WriteTag(fieldNumber, WireTypeVarint);
            WriteRawVarint64(value);
        }

        public void WriteEnum(int fieldNumber, int value)
        {
            if (value == 0)
            {
                return;
            }

            WriteTag(fieldNumber, WireTypeVarint);
            unchecked
            {
                WriteRawVarint64((ulong)value);
            }
        }

        public void WriteBool(int fieldNumber, bool value)
        {
            if (!value)
            {
                return;
            }

            WriteBoolAlways(fieldNumber, true);
        }

        public void WriteBoolAlways(int fieldNumber, bool value)
        {
            WriteTag(fieldNumber, WireTypeVarint);
            EnsureCapacity(1);
            _buffer[_position++] = value ? (byte)1 : (byte)0;
        }

        public void WriteSInt32(int fieldNumber, int value)
        {
            if (value == 0)
            {
                return;
            }

            WriteSInt32Always(fieldNumber, value);
        }

        public void WriteSInt32Always(int fieldNumber, int value)
        {
            WriteTag(fieldNumber, WireTypeVarint);
            WriteRawVarint32(EncodeZigZag32(value));
        }

        public void WriteSInt64(int fieldNumber, long value)
        {
            if (value == 0)
            {
                return;
            }

            WriteSInt64Always(fieldNumber, value);
        }

        public void WriteSInt64Always(int fieldNumber, long value)
        {
            WriteTag(fieldNumber, WireTypeVarint);
            WriteRawVarint64(EncodeZigZag64(value));
        }

        public void WriteFloat(int fieldNumber, float value)
        {
            if (value == 0f)
            {
                return;
            }

            WriteFloatAlways(fieldNumber, value);
        }

        public void WriteFloatAlways(int fieldNumber, float value)
        {
            WriteTag(fieldNumber, WireTypeFixed32);
            EnsureCapacity(4);
            int bits = BitConverter.SingleToInt32Bits(value);
            _buffer[_position++] = (byte)bits;
            _buffer[_position++] = (byte)(bits >> 8);
            _buffer[_position++] = (byte)(bits >> 16);
            _buffer[_position++] = (byte)(bits >> 24);
        }

        public void WriteDouble(int fieldNumber, double value)
        {
            if (value == 0d)
            {
                return;
            }

            WriteDoubleAlways(fieldNumber, value);
        }

        public void WriteDoubleAlways(int fieldNumber, double value)
        {
            WriteTag(fieldNumber, WireTypeFixed64);
            EnsureCapacity(8);
            long bits = BitConverter.DoubleToInt64Bits(value);
            _buffer[_position++] = (byte)bits;
            _buffer[_position++] = (byte)(bits >> 8);
            _buffer[_position++] = (byte)(bits >> 16);
            _buffer[_position++] = (byte)(bits >> 24);
            _buffer[_position++] = (byte)(bits >> 32);
            _buffer[_position++] = (byte)(bits >> 40);
            _buffer[_position++] = (byte)(bits >> 48);
            _buffer[_position++] = (byte)(bits >> 56);
        }

        public void WriteString(int fieldNumber, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            WriteStringAlways(fieldNumber, value);
        }

        public void WriteStringAlways(int fieldNumber, string value)
        {
            string safe = value ?? string.Empty;
            WriteTag(fieldNumber, WireTypeLengthDelimited);
            if (safe.Length == 0)
            {
                WriteRawVarint32(0);
                return;
            }

            int byteCount = System.Text.Encoding.UTF8.GetByteCount(safe);
            WriteRawVarint32((uint)byteCount);
            EnsureCapacity(byteCount);
            System.Text.Encoding.UTF8.GetBytes(safe, 0, safe.Length, _buffer, _position);
            _position += byteCount;
        }

        public void WritePackedSInt32(int fieldNumber, List<int> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            int count = values.Count;
            int totalBytes = 0;
            for (int i = 0; i < count; i++)
            {
                totalBytes += ComputeVarint32Size(EncodeZigZag32(values[i]));
            }

            WriteTag(fieldNumber, WireTypeLengthDelimited);
            WriteRawVarint32((uint)totalBytes);
            EnsureCapacity(totalBytes);

            for (int i = 0; i < count; i++)
            {
                uint zz = EncodeZigZag32(values[i]);
                while (zz >= 0x80u)
                {
                    _buffer[_position++] = (byte)((zz & 0x7fu) | 0x80u);
                    zz >>= 7;
                }

                _buffer[_position++] = (byte)zz;
            }
        }

        /// <summary>
        /// Begins writing a length-delimited submessage in place without allocating an intermediate buffer.
        /// Must be paired with <see cref="EndLengthDelimited"/>.
        /// </summary>
        public int BeginLengthDelimited(int fieldNumber)
        {
            WriteTag(fieldNumber, WireTypeLengthDelimited);
            EnsureCapacity(1);
            int lengthPos = _position;
            _position += 1;
            return lengthPos;
        }

        /// <summary>
        /// Backpatches the byte length for a submessage started with <see cref="BeginLengthDelimited"/>.
        /// </summary>
        public void EndLengthDelimited(int lengthPos)
        {
            int payloadStart = lengthPos + 1;
            int payloadLen = _position - payloadStart;
            int varintSize = ComputeVarint32Size((uint)payloadLen);

            if (varintSize == 1)
            {
                _buffer[lengthPos] = (byte)payloadLen;
                return;
            }

            int shift = varintSize - 1;
            EnsureCapacity(shift);
            if (payloadLen > 0)
            {
                System.Buffer.BlockCopy(_buffer, payloadStart, _buffer, payloadStart + shift, payloadLen);
            }

            int finalPosition = _position + shift;
            _position = lengthPos;
            WriteRawVarint32((uint)payloadLen);
            _position = finalPosition;
        }

        private void EnsureCapacity(int additionalBytes)
        {
            int required = _position + additionalBytes;
            if (required <= _buffer.Length)
            {
                return;
            }

            int newCapacity = _buffer.Length * 2;
            while (newCapacity < required)
            {
                newCapacity *= 2;
            }

            byte[] grown = new byte[newCapacity];
            System.Buffer.BlockCopy(_buffer, 0, grown, 0, _position);
            _buffer = grown;
        }
    }
}

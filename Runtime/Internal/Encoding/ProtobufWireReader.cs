using System;
using System.Collections.Generic;

namespace Asparagus.Sdk.Internal.Encoding
{
    /// <summary>
    /// Bounds-safe, non-throwing Protobuf binary wire reader implemented as a value type
    /// so nested submessage readers incur zero heap allocations.
    /// </summary>
    public struct ProtobufWireReader
    {
        private const int MaxRecursionDepth = 64;

        private readonly byte[] _buffer;
        private int _pos;
        private readonly int _end;
        private readonly int _depth;

        public ProtobufWireReader(byte[] buffer, int offset, int count)
            : this(buffer, offset, count, 0)
        {
        }

        private ProtobufWireReader(byte[] buffer, int offset, int count, int depth)
        {
            _buffer = buffer;
            _pos = offset;
            _end = offset + count;
            _depth = depth;
        }

        public bool IsAtEnd
        {
            get { return _buffer == null || _pos >= _end; }
        }

        public int Remaining
        {
            get { return _buffer == null ? 0 : (_end - _pos); }
        }

        public static int DecodeZigZag32(uint value)
        {
            unchecked
            {
                return (int)((value >> 1) ^ (uint)-(int)(value & 1u));
            }
        }

        public static long DecodeZigZag64(ulong value)
        {
            unchecked
            {
                return (long)((value >> 1) ^ (ulong)-(long)(value & 1ul));
            }
        }

        public bool TryReadTag(out int fieldNumber, out int wireType)
        {
            fieldNumber = 0;
            wireType = 0;

            if (!TryReadVarint32(out uint tag))
            {
                return false;
            }

            fieldNumber = (int)(tag >> 3);
            wireType = (int)(tag & 0x07u);
            if (fieldNumber <= 0)
            {
                return false;
            }

            // Proto3 only permits wire types 0 (varint), 1 (fixed64), 2 (length-delimited), 5 (fixed32).
            return wireType == ProtobufWireWriter.WireTypeVarint
                || wireType == ProtobufWireWriter.WireTypeFixed64
                || wireType == ProtobufWireWriter.WireTypeLengthDelimited
                || wireType == ProtobufWireWriter.WireTypeFixed32;
        }

        public bool TryReadVarint32(out uint value)
        {
            if (!TryReadVarint64(out ulong raw))
            {
                value = 0;
                return false;
            }

            unchecked
            {
                value = (uint)raw;
            }
            return true;
        }

        public bool TryReadVarint64(out ulong value)
        {
            value = 0;
            if (_buffer == null)
            {
                return false;
            }

            int shift = 0;
            for (int i = 0; i < 10; i++)
            {
                if (_pos >= _end)
                {
                    return false;
                }

                byte b = _buffer[_pos++];
                if (i == 9 && b > 1)
                {
                    return false;
                }

                value |= (ulong)(b & 0x7fu) << shift;
                if ((b & 0x80u) == 0)
                {
                    return true;
                }

                shift += 7;
            }

            return false;
        }

        public bool TryReadInt32(out int value)
        {
            if (!TryReadVarint64(out ulong raw))
            {
                value = 0;
                return false;
            }

            unchecked
            {
                value = (int)raw;
            }
            return true;
        }

        public bool TryReadSInt32(out int value)
        {
            if (!TryReadVarint64(out ulong raw))
            {
                value = 0;
                return false;
            }

            unchecked
            {
                value = DecodeZigZag32((uint)raw);
            }
            return true;
        }

        public bool TryReadSInt64(out long value)
        {
            if (!TryReadVarint64(out ulong raw))
            {
                value = 0;
                return false;
            }

            value = DecodeZigZag64(raw);
            return true;
        }

        public bool TryReadBool(out bool value)
        {
            if (!TryReadVarint64(out ulong raw))
            {
                value = false;
                return false;
            }

            value = raw != 0;
            return true;
        }

        public bool TryReadFixed32(out uint value)
        {
            if (_buffer == null || _end - _pos < 4)
            {
                value = 0;
                return false;
            }

            unchecked
            {
                value = (uint)_buffer[_pos]
                    | ((uint)_buffer[_pos + 1] << 8)
                    | ((uint)_buffer[_pos + 2] << 16)
                    | ((uint)_buffer[_pos + 3] << 24);
            }
            _pos += 4;
            return true;
        }

        public bool TryReadFloat(out float value)
        {
            if (!TryReadFixed32(out uint bits))
            {
                value = 0f;
                return false;
            }

            unchecked
            {
                value = BitConverter.Int32BitsToSingle((int)bits);
            }
            return true;
        }

        public bool TryReadFixed64(out ulong value)
        {
            if (_buffer == null || _end - _pos < 8)
            {
                value = 0;
                return false;
            }

            unchecked
            {
                value = (ulong)_buffer[_pos]
                    | ((ulong)_buffer[_pos + 1] << 8)
                    | ((ulong)_buffer[_pos + 2] << 16)
                    | ((ulong)_buffer[_pos + 3] << 24)
                    | ((ulong)_buffer[_pos + 4] << 32)
                    | ((ulong)_buffer[_pos + 5] << 40)
                    | ((ulong)_buffer[_pos + 6] << 48)
                    | ((ulong)_buffer[_pos + 7] << 56);
            }
            _pos += 8;
            return true;
        }

        public bool TryReadDouble(out double value)
        {
            if (!TryReadFixed64(out ulong bits))
            {
                value = 0d;
                return false;
            }

            unchecked
            {
                value = BitConverter.Int64BitsToDouble((long)bits);
            }
            return true;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            if (!TryReadLength(out int length))
            {
                return false;
            }

            if (length == 0)
            {
                return true;
            }

            value = System.Text.Encoding.UTF8.GetString(_buffer, _pos, length);
            _pos += length;
            return true;
        }

        public bool TryReadSubReader(out ProtobufWireReader subReader)
        {
            subReader = default;
            if (_depth >= MaxRecursionDepth)
            {
                return false;
            }

            if (!TryReadLength(out int length))
            {
                return false;
            }

            subReader = new ProtobufWireReader(_buffer, _pos, length, _depth + 1);
            _pos += length;
            return true;
        }

        public bool TryReadPackedOrSingleSInt32(int wireType, List<int> destination)
        {
            if (destination == null)
            {
                return false;
            }

            if (wireType == ProtobufWireWriter.WireTypeLengthDelimited)
            {
                if (!TryReadSubReader(out ProtobufWireReader packed))
                {
                    return false;
                }

                while (!packed.IsAtEnd)
                {
                    if (!packed.TryReadSInt32(out int v))
                    {
                        return false;
                    }

                    destination.Add(v);
                }

                return true;
            }

            if (wireType == ProtobufWireWriter.WireTypeVarint)
            {
                if (!TryReadSInt32(out int v))
                {
                    return false;
                }

                destination.Add(v);
                return true;
            }

            return false;
        }

        public bool TrySkipField(int wireType)
        {
            switch (wireType)
            {
                case ProtobufWireWriter.WireTypeVarint:
                    return TryReadVarint64(out _);

                case ProtobufWireWriter.WireTypeFixed64:
                    if (_buffer == null || _end - _pos < 8)
                    {
                        return false;
                    }
                    _pos += 8;
                    return true;

                case ProtobufWireWriter.WireTypeLengthDelimited:
                    if (!TryReadLength(out int length))
                    {
                        return false;
                    }
                    _pos += length;
                    return true;

                case ProtobufWireWriter.WireTypeFixed32:
                    if (_buffer == null || _end - _pos < 4)
                    {
                        return false;
                    }
                    _pos += 4;
                    return true;

                default:
                    return false;
            }
        }

        private bool TryReadLength(out int length)
        {
            length = 0;
            if (!TryReadVarint64(out ulong raw))
            {
                return false;
            }

            if (raw > (ulong)int.MaxValue)
            {
                return false;
            }

            int len = (int)raw;
            if (_buffer == null || len < 0 || len > _end - _pos)
            {
                return false;
            }

            length = len;
            return true;
        }
    }
}

using System.Text;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Encoding
{
    /// <summary>
    /// UTF-8 byte-length bounds helper guaranteeing valid UTF-8 truncation without splitting
    /// multi-byte UTF-8 sequences or UTF-16 surrogate pairs.
    /// </summary>
    public static class Utf8StringBounds
    {
        public static int GetUtf8ByteCount(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            return System.Text.Encoding.UTF8.GetByteCount(value);
        }

        public static string TruncateUtf8(string value, int maxBytes = BatchConstants.MaxHeaderStringBytes)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value ?? string.Empty;
            }

            if (maxBytes <= 0)
            {
                return string.Empty;
            }

            // Fast path: worst-case BMP expansion is 3 UTF-8 bytes per UTF-16 code unit
            // (surrogate pairs are 2 code units -> 4 bytes = 2 bytes/code unit).
            if (value.Length <= maxBytes / 3 || System.Text.Encoding.UTF8.GetByteCount(value) <= maxBytes)
            {
                return value;
            }

            int byteCount = 0;
            int index = 0;
            int length = value.Length;

            while (index < length)
            {
                char c = value[index];
                int charBytes;
                int charAdvance = 1;

                if (c < 0x80)
                {
                    charBytes = 1;
                }
                else if (c < 0x800)
                {
                    charBytes = 2;
                }
                else if (char.IsHighSurrogate(c))
                {
                    if (index + 1 < length && char.IsLowSurrogate(value[index + 1]))
                    {
                        charBytes = 4;
                        charAdvance = 2;
                    }
                    else
                    {
                        // Unpaired surrogate encodes as U+FFFD (3 bytes in UTF-8).
                        charBytes = 3;
                    }
                }
                else
                {
                    charBytes = 3;
                }

                if (byteCount + charBytes > maxBytes)
                {
                    break;
                }

                byteCount += charBytes;
                index += charAdvance;
            }

            return index == 0 ? string.Empty : value.Substring(0, index);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    ///     Splits a TCP byte stream into complete top-level JSON values by tracking
    ///     brace depth outside string literals. JSON structural characters are ASCII,
    ///     so scanning raw UTF-8 bytes is safe and a multi-byte character split
    ///     across reads is decoded only once its message is complete.
    /// </summary>
    public class JsonMessageFramer
    {
        public const int MaxMessageBytes = 64 * 1024 * 1024;

        private readonly MemoryStream _pending = new MemoryStream();
        private int _depth;
        private bool _inString;
        private bool _escaped;

        public IEnumerable<string> Append(byte[] data, int count)
        {
            var messages = new List<string>();
            for (int i = 0; i < count; i++)
            {
                byte b = data[i];

                // Skip whitespace between messages.
                if (_depth == 0 && _pending.Length == 0 && (b == ' ' || b == '\r' || b == '\n' || b == '\t'))
                    continue;

                _pending.WriteByte(b);
                if (_pending.Length > MaxMessageBytes)
                    throw new InvalidOperationException("JSON-RPC message exceeds the size limit.");

                if (_inString)
                {
                    if (_escaped) _escaped = false;
                    else if (b == '\\') _escaped = true;
                    else if (b == '"') _inString = false;
                    continue;
                }

                if (b == '"') _inString = true;
                else if (b == '{' || b == '[') _depth++;
                else if ((b == '}' || b == ']') && --_depth == 0)
                {
                    messages.Add(Encoding.UTF8.GetString(_pending.GetBuffer(), 0, (int)_pending.Length));
                    _pending.SetLength(0);
                }
            }

            return messages;
        }
    }
}

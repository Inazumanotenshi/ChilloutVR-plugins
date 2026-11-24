using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BlankUpdater
{
    /// <summary>
    /// Small JSON reader so the updater does not depend on any JSON library.
    /// Objects become Dictionary&lt;string, object&gt;, arrays List&lt;object&gt;,
    /// numbers double, plus string, bool and null.
    /// </summary>
    internal sealed class Json
    {
        private readonly string _text;
        private int _pos;

        private Json(string text)
        {
            _text = text;
        }

        public static object Parse(string text)
        {
            var parser = new Json(text);
            object value = parser.ReadValue();
            parser.SkipWhitespace();
            if (parser._pos != text.Length)
                throw new FormatException("Unexpected data after JSON value");
            return value;
        }

        private object ReadValue()
        {
            SkipWhitespace();
            if (_pos >= _text.Length)
                throw new FormatException("Unexpected end of JSON");

            char c = _text[_pos];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': Expect("true"); return true;
                case 'f': Expect("false"); return false;
                case 'n': Expect("null"); return null;
                default: return ReadNumber();
            }
        }

        private Dictionary<string, object> ReadObject()
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            _pos++;
            SkipWhitespace();
            if (Peek() == '}')
            {
                _pos++;
                return result;
            }
            while (true)
            {
                SkipWhitespace();
                string key = ReadString();
                SkipWhitespace();
                if (Next() != ':')
                    throw new FormatException("Expected ':' in JSON object");
                result[key] = ReadValue();
                SkipWhitespace();
                char c = Next();
                if (c == '}')
                    return result;
                if (c != ',')
                    throw new FormatException("Expected ',' or '}' in JSON object");
            }
        }

        private List<object> ReadArray()
        {
            var result = new List<object>();
            _pos++;
            SkipWhitespace();
            if (Peek() == ']')
            {
                _pos++;
                return result;
            }
            while (true)
            {
                result.Add(ReadValue());
                SkipWhitespace();
                char c = Next();
                if (c == ']')
                    return result;
                if (c != ',')
                    throw new FormatException("Expected ',' or ']' in JSON array");
            }
        }

        private string ReadString()
        {
            if (Next() != '"')
                throw new FormatException("Expected string in JSON");
            var sb = new StringBuilder();
            while (true)
            {
                char c = Next();
                if (c == '"')
                    return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                char e = Next();
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (_pos + 4 > _text.Length)
                            throw new FormatException("Bad unicode escape in JSON");
                        sb.Append((char)Convert.ToInt32(_text.Substring(_pos, 4), 16));
                        _pos += 4;
                        break;
                    default: throw new FormatException("Bad escape in JSON");
                }
            }
        }

        private double ReadNumber()
        {
            int start = _pos;
            while (_pos < _text.Length && "+-0123456789.eE".IndexOf(_text[_pos]) >= 0)
                _pos++;
            if (start == _pos)
                throw new FormatException("Unexpected character in JSON");
            return double.Parse(_text.Substring(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private void Expect(string word)
        {
            if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
                throw new FormatException("Unexpected token in JSON");
            _pos += word.Length;
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }

        private char Peek() => _pos < _text.Length ? _text[_pos] : '\0';

        private char Next()
        {
            if (_pos >= _text.Length)
                throw new FormatException("Unexpected end of JSON");
            return _text[_pos++];
        }
    }
}

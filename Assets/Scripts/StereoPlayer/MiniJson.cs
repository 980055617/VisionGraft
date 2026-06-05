using System.Collections.Generic;
using System.Globalization;
using System.Text;

public static class MiniJson
{
    public static object Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        Parser parser = new Parser(json);
        return parser.ParseValue();
    }

    private sealed class Parser
    {
        private readonly string json;
        private int index;

        public Parser(string json)
        {
            this.json = json;
        }

        public object ParseValue()
        {
            SkipWhitespace();
            if (index >= json.Length)
            {
                return null;
            }

            char c = json[index];
            if (c == '{') return ParseObject();
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (c == '-' || char.IsDigit(c)) return ParseNumber();
            if (Match("true")) return true;
            if (Match("false")) return false;
            if (Match("null")) return null;
            return null;
        }

        private Dictionary<string, object> ParseObject()
        {
            Dictionary<string, object> obj = new Dictionary<string, object>();
            index++;
            while (true)
            {
                SkipWhitespace();
                if (index >= json.Length)
                {
                    return obj;
                }
                if (json[index] == '}')
                {
                    index++;
                    return obj;
                }

                string key = ParseString();
                SkipWhitespace();
                if (index < json.Length && json[index] == ':')
                {
                    index++;
                }

                obj[key] = ParseValue();
                SkipWhitespace();
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (index < json.Length && json[index] == '}')
                {
                    index++;
                    return obj;
                }
            }
        }

        private List<object> ParseArray()
        {
            List<object> array = new List<object>();
            index++;
            while (true)
            {
                SkipWhitespace();
                if (index >= json.Length)
                {
                    return array;
                }
                if (json[index] == ']')
                {
                    index++;
                    return array;
                }

                array.Add(ParseValue());
                SkipWhitespace();
                if (index < json.Length && json[index] == ',')
                {
                    index++;
                    continue;
                }
                if (index < json.Length && json[index] == ']')
                {
                    index++;
                    return array;
                }
            }
        }

        private string ParseString()
        {
            if (index >= json.Length || json[index] != '"')
            {
                return string.Empty;
            }

            index++;
            StringBuilder sb = new StringBuilder();
            while (index < json.Length)
            {
                char c = json[index++];
                if (c == '"')
                {
                    break;
                }
                if (c != '\\' || index >= json.Length)
                {
                    sb.Append(c);
                    continue;
                }

                char esc = json[index++];
                if (esc == '"' || esc == '\\' || esc == '/') sb.Append(esc);
                else if (esc == 'b') sb.Append('\b');
                else if (esc == 'f') sb.Append('\f');
                else if (esc == 'n') sb.Append('\n');
                else if (esc == 'r') sb.Append('\r');
                else if (esc == 't') sb.Append('\t');
                else if (esc == 'u' && index + 4 <= json.Length)
                {
                    string hex = json.Substring(index, 4);
                    if (ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                    {
                        sb.Append((char)code);
                    }
                    index += 4;
                }
            }

            return sb.ToString();
        }

        private object ParseNumber()
        {
            int start = index;
            if (json[index] == '-')
            {
                index++;
            }
            while (index < json.Length && char.IsDigit(json[index]))
            {
                index++;
            }

            bool isFloat = false;
            if (index < json.Length && json[index] == '.')
            {
                isFloat = true;
                index++;
                while (index < json.Length && char.IsDigit(json[index]))
                {
                    index++;
                }
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                isFloat = true;
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-'))
                {
                    index++;
                }
                while (index < json.Length && char.IsDigit(json[index]))
                {
                    index++;
                }
            }

            string token = json.Substring(start, index - start);
            if (!isFloat && long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
            {
                return longValue;
            }

            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
            {
                return doubleValue;
            }

            return 0L;
        }

        private bool Match(string token)
        {
            if (index + token.Length > json.Length)
            {
                return false;
            }

            for (int i = 0; i < token.Length; i++)
            {
                if (json[index + i] != token[i])
                {
                    return false;
                }
            }

            index += token.Length;
            return true;
        }

        private void SkipWhitespace()
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
        }
    }
}

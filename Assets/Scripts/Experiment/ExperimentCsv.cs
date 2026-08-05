using System.Globalization;
using System.Text;

// 実験ログ CSV の行組み立て。ファイル I/O は ExperimentLogWriter が担当する。
//
// 分析は後日 pandas 等で行うため、崩れない CSV を出すことを最優先にする:
//   - 区切り文字・引用符・改行を含む値は RFC 4180 準拠でクォートする
//   - 数値は必ず InvariantCulture（実験機のロケール次第で小数点が "," になるのを防ぐ）
public static class ExperimentCsv
{
    public const string Delimiter = ",";

    public static string BuildRow(params string[] fields)
    {
        if (fields == null || fields.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(Delimiter);
            }

            builder.Append(EscapeField(fields[i]));
        }

        return builder.ToString();
    }

    public static string EscapeField(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool needsQuoting =
            value.IndexOf('"') >= 0 ||
            value.IndexOf(',') >= 0 ||
            value.IndexOf('\n') >= 0 ||
            value.IndexOf('\r') >= 0;

        if (!needsQuoting)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string Format(float value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    public static string Format(double value)
    {
        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }

    public static string Format(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    public static string Format(bool value)
    {
        return value ? "1" : "0";
    }

    // ログ全体で時刻表記を揃える。ミリ秒まで残さないと操作ログと頭部姿勢ログの
    // 突き合わせができない。
    public static string FormatTimestamp(System.DateTime value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}

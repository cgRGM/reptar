using System.Text;

namespace Reptar;

public static class SqlParamParser
{
    public static (string Sql, List<string> ParamNames) ParseNamedParameters(
        string sql)
    {
        var rewritten = new StringBuilder(sql.Length);
        var names = new List<string>();

        for (var index = 0; index < sql.Length; index += 1)
        {
            var current = sql[index];

            if (current is '\'' or '"' or '`')
            {
                var quote = current;
                rewritten.Append(current);

                for (index += 1; index < sql.Length; index += 1)
                {
                    rewritten.Append(sql[index]);

                    if (sql[index] != quote)
                    {
                        continue;
                    }

                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                    {
                        rewritten.Append(sql[index + 1]);
                        index += 1;
                        continue;
                    }

                    break;
                }

                continue;
            }

            if (current == '-' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    rewritten.Append(sql[index]);
                    index += 1;
                }

                if (index < sql.Length)
                {
                    rewritten.Append(sql[index]);
                }

                continue;
            }

            if (current == '/' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '*')
            {
                rewritten.Append("/*");
                index += 2;

                while (index < sql.Length)
                {
                    if (sql[index] == '*' &&
                        index + 1 < sql.Length &&
                        sql[index + 1] == '/')
                    {
                        rewritten.Append("*/");
                        index += 1;
                        break;
                    }

                    rewritten.Append(sql[index]);
                    index += 1;
                }

                continue;
            }

            if ((current == '@' || current == '$') &&
                index + 1 < sql.Length &&
                (char.IsLetter(sql[index + 1]) || sql[index + 1] == '_'))
            {
                var start = index + 1;
                var end = start;

                while (end < sql.Length &&
                       (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
                {
                    end += 1;
                }

                names.Add(sql[start..end]);
                rewritten.Append('?');
                index = end - 1;
                continue;
            }

            rewritten.Append(current);
        }

        return (rewritten.ToString(), names);
    }

    public static (string Sql, List<string> ParamNames) ParsePostgresParameters(
        string sql)
    {
        var rewritten = new StringBuilder(sql.Length);
        var names = new List<string>();
        var paramIndex = 1;

        for (var index = 0; index < sql.Length; index += 1)
        {
            var current = sql[index];

            if (current is '\'' or '"' or '`')
            {
                var quote = current;
                rewritten.Append(current);

                for (index += 1; index < sql.Length; index += 1)
                {
                    rewritten.Append(sql[index]);

                    if (sql[index] != quote)
                    {
                        continue;
                    }

                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                    {
                        rewritten.Append(sql[index + 1]);
                        index += 1;
                        continue;
                    }

                    break;
                }

                continue;
            }

            if (current == '-' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '-')
            {
                while (index < sql.Length && sql[index] != '\n')
                {
                    rewritten.Append(sql[index]);
                    index += 1;
                }

                if (index < sql.Length)
                {
                    rewritten.Append(sql[index]);
                }

                continue;
            }

            if (current == '/' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '*')
            {
                rewritten.Append("/*");
                index += 2;

                while (index < sql.Length)
                {
                    if (sql[index] == '*' &&
                        index + 1 < sql.Length &&
                        sql[index + 1] == '/')
                    {
                        rewritten.Append("*/");
                        index += 1;
                        break;
                    }

                    rewritten.Append(sql[index]);
                    index += 1;
                }

                continue;
            }

            if (current == '?')
            {
                rewritten.Append('$').Append(paramIndex);
                paramIndex += 1;
                continue;
            }

            if ((current == '@' || current == '$') &&
                index + 1 < sql.Length &&
                (char.IsLetter(sql[index + 1]) || sql[index + 1] == '_'))
            {
                var start = index + 1;
                var end = start;

                while (end < sql.Length &&
                       (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
                {
                    end += 1;
                }

                names.Add(sql[start..end]);
                rewritten.Append('$').Append(paramIndex);
                paramIndex += 1;
                index = end - 1;
                continue;
            }

            rewritten.Append(current);
        }

        return (rewritten.ToString(), names);
    }
}

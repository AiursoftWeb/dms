using System.Text;

namespace Aiursoft.DMS.Contracts;

// SQL grammar checks are an additional boundary. Database-side read-only roles remain mandatory.

public static class ReadOnlySql
{
    private static readonly HashSet<string> Forbidden = new("INSERT UPDATE DELETE MERGE CREATE ALTER DROP TRUNCATE GRANT REVOKE SET RESET CALL DO EXEC EXECUTE COPY INTO OUTFILE DUMPFILE ATTACH DETACH OPTIMIZE SYSTEM BACKUP RESTORE KILL LOCK UNLOCK LOAD INSTALL SETTINGS FORMAT GLOBAL".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Functions = new("count sum avg min max any anyLast argMin argMax uniq uniqExact quantile median round floor ceil ceiling abs sign sqrt pow power exp log log10 ln mod greatest least coalesce nullif if ifnull isnull multiIf cast try_cast toString toInt32 toInt64 toUInt64 toFloat64 toDate toDateTime toStartOfDay toStartOfHour toStartOfMinute formatDateTime dateDiff dateAdd now today yesterday current_timestamp current_date date_trunc extract lower upper length char_length octet_length concat concat_ws substring substr left right trim ltrim rtrim replace position strpos locate startsWith endsWith match JSONExtractString JSONExtractInt JSONExtract json_extract json_unquote jsonb_extract_path_text arrayJoin arrayLength array_length array_agg groupArray groupUniqArray string_agg group_concat row_number rank dense_rank lag lead first_value last_value ntile decode convert_from unhex toTypeName pg_database_size pg_relation_size pg_total_relation_size pg_size_pretty pg_has_role".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ParenthesisKeywords = new("SELECT FROM WHERE IN NOT EXISTS AS OVER PARTITION BY ORDER GROUP HAVING DISTINCT WITH VALUES UNION ALL EXCEPT INTERSECT USING ON CASE WHEN THEN ELSE END FILTER TUPLE".Split(' '), StringComparer.OrdinalIgnoreCase);
    public static string Validate(string sql)
    {
        var tokens = Tokens(sql);
        if (tokens.Count == 0 || tokens[0].Kind != 1 || tokens[0].Value.ToUpperInvariant() is not ("SELECT" or "WITH" or "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN")) throw new ArgumentException("Use a read query; arbitrary statements require execute-sql.");
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == 1 && Forbidden.Contains(token.Value)) throw new ArgumentException("This read query contains a write, settings, external-output or administrative construct.");
            if (token.Kind == 0 && token.Value == ";" && i != tokens.Count - 1) throw new ArgumentException("A read request contains exactly one statement.");
            if (token.Kind is 1 or 2 && i + 1 < tokens.Count && tokens[i + 1].Value == "(" && !ParenthesisKeywords.Contains(token.Value))
            {
                if (!Functions.Contains(token.Value)) throw new ArgumentException("This function is outside the read-query function allowlist. External/file/network/user-defined functions require execute-sql.");
                if (i > 1 && tokens[i - 1].Value == "." && tokens[i - 2].Value != "pg_catalog") throw new ArgumentException("Qualified functions must use the trusted pg_catalog namespace.");
            }
        }
        return sql.Trim().TrimEnd(';');
    }
    internal sealed record Token(int Kind, string Value);
    internal static List<Token> Tokens(string sql)
    {
        List<Token> result = [];
        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i]; if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\\' || c == '\0' || c == '$') throw new ArgumentException("SQL client commands, dollar quoting and NUL are not accepted in read requests.");
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                if (i + 2 < sql.Length && sql[i + 2] is '!' or '+') throw new ArgumentException("Executable SQL comments are refused.");
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) throw new ArgumentException("Unterminated SQL comment."); i = end + 2; continue;
            }
            if (c is '\'' or '"' or '`')
            {
                var quote = c; var text = new StringBuilder(); i++; var ended = false;
                while (i < sql.Length)
                {
                    if (sql[i] == '\\') throw new ArgumentException("Backslash escapes are refused to keep SQL modes unambiguous.");
                    if (sql[i] == quote)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == quote) { text.Append(quote); i += 2; continue; }
                        i++; ended = true; break;
                    }
                    text.Append(sql[i++]);
                }
                if (!ended) throw new ArgumentException("Unterminated SQL literal.");
                result.Add(new Token(quote == '\'' ? 3 : 2, text.ToString())); continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++; while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                result.Add(new Token(1, sql[start..i])); continue;
            }
            result.Add(new Token(0, c.ToString())); i++;
        }
        return result;
    }
}

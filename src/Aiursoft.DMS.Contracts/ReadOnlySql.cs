using System.Text;

namespace Aiursoft.DMS.Contracts;

// SQL grammar checks are an additional boundary. Database-side read-only roles remain mandatory.

public static class ReadOnlySql
{
    private static readonly HashSet<string> Forbidden = new(
        "INSERT UPDATE DELETE MERGE CREATE ALTER DROP TRUNCATE GRANT REVOKE SET RESET CALL DO EXEC EXECUTE COPY INTO OUTFILE DUMPFILE ATTACH DETACH OPTIMIZE SYSTEM BACKUP RESTORE KILL LOCK UNLOCK LOAD INSTALL SETTINGS FORMAT GLOBAL"
            .Split(' '), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SafeAggregates = new(
        "count sum avg min max any anyLast argMin argMax uniq uniqExact uniqCombined uniqCombined64 uniqHLL12 uniqTheta quantile quantiles quantileExact median groupArray groupUniqArray topK topKWeighted stddevPop stddevSamp varPop varSamp covarPop covarSamp corr sumWithOverflow avgWeighted"
            .Split(' '), StringComparer.OrdinalIgnoreCase);

    private static readonly string[] AggregateCombinators =
    [
        "DistinctIf", "ArrayIf", "OrDefault", "IfMerge", "IfState",
        "Distinct", "OrNull", "Array", "Merge", "State", "If"
    ];

    private static readonly HashSet<string> Functions = new(
        (
            // Aggregates
            "count sum avg min max any anyLast argMin argMax uniq uniqExact uniqCombined uniqCombined64 uniqHLL12 uniqTheta quantile quantiles quantileExact median groupArray groupUniqArray topK topKWeighted stddevPop stddevSamp varPop varSamp covarPop covarSamp corr sumWithOverflow avgWeighted string_agg group_concat array_agg " +
            // Math & numeric
            "round floor ceil ceiling abs sign sqrt pow power exp exp2 exp10 log log2 log10 ln mod modulo moduloOrZero intDiv intDivOrZero negate trunc truncate roundDown roundAge cbrt sin cos tan asin acos atan atan2 pi e isFinite isInfinite isNaN ifNotFinite greatest least " +
            // Conditional & nullability
            "coalesce nullif if ifnull isnull isNotNull assumeNotNull toNullable multiIf " +
            // Casting & type conversion
            "cast try_cast toString toInt8 toInt16 toInt32 toInt64 toInt128 toInt256 toUInt8 toUInt16 toUInt32 toUInt64 toUInt128 toUInt256 toFloat32 toFloat64 toDecimal32 toDecimal64 toDecimal128 toDate toDate32 toDateTime toDateTime64 toUUID toUnixTimestamp toUnixTimestamp64Milli toUnixTimestamp64Micro toUnixTimestamp64Nano toInt32OrZero toInt64OrZero toUInt64OrZero toFloat64OrZero toInt32OrNull toInt64OrNull toUInt64OrNull toFloat64OrNull toDateOrNull toDateTimeOrNull parseDateTimeBestEffort parseDateTimeBestEffortOrNull parseDateTime64BestEffort parseDateTime64BestEffortOrNull toTypeName " +
            // Date & time
            "now now64 today yesterday current_timestamp current_date date_trunc dateTrunc dateName extract formatDateTime dateDiff dateAdd dateSub date_add date_sub date_diff timestampdiff timestampadd from_unixtime unix_timestamp toTimeZone toStartOfSecond toStartOfMinute toStartOfFiveMinutes toStartOfTenMinutes toStartOfFifteenMinutes toStartOfHour toStartOfDay toStartOfWeek toStartOfMonth toStartOfQuarter toStartOfYear toStartOfInterval toYear toQuarter toMonth toDayOfYear toDayOfMonth toDayOfWeek toHour toMinute toSecond toYYYYMM toYYYYMMDD toYYYYMMDDhhmmss toIntervalSecond toIntervalMinute toIntervalHour toIntervalDay toIntervalWeek toIntervalMonth toIntervalQuarter toIntervalYear addSeconds addMinutes addHours addDays addWeeks addMonths addYears subtractSeconds subtractMinutes subtractHours subtractDays subtractWeeks subtractMonths subtractYears " +
            // String
            "empty notEmpty lower upper lowerUTF8 upperUTF8 length lengthUTF8 char_length charLength character_length octet_length concat concat_ws concatAssumeInjective substring substr substringUTF8 mid left right leftUTF8 rightUTF8 trim ltrim rtrim trimLeft trimRight trimBoth replace replaceOne replaceAll replaceRegexpOne replaceRegexpAll position positionUTF8 positionCaseInsensitive positionCaseInsensitiveUTF8 strpos locate startsWith endsWith match ilike notLike notILike multiSearchAny multiSearchAnyCaseInsensitive multiSearchFirstPosition extractAll extractAllGroups splitByChar splitByString splitByWhitespace arrayStringConcat reverse reverseUTF8 repeat lpad rpad base64Encode base64Decode tryBase64Decode hex unhex bin ascii decode convert_from " +
            // JSON
            "JSONHas JSONLength JSONType JSONExtractString JSONExtractInt JSONExtractUInt JSONExtractFloat JSONExtractBool JSONExtractRaw JSONExtract JSONExtractKeys JSONExtractKeysAndValues JSONExtractArrayRaw json_extract json_unquote json_value json_type json_length json_contains jsonb_extract_path jsonb_extract_path_text jsonb_typeof " +
            // Array, map & tuple (pure, non-dynamic)
            "arrayJoin arrayLength array_length has hasAll hasAny hasSubstr indexOf countEqual arrayElement arrayConcat arraySlice arrayReverse arrayDistinct arraySort arrayReverseSort arrayCompact arrayFlatten arrayZip arrayPushBack arrayPushFront arrayPopBack arrayPopFront arrayCount arrayExists arrayAll arrayFirst arrayLast arrayMin arrayMax arraySum arrayAvg arrayUniq mapKeys mapValues mapContains mapFromArrays tupleElement " +
            // Window
            "row_number rank dense_rank lag lead first_value last_value nth_value ntile cume_dist percent_rank " +
            // Pure hashing, formatting, URL & IP string parsing
            "MD5 SHA1 SHA256 SHA512 cityHash64 sipHash64 xxHash64 farmHash64 murmurHash3_64 formatReadableSize formatReadableQuantity formatReadableTimeDelta domain domainWithoutWWW topLevelDomain protocol path pathFull queryString extractURLParameter cutURLParameter decodeURLComponent IPv4NumToString IPv4StringToNum IPv4StringToNumOrDefault IPv6NumToString IPv6StringToNum isIPv4String isIPv6String " +
            // PostgreSQL catalog metadata
            "pg_database_size pg_relation_size pg_total_relation_size pg_size_pretty pg_has_role"
        ).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ParenthesisKeywords = new(
        "SELECT FROM WHERE PREWHERE IN NOT EXISTS AS OVER PARTITION BY ORDER GROUP HAVING DISTINCT WITH VALUES UNION ALL EXCEPT INTERSECT USING ON CASE WHEN THEN ELSE END FILTER TUPLE AND OR XOR IS LIKE ILIKE RLIKE REGEXP BETWEEN ANY SOME JOIN LEFT RIGHT INNER OUTER CROSS FULL NATURAL LIMIT OFFSET ARRAY MAP Nullable LowCardinality DateTime DateTime64 Decimal Decimal32 Decimal64 Decimal128 Decimal256 FixedString Enum8 Enum16"
            .Split(' '), StringComparer.OrdinalIgnoreCase);

    public static string Validate(string sql)
    {
        var tokens = Tokens(sql);
        if (tokens.Count == 0 || tokens[0].Kind != 1 || tokens[0].Value.ToUpperInvariant() is not ("SELECT" or "WITH" or "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN"))
            throw new ArgumentException("Use a read query; arbitrary statements require execute-sql.");
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind == 1 && Forbidden.Contains(token.Value))
                throw new ArgumentException("This read query contains a write, settings, external-output or administrative construct.");
            if (token.Kind == 0 && token.Value == ";" && i != tokens.Count - 1)
                throw new ArgumentException("A read request contains exactly one statement.");
            if (token.Kind is 1 or 2 && i + 1 < tokens.Count && tokens[i + 1].Value == "(" && !ParenthesisKeywords.Contains(token.Value))
            {
                if (!IsAllowedFunction(token.Value))
                    throw new ArgumentException($"Function '{SafeFunctionName(token.Value)}' is outside the read-query function allowlist. External/file/network/user-defined functions require execute-sql.");
                if (i > 1 && tokens[i - 1].Value == "." && tokens[i - 2].Value != "pg_catalog")
                    throw new ArgumentException("Qualified functions must use the trusted pg_catalog namespace.");
            }
        }
        return sql.Trim().TrimEnd(';');
    }

    private static bool IsAllowedFunction(string name)
    {
        if (Functions.Contains(name)) return true;
        foreach (var suffix in AggregateCombinators)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var stem = name[..^suffix.Length];
                if (SafeAggregates.Contains(stem)) return true;
            }
        }
        return false;
    }

    private static string SafeFunctionName(string name)
    {
        var clean = new string(name.Where(c => c is >= ' ' and <= '~').Take(64).ToArray());
        return clean.Length == 0 ? "<unnamed>" : clean;
    }

    internal sealed record Token(int Kind, string Value);

    internal static List<Token> Tokens(string sql)
    {
        List<Token> result = [];
        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '\\' || c == '\0' || c == '$')
                throw new ArgumentException("SQL client commands, dollar quoting and NUL are not accepted in read requests.");
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                if (i + 2 < sql.Length && sql[i + 2] is '!' or '+')
                    throw new ArgumentException("Executable SQL comments are refused.");
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new ArgumentException("Unterminated SQL comment.");
                i = end + 2;
                continue;
            }
            if (c is '\'' or '"' or '`')
            {
                var quote = c;
                var text = new StringBuilder();
                i++;
                var ended = false;
                while (i < sql.Length)
                {
                    if (sql[i] == '\\') throw new ArgumentException("Backslash escapes are refused to keep SQL modes unambiguous.");
                    if (sql[i] == quote)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == quote) { text.Append(quote); i += 2; continue; }
                        i++;
                        ended = true;
                        break;
                    }
                    text.Append(sql[i++]);
                }
                if (!ended) throw new ArgumentException("Unterminated SQL literal.");
                result.Add(new Token(quote == '\'' ? 3 : 2, text.ToString()));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                result.Add(new Token(1, sql[start..i]));
                continue;
            }
            result.Add(new Token(0, c.ToString()));
            i++;
        }
        return result;
    }
}

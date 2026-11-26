using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class ReadOnlySqlTests
{
    [TestMethod]
    [DataRow("SELECT count(*) FROM events WHERE a = 1 AND (b = 2 OR (c = 3 AND d = 4))")]
    [DataRow("SELECT countIf(status >= 500), minIf(ts, ok = 1), maxIf(ts, ok = 1), sumIf(bytes, ok = 1), uniqExactIf(user_id, ok = 1) FROM logs WHERE ts >= now() - INTERVAL 1 HOUR")]
    [DataRow("SELECT avgOrNull(latency), quantileExactOrDefault(0.95)(latency) FROM metrics PREWHERE (env = 'prod')")]
    [DataRow("SELECT toStartOfInterval(ts, toIntervalMinute(5)) AS bucket, formatReadableSize(sum(bytes)) FROM access WHERE positionCaseInsensitive(path, 'hr-hr') > 0 AND (notEmpty(agent) OR has(tags, 'web')) GROUP BY bucket")]
    [DataRow("SELECT CAST(val AS Nullable(DateTime64(3))), CAST(code AS LowCardinality(String)), now64(3) FROM events")]
    [DataRow("SELECT JSONExtractString(payload, 'lang'), JSONHas(payload, 'items'), domain(url_text) FROM requests")]
    [DataRow("SELECT a FROM t1 JOIN (SELECT id FROM t2 WHERE x = 1) AS s ON t1.id = s.id")]
    public void ReadOnlySqlAllowsClickHouseAnalyticsAndParenthesizedBooleanExpressions(string sql)
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(ReadOnlySql.Validate(sql)));
    }

    [TestMethod]
    [DataRow("SELECT urlIf('https://example.test', 1)", "urlIf")]
    [DataRow("SELECT fileIf('/etc/passwd', 1)", "fileIf")]
    [DataRow("SELECT remoteIf('example.test', 'db', 't', 1)", "remoteIf")]
    [DataRow("SELECT sleepIf(5, 1)", "sleepIf")]
    [DataRow("SELECT dictGet('users', 'secret', toUInt64(1))", "dictGet")]
    [DataRow("SELECT arrayReduce('url', ['https://example.test'])", "arrayReduce")]
    [DataRow("SELECT getSetting('readonly')", "getSetting")]
    [DataRow("SELECT executable('cat /etc/passwd', 'TabSeparated', 's String')", "executable")]
    [DataRow("SELECT s3('https://bucket.example.test/data.parquet')", "s3")]
    [DataRow("SELECT sleep(5)", "sleep")]
    public void ReadOnlySqlRefusesDangerousFunctionsAndReportsFunctionName(string sql, string rejectedFunction)
    {
        var ex = Assert.Throws<ArgumentException>(() => ReadOnlySql.Validate(sql));
        StringAssert.Contains(ex.Message, rejectedFunction);
    }
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public sealed record DatabaseConnectionProfile(string Name, string Engine, string Host, int Port, string User,
    string Database, string PasswordFile, bool ReadOnly = true, bool Tls = true, string? Container = null);

public static partial class DatabaseOperations
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
    private static async Task<DatabaseConnectionProfile[]> ProfilesAsync(OperationHost host, CancellationToken token)
    {
        var path = host.ProfilesPath;
        if (!File.Exists(path)) return [];
        PrivateFile(path);
        var profiles = JsonSerializer.Deserialize<DatabaseConnectionProfile[]>(await File.ReadAllTextAsync(path, token), new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Invalid database profiles.");
        if (profiles.Length > 100 || profiles.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != profiles.Length) throw new InvalidDataException("Database profiles must have unique names and at most 100 entries.");
        foreach (var profile in profiles)
        {
            OperationValidation.Name(profile.Name); OperationValidation.Identifier(profile.Database); OperationValidation.Name(profile.User);
            new DnsQueryInput(profile.Host).Validate(); OperationValidation.Path(profile.PasswordFile, false);
            if (profile.Engine is not ("mysql" or "postgresql" or "clickhouse") || profile.Port is < 1 or > 65535) throw new InvalidDataException("Unsupported database profile engine or port.");
            if (profile.Container != null) new DockerContainerInput(profile.Container).Validate();
        }
        return profiles;
    }
    private static void PrivateFile(string path)
    {
        SafeOperationPaths.Check(path);
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Database profile and password files must be private to the Agent account (0600).");
    }
    private static async Task<DatabaseConnectionProfile> ProfileAsync(string name, AgentEnvironment agent, CancellationToken token) =>
        (await ProfilesAsync(OperationOutput.Host(agent), token)).SingleOrDefault(p => p.Name == name) ?? throw new ArgumentException("This database connection is not registered in the machine's private profiles.");

    public static async Task<OperationResult> DiscoverAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent); var profiles = await ProfilesAsync(host, token);
        var processes = (await PerformanceOperations.ReadProcessesAsync(host, token)).Where(p => p.Name.Contains("mysql", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("postgres", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("clickhouse", StringComparison.OrdinalIgnoreCase)).Select(p => new { p.Pid, p.Name, p.ContainerId }).ToArray();
        string? containers = null;
        try
        {
            var docker = await host.RunAsync(new NativeCommand("docker", DockerOperations.Arguments("ps", "--no-trunc", "--format", "{{.ID}}\t{{.Image}}\t{{.Names}}")), token);
            if (docker.ExitCode == 0) containers = string.Join('\n', docker.Output.Split('\n').Where(l => DatabaseImage().IsMatch(l)));
        }
        catch (NotSupportedException) { }
        return await OperationOutput.ReportAsync(context, "Registered database targets and visible database processes/containers discovered; passwords are excluded.", new
        {
            Connections = profiles.Select(p => new { p.Name, p.Engine, p.Host, p.Port, p.Database, p.ReadOnly, p.Tls, p.Container }).ToArray(), Processes = processes, Containers = containers,
            ConnectionStatus = "Discovery does not authenticate every connection; use a database metadata workflow to verify access."
        });
    }
    public static async Task<OperationResult> ListDatabasesAsync(MachineWorkflowContext context, DatabaseInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var profile = await ProfileAsync(input.Connection, agent, token);
        var sql = profile.Engine switch
        {
            "mysql" => "SELECT SCHEMA_NAME AS DatabaseName FROM information_schema.SCHEMATA ORDER BY SCHEMA_NAME",
            "postgresql" => "SELECT datname AS database_name FROM pg_catalog.pg_database WHERE datallowconn ORDER BY datname",
            _ => "SELECT name,engine FROM system.databases ORDER BY name"
        };
        return await QueryAsync(context, profile, sql, 30, 1000, false, agent, token);
    }
    public static async Task<OperationResult> ListTablesAsync(MachineWorkflowContext context, DatabaseNameInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var profile = await ProfileAsync(input.Connection, agent, token);
        var sql = profile.Engine switch
        {
            "mysql" => "SELECT TABLE_NAME,TABLE_TYPE,TABLE_ROWS,DATA_LENGTH,INDEX_LENGTH FROM information_schema.TABLES WHERE TABLE_SCHEMA=" + Literal(profile.Engine, input.Database) + " ORDER BY TABLE_NAME",
            "postgresql" => "SELECT schemaname,relname,n_live_tup,n_dead_tup FROM pg_catalog.pg_stat_user_tables ORDER BY schemaname,relname",
            _ => "SELECT name,engine,total_rows,total_bytes FROM system.tables WHERE database=" + Literal(profile.Engine, input.Database) + " ORDER BY name"
        };
        if (profile.Engine == "postgresql" && input.Database != profile.Database) throw new ArgumentException("PostgreSQL databases need their own registered connection.");
        return await QueryAsync(context, profile, sql, 30, 1000, false, agent, token);
    }
    public static async Task<OperationResult> DescribeTableAsync(MachineWorkflowContext context, DatabaseTableInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var profile = await ProfileAsync(input.Connection, agent, token);
        var sql = profile.Engine switch
        {
            "mysql" => "SELECT COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_KEY,COLUMN_DEFAULT,EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=" + Literal(profile.Engine, input.Database) + " AND TABLE_NAME=" + Literal(profile.Engine, input.Table) + " ORDER BY ORDINAL_POSITION",
            "postgresql" => "SELECT column_name,data_type,is_nullable,column_default FROM information_schema.columns WHERE table_schema='public' AND table_name=" + Literal(profile.Engine, input.Table) + " ORDER BY ordinal_position",
            _ => "SELECT name,type,default_kind,default_expression,is_in_partition_key,is_in_sorting_key,is_in_primary_key FROM system.columns WHERE database=" + Literal(profile.Engine, input.Database) + " AND table=" + Literal(profile.Engine, input.Table) + " ORDER BY position"
        };
        if (profile.Engine == "postgresql" && input.Database != profile.Database) throw new ArgumentException("PostgreSQL databases need their own registered connection.");
        await QueryAsync(context, profile, sql, 30, 1000, false, agent, token);
        if (profile.Engine == "postgresql")
            return await QueryAsync(context, profile, "SELECT indexname,indexdef FROM pg_catalog.pg_indexes WHERE schemaname='public' AND tablename=" + Literal(profile.Engine, input.Table), 30, 1000, false, agent, token);
        if (profile.Engine == "mysql")
            return await QueryAsync(context, profile, "SELECT INDEX_NAME,NON_UNIQUE,SEQ_IN_INDEX,COLUMN_NAME,INDEX_TYPE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=" + Literal(profile.Engine, input.Database) + " AND TABLE_NAME=" + Literal(profile.Engine, input.Table) + " ORDER BY INDEX_NAME,SEQ_IN_INDEX", 30, 1000, false, agent, token);
        return new OperationResult("ClickHouse column and key structure collected.");
    }
    public static async Task<OperationResult> QueryTableAsync(MachineWorkflowContext context, DatabaseQueryInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var profile = await ProfileAsync(input.Connection, agent, token);
        if (profile.Engine == "postgresql" && input.Database != profile.Database) throw new ArgumentException("PostgreSQL databases need their own registered connection.");
        var table = profile.Engine == "postgresql" ? "public." + Quote(profile.Engine, input.Table) : Quote(profile.Engine, input.Database) + "." + Quote(profile.Engine, input.Table);
        var sql = "SELECT " + string.Join(',', input.Columns.Select(c => Quote(profile.Engine, c))) + " FROM " + table;
        var filters = (input.Filters ?? []).Select(f => FilterSql(profile.Engine, f)).ToArray();
        if (filters.Length != 0) sql += " WHERE " + string.Join(" AND ", filters);
        if (input.SortBy != null) sql += " ORDER BY " + Quote(profile.Engine, input.SortBy) + (input.Descending ? " DESC" : " ASC");
        sql += $" LIMIT {input.Limit} OFFSET {input.Offset}";
        return await QueryAsync(context, profile, sql, 30, input.Limit, false, agent, token);
    }
    public static async Task<OperationResult> QuerySqlAsync(MachineWorkflowContext context, DatabaseSqlInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var sql = ReadOnlySql.Validate(await FilesystemOperations.ReadArtifactTextAsync(input.Artifact, agent, 131072, token));
        return await QueryAsync(context, await ProfileAsync(input.Connection, agent, token), sql, input.TimeoutSeconds, input.MaxRows, false, agent, token);
    }
    public static async Task<OperationResult> ExecuteSqlAsync(MachineWorkflowContext context, DatabaseSqlInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        return await QueryAsync(context, await ProfileAsync(input.Connection, agent, token), await FilesystemOperations.ReadArtifactTextAsync(input.Artifact, agent, 131072, token), input.TimeoutSeconds, input.MaxRows, true, agent, token);
    }
    public static async Task<OperationResult> DiagnosticsAsync(MachineWorkflowContext context, DatabaseInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var profile = await ProfileAsync(input.Connection, agent, token);
        string[] queries = profile.Engine switch
        {
            "mysql" => ["SELECT ID,USER,HOST,DB,COMMAND,TIME,STATE FROM information_schema.PROCESSLIST ORDER BY TIME DESC", "SELECT OBJECT_SCHEMA,OBJECT_NAME,LOCK_TYPE,LOCK_STATUS,OWNER_THREAD_ID FROM performance_schema.metadata_locks", "SELECT TABLE_SCHEMA,SUM(DATA_LENGTH) AS DataBytes,SUM(INDEX_LENGTH) AS IndexBytes FROM information_schema.TABLES GROUP BY TABLE_SCHEMA", "SELECT SCHEMA_NAME,DIGEST,COUNT_STAR,SUM_TIMER_WAIT,MAX_TIMER_WAIT,SUM_ROWS_EXAMINED,SUM_ROWS_SENT FROM performance_schema.events_statements_summary_by_digest ORDER BY SUM_TIMER_WAIT DESC"],
            "postgresql" => ["SELECT pid,usename,datname,state,wait_event_type,wait_event,query_start FROM pg_catalog.pg_stat_activity", "SELECT pid,locktype,database,relation,mode,granted FROM pg_catalog.pg_locks", "SELECT schemaname,relname,n_live_tup,n_dead_tup FROM pg_catalog.pg_stat_user_tables", "SELECT queryid,calls,total_exec_time,mean_exec_time,rows,shared_blks_read FROM public.pg_stat_statements ORDER BY total_exec_time DESC"],
            _ => ["SELECT query_id,user,elapsed,read_rows,read_bytes,memory_usage FROM system.processes", "SELECT database,table,sum(bytes_on_disk) AS bytes_on_disk,count() AS parts FROM system.parts WHERE active GROUP BY database,table", "SELECT database,table,mutation_id,is_done,latest_fail_reason FROM system.mutations ORDER BY create_time DESC", "SELECT event_time,query_id,user,query_duration_ms,read_rows,read_bytes,memory_usage FROM system.query_log WHERE type='QueryFinish' ORDER BY query_duration_ms DESC"]
        };
        var failures = 0;
        foreach (var query in queries)
        {
            try { await QueryAsync(context, profile, query, 30, 1000, false, agent, token); }
            catch (Exception exception) when (exception is IOException or HttpRequestException)
            { failures++; await context.WarningAsync("One database diagnostic was unavailable with this engine/account: " + exception.Message); }
        }
        return new OperationResult("Database query/connection/lock/storage diagnostics completed; unavailable sources are explicit warnings.", failures == 0);
    }
    public static string Quote(string engine, string identifier)
    {
        OperationValidation.Identifier(identifier); return engine == "postgresql" ? "\"" + identifier + "\"" : "`" + identifier + "`";
    }
    public static string Literal(string engine, string value) => engine switch
    {
        "mysql" => "CONVERT(0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + " USING utf8mb4)",
        "postgresql" => "convert_from(decode('" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + "','hex'),'UTF8')",
        _ => "unhex('" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + "')"
    };
    public static string FilterSql(string engine, DatabaseFilter filter)
    {
        filter.Validate(); var column = Quote(engine, filter.Column);
        if (filter.Operator == "is-null") return column + " IS NULL";
        var value = filter.Value.ValueKind switch
        {
            JsonValueKind.Null => "NULL", JsonValueKind.True => "TRUE", JsonValueKind.False => "FALSE",
            JsonValueKind.Number => filter.Value.GetRawText(), _ => Literal(engine, filter.Value.GetString()!)
        };
        if (filter.Value.ValueKind == JsonValueKind.Null) return column + (filter.Operator == "eq" ? " IS NULL" : filter.Operator == "ne" ? " IS NOT NULL" : throw new ArgumentException("Null supports only eq/ne/is-null."));
        if (filter.Operator == "contains")
        {
            if (filter.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("Contains requires a string.");
            return engine == "postgresql" ? "strpos(" + column + "," + value + ")>0" : engine == "mysql" ? "LOCATE(" + value + "," + column + ")>0" : "position(" + column + "," + value + ")>0";
        }
        var op = filter.Operator switch { "eq" => "=", "ne" => "<>", "lt" => "<", "le" => "<=", "gt" => ">", "ge" => ">=", _ => throw new ArgumentException("Invalid operator.") };
        return column + op + value;
    }
    private static async Task<OperationResult> QueryAsync(MachineWorkflowContext context, DatabaseConnectionProfile profile, string sql,
        int seconds, int maxRows, bool write, AgentEnvironment agent, CancellationToken token)
    {
        if (!write && !profile.ReadOnly) throw new ArgumentException("Read operations require a registered read-only connection profile, separate from privileged execution credentials.");
        var host = OperationOutput.Host(agent); var passwordFile = host.FilePath(profile.PasswordFile); PrivateFile(passwordFile);
        var password = await host.ReadAsync(profile.PasswordFile, token, 65536); password = password.TrimEnd('\r', '\n');
        var select = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
        var boundedSql = !write && select ? $"SELECT * FROM ({sql.Trim().TrimEnd(';')}) AS dms_bounded LIMIT {maxRows + 1}" : sql;
        await context.TraceAsync($"Database profile {profile.Name}; engine {profile.Engine}; target {profile.Host}:{profile.Port}/{profile.Database}; read-only={!write}; maximum rows {maxRows}; timeout {seconds}s. Credentials are machine-local.");
        string output;
        try
        {
            if (profile.Engine == "clickhouse") output = await ClickHouseAsync(profile, password, boundedSql, seconds, maxRows, write, token);
            else
            {
                if (!write) await VerifyReaderAsync(profile, password, host, token);
                var statement = profile.Engine == "mysql" && !write
                    ? $"SET SESSION max_execution_time={seconds * 1000}; SET SESSION TRANSACTION READ ONLY; START TRANSACTION READ ONLY;\n{boundedSql.Trim().TrimEnd(';')};\nROLLBACK;\n"
                    : profile.Engine == "postgresql" && !write ? $"BEGIN READ ONLY; SET LOCAL statement_timeout={seconds * 1000}; SET LOCAL search_path=pg_catalog,public;\n{boundedSql.Trim().TrimEnd(';')};\nROLLBACK;"
                    : "/* Aiursoft DMS approved SQL */\n" + sql;
                var result = await ClientAsync(profile, password, statement, seconds + 5, host, token);
                if (result.ExitCode != 0)
                {
                    var message = "Database client rejected the query. " + SafeClientError(result);
                    await context.TraceAsync(message, "error");
                    throw new IOException(message);
                }
                output = result.Output;
            }
        }
        catch (Exception exception) when (write && exception is IOException or HttpRequestException or OperationCanceledException)
        {
            await context.TraceAsync("SQL execution did not confirm completion. Changes, asynchronous mutations or statements may already have committed; inspect the database before another request.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
        if (password.Length != 0) output = output.Replace(password, "[redacted]", StringComparison.Ordinal);
        var lines = ResultRows(output, profile.Engine == "postgresql"); var header = profile.Engine is "mysql" or "postgresql" ? 1 : 0;
        var complete = lines.Length <= maxRows + header;
        await OperationOutput.TraceTextAsync(context, string.Join('\n', lines.Take(maxRows + header)), "data");
        return await OperationOutput.ReportAsync(context, write ? "Approved SQL completed; engine-specific transaction/asynchronous behavior applies." : "Bounded database query completed.",
            new { Connection = profile.Name, profile.Engine, ReadOnly = !write, RowsIncluded = Math.Min(maxRows, Math.Max(0, lines.Length - header)), MoreRows = !complete, Format = profile.Engine == "mysql" ? "escaped TSV with header" : profile.Engine == "postgresql" ? "psql CSV" : "JSONEachRow" }, complete);
    }
    public static string[] ResultRows(string output, bool csv)
    {
        if (!csv) return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> rows = []; var start = 0; var quoted = false;
        for (var index = 0; index < output.Length; index++)
        {
            if (output[index] == '"')
            {
                if (quoted && index + 1 < output.Length && output[index + 1] == '"') index++;
                else quoted = !quoted;
            }
            else if (output[index] == '\n' && !quoted)
            {
                if (index > start) rows.Add(output[start..index]);
                start = index + 1;
            }
        }
        if (quoted) throw new InvalidDataException("Database returned an incomplete CSV record.");
        if (start < output.Length) rows.Add(output[start..]);
        return rows.ToArray();
    }
    private static async Task VerifyReaderAsync(DatabaseConnectionProfile profile, string password, OperationHost host, CancellationToken token)
    {
        if (profile.Engine == "mysql")
        {
            var grants = await ClientAsync(profile, password, "SHOW GRANTS FOR CURRENT_USER", 15, host, token);
            if (grants.ExitCode != 0) throw new IOException("Cannot verify this MySQL reader's grants. " + SafeClientError(grants));
            var lines = grants.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            if (lines.Length == 0 || lines.Any(line =>
            {
                var match = MySqlGrant().Match(line);
                return !match.Success || match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries).Any(p => p is not ("SELECT" or "SHOW VIEW" or "USAGE" or "PROCESS")) || line.Contains("GRANT OPTION", StringComparison.OrdinalIgnoreCase);
            })) throw new IOException("The MySQL reader must have only direct SELECT/SHOW VIEW/USAGE/PROCESS grants; privileged or role-based grants are refused.");
        }
        else
        {
            const string check = "SELECT (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls OR pg_catalog.pg_has_role(current_user,'pg_read_server_files','MEMBER') OR pg_catalog.pg_has_role(current_user,'pg_write_server_files','MEMBER') OR pg_catalog.pg_has_role(current_user,'pg_execute_server_program','MEMBER')) AS unsafe FROM pg_catalog.pg_roles WHERE rolname=current_user";
            var result = await ClientAsync(profile, password, check, 15, host, token);
            if (result.ExitCode != 0 || result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() is not ("f" or "false")) throw new IOException("The PostgreSQL reader must not be a privileged or server-file/program role.");
        }
    }
    private static Task<NativeResult> ClientAsync(DatabaseConnectionProfile profile, string password, string sql, int seconds, OperationHost host, CancellationToken token)
    {
        List<string> args; string executable; Dictionary<string, string> variables = [];
        if (profile.Engine == "mysql")
        {
            executable = "mysql"; args = ["--no-defaults", "--batch", "--column-names", "--binary-mode", "--local-infile=0", "--default-character-set=utf8mb4", "--connect-timeout=10",
                "--host=" + profile.Host, "--port=" + profile.Port, "--user=" + profile.User, "--database=" + profile.Database, "--ssl-mode=" + (profile.Tls ? "VERIFY_IDENTITY" : "DISABLED")];
            variables["MYSQL_PWD"] = password;
            // Explicitly unencrypted profiles still need RSA password exchange for MySQL's
            // caching_sha2_password authentication. TLS profiles keep certificate verification.
            if (!profile.Tls) args.Add("--get-server-public-key");
        }
        else
        {
            executable = "psql"; args = ["--no-psqlrc", "--no-password", "--quiet", "--csv", "--set=ON_ERROR_STOP=1", "--set=VERBOSITY=sqlstate", "--host=" + profile.Host, "--port=" + profile.Port,
                "--username=" + profile.User, "--dbname=" + profile.Database, "--file=-"];
            variables["PGPASSWORD"] = password; variables["PGSSLMODE"] = profile.Tls ? "verify-full" : "disable";
            variables["PGCONNECT_TIMEOUT"] = "10"; variables["PGAPPNAME"] = "Aiursoft DMS";
        }
        // gexec sends each returned string to the SQL server, without interpreting it as a
        // psql meta-command. The only client command is this compiled wrapper. Hex data on
        // stdin keeps approved SQL literals out of process arguments and prevents \! escapes.
        var standardInput = profile.Engine == "mysql" ? sql : "SELECT pg_catalog.convert_from(pg_catalog.decode('"
            + Convert.ToHexString(Encoding.UTF8.GetBytes(sql)) + "','hex'),'UTF8') \\gexec\n";
        if (profile.Container != null)
        {
            List<string> docker = ["exec", "--interactive"];
            foreach (var key in variables.Keys) docker.AddRange(["--env", key]); docker.Add(profile.Container); docker.Add(executable); docker.AddRange(args);
            return host.RunAsync(new NativeCommand("docker", DockerOperations.Arguments(docker.ToArray()), seconds, standardInput, variables), token);
        }
        return host.RunAsync(new NativeCommand(executable, args, seconds, standardInput, variables), token);
    }
    private static async Task<string> ClickHouseAsync(DatabaseConnectionProfile profile, string password, string sql, int seconds, int maxRows, bool write, CancellationToken token)
    {
        if (profile.Container != null) throw new ArgumentException("ClickHouse HTTP profiles use a reachable HTTP(S) endpoint, not a container exec credential.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(seconds + 5));
        var builder = new UriBuilder(profile.Tls ? "https" : "http", profile.Host, profile.Port)
        {
            Query = "database=" + Uri.EscapeDataString(profile.Database) + "&default_format=JSONEachRow&max_execution_time=" + seconds + "&max_result_rows=" + (maxRows + 1)
                + "&result_overflow_mode=break&max_memory_usage=536870912&max_bytes_to_read=1073741824&wait_end_of_query=1"
                + (write ? "&mutations_sync=2&distributed_ddl_task_timeout=" + seconds : "&readonly=1&allow_ddl=0&allow_introspection_functions=0")
        };
        using var content = new StringContent(sql, Encoding.UTF8, "text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, builder.Uri);
        request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(profile.User + ":" + password)));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ClickHouse rejected the statement with HTTP {(int)response.StatusCode}; server text is withheld.");
        if (response.Headers.TryGetValues("X-ClickHouse-Exception-Code", out var exceptionCodes) && exceptionCodes.Any(c => c != "0"))
            throw new IOException("ClickHouse reported a query exception; server text is withheld.");
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(deadline.Token));
        var output = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token)) != 0)
        { if (output.Length + count > 4194304) throw new IOException("Database output exceeds 4 MiB; use a narrower query."); output.Append(buffer, 0, count); }
        // ClickHouse can send query errors after an HTTP 200 header. JSONEachRow output must be valid row data.
        if (output.ToString().Split('\n').Any(l => l.StartsWith("Code: ", StringComparison.Ordinal) && l.Contains("DB::Exception", StringComparison.Ordinal))) throw new IOException("ClickHouse reported an exception in the query response.");
        foreach (var line in output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (write) continue;
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("ClickHouse returned a non-row response.");
        }
        return output.ToString();
    }
    private static string SafeClientError(NativeResult result)
    {
        var code = ClientErrorCode().Match(result.Error);
        return code.Success ? "Database error code/SQLSTATE: " + code.Value + ". Server text is withheld."
            : "Client exit code " + result.ExitCode + "; server text is withheld.";
    }
    [GeneratedRegex(@"(?<=ERROR )\d{3,5} \([A-Z0-9]{5}\)|(?<=(?:ERROR|FATAL): )\s*[A-Z0-9]{5}\b", RegexOptions.CultureInvariant)]
    private static partial Regex ClientErrorCode();
    [GeneratedRegex(@"\AGRANT (.+?) ON ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MySqlGrant();
    [GeneratedRegex("mysql|mariadb|postgres|clickhouse", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseImage();
}

// SQL grammar checks are an additional boundary. Database-side read-only sessions/roles remain mandatory.
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
    private sealed record Token(int Kind, string Value);
    private static List<Token> Tokens(string sql)
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

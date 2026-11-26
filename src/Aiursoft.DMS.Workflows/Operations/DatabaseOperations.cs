using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public sealed record DatabaseConnectionProfile(string Name, string Engine, string Host, int Port, string User,
    string Database, string PasswordFile, bool ReadOnly = true, bool Tls = true, string? Container = null);

public static partial class DatabaseOperations
{
    private sealed record ResolvedDatabaseTarget(
        string Label,
        string Engine,
        bool IsMariaDb,
        string? DefaultDatabase,
        DatabaseConnectionProfile? Profile,
        string? DirectContainerId);

    private static readonly string[] EnvCredentialKeys =
    [
        "MYSQL_ROOT_PASSWORD", "MYSQL_ROOT_PASSWORD_FILE",
        "MARIADB_ROOT_PASSWORD", "MARIADB_ROOT_PASSWORD_FILE",
        "MYSQL_PASSWORD", "MYSQL_PASSWORD_FILE",
        "MARIADB_PASSWORD", "MARIADB_PASSWORD_FILE",
        "POSTGRES_PASSWORD", "POSTGRES_PASSWORD_FILE",
        "POSTGRES_HOST_AUTH_METHOD"
    ];

    // Runs strictly inside the target MySQL/MariaDB container so credentials never leave the container.
    private const string MySqlContainerBootstrap = """
        set -eu
        bin=$(command -v mariadb || command -v mysql || true)
        [ -n "$bin" ] || { echo "mysql/mariadb client not found in container" >&2; exit 127; }
        read_secret() {
            if [ -n "${1:-}" ] && [ -r "$1" ]; then
                tr -d '\r\n' < "$1"
            fi
        }
        u=""
        p=""
        if [ -n "${MARIADB_ROOT_PASSWORD_FILE:-}" ]; then u="root"; p=$(read_secret "$MARIADB_ROOT_PASSWORD_FILE")
        elif [ -n "${MYSQL_ROOT_PASSWORD_FILE:-}" ]; then u="root"; p=$(read_secret "$MYSQL_ROOT_PASSWORD_FILE")
        elif [ -n "${MARIADB_ROOT_PASSWORD:-}" ]; then u="root"; p="$MARIADB_ROOT_PASSWORD"
        elif [ -n "${MYSQL_ROOT_PASSWORD:-}" ]; then u="root"; p="$MYSQL_ROOT_PASSWORD"
        elif [ -n "${MARIADB_USER:-}" ] && [ -n "${MARIADB_PASSWORD_FILE:-}" ]; then u="$MARIADB_USER"; p=$(read_secret "$MARIADB_PASSWORD_FILE")
        elif [ -n "${MYSQL_USER:-}" ] && [ -n "${MYSQL_PASSWORD_FILE:-}" ]; then u="$MYSQL_USER"; p=$(read_secret "$MYSQL_PASSWORD_FILE")
        elif [ -n "${MARIADB_USER:-}" ] && [ -n "${MARIADB_PASSWORD:-}" ]; then u="$MARIADB_USER"; p="$MARIADB_PASSWORD"
        elif [ -n "${MYSQL_USER:-}" ] && [ -n "${MYSQL_PASSWORD:-}" ]; then u="$MYSQL_USER"; p="$MYSQL_PASSWORD"
        else u="root"
        fi
        export MYSQL_PWD="$p"
        db="${1:-}"
        if [ -n "$db" ]; then
            exec "$bin" --no-defaults --batch --column-names --binary-mode --local-infile=0 --default-character-set=utf8mb4 --connect-timeout=10 --user="$u" --database="$db"
        else
            exec "$bin" --no-defaults --batch --column-names --binary-mode --local-infile=0 --default-character-set=utf8mb4 --connect-timeout=10 --user="$u"
        fi
        """;

    // Runs strictly inside the target PostgreSQL container so credentials never leave the container.
    private const string PostgresContainerBootstrap = """
        set -eu
        command -v psql >/dev/null 2>&1 || { echo "psql client not found in container" >&2; exit 127; }
        u="${POSTGRES_USER:-postgres}"
        p=""
        if [ -n "${POSTGRES_PASSWORD_FILE:-}" ] && [ -r "$POSTGRES_PASSWORD_FILE" ]; then
            p=$(tr -d '\r\n' < "$POSTGRES_PASSWORD_FILE")
        elif [ -n "${POSTGRES_PASSWORD:-}" ]; then
            p="$POSTGRES_PASSWORD"
        fi
        db="${1:-${POSTGRES_DB:-$u}}"
        export PGPASSWORD="$p"
        export PGCONNECT_TIMEOUT=10
        export PGAPPNAME="Aiursoft DMS"
        exec psql --no-psqlrc --no-password --quiet --csv --set=ON_ERROR_STOP=1 --set=VERBOSITY=sqlstate --username="$u" --dbname="$db" --file=-
        """;

    private static async Task<DatabaseConnectionProfile[]> ProfilesAsync(OperationHost host, CancellationToken token)
    {
        var path = host.ProfilesPath;
        if (!File.Exists(path)) return [];
        PrivateFile(path);
        var profiles = JsonSerializer.Deserialize<DatabaseConnectionProfile[]>(await File.ReadAllTextAsync(path, token), new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
            ?? throw new InvalidDataException("Invalid database profiles.");
        if (profiles.Length > 100 || profiles.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != profiles.Length)
            throw new InvalidDataException("Database profiles must have unique names and at most 100 entries.");
        foreach (var profile in profiles)
        {
            OperationValidation.Name(profile.Name); OperationValidation.Identifier(profile.Database); OperationValidation.Name(profile.User);
            new DnsQueryInput(profile.Host).Validate(); OperationValidation.Path(profile.PasswordFile, false);
            if (profile.Engine is not ("mysql" or "postgresql" or "clickhouse") || profile.Port is < 1 or > 65535)
                throw new InvalidDataException("Unsupported database profile engine or port.");
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

    private static bool IsContainerProfile(DatabaseConnectionProfile profile) =>
        profile.Container != null && profile.Engine is "mysql" or "postgresql";

    private static async Task<DatabaseConnectionProfile> ProfileAsync(string name, AgentEnvironment agent, CancellationToken token)
    {
        var profile = (await ProfilesAsync(OperationOutput.Host(agent), token)).SingleOrDefault(p => p.Name == name);
        if (profile == null) throw new ArgumentException("This database connection is not registered in the machine's private profiles.");
        if (!IsContainerProfile(profile))
            throw new ArgumentException("Use a MySQL or PostgreSQL profile with a full local container ID. For ClickHouse, use dms machine clickhouse.");
        return profile;
    }

    private static async Task<ResolvedDatabaseTarget> ResolveTargetAsync(
        MachineWorkflowContext context,
        string? connection,
        string? container,
        AgentEnvironment agent,
        CancellationToken token)
    {
        OperationValidation.DatabaseTarget(connection, container);
        if (!string.IsNullOrWhiteSpace(connection))
        {
            var profile = await ProfileAsync(connection, agent, token);
            return new ResolvedDatabaseTarget(profile.Name, profile.Engine, false, profile.Database, profile, null);
        }

        var inspected = await DockerOperations.ReadContainerAsync(context, container!, agent, token);
        if (!inspected.GetProperty("State").GetProperty("Running").GetBoolean())
            throw new IOException("The selected database container is not running.");
        var config = inspected.GetProperty("Config");
        var image = config.GetProperty("Image").GetString() ?? "";
        if (!DatabaseImage().IsMatch(image))
            throw new ArgumentException("The selected container image is not a recognized MySQL, MariaDB, or PostgreSQL database container.");
        var isPostgres = image.Contains("postgres", StringComparison.OrdinalIgnoreCase);
        var isMariaDb = !isPostgres && image.Contains("mariadb", StringComparison.OrdinalIgnoreCase);
        var env = ParseEnvKeysAndSafeValues(config);
        var name = (inspected.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() : null)?.TrimStart('/') ?? container!;
        return new ResolvedDatabaseTarget(
            $"container:{name}",
            isPostgres ? "postgresql" : "mysql",
            isMariaDb,
            env.DefaultDatabase,
            null,
            container!);
    }

    private static (string? DefaultDatabase, bool HasEnvCredentials) ParseEnvKeysAndSafeValues(JsonElement config)
    {
        string? defaultDatabase = null;
        var hasCreds = false;
        if (!config.TryGetProperty("Env", out var envEl) || envEl.ValueKind != JsonValueKind.Array)
            return (null, false);
        foreach (var item in envEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var raw = item.GetString()!;
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;
            var key = raw[..eq];
            var val = raw[(eq + 1)..];
            if (string.IsNullOrEmpty(val)) continue;
            if (key is "MYSQL_DATABASE" or "MARIADB_DATABASE" or "POSTGRES_DB")
                defaultDatabase ??= val;
            if (EnvCredentialKeys.Contains(key, StringComparer.Ordinal))
                hasCreds = true;
        }
        return (defaultDatabase, hasCreds);
    }

    public static async Task<OperationResult> DiscoverAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent);
        var profiles = (await ProfilesAsync(host, token)).Where(IsContainerProfile);
        var docker = await OperationOutput.CommandAsync(context, agent, "docker",
            DockerOperations.Arguments("ps", "--no-trunc", "--format", "{{.ID}}\t{{.Image}}\t{{.Names}}"),
            token, traceOutput: false, seconds: 10);
        var matchingRows = docker.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length >= 3 && DatabaseImage().IsMatch(parts[1]))
            .Take(50)
            .ToArray();

        List<object> containers = [];
        if (matchingRows.Length > 0)
        {
            var ids = matchingRows.Select(r => r[0]).ToArray();
            var inspectResult = await OperationOutput.CommandAsync(context, agent, "docker",
                DockerOperations.Arguments(["container", "inspect", .. ids]),
                token, traceOutput: false, seconds: 15, allowedExitCodes: [0, 1]);
            Dictionary<string, JsonElement> inspectedById = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(inspectResult.Output))
            {
                try
                {
                    using var doc = JsonDocument.Parse(inspectResult.Output);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            if (el.TryGetProperty("Id", out var idEl) && idEl.GetString() is { Length: > 0 } cid)
                                inspectedById[cid] = el.Clone();
                        }
                    }
                }
                catch (JsonException) { }
            }

            foreach (var row in matchingRows)
            {
                var cid = row[0];
                var image = row[1];
                var name = row[2];
                var engine = image.Contains("postgres", StringComparison.OrdinalIgnoreCase) ? "postgresql" : "mysql";
                string? swarmService = null;
                string? swarmStack = null;
                string? defaultDatabase = null;
                var hasEnvCredentials = false;
                if (inspectedById.TryGetValue(cid, out var details) && details.TryGetProperty("Config", out var cfg))
                {
                    if (cfg.TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object)
                    {
                        if (labels.TryGetProperty("com.docker.swarm.service.name", out var svc)) swarmService = svc.GetString();
                        if (labels.TryGetProperty("com.docker.stack.namespace", out var stk)) swarmStack = stk.GetString();
                    }
                    (defaultDatabase, hasEnvCredentials) = ParseEnvKeysAndSafeValues(cfg);
                }

                containers.Add(new
                {
                    ContainerId = cid,
                    Name = name,
                    Image = image,
                    Engine = engine,
                    SwarmService = swarmService,
                    SwarmStack = swarmStack,
                    DefaultDatabase = defaultDatabase,
                    HasEnvCredentials = hasEnvCredentials
                });
            }
        }

        return await OperationOutput.ReportAsync(context, "Local database containers and registered container profiles discovered.", new
        {
            Connections = profiles.Select(p => new { p.Name, p.Engine, p.Host, p.Port, p.Database, p.ReadOnly, p.Tls, p.Container }).ToArray(),
            Containers = containers,
            ConnectionStatus = "Discovery does not authenticate every connection; pass --connection or --container to query a database."
        });
    }

    public static async Task<OperationResult> ListDatabasesAsync(MachineWorkflowContext context, DatabaseInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        var sql = target.Engine switch
        {
            "mysql" => "SELECT SCHEMA_NAME AS DatabaseName FROM information_schema.SCHEMATA ORDER BY SCHEMA_NAME",
            _ => "SELECT datname AS database_name FROM pg_catalog.pg_database WHERE datallowconn ORDER BY datname",
        };
        return await QueryAsync(context, target, null, sql, 30, 1000, false, agent, token);
    }

    public static async Task<OperationResult> ListTablesAsync(MachineWorkflowContext context, DatabaseNameInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        if (target.Profile != null && target.Engine == "postgresql" && input.Database != target.Profile.Database)
            throw new ArgumentException("PostgreSQL databases need their own registered connection.");
        var sql = target.Engine switch
        {
            "mysql" => "SELECT TABLE_NAME,TABLE_TYPE,TABLE_ROWS,DATA_LENGTH,INDEX_LENGTH FROM information_schema.TABLES WHERE TABLE_SCHEMA=" + Literal(target.Engine, input.Database) + " ORDER BY TABLE_NAME",
            _ => "SELECT schemaname,relname,n_live_tup,n_dead_tup FROM pg_catalog.pg_stat_user_tables ORDER BY schemaname,relname",
        };
        return await QueryAsync(context, target, input.Database, sql, 30, 1000, false, agent, token);
    }

    public static async Task<OperationResult> DescribeTableAsync(MachineWorkflowContext context, DatabaseTableInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        if (target.Profile != null && target.Engine == "postgresql" && input.Database != target.Profile.Database)
            throw new ArgumentException("PostgreSQL databases need their own registered connection.");
        var sql = target.Engine switch
        {
            "mysql" => "SELECT COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,COLUMN_KEY,COLUMN_DEFAULT,EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=" + Literal(target.Engine, input.Database) + " AND TABLE_NAME=" + Literal(target.Engine, input.Table) + " ORDER BY ORDINAL_POSITION",
            _ => "SELECT column_name,data_type,is_nullable,column_default FROM information_schema.columns WHERE table_schema='public' AND table_name=" + Literal(target.Engine, input.Table) + " ORDER BY ordinal_position",
        };
        await QueryAsync(context, target, input.Database, sql, 30, 1000, false, agent, token);
        if (target.Engine == "postgresql")
            return await QueryAsync(context, target, input.Database, "SELECT indexname,indexdef FROM pg_catalog.pg_indexes WHERE schemaname='public' AND tablename=" + Literal(target.Engine, input.Table), 30, 1000, false, agent, token);
        return await QueryAsync(context, target, input.Database, "SELECT INDEX_NAME,NON_UNIQUE,SEQ_IN_INDEX,COLUMN_NAME,INDEX_TYPE FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=" + Literal(target.Engine, input.Database) + " AND TABLE_NAME=" + Literal(target.Engine, input.Table) + " ORDER BY INDEX_NAME,SEQ_IN_INDEX", 30, 1000, false, agent, token);
    }

    public static async Task<OperationResult> QueryTableAsync(MachineWorkflowContext context, DatabaseQueryInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        if (target.Profile != null && target.Engine == "postgresql" && input.Database != target.Profile.Database)
            throw new ArgumentException("PostgreSQL databases need their own registered connection.");
        var table = target.Engine == "postgresql" ? "public." + Quote(target.Engine, input.Table) : Quote(target.Engine, input.Database) + "." + Quote(target.Engine, input.Table);
        var sql = "SELECT " + string.Join(',', input.Columns.Select(c => Quote(target.Engine, c))) + " FROM " + table;
        var filters = (input.Filters ?? []).Select(f => FilterSql(target.Engine, f)).ToArray();
        if (filters.Length != 0) sql += " WHERE " + string.Join(" AND ", filters);
        if (input.SortBy != null) sql += " ORDER BY " + Quote(target.Engine, input.SortBy) + (input.Descending ? " DESC" : " ASC");
        sql += $" LIMIT {input.Limit} OFFSET {input.Offset}";
        return await QueryAsync(context, target, input.Database, sql, 30, input.Limit, false, agent, token);
    }

    public static async Task<OperationResult> QuerySqlAsync(MachineWorkflowContext context, DatabaseSqlInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var sql = ReadOnlySql.Validate(await FilesystemOperations.ReadArtifactTextAsync(input.Artifact, agent, 131072, token));
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        return await QueryAsync(context, target, input.Database, sql, input.TimeoutSeconds, input.MaxRows, false, agent, token);
    }

    public static async Task<OperationResult> ExecuteSqlAsync(MachineWorkflowContext context, DatabaseSqlInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var sql = await FilesystemOperations.ReadArtifactTextAsync(input.Artifact, agent, 131072, token);
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        return await QueryAsync(context, target, input.Database, sql, input.TimeoutSeconds, input.MaxRows, true, agent, token);
    }

    public static async Task<OperationResult> UpsertAsync(MachineWorkflowContext context, DatabaseUpsertInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        if (target.Profile != null && target.Engine == "postgresql" && input.Database != target.Profile.Database)
            throw new ArgumentException("PostgreSQL databases need their own registered connection.");

        Dictionary<string, string>? secrets = null;
        if (input.SecretColumns is { Length: > 0 })
        {
            var transport = agent.DatabaseSecrets
                ?? throw new InvalidOperationException("Database secret transfer is not available on this Agent host.");
            secrets = await transport.TakeUpsertSecretsAsync(context.Id, token);
            new DatabaseUpsertSecretsPayload(secrets).Validate(input.SecretColumns);
        }

        var sql = BuildUpsertSql(target.Engine, input, secrets);
        var secretValues = secrets?.Values.Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToArray() ?? [];
        await context.TraceAsync(
            $"Structured upsert on {target.Label} ({target.Engine}) {input.Database}.{input.Table}; match=[{string.Join(',', input.MatchColumns)}]; columns=[{string.Join(',', input.Values.Keys.Order(StringComparer.Ordinal))}]; secret_columns=[{string.Join(',', input.SecretColumns ?? [])}]. Secret values are never logged.");

        await QueryAsync(context, target, input.Database, sql, input.TimeoutSeconds, 100, write: true, agent, token, extraSecretsToScrub: secretValues);

        return await OperationOutput.ReportAsync(context,
            "Structured database upsert completed.",
            new
            {
                Target = target.Label,
                target.Engine,
                input.Database,
                input.Table,
                input.MatchColumns,
                NonSecretColumns = input.Values.Keys.Order(StringComparer.Ordinal).ToArray(),
                SecretColumns = (input.SecretColumns ?? []).Order(StringComparer.Ordinal).ToArray()
            });
    }

    public static string BuildUpsertSql(string engine, DatabaseUpsertInput input, IReadOnlyDictionary<string, string>? secrets = null)
    {
        input.Validate();
        List<(string Column, string ValueSql)> entries = [];
        foreach (var col in input.Values.Keys.Order(StringComparer.Ordinal))
            entries.Add((col, ScalarSql(engine, input.Values[col])));

        if (input.SecretColumns is { Length: > 0 })
        {
            if (secrets == null)
                throw new InvalidOperationException("Secret values are required for declared SecretColumns.");
            foreach (var col in input.SecretColumns.Order(StringComparer.Ordinal))
            {
                if (!secrets.TryGetValue(col, out var secretVal))
                    throw new InvalidOperationException($"Missing secret value for column '{col}'.");
                entries.Add((col, Literal(engine, secretVal)));
            }
        }

        var tableRef = engine == "postgresql"
            ? "public." + Quote(engine, input.Table)
            : Quote(engine, input.Database) + "." + Quote(engine, input.Table);
        var columnsSql = string.Join(',', entries.Select(e => Quote(engine, e.Column)));
        var valuesSql = string.Join(',', entries.Select(e => e.ValueSql));
        var matchSet = input.MatchColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updateEntries = entries.Where(e => !matchSet.Contains(e.Column)).ToArray();

        if (engine == "postgresql")
        {
            var conflictCols = string.Join(',', input.MatchColumns.Select(c => Quote(engine, c)));
            var action = updateEntries.Length == 0
                ? "DO NOTHING"
                : "DO UPDATE SET " + string.Join(", ", updateEntries.Select(e => $"{Quote(engine, e.Column)}=EXCLUDED.{Quote(engine, e.Column)}"));
            return $"INSERT INTO {tableRef} ({columnsSql}) VALUES ({valuesSql}) ON CONFLICT ({conflictCols}) {action};";
        }

        var mysqlUpdate = updateEntries.Length == 0
            ? $"{Quote(engine, input.MatchColumns[0])}={Quote(engine, input.MatchColumns[0])}"
            : string.Join(", ", updateEntries.Select(e => $"{Quote(engine, e.Column)}={e.ValueSql}"));
        return $"INSERT INTO {tableRef} ({columnsSql}) VALUES ({valuesSql}) ON DUPLICATE KEY UPDATE {mysqlUpdate};";
    }

    private static string ScalarSql(string engine, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => "NULL",
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String => Literal(engine, value.GetString()!),
        _ => throw new ArgumentException("Only scalar JSON values are supported in database upsert.")
    };

    public static async Task<OperationResult> DiagnosticsAsync(MachineWorkflowContext context, DatabaseInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var target = await ResolveTargetAsync(context, input.Connection, input.Container, agent, token);
        string[] queries = target.Engine switch
        {
            "mysql" => ["SELECT ID,USER,HOST,DB,COMMAND,TIME,STATE FROM information_schema.PROCESSLIST ORDER BY TIME DESC", "SELECT OBJECT_SCHEMA,OBJECT_NAME,LOCK_TYPE,LOCK_STATUS,OWNER_THREAD_ID FROM performance_schema.metadata_locks", "SELECT TABLE_SCHEMA,SUM(DATA_LENGTH) AS DataBytes,SUM(INDEX_LENGTH) AS IndexBytes FROM information_schema.TABLES GROUP BY TABLE_SCHEMA", "SELECT SCHEMA_NAME,DIGEST,COUNT_STAR,SUM_TIMER_WAIT,MAX_TIMER_WAIT,SUM_ROWS_EXAMINED,SUM_ROWS_SENT FROM performance_schema.events_statements_summary_by_digest ORDER BY SUM_TIMER_WAIT DESC"],
            _ => ["SELECT pid,usename,datname,state,wait_event_type,wait_event,query_start FROM pg_catalog.pg_stat_activity", "SELECT pid,locktype,database,relation,mode,granted FROM pg_catalog.pg_locks", "SELECT schemaname,relname,n_live_tup,n_dead_tup FROM pg_catalog.pg_stat_user_tables", "SELECT queryid,calls,total_exec_time,mean_exec_time,rows,shared_blks_read FROM public.pg_stat_statements ORDER BY total_exec_time DESC"],
        };
        var failures = 0;
        foreach (var query in queries)
        {
            try { await QueryAsync(context, target, null, query, 30, 1000, false, agent, token); }
            catch (Exception exception) when (exception is IOException)
            { failures++; await context.WarningAsync("One database diagnostic was unavailable with this engine/account: " + exception.Message); }
        }
        return new OperationResult("Database query/connection/lock/storage diagnostics completed; unavailable sources are explicit warnings.", failures == 0);
    }

    public static string Quote(string engine, string identifier)
    {
        OperationValidation.Identifier(identifier);
        return engine == "postgresql" ? "\"" + identifier + "\"" : "`" + identifier + "`";
    }

    public static string Literal(string engine, string value) => engine switch
    {
        "mysql" => "CONVERT(0x" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + " USING utf8mb4)",
        _ => "convert_from(decode('" + Convert.ToHexString(Encoding.UTF8.GetBytes(value)) + "','hex'),'UTF8')"
    };

    public static string FilterSql(string engine, DatabaseFilter filter)
    {
        filter.Validate();
        var column = Quote(engine, filter.Column);
        if (filter.Operator == "is-null") return column + " IS NULL";
        var value = filter.Value.ValueKind switch
        {
            JsonValueKind.Null => "NULL",
            JsonValueKind.True => "TRUE",
            JsonValueKind.False => "FALSE",
            JsonValueKind.Number => filter.Value.GetRawText(),
            _ => Literal(engine, filter.Value.GetString()!)
        };
        if (filter.Value.ValueKind == JsonValueKind.Null) return column + (filter.Operator == "eq" ? " IS NULL" : filter.Operator == "ne" ? " IS NOT NULL" : throw new ArgumentException("Null supports only eq/ne/is-null."));
        if (filter.Operator == "contains")
        {
            if (filter.Value.ValueKind != JsonValueKind.String) throw new ArgumentException("Contains requires a string.");
            return engine == "postgresql" ? "strpos(" + column + "," + value + ")>0" : "LOCATE(" + value + "," + column + ")>0";
        }
        var op = filter.Operator switch { "eq" => "=", "ne" => "<>", "lt" => "<", "le" => "<=", "gt" => ">", "ge" => ">=", _ => throw new ArgumentException("Invalid operator.") };
        return column + op + value;
    }

    private static async Task<OperationResult> QueryAsync(
        MachineWorkflowContext context,
        ResolvedDatabaseTarget target,
        string? databaseOverride,
        string sql,
        int seconds,
        int maxRows,
        bool write,
        AgentEnvironment agent,
        CancellationToken token,
        string[]? extraSecretsToScrub = null)
    {
        var host = OperationOutput.Host(agent);
        string password = "";
        if (target.Profile != null)
        {
            if (!write && !target.Profile.ReadOnly)
                throw new ArgumentException("Read operations require a registered read-only connection profile, separate from privileged execution credentials.");
            var passwordFile = host.FilePath(target.Profile.PasswordFile);
            PrivateFile(passwordFile);
            password = (await host.ReadAsync(target.Profile.PasswordFile, token, 65536)).TrimEnd('\r', '\n');
        }

        var select = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.TrimStart().StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
        var boundedSql = !write && select ? $"SELECT * FROM ({sql.Trim().TrimEnd(';')}) AS dms_bounded LIMIT {maxRows + 1}" : sql;
        var targetDb = databaseOverride ?? target.DefaultDatabase ?? "";
        await context.TraceAsync(target.Profile != null
            ? $"Database profile {target.Profile.Name}; engine {target.Engine}; target {target.Profile.Host}:{target.Profile.Port}/{target.Profile.Database}; read-only={!write}; maximum rows {maxRows}; timeout {seconds}s. Credentials are machine-local."
            : $"Database container {target.Label}; engine {target.Engine}; database {(string.IsNullOrEmpty(targetDb) ? "(default)" : targetDb)}; read-only={!write}; maximum rows {maxRows}; timeout {seconds}s. Credentials remain inside the container.");

        string output;
        try
        {
            if (!write && target.Profile != null)
                await VerifyReaderAsync(target.Profile, password, host, token);

            var statement = BuildWrappedStatement(target.Engine, target.IsMariaDb, boundedSql, seconds, write);
            var result = target.Profile != null
                ? await ClientProfileAsync(target.Profile, password, statement, seconds + 5, host, token)
                : await ClientContainerEnvAsync(target.DirectContainerId!, target.Engine, targetDb, statement, seconds + 5, host, token);

            if (result.ExitCode != 0)
            {
                var message = "Database client rejected the query. " + SafeClientError(result);
                await context.TraceAsync(message, "error");
                throw new IOException(message);
            }
            output = result.Output;
        }
        catch (Exception exception) when (write && exception is IOException or HttpRequestException or OperationCanceledException)
        {
            await context.TraceAsync("SQL execution did not confirm completion. Changes, asynchronous mutations or statements may already have committed; inspect the database before another request.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }

        if (password.Length != 0) output = output.Replace(password, "[redacted]", StringComparison.Ordinal);
        if (extraSecretsToScrub != null)
        {
            foreach (var secret in extraSecretsToScrub)
                output = output.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        var lines = ResultRows(output, target.Engine == "postgresql");
        const int header = 1;
        var complete = lines.Length <= maxRows + header;
        await OperationOutput.TraceTextAsync(context, string.Join('\n', lines.Take(maxRows + header)), "data");
        return await OperationOutput.ReportAsync(context,
            write ? "Approved SQL completed; engine-specific transaction/asynchronous behavior applies." : "Bounded database query completed.",
            new
            {
                Connection = target.Label,
                target.Engine,
                ReadOnly = !write,
                RowsIncluded = Math.Min(maxRows, Math.Max(0, lines.Length - header)),
                MoreRows = !complete,
                Format = target.Engine == "mysql" ? "escaped TSV with header" : "psql CSV"
            }, complete);
    }

    private static string BuildWrappedStatement(string engine, bool isMariaDb, string sql, int seconds, bool write)
    {
        if (write) return "/* Aiursoft DMS approved SQL */\n" + sql;
        var trimmed = sql.Trim().TrimEnd(';');
        if (engine == "mysql")
        {
            var timeoutStmt = isMariaDb
                ? $"SET SESSION max_statement_time={seconds.ToString(CultureInfo.InvariantCulture)};"
                : $"SET SESSION max_execution_time={(seconds * 1000).ToString(CultureInfo.InvariantCulture)};";
            return $"{timeoutStmt} SET SESSION TRANSACTION READ ONLY; START TRANSACTION READ ONLY;\n{trimmed};\nROLLBACK;\n";
        }
        return $"BEGIN READ ONLY; SET LOCAL statement_timeout={(seconds * 1000).ToString(CultureInfo.InvariantCulture)}; SET LOCAL search_path=pg_catalog,public;\n{trimmed};\nROLLBACK;";
    }

    public static string[] ResultRows(string output, bool csv)
    {
        if (!csv) return output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        List<string> rows = [];
        var start = 0;
        var quoted = false;
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
            var grants = await ClientProfileAsync(profile, password, "SHOW GRANTS FOR CURRENT_USER", 15, host, token);
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
            var result = await ClientProfileAsync(profile, password, check, 15, host, token);
            if (result.ExitCode != 0 || result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() is not ("f" or "false"))
                throw new IOException("The PostgreSQL reader must not be a privileged or server-file/program role.");
        }
    }

    private static Task<NativeResult> ClientProfileAsync(DatabaseConnectionProfile profile, string password, string sql, int seconds, OperationHost host, CancellationToken token)
    {
        List<string> args;
        string executable;
        Dictionary<string, string> variables = [];
        if (profile.Engine == "mysql")
        {
            executable = "mysql";
            args = ["--no-defaults", "--batch", "--column-names", "--binary-mode", "--local-infile=0", "--default-character-set=utf8mb4", "--connect-timeout=10",
                "--host=" + profile.Host, "--port=" + profile.Port, "--user=" + profile.User, "--database=" + profile.Database, "--ssl-mode=" + (profile.Tls ? "VERIFY_IDENTITY" : "DISABLED")];
            variables["MYSQL_PWD"] = password;
            if (!profile.Tls) args.Add("--get-server-public-key");
        }
        else
        {
            executable = "psql";
            args = ["--no-psqlrc", "--no-password", "--quiet", "--csv", "--set=ON_ERROR_STOP=1", "--set=VERBOSITY=sqlstate", "--host=" + profile.Host, "--port=" + profile.Port,
                "--username=" + profile.User, "--dbname=" + profile.Database, "--file=-"];
            variables["PGPASSWORD"] = password;
            variables["PGSSLMODE"] = profile.Tls ? "verify-full" : "disable";
            variables["PGCONNECT_TIMEOUT"] = "10";
            variables["PGAPPNAME"] = "Aiursoft DMS";
        }
        var standardInput = EncodeClientStdin(profile.Engine, sql);
        List<string> docker = ["exec", "--interactive"];
        foreach (var key in variables.Keys) docker.AddRange(["--env", key]);
        docker.Add(profile.Container ?? throw new InvalidDataException("A local database container is required."));
        docker.Add(executable);
        docker.AddRange(args);
        return host.RunAsync(new NativeCommand("docker", DockerOperations.Arguments(docker.ToArray()), seconds, standardInput, variables), token);
    }

    private static Task<NativeResult> ClientContainerEnvAsync(string containerId, string engine, string database, string sql, int seconds, OperationHost host, CancellationToken token)
    {
        var standardInput = EncodeClientStdin(engine, sql);
        var script = engine == "postgresql" ? PostgresContainerBootstrap : MySqlContainerBootstrap;
        var args = DockerOperations.Arguments("exec", "--interactive", containerId, "sh", "-c", script, "dms-db", database);
        return host.RunAsync(new NativeCommand("docker", args, seconds, standardInput), token);
    }

    private static string EncodeClientStdin(string engine, string sql) =>
        engine == "mysql"
            ? sql
            : "SELECT pg_catalog.convert_from(pg_catalog.decode('"
              + Convert.ToHexString(Encoding.UTF8.GetBytes(sql)) + "','hex'),'UTF8') \\gexec\n";

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
    [GeneratedRegex("mysql|mariadb|postgres", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseImage();
}

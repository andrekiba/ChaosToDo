using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Azure.Provisioning.Sql;
using static Azure.Provisioning.Expressions.BicepFunction;

namespace ChaosToDo.AppHost;

/// <summary>
/// Extension methods for granting extra principals — an existing Entra ID (Azure AD) group, or a
/// single shared <see cref="AzureUserAssignedIdentityResource"/> — access to an
/// <see cref="AzureSqlServerResource"/>'s databases, without making that principal the server's AAD
/// administrator.
/// </summary>
/// <remarks>
/// <para>
/// Aspire's Azure SQL integration supports only a single AAD administrator per server. When a
/// compute resource references a database (e.g. <c>.WithReference(sqldb)</c>), Aspire automatically
/// generates a deployment script that grants that resource's managed identity database access,
/// running the script as the SQL Server's AAD administrator. Aspire assumes that administrator is a
/// User-Assigned Managed Identity: it builds the script's run-as identity resource id directly from
/// <c>sqlServer.Administrators.Login</c>. By default, when the server's <c>Administrators</c> is left
/// untouched, Aspire creates and uses its own auto-generated admin identity (Bicep identifier
/// <c>sqlServerAdminManagedIdentity</c>, referred to below as <c>sql-admin</c>) for this purpose.
/// Every deployment script added by either extension method in this class runs as that same
/// <c>sql-admin</c> identity, because only the server's AAD administrator is allowed to create
/// external (AAD) database users/logins — <c>sql-admin</c> is therefore required infrastructure for
/// running these scripts, not just a naming convention.
/// </para>
/// <para>
/// Overriding <c>Administrators</c> with an AAD *group* (e.g. for interactive/admin console access)
/// breaks that mechanism, because a group's display name is not a real managed identity resource
/// (deploy fails with <c>FailedIdentityOperation</c> / invalid <c>userAssignedIdentities/&lt;group
/// name&gt;</c> resource id). Keep the server's <c>Administrators</c> at Aspire's default
/// <c>sql-admin</c> identity, and use the two methods below to grant extra principals access instead:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <see cref="WithAadGroupDatabaseAccess"/> — grants an existing Entra ID group access to every
/// database, via its own per-database deployment script run under <c>sql-admin</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <see cref="WithManagedIdentityDatabaseAccess"/> — grants a single shared user-assigned managed
/// identity (the one every compute resource is bound to) access to every database, via ONE
/// deployment script per database run under <c>sql-admin</c>. Use this together with
/// <c>.ClearDefaultRoleAssignments()</c> when several compute resources share the same identity, to
/// avoid Aspire's automatic per-resource scripts colliding on an identical resource name (see the
/// remarks on that method for details).
/// </description>
/// </item>
/// </list>
/// <example>
/// <code>
/// var devsGroupName = builder.AddParameter("sqlAdminLogin");
/// var devsGroupObjectId = builder.AddParameter("sqlAdminSid");
///
/// var sql = builder.AddAzureSqlServer("sql")
///     .WithAadGroupDatabaseAccess(devsGroupName, devsGroupObjectId)
///     .ClearDefaultRoleAssignments()
///     .WithManagedIdentityDatabaseAccess(identity);
/// var sqldb = sql.AddDatabase("Db");
/// </code>
/// </example>
/// </remarks>
public static class SqlAzureAccessExtensions
{
    const string SqlServerAdminManagedIdentityBicepIdentifier = "sqlServerAdminManagedIdentity";

    /// <param name="builder">The Azure SQL Server resource builder.</param>
    extension(IResourceBuilder<AzureSqlServerResource> builder)
    {
        /// <summary>
        /// Grants an existing Entra ID group access to every database currently defined on this Azure SQL
        /// Server, by running a deployment script (as the server's default auto-generated admin managed
        /// identity) that creates/reconciles an external database user for the group and adds it to the
        /// given database role.
        /// </summary>
        /// <param name="groupName">
        /// Parameter holding the Entra ID group's display name. Used as the SQL external user's name, so it
        /// must be resolvable as a T-SQL quoted identifier (e.g. via <c>QUOTENAME</c>) — spaces are fine.
        /// </param>
        /// <param name="groupObjectId">
        /// Parameter holding the Entra ID group's object id (GUID), used to compute the external user's SID.
        /// </param>
        /// <param name="databaseRole">The database role to add the group to. Defaults to <c>"db_owner"</c>.</param>
        /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
        /// <remarks>
        /// Requires the server's <c>Administrators</c> to be left at Aspire's default (its own
        /// auto-generated user-assigned managed identity, named <c>sqlServerAdminManagedIdentity</c>) — do
        /// not combine this with a custom <c>sqlServer.Administrators</c> override in
        /// <c>ConfigureInfrastructure</c>.
        /// </remarks>
        public IResourceBuilder<AzureSqlServerResource> WithAadGroupDatabaseAccess(IResourceBuilder<ParameterResource> groupName,
            IResourceBuilder<ParameterResource> groupObjectId,
            string databaseRole = "db_owner")
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(groupName);
            ArgumentNullException.ThrowIfNull(groupObjectId);
            ArgumentException.ThrowIfNullOrEmpty(databaseRole);

            builder.ConfigureInfrastructure(infra =>
            {
                var resources = infra.GetProvisionableResources().ToList();
                var sqlServer = resources.OfType<SqlServer>().Single();
                var sqlDatabases = resources.OfType<SqlDatabase>().ToList();

                if (sqlDatabases.Count == 0)
                {
                    // No databases defined (yet) on this infra pass — nothing to grant access to.
                    return;
                }

                var adminIdentity = resources.OfType<UserAssignedIdentity>()
                                        .SingleOrDefault(i => i.BicepIdentifier == SqlServerAdminManagedIdentityBicepIdentifier)
                                    ?? throw new InvalidOperationException(
                                        $"Could not find the Azure SQL Server's default admin managed identity ('{SqlServerAdminManagedIdentityBicepIdentifier}'). " +
                                        $"{nameof(WithAadGroupDatabaseAccess)} requires Aspire's default AAD administrator (its own auto-generated " +
                                        "user-assigned managed identity); it cannot be combined with a custom sqlServer.Administrators override.");

                var adminIdentityResourceId = Interpolate($"{adminIdentity.Id}").Compile().ToString();
                var groupNameParameter = groupName.AsProvisioningParameter(infra);
                var groupObjectIdParameter = groupObjectId.AsProvisioningParameter(infra);

                foreach (var sqlDatabase in sqlDatabases)
                {
                    var scriptIdentifier = Infrastructure.NormalizeBicepIdentifier($"script_grant_aad_group_{sqlDatabase.BicepIdentifier}");

                    var script = new AzurePowerShellScript(scriptIdentifier)
                    {
                        Name = Take(Interpolate($"grant-aad-{GetUniqueString(new StringLiteralExpression(sqlDatabase.BicepIdentifier), GetResourceGroup().Id)}"), 24),
                        RetentionInterval = TimeSpan.FromHours(1),
                        // List of supported versions: https://mcr.microsoft.com/v2/azuredeploymentscripts-powershell/tags/list
                        AzPowerShellVersion = "14.0",
                        Identity =
                        {
                            // Run the script as the SQL Server's default admin identity, which is the only
                            // principal allowed to create external (AAD) database users.
                            IdentityType = ArmDeploymentScriptManagedIdentityType.UserAssigned,
                            UserAssignedIdentities =
                            {
                                [adminIdentityResourceId] = new UserAssignedIdentityDetails()
                            }
                        }
                    };

                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBNAME", Value = sqlDatabase.Name });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBSERVER", Value = sqlServer.FullyQualifiedDomainName });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "PRINCIPALNAME", Value = groupNameParameter });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "ID", Value = groupObjectIdParameter });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBROLE", Value = databaseRole });
                    script.ScriptContent = GrantAadGroupSqlAccessScript;
                    script.DependsOn.Add(sqlDatabase);

                    infra.Add(script);
                }
            });

            return builder;
        }

        /// <summary>
        /// Grants a single, shared <see cref="AzureUserAssignedIdentityResource"/> access to every
        /// database currently defined on this Azure SQL Server, by running ONE deployment script (as
        /// the server's default auto-generated admin managed identity) per database.
        /// </summary>
        /// <param name="identity">
        /// The shared user-assigned managed identity to grant database access to. Use the same
        /// <see cref="AzureUserAssignedIdentityResource"/> that every compute resource is bound to via
        /// <c>WithAzureUserAssignedIdentity</c>.
        /// </param>
        /// <param name="databaseRole">The database role to add the identity to. Defaults to <c>"db_owner"</c>.</param>
        /// <returns>The same <paramref name="builder"/>, for chaining.</returns>
        /// <remarks>
        /// <para>
        /// When multiple compute resources reference the same database via <c>.WithReference(sqldb)</c>
        /// and are ALL bound to the same shared identity (via <c>WithAzureUserAssignedIdentity</c>),
        /// Aspire's own automatic per-resource role-assignment script (see the remarks on this class)
        /// computes an IDENTICAL deployment script resource name for every one of them (its name is a
        /// hash of the principal name + database + resource group, and the principal name is now the
        /// same shared identity for all of them). Azure then runs several concurrent deployments trying
        /// to create/update/execute that same <c>Microsoft.Resources/deploymentScripts</c> resource at
        /// once, which fails with errors like "container instance ... was not found" because the
        /// underlying ephemeral Azure Container Instance is not safe for concurrent identical runs.
        /// </para>
        /// <para>
        /// The fix is to disable Aspire's automatic per-resource script entirely (call
        /// <c>.ClearDefaultRoleAssignments()</c> on the SQL server builder) and grant the shared
        /// identity access exactly ONCE via this method instead.
        /// </para>
        /// <para>
        /// Like <see cref="WithAadGroupDatabaseAccess"/>, this requires the server's
        /// <c>Administrators</c> to be left at Aspire's default auto-generated admin identity.
        /// </para>
        /// </remarks>
        public IResourceBuilder<AzureSqlServerResource> WithManagedIdentityDatabaseAccess(IResourceBuilder<AzureUserAssignedIdentityResource> identity,
            string databaseRole = "db_owner")
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(identity);
            ArgumentException.ThrowIfNullOrEmpty(databaseRole);

            builder.ConfigureInfrastructure(infra =>
            {
                var resources = infra.GetProvisionableResources().ToList();
                var sqlServer = resources.OfType<SqlServer>().Single();
                var sqlDatabases = resources.OfType<SqlDatabase>().ToList();

                if (sqlDatabases.Count == 0)
                {
                    // No databases defined (yet) on this infra pass — nothing to grant access to.
                    return;
                }

                var adminIdentity = resources.OfType<UserAssignedIdentity>()
                                        .SingleOrDefault(i => i.BicepIdentifier == SqlServerAdminManagedIdentityBicepIdentifier)
                                    ?? throw new InvalidOperationException(
                                        $"Could not find the Azure SQL Server's default admin managed identity ('{SqlServerAdminManagedIdentityBicepIdentifier}'). " +
                                        $"{nameof(WithManagedIdentityDatabaseAccess)} requires Aspire's default AAD administrator (its own auto-generated " +
                                        "user-assigned managed identity); it cannot be combined with a custom sqlServer.Administrators override.");

                var adminIdentityResourceId = Interpolate($"{adminIdentity.Id}").Compile().ToString();
                // The external SQL user is named after the identity's ARM resource name (matching
                // Aspire's own convention for per-resource UAMI grants), and its SID is derived from the
                // identity's *client id* (application id) — not its principal/object id, which is what
                // AAD *groups* and *users* use instead. This is the standard Azure AD auth nuance for
                // authenticating a user-assigned managed identity against SQL Server.
                var principalNameParameter = identity.Resource.PrincipalName.AsProvisioningParameter(infra);
                var clientIdParameter = identity.Resource.ClientId.AsProvisioningParameter(infra);

                foreach (var sqlDatabase in sqlDatabases)
                {
                    var scriptIdentifier = Infrastructure.NormalizeBicepIdentifier($"script_grant_mi_{sqlDatabase.BicepIdentifier}");

                    var script = new AzurePowerShellScript(scriptIdentifier)
                    {
                        Name = Take(Interpolate($"grant-mi-{GetUniqueString(new StringLiteralExpression(sqlDatabase.BicepIdentifier), GetResourceGroup().Id)}"), 24),
                        RetentionInterval = TimeSpan.FromHours(1),
                        // List of supported versions: https://mcr.microsoft.com/v2/azuredeploymentscripts-powershell/tags/list
                        AzPowerShellVersion = "14.0",
                        Identity =
                        {
                            // Run the script as the SQL Server's default admin identity, which is the only
                            // principal allowed to create external (AAD) database users.
                            IdentityType = ArmDeploymentScriptManagedIdentityType.UserAssigned,
                            UserAssignedIdentities =
                            {
                                [adminIdentityResourceId] = new UserAssignedIdentityDetails()
                            }
                        }
                    };

                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBNAME", Value = sqlDatabase.Name });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBSERVER", Value = sqlServer.FullyQualifiedDomainName });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "PRINCIPALNAME", Value = principalNameParameter });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "ID", Value = clientIdParameter });
                    script.EnvironmentVariables.Add(new ScriptEnvironmentVariable { Name = "DBROLE", Value = databaseRole });
                    script.ScriptContent = GrantManagedIdentitySqlAccessScript;
                    script.DependsOn.Add(sqlDatabase);

                    infra.Add(script);
                }
            });

            return builder;
        }
    }

    // Adapted from Aspire's own PrincipalReconciliationScript
    // (Aspire.Hosting.Azure.Sql/AzureSqlServerResource.cs), but for an external AAD *group*
    // (TYPE = X, not TYPE = E, since a group — not a user — is being granted access) and with a
    // configurable role (via the DBROLE env var) instead of a single hard-coded job identity role.
    const string GrantAadGroupSqlAccessScript = """
                                                $sqlServerFqdn = "$env:DBSERVER"
                                                $sqlDatabaseName = "$env:DBNAME"
                                                $principalName = "$env:PRINCIPALNAME"
                                                $id = "$env:ID"
                                                $dbRole = "$env:DBROLE"

                                                # The principal name is interpolated into a T-SQL string literal below; groups can legitimately
                                                # contain an apostrophe in their display name, so double it up to keep the literal well formed.
                                                $escapedPrincipalName = $principalName.Replace("'", "''")
                                                $escapedDbRole = $dbRole.Replace("'", "''")

                                                $sqlCmd = @"
                                                DECLARE @name SYSNAME = '$escapedPrincipalName';
                                                DECLARE @role SYSNAME = '$escapedDbRole';
                                                DECLARE @id UNIQUEIDENTIFIER = '$id';

                                                -- The SID of an Entra principal is the raw bytes of its object id. @castId is that same
                                                -- value rendered as the 0x... literal that CREATE USER ... WITH SID requires.
                                                DECLARE @sid VARBINARY(16) = CONVERT(VARBINARY(16), @id);
                                                DECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), @sid, 1);

                                                -- Reconciliation below can drop and recreate the principal, so run the whole sequence as a
                                                -- single unit. XACT_ABORT rolls the transaction back on any error, so a failure between
                                                -- DROP USER and CREATE USER cannot leave the database with no user for this identity.
                                                SET XACT_ABORT ON;
                                                BEGIN TRANSACTION;

                                                -- Only external (Entra) groups are considered (type 'X'), because that is the only kind this
                                                -- script creates. A stale principal with the same name but a different type (SQL user, Windows
                                                -- user, role, dbo, ...) would have a different sid, so leaving it alone here is intentional:
                                                -- CREATE USER below would then fail loudly with 'Msg 15023: User already exists', rather than
                                                -- silently dropping a principal this script does not own.
                                                DECLARE @existingSid VARBINARY(85) = (SELECT sid FROM sys.database_principals WHERE name = @name AND type = 'X');

                                                -- A group left over from an earlier deployment can carry a stale SID if it was deleted and
                                                -- recreated in Entra ID (keeping the name but changing the object id). Granting a role to that
                                                -- principal would report success while members of the group still failed to log in, so drop it
                                                -- and let it be recreated against the object id we were actually given.
                                                IF @existingSid IS NOT NULL AND @existingSid <> @sid
                                                BEGIN
                                                    DECLARE @dropCmd NVARCHAR(MAX) = N'DROP USER ' + QUOTENAME(@name);
                                                    EXEC (@dropCmd);
                                                    SET @existingSid = NULL;
                                                END

                                                -- Only create the user when it is missing. This script is re-executed on redeploys, and the
                                                -- retry loop below can also re-run this batch after a transient failure that occurred *after*
                                                -- the user was already created. An unguarded CREATE USER would then fail with
                                                -- 'Msg 15023: User already exists in current database', turning a transient error into a
                                                -- permanent deployment failure.
                                                IF @existingSid IS NULL
                                                BEGIN
                                                    DECLARE @cmd NVARCHAR(MAX) = N'CREATE USER ' + QUOTENAME(@name) + N' WITH SID = ' + @castId + N', TYPE = X;'
                                                    EXEC (@cmd);
                                                END

                                                -- Assign the requested role to the group. ALTER ROLE ... ADD MEMBER is a no-op when the
                                                -- principal is already a member.
                                                DECLARE @roleCmd NVARCHAR(MAX) = N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + QUOTENAME(@name);
                                                EXEC (@roleCmd);

                                                COMMIT TRANSACTION;
                                                "@

                                                Write-Host $sqlCmd

                                                # This script deliberately avoids the SqlServer PowerShell module (Invoke-Sqlcmd) in favor of
                                                # System.Data.SqlClient with a managed identity access token — see Aspire's own
                                                # PrincipalReconciliationScript (Aspire.Hosting.Azure.Sql/AzureSqlServerResource.cs) for the
                                                # detailed rationale (module/assembly version conflicts inside the deployment script image).
                                                # The token audience is cloud specific, so derive it from the deployment script's Az context
                                                # rather than assuming public cloud.
                                                $sqlDnsSuffix = (Get-AzContext).Environment.SqlDatabaseDnsSuffix
                                                if ([string]::IsNullOrWhiteSpace($sqlDnsSuffix)) {
                                                    $sqlDnsSuffix = ".database.windows.net"
                                                }
                                                $sqlAudience = "https://" + $sqlDnsSuffix.TrimStart('.') + "/"

                                                $connectionString = "Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"

                                                $maxRetries = 5
                                                $retryDelay = 60
                                                $attempt = 0
                                                $success = $false

                                                while (-not $success -and $attempt -lt $maxRetries) {
                                                    $attempt++
                                                    Write-Host "Attempt $attempt of $maxRetries..."
                                                    $connection = $null
                                                    try {
                                                        # Acquired inside the loop so a transient token failure is retried like any other
                                                        # failure, rather than aborting the script before the first attempt.
                                                        $tokenResponse = Get-AzAccessToken -ResourceUrl $sqlAudience

                                                        # Az.Accounts 5.x returns the token as a SecureString, earlier majors return a plain string.
                                                        $accessToken = if ($tokenResponse.Token -is [System.Security.SecureString]) {
                                                            [System.Net.NetworkCredential]::new("", $tokenResponse.Token).Password
                                                        } else {
                                                            $tokenResponse.Token
                                                        }

                                                        $connection = New-Object System.Data.SqlClient.SqlConnection
                                                        $connection.ConnectionString = $connectionString
                                                        $connection.AccessToken = $accessToken
                                                        $connection.Open()

                                                        $command = $connection.CreateCommand()
                                                        $command.CommandText = $sqlCmd
                                                        [void]$command.ExecuteNonQuery()

                                                        $success = $true
                                                        Write-Host "SQL command succeeded on attempt $attempt."
                                                    } catch {
                                                        Write-Host "Attempt $attempt failed: $_"
                                                        if ($attempt -lt $maxRetries) {
                                                            Write-Host "Retrying in $retryDelay seconds..."
                                                            Start-Sleep -Seconds $retryDelay
                                                        } else {
                                                            throw
                                                        }
                                                    } finally {
                                                        if ($null -ne $connection) {
                                                            $connection.Dispose()
                                                        }
                                                    }
                                                }
                                                """;

    // Adapted from Aspire's own PrincipalReconciliationScript (Aspire.Hosting.Azure.Sql/AzureSqlServerResource.cs),
    // but running once for a single shared identity (via DBROLE) instead of once per compute resource.
    // TYPE = E (external service principal) is used, and the SID is derived from the identity's
    // *client id*, matching how Aspire's own per-resource script grants a user-assigned managed identity access.
    const string GrantManagedIdentitySqlAccessScript = """
                                                       $sqlServerFqdn = "$env:DBSERVER"
                                                       $sqlDatabaseName = "$env:DBNAME"
                                                       $principalName = "$env:PRINCIPALNAME"
                                                       $id = "$env:ID"
                                                       $dbRole = "$env:DBROLE"

                                                       # The principal name is interpolated into a T-SQL string literal below. Managed identity
                                                       # resource names cannot contain an apostrophe, but escape defensively anyway for consistency
                                                       # with the AAD group variant of this script.
                                                       $escapedPrincipalName = $principalName.Replace("'", "''")
                                                       $escapedDbRole = $dbRole.Replace("'", "''")

                                                       $sqlCmd = @"
                                                       DECLARE @name SYSNAME = '$escapedPrincipalName';
                                                       DECLARE @role SYSNAME = '$escapedDbRole';
                                                       DECLARE @id UNIQUEIDENTIFIER = '$id';

                                                       -- The SID of a user-assigned managed identity, for SQL external-user purposes, is the raw
                                                       -- bytes of its *client id* (application id) - not its principal/object id, which is what
                                                       -- AAD users/groups use instead. @castId is that same value rendered as the 0x... literal
                                                       -- that CREATE USER ... WITH SID requires.
                                                       DECLARE @sid VARBINARY(16) = CONVERT(VARBINARY(16), @id);
                                                       DECLARE @castId NVARCHAR(MAX) = CONVERT(VARCHAR(MAX), @sid, 1);

                                                       -- Reconciliation below can drop and recreate the principal, so run the whole sequence as a
                                                       -- single unit. XACT_ABORT rolls the transaction back on any error, so a failure between
                                                       -- DROP USER and CREATE USER cannot leave the database with no user for this identity.
                                                       SET XACT_ABORT ON;
                                                       BEGIN TRANSACTION;

                                                       -- Only external (Entra) users are considered (type 'E'), because that is the only kind this
                                                       -- script creates. A stale principal with the same name but a different type (SQL user,
                                                       -- Windows user, role, dbo, ...) would have a different sid, so leaving it alone here is
                                                       -- intentional: CREATE USER below would then fail loudly with 'Msg 15023: User already
                                                       -- exists', rather than silently dropping a principal this script does not own.
                                                       DECLARE @existingSid VARBINARY(85) = (SELECT sid FROM sys.database_principals WHERE name = @name AND type = 'E');

                                                       -- A user left over from an earlier deployment can carry a stale SID, because deleting and
                                                       -- recreating a managed identity keeps the name but changes the client id. Granting a role to
                                                       -- that principal would report success while the application still failed to log in, so drop
                                                       -- it and let it be recreated against the client id we were actually given.
                                                       IF @existingSid IS NOT NULL AND @existingSid <> @sid
                                                       BEGIN
                                                           DECLARE @dropCmd NVARCHAR(MAX) = N'DROP USER ' + QUOTENAME(@name);
                                                           EXEC (@dropCmd);
                                                           SET @existingSid = NULL;
                                                       END

                                                       -- Only create the user when it is missing. This script is re-executed on redeploys, and the
                                                       -- retry loop below can also re-run this batch after a transient failure that occurred *after*
                                                       -- the user was already created. An unguarded CREATE USER would then fail with
                                                       -- 'Msg 15023: User already exists in current database', turning a transient error into a
                                                       -- permanent deployment failure.
                                                       IF @existingSid IS NULL
                                                       BEGIN
                                                           DECLARE @cmd NVARCHAR(MAX) = N'CREATE USER ' + QUOTENAME(@name) + N' WITH SID = ' + @castId + N', TYPE = E;'
                                                           EXEC (@cmd);
                                                       END

                                                       -- Assign the requested role to the identity. ALTER ROLE ... ADD MEMBER is a no-op when the
                                                       -- principal is already a member.
                                                       DECLARE @roleCmd NVARCHAR(MAX) = N'ALTER ROLE ' + QUOTENAME(@role) + N' ADD MEMBER ' + QUOTENAME(@name);
                                                       EXEC (@roleCmd);

                                                       COMMIT TRANSACTION;
                                                       "@

                                                       Write-Host $sqlCmd

                                                       # This script deliberately avoids the SqlServer PowerShell module (Invoke-Sqlcmd) in favor of
                                                       # System.Data.SqlClient with a managed identity access token — see Aspire's own
                                                       # PrincipalReconciliationScript (Aspire.Hosting.Azure.Sql/AzureSqlServerResource.cs) for the
                                                       # detailed rationale (module/assembly version conflicts inside the deployment script image).
                                                       # The token audience is cloud specific, so derive it from the deployment script's Az context
                                                       # rather than assuming public cloud.
                                                       $sqlDnsSuffix = (Get-AzContext).Environment.SqlDatabaseDnsSuffix
                                                       if ([string]::IsNullOrWhiteSpace($sqlDnsSuffix)) {
                                                           $sqlDnsSuffix = ".database.windows.net"
                                                       }
                                                       $sqlAudience = "https://" + $sqlDnsSuffix.TrimStart('.') + "/"

                                                       $connectionString = "Server=tcp:${sqlServerFqdn},1433;Initial Catalog=${sqlDatabaseName};Encrypt=True;TrustServerCertificate=False;"

                                                       $maxRetries = 5
                                                       $retryDelay = 60
                                                       $attempt = 0
                                                       $success = $false

                                                       while (-not $success -and $attempt -lt $maxRetries) {
                                                           $attempt++
                                                           Write-Host "Attempt $attempt of $maxRetries..."
                                                           $connection = $null
                                                           try {
                                                               # Acquired inside the loop so a transient token failure is retried like any other
                                                               # failure, rather than aborting the script before the first attempt.
                                                               $tokenResponse = Get-AzAccessToken -ResourceUrl $sqlAudience

                                                               # Az.Accounts 5.x returns the token as a SecureString, earlier majors return a plain string.
                                                               $accessToken = if ($tokenResponse.Token -is [System.Security.SecureString]) {
                                                                   [System.Net.NetworkCredential]::new("", $tokenResponse.Token).Password
                                                               } else {
                                                                   $tokenResponse.Token
                                                               }

                                                               $connection = New-Object System.Data.SqlClient.SqlConnection
                                                               $connection.ConnectionString = $connectionString
                                                               $connection.AccessToken = $accessToken
                                                               $connection.Open()

                                                               $command = $connection.CreateCommand()
                                                               $command.CommandText = $sqlCmd
                                                               [void]$command.ExecuteNonQuery()

                                                               $success = $true
                                                               Write-Host "SQL command succeeded on attempt $attempt."
                                                           } catch {
                                                               Write-Host "Attempt $attempt failed: $_"
                                                               if ($attempt -lt $maxRetries) {
                                                                   Write-Host "Retrying in $retryDelay seconds..."
                                                                   Start-Sleep -Seconds $retryDelay
                                                               } else {
                                                                   throw
                                                               }
                                                           } finally {
                                                               if ($null -ne $connection) {
                                                                   $connection.Dispose()
                                                               }
                                                           }
                                                       }
                                                       """;
}

#if NETFRAMEWORK
using System.Data.Entity;
#else
using Microsoft.EntityFrameworkCore;
#endif
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace TAMS.EfBulk
{
    public static class BulkExtensions
    {
        /// <summary>
        /// Gets the table name of a DbSet entity type from the DbContext.
        /// </summary>
        /// <typeparam name="T">The entity type of the DbSet.</typeparam>
        /// <param name="context">The DbContext instance.</param>
        /// <returns>The table name of the DbSet entity type.</returns>
        public static string GetDbSetTableName<T>(this DbContext context) where T : class
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var dbSet = context.Set<T>();
            if (dbSet == null)
                throw new ArgumentException("Type must exist as a DbSet in the DbContext.", nameof(T));

            var tableAttribute = typeof(T).GetCustomAttribute<TableAttribute>(true);
            if (tableAttribute == null)
                throw new CustomAttributeFormatException("DbSet must be decorated with the Table attribute.");

            string tableName = $"[{tableAttribute.Name}]";
            if (!string.IsNullOrEmpty(tableAttribute.Schema))
                tableName = $"[{tableAttribute.Schema}].{tableName}";

            return tableName;
        }

        /// <summary>
        /// Gets the SqlConnection instance from the DbContext.
        /// </summary>
        /// <param name="context">The DbContext instance.</param>
        /// <returns>The SqlConnection instance.</returns>
        private static SqlConnection GetConnection(DbContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            SqlConnection connection;
#if NETFRAMEWORK
            connection = context.Database.Connection as SqlConnection;
#else
            connection = context.Database.GetDbConnection() as SqlConnection;
#endif
            if (connection == null)
                throw new InvalidOperationException("Failed to obtain SQL connection from DbContext.");

            return connection;
        }

        /// <summary>
        /// Creates a blank DataTable from the provided DbSet entity type.
        /// </summary>
        /// <typeparam name="T">Reference to the entity type for a DbSet in the context.</typeparam>
        /// <param name="context">Reference to the DbContext.</param>
        /// <returns>Blank DataTable with the schema copied from the relevant DbSet of type T.</returns>
        public static async Task<DataTable> CreateDataTableAsync<T>(this DbContext context) where T : class
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var table = new DataTable();

            string tableName = context.GetDbSetTableName<T>();
            string sqlQuery = $"SELECT TOP 0 * FROM {tableName};";
            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
                await cnn.OpenAsync();

            using (SqlDataAdapter adapter = new SqlDataAdapter(sqlQuery, cnn))
            {
                adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
                adapter.Fill(table);
            }

            await ApplyAutoIncrementFromDatabaseAsync(table, tableName, cnn);
            table.TableName = tableName;

            return table;
        }

        /// <summary>
        /// Applies auto-increment settings from the database to the DataTable.
        /// </summary>
        /// <param name="table">The DataTable instance.</param>
        /// <param name="tableName">The table name.</param>
        /// <param name="cnn">The SqlConnection instance.</param>
        private static async Task ApplyAutoIncrementFromDatabaseAsync(DataTable table, string tableName, SqlConnection cnn)
        {
            if (table.PrimaryKey.Any(o => o.AutoIncrement))
            {
                string getCurrentAutoIncrementScript = string.Format("SELECT IDENT_CURRENT ( '{0}' ) CurrentValue, IDENT_SEED ( '{0}' ) SeedValue, IDENT_INCR ( '{0}' ) IncrementValue;", tableName);
                using (SqlCommand cmd = new SqlCommand(getCurrentAutoIncrementScript, cnn))
                using (SqlDataReader rdr = await cmd.ExecuteReaderAsync())
                {
                    if (!rdr.HasRows || !await rdr.ReadAsync()) return;

                    long currentValue = Int64.Parse(rdr["CurrentValue"].ToString());
                    long originalSeed = Int64.Parse(rdr["SeedValue"].ToString());
                    long incrementAmount = Int64.Parse(rdr["IncrementValue"].ToString());
                    foreach (var primaryKey in table.PrimaryKey)
                    {
                        primaryKey.AutoIncrementSeed = currentValue + incrementAmount;
                        primaryKey.AutoIncrementStep = incrementAmount;
                    }
                }
            }
        }

        /// <summary>
        /// Creates a DataTable from the provided DbSet entity type and fills it with records from the database.
        /// </summary>
        /// <typeparam name="T">Reference to the entity type for a DbSet in the context.</typeparam>
        /// <param name="context">Reference to the DbContext.</param>
        /// <returns>Blank DataTable with the schema copied from the relevant DbSet of type T.</returns>
        public static async Task<DataTable> CreateOfflineDataTableAsync<T>(this DbContext context) where T : class
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var table = new DataTable();

            string tableName = context.GetDbSetTableName<T>();
            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
                await cnn.OpenAsync();

            using (var adapter = new SqlDataAdapter($"SELECT * FROM {tableName};", cnn))
            {
                adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
                adapter.Fill(table);
            }
            await ApplyAutoIncrementFromDatabaseAsync(table, tableName, cnn);
            table.TableName = tableName;
            return table;
        }

        /// <summary>
        /// Performs a bulk insert operation using the provided DataTable.
        /// </summary>
        /// <typeparam name="T">Reference to the entity type for a DbSet in the context.</typeparam>
        /// <param name="context">Reference to the DbContext.</param>
        /// <param name="table">The DataTable containing the data to insert.</param>
        /// <param name="batchSize">The number of rows to insert in each batch. Default is 0 (all rows in one batch).</param>
        /// <param name="timeout">The timeout period for the bulk copy operation. Default is 30 seconds.</param>
        /// <param name="sqlRowsCopiedEventHandler">The event handler for SQL rows copied event.</param>
        public static async Task BulkInsertAsync<T>(
          this DbContext context,
          DataTable table,
          int batchSize = 0,
          int timeout = 30,
          SqlRowsCopiedEventHandler sqlRowsCopiedEventHandler = null
        ) where T : class
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            if (string.IsNullOrEmpty(table.TableName))
                throw new Exception("DataTable is missing TableName. Cannot insert DataTable without a reference to the TableName.");

            string tableName = context.GetDbSetTableName<T>();
            if (table.TableName != tableName)
                throw new ArgumentException("DataTable target entity type does not match the generic type argument.", nameof(table));

            if (table.Rows.Count <= 0)
                throw new DBConcurrencyException();

            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
                await cnn.OpenAsync();

            using (var bulk = new SqlBulkCopy(cnn))
            {
                bulk.DestinationTableName = table.TableName;
                foreach (DataColumn column in table.Columns)
                {
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }
                bulk.BatchSize = batchSize;
                bulk.BulkCopyTimeout = timeout;

                if (sqlRowsCopiedEventHandler != null)
                {
                    bulk.SqlRowsCopied += sqlRowsCopiedEventHandler;
                    if (batchSize > 0)
                        bulk.NotifyAfter = batchSize;
                }

                await bulk.WriteToServerAsync(table);
            }
        }

        /// <summary>
        /// Performs a SQL MERGE operation using the provided DataTable.
        /// </summary>
        /// <typeparam name="T">Reference to the underlying entity type of the table.</typeparam>
        /// <param name="context">The DbContext instance.</param>
        /// <param name="table">The DataTable containing the data to merge.</param>
        /// <param name="allowInsert">Indicates whether insert operations are allowed.</param>
        /// <param name="allowDelete">Indicates whether delete operations are allowed.</param>
        /// <param name="batchSize">The number of rows to process in each batch. Default is 0 (all rows in one batch).</param>
        /// <param name="timeout">The timeout period for the merge operation. Default is 30 seconds.</param>
        /// <param name="sqlRowsCopiedEventHandler">The event handler for SQL rows copied event.</param>
        public static async Task BulkMergeAsync<T>(
          this DbContext context,
          DataTable table,
          bool allowInsert,
          bool allowDelete,
          int batchSize = 0,
          int timeout = 30,
          SqlRowsCopiedEventHandler sqlRowsCopiedEventHandler = null
        ) where T : class
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (table == null)
                throw new ArgumentNullException(nameof(table));
            if (string.IsNullOrEmpty(table.TableName))
                throw new Exception("DataTable is missing TableName. Cannot insert DataTable without a reference to the TableName.");

            string tableName = context.GetDbSetTableName<T>();
            if (table.TableName != tableName)
                throw new ArgumentException("DataTable target entity type does not match the generic type argument.", nameof(table));

            if (table.Rows.Count <= 0)
                throw new DBConcurrencyException();

            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
                await cnn.OpenAsync();

            string temporaryTableName = $"[{typeof(T).Name}]";
            using (var cmd = new SqlCommand(BuildCreateTableScript(table, "tmp", temporaryTableName), cnn))
            using (var bulk = new SqlBulkCopy(cnn))
            {
                await cmd.ExecuteNonQueryAsync();

                bulk.DestinationTableName = $"[tmp].{temporaryTableName}";
                foreach (DataColumn column in table.Columns)
                {
                    bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
                }
                bulk.BatchSize = batchSize;
                bulk.BulkCopyTimeout = timeout;

                if (sqlRowsCopiedEventHandler != null)
                {
                    bulk.SqlRowsCopied += sqlRowsCopiedEventHandler;
                    if (batchSize > 0)
                        bulk.NotifyAfter = batchSize;
                }

                await bulk.WriteToServerAsync(table);

                cmd.CommandTimeout = bulk.BulkCopyTimeout;
                cmd.CommandText = BuildMergeTableScript(table, $"[tmp].{temporaryTableName}", allowInsert, allowDelete);
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = $"DROP TABLE [tmp].{temporaryTableName};";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        #region Copied From StackExchange
        /// <summary>
        /// Creates a SQL script that creates a table where the columns match that of the specified DataTable.
        /// </summary>
        private static string BuildCreateTableScript(DataTable Table, string schema, string tableName)
        {
            string fullTableName = $"[tmp].{tableName}";
            StringBuilder result = new StringBuilder();
            result.AppendFormat("IF SCHEMA_ID ('{1}') IS NULL{0}", Environment.NewLine, schema);
            result.AppendFormat("\tEXECUTE ('CREATE SCHEMA {1}');{0}", Environment.NewLine, schema);
            result.AppendFormat("IF OBJECT_ID ('{1}', 'table') IS NOT NULL{0}", Environment.NewLine, fullTableName);
            result.AppendFormat("\tDROP TABLE {1};{0}{0}", Environment.NewLine, fullTableName);
            result.AppendFormat("CREATE TABLE {1} ({0}   ", Environment.NewLine, fullTableName);

            bool FirstTime = true;
            foreach (DataColumn column in Table.Columns.OfType<DataColumn>())
            {
                if (FirstTime) FirstTime = false;
                else
                    result.Append("   ,");

                result.AppendFormat("[{0}] {1} {2} {3}",
                    column.ColumnName, // 0
                    GetSQLTypeAsString(column), // 1
                    column.AllowDBNull ? "NULL" : "NOT NULL", // 2
                    Environment.NewLine // 3
                );
            }
            result.AppendFormat(") ON [PRIMARY]{0}", Environment.NewLine);

            if (Table.PrimaryKey.Length > 0)
                result.Append(BuildKeysScript(Table, fullTableName));

            return result.ToString();
        }

        /// <summary>
        /// Creates a SQL script that merges one table into another where the schema is the same for both tables.
        /// </summary>
        /// <param name="Table">DataTable containing the update information and the reference to the target table by DataTable.TableName.</param>
        /// <param name="tableSourceName">Name of the source table.</param>
        /// <returns>The SQL merge script.</returns>
        private static string BuildMergeTableScript(DataTable Table, string tableSourceName, bool allowInserts = true, bool allowDeletes = false)
        {
            StringBuilder result = new StringBuilder();
            result.AppendFormat("MERGE {1} AS Target {0}USING {2} AS [Source] {0}", Environment.NewLine, Table.TableName, tableSourceName);
            result.Append("ON ");
            for (int i = 0; i < Table.PrimaryKey.Length; i++)
            {
                result.AppendFormat("([Source].[{0}] = [Target].[{0}])", Table.PrimaryKey[i].ColumnName);
                if (i != Table.PrimaryKey.Length - 1)
                    result.Append(" AND ");
            }
            result.AppendLine(" ");

            var dataColumns = Table.Columns.OfType<DataColumn>().Except(Table.PrimaryKey);

            bool FirstTime = true;
            if (allowInserts)
            {
                result.AppendLine("-- For Inserts");
                result.AppendLine("WHEN NOT MATCHED BY Target THEN");
                result.Append("\tINSERT (");
                FirstTime = true;
                foreach (DataColumn column in dataColumns)
                {
                    if (FirstTime) FirstTime = false;
                    else
                        result.Append(", ");

                    result.AppendFormat("[{0}]", column.ColumnName);
                }
                result.AppendLine(")");

                result.Append("\tVALUES (");
                FirstTime = true;
                foreach (DataColumn column in dataColumns)
                {
                    if (FirstTime) FirstTime = false;
                    else
                        result.Append(", ");

                    result.AppendFormat("[Source].[{0}]", column.ColumnName);
                }
                result.AppendLine(")");
            }

            result.AppendLine("-- For Updates");
            result.AppendLine("WHEN MATCHED THEN UPDATE SET");
            FirstTime = true;
            foreach (DataColumn column in dataColumns)
            {
                if (FirstTime) FirstTime = false;
                else
                    result.Append(", ");

                result.AppendFormat("{0}\t[Target].[{1}] = [Source].[{1}]", Environment.NewLine, column.ColumnName);
            }
            result.AppendLine("");

            FirstTime = true;
            if (allowDeletes)
            {
                result.AppendLine("-- For Deletes");
                result.AppendLine("WHEN NOT MATCHED BY Source THEN");
                result.AppendLine("\tDELETE");
            }

            result.AppendLine(";");

            return result.ToString();
        }

        /// <summary>
        /// Builds an ALTER TABLE script that adds a primary or composite key to a table that already exists.
        /// </summary>
        private static string BuildKeysScript(DataTable Table, string tableName)
        {
            if (Table.PrimaryKey.Length < 1) return string.Empty;

            StringBuilder result = new StringBuilder();

            if (Table.PrimaryKey.Length == 1)
                result.AppendFormat("ALTER TABLE {1}{0}   ADD PRIMARY KEY ({2}){0}", Environment.NewLine, tableName, Table.PrimaryKey[0].ColumnName);
            else
            {
                List<string> compositeKeys = Table.PrimaryKey.OfType<DataColumn>().Select(dc => dc.ColumnName).ToList();
                string keyName = compositeKeys.Aggregate((a, b) => a + b);
                string keys = compositeKeys.Aggregate((a, b) => string.Format("{0}, {1}", a, b));
                result.AppendFormat("ALTER TABLE {1}{0}ADD CONSTRAINT pk_{3} PRIMARY KEY ({2}){0}", Environment.NewLine, tableName, keys, keyName);
            }

            return result.ToString();
        }

        /// <summary>
        /// Returns the SQL data type equivalent, as a string for use in SQL script generation methods.
        /// </summary>
        private static string GetSQLTypeAsString(DataColumn column)
        {
            switch (column.DataType.Name)
            {
                case "Boolean": return "[bit]";
                case "Char": return "[char]";
                case "SByte": return "[tinyint]";
                case "Int16": return "[smallint]";
                case "Int32": return "[int]";
                case "Int64": return "[bigint]";
                case "Byte": return "[tinyint] UNSIGNED";
                case "UInt16": return "[smallint] UNSIGNED";
                case "UInt32": return "[int] UNSIGNED";
                case "UInt64": return "[bigint] UNSIGNED";
                case "Single": return "[float]";
                case "Double": return "[float]";
                case "Decimal": return "[decimal]";
                case "DateTime": return "[datetime]";
                case "Guid": return "[uniqueidentifier]";
                case "Object": return "[variant]";
                case "String": return $"[nvarchar]({column.MaxLength})";
                default: return "[nvarchar](MAX)";
            }
        }
        #endregion
    }
}

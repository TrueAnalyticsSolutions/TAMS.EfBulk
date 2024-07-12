# if NETFRAMEWORK
using System.Data.Entity;
# else
using Microsoft.EntityFrameworkCore;
# endif
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TAMS.EfBulk
{
    public static class BulkExtensions
    {
        public static string GetDbSetTableName<T>(this DbContext context) where T : class
        {
            if (context.Set<T>() == null)
            {
                throw new ArgumentException("Type must exist as a DbSet in the DbContext.", nameof(T));
            }

            var tableAttribute = typeof(T).GetCustomAttribute<TableAttribute>(true);
            if (tableAttribute == null)
            {
                throw new CustomAttributeFormatException("DbSet must be decorated with the Table attribute.");
            }

            string tableName = $"[{tableAttribute.Name}]";
            if (!string.IsNullOrEmpty(tableAttribute.Schema))
            {
                tableName = $"[{tableAttribute.Schema}].{tableName}";
            }
            return tableName;
        }

        private static SqlConnection GetConnection(DbContext context)
        {
            SqlConnection connection;
#if NETFRAMEWORK
            connection = context.Database.Connection as SqlConnection;
#else
            connection = context.Database.GetDbConnection() as SqlConnection;
#endif
            return connection;
        }

        /// <summary>
        /// Creates a blank <see cref="DataTable"/> from the provided <see cref="DbSet"/> entity type.
        /// </summary>
        /// <typeparam name="T">Reference to the entity type for a <see cref="DbSet"/> in the <paramref name="context"/>.</typeparam>
        /// <param name="context">Reference to the <see cref="DbContext"/>.</param>
        /// <returns>Blank <see cref="DataTable"/> with the schema copied from the relavent <see cref="DbSet"/> of type <typeparamref name="T"/>.</returns>
        public static DataTable CreateDataTable<T>(this DbContext context) where T : class
        {
            var table = new DataTable();

            string tableName = context.GetDbSetTableName<T>();
            string sqlQuery = $"SELECT TOP 0 * FROM {tableName};";
            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
            {
                cnn.Open();
            }

            using (SqlDataAdapter adapter = new SqlDataAdapter(sqlQuery, cnn))
            {
                adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
                adapter.Fill(table);
            }

            ApplyAutoIncrementFromDatabase(table, tableName, cnn);
            table.TableName = tableName;

            return table;
        }

        private static void ApplyAutoIncrementFromDatabase(DataTable table, string tableName, SqlConnection cnn)
        {
            if (table.PrimaryKey.Any(o => o.AutoIncrement))
            {
                string getCurrentAutoIncrementScript = string.Format("SELECT IDENT_CURRENT ( '{0}' ) CurrentValue, IDENT_SEED ( '{0}' ) SeedValue, IDENT_INCR ( '{0}' ) IncrementValue;", tableName);
                using (SqlCommand cmd = new SqlCommand(getCurrentAutoIncrementScript, cnn))
                using (SqlDataReader rdr = cmd.ExecuteReader())
                {
                    if (!rdr.HasRows || !rdr.Read()) return;

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
        /// Creates a <see cref="DataTable"/> from the provided <see cref="DbSet"/> entity type and fills it with records from the database.
        /// </summary>
        /// <typeparam name="T">Reference to the entity type for a <see cref="DbSet"/> in the <paramref name="context"/>.</typeparam>
        /// <param name="context">Reference to the <see cref="DbContext"/>.</param>
        /// <returns>Blank <see cref="DataTable"/> with the schema copied from the relavent <see cref="DbSet"/> of type <typeparamref name="T"/>.</returns>
        public static DataTable CreateOfflineDataTable<T>(this DbContext context) where T : class
        {
            var table = new DataTable();

            string tableName = context.GetDbSetTableName<T>();
            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
            {
                cnn.Open();
            }
            using (var adapter = new SqlDataAdapter($"SELECT * FROM {tableName};", cnn))
            {
                adapter.MissingSchemaAction = MissingSchemaAction.AddWithKey;
                adapter.Fill(table);
            }
            ApplyAutoIncrementFromDatabase(table, tableName, cnn);
            table.TableName = tableName;
            return table;
        }

        public static void BulkInsert<T>(
          this DbContext context,
          DataTable table,
          int batchSize = 0,
          int timeout = 30,
          SqlRowsCopiedEventHandler sqlRowsCopiedEventHandler = null
        ) where T : class
        {
            if (string.IsNullOrEmpty(table.TableName))
            {
                throw new Exception("DataTable is missing TableName. Cannot insert DataTable without a reference to the TableName.");
            }
            string tableName = context.GetDbSetTableName<T>();
            if (table.TableName != tableName)
            {
                throw new ArgumentException("DataTable target entity type does not match the generic type argument.", nameof(table));
            }

            if (table.Rows.Count <= 0)
            {
                throw new DBConcurrencyException();
            }

            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
            {
                cnn.Open();
            }

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
                    {
                        bulk.NotifyAfter = batchSize;
                    }
                }

                bulk.WriteToServer(table);
            }
        }

        /// <summary>
        /// Performs a SQL <c>MERGE</c> using the provided <paramref name="table"/>.
        /// </summary>
        /// <typeparam name="T">Reference to the underlying entity type of the <paramref name="table"/>.</typeparam>
        /// <param name="context"></param>
        /// <param name="table"></param>
        /// <param name="allowInsert"></param>
        /// <param name="allowDelete"></param>
        /// <param name="batchSize"></param>
        /// <param name="timeout"></param>
        /// <param name="sqlRowsCopiedEventHandler"></param>
        public static void BulkMerge<T>(
          this DbContext context,
          DataTable table,
          bool allowInsert,
          bool allowDelete,
          int batchSize = 0,
          int timeout = 30,
          SqlRowsCopiedEventHandler sqlRowsCopiedEventHandler = null
        ) where T : class
        {
            if (string.IsNullOrEmpty(table.TableName))
            {
                throw new Exception("DataTable is missing TableName. Cannot insert DataTable without a reference to the TableName.");
            }
            string tableName = context.GetDbSetTableName<T>();
            if (table.TableName != tableName)
            {
                throw new ArgumentException("DataTable target entity type does not match the generic type argument.", nameof(table));
            }

            if (table.Rows.Count <= 0)
            {
                throw new DBConcurrencyException();
            }

            SqlConnection cnn = GetConnection(context);
            if (cnn.State == ConnectionState.Closed)
            {
                cnn.Open();
            }

            string temporaryTableName = $"[{typeof(T).Name}]";
            using (var cmd = new SqlCommand(BuildCreateTableScript(table, "tmp", temporaryTableName), cnn))
            using (var bulk = new SqlBulkCopy(cnn))
            {
                cmd.ExecuteNonQuery();

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
                    {
                        bulk.NotifyAfter = batchSize;
                    }
                }

                // Bulk insert the data into a temporary table in SQL Server
                bulk.WriteToServer(table);

                // Copy the data from the temporary table into the destination
                cmd.CommandTimeout = bulk.BulkCopyTimeout;
                cmd.CommandText = BuildMergeTableScript(table, $"[tmp].{temporaryTableName}", allowInsert, allowDelete);
                cmd.ExecuteNonQuery();

                // Delete the temporary table
                cmd.CommandText = $"DROP TABLE [tmp].{temporaryTableName};";
                cmd.ExecuteNonQuery();
            }
        }

        /// <see href="https://stackoverflow.com/a/29492560/4585104"/>
        #region Copied From StackExchange
        /// <summary>
        /// Creates a SQL script that creates a table where the columns matches that of the specified DataTable.
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

            // Build an ALTER TABLE script that adds keys to a table that already exists.
            if (Table.PrimaryKey.Length > 0)
                result.Append(BuildKeysScript(Table, fullTableName));

            return result.ToString();
        }

        /// <summary>
        /// Creates a SQL script that merges one table into another where the schema is the same for both tables.
        /// </summary>
        /// <param name="Table"><see cref="DataTable"/> containin the update information and the reference to the target table by <see cref="DataTable.TableName"/>.</param>
        /// <param name="tableSourceName">Name of the source table.</param>
        /// <returns></returns>
        private static string BuildMergeTableScript(DataTable Table, string tableSourceName, bool allowInserts = true, bool allowDeletes = false)
        {
            StringBuilder result = new StringBuilder();
            result.AppendFormat("MERGE {1} AS Target {0}USING {2} AS [Source] {0}", Environment.NewLine, Table.TableName, tableSourceName);
            result.Append("ON ");
            for (int i = 0; i < Table.PrimaryKey.Length; i++)
            {
                result.AppendFormat("([Source].[{0}] = [Target].[{0}])", Table.PrimaryKey[i].ColumnName);
                if (i != Table.PrimaryKey.Length - 1)
                {
                    result.Append(" AND ");
                }
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
            // Already checked by public method CreateTable. Un-comment if making the method public
            // if (Helper.IsValidDatatable(Table, IgnoreZeroRows: true)) return string.Empty;
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

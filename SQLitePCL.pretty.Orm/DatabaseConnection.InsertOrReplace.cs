﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace SQLitePCL.pretty.Orm
{
    public static partial class DatabaseConnection
    { 
        private static readonly ConditionalWeakTable<TableMapping, string> insertOrReplaceQueries = 
            new ConditionalWeakTable<TableMapping, string>();

        /// <summary>
        /// Prepares a SQLite statement that can be bound to an object of type T to insert into the database.
        /// </summary>
        /// <returns>A prepared statement.</returns>
        /// <param name="This">The database connection</param>
        /// <param name="tableMapping">The table mapping.</param>
        public static IStatement PrepareInsertOrReplaceStatement<T>(this IDatabaseConnection This)
        {
            Contract.Requires(This != null);

            var sql = insertOrReplaceQueries.GetValue(TableMapping.Create<T>(), mapping => 
                {
                    var column = mapping.PrimaryKeyColumn();
                    return SQLBuilder.InsertOrReplace(mapping.TableName, mapping.Columns.Select(x => x.Key));     
                });

            return This.PrepareStatement(sql);   
        }

        private static IEnumerable<KeyValuePair<TInserted, TResult>> YieldInsertOrReplaceAll<TInserted, TResult>(
            this IDatabaseConnection This, 
            IEnumerable<TInserted> objects,
            Func<IReadOnlyList<IResultSetValue>,TResult> resultSelector)
        {
            using (var insertOrReplaceStmt = This.PrepareInsertOrReplaceStatement<TInserted>())
            using (var findStmt = This.PrepareFindByRowId(TableMapping.Create<TInserted>().TableName))
            {
                foreach (var obj in objects)
                {
                    insertOrReplaceStmt.ExecuteWithProperties(obj);
                    var rowId = This.LastInsertedRowId;
                    yield return new KeyValuePair<TInserted, TResult>(obj, findStmt.Query(rowId).Select(resultSelector).First());
                } 
            }
        }

        /// <summary>
        /// Inserts an object into the database, replacing the existing entry if the primary key already exists.
        /// </summary>
        /// <returns>The object inserted into the database including it's primary key.</returns>
        /// <param name="This">The database connection.</param>
        /// <param name="tableMapping">The table mapping.</param>
        /// <param name="obj">The object to insert.</param>
        /// <typeparam name="T">The mapped type.</typeparam>     
        public static TResult InsertOrReplace<TInserted, TResult>(
            this IDatabaseConnection This, 
            TInserted obj,
            Func<IReadOnlyList<IResultSetValue>,TResult> resultSelector)
        {
            Contract.Requires(This != null);
            Contract.Requires(obj != null);
            Contract.Requires(resultSelector != null);

            return This.YieldInsertOrReplaceAll(new TInserted[] {obj}, resultSelector).First().Value;
        }

        /// <summary>
        /// Inserts the objects into the database, replacing existing entries if the given primary keys already exist.
        /// </summary>
        /// <returns>A dictionary mapping the provided objects to the objects that were inserted into the database.</returns>
        /// <param name="This">The database connection.</param>
        /// <param name="tableMapping">The table mapping.</param>
        /// <param name="objects">The objects to be inserted into the database.</param>
        /// <typeparam name="T">The mapped type.</typeparam> 
        public static IReadOnlyDictionary<TInserted,TResult> InsertOrReplaceAll<TInserted, TResult>(
            this IDatabaseConnection This, 
            IEnumerable<TInserted> objects,
            Func<IReadOnlyList<IResultSetValue>,TResult> resultSelector)
        {
            Contract.Requires(This != null);
            Contract.Requires(objects != null);
            Contract.Requires(resultSelector != null);

            return This.RunInTransaction(_ => 
                This.YieldInsertOrReplaceAll(objects, resultSelector)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value));
        }
     }

     public static partial class AsyncDatabaseConnection
     {
        /// <summary>
        /// Inserts the objects into the database, replacing existing entries if the given primary keys already exist.
        /// </summary>
        /// <returns>A Task that completes with a dictionary mapping the provided objects to the objects that were inserted into the database.</returns>
        /// <param name="This">The database connection.</param>
        /// <param name="tableMapping">The table mapping.</param>
        /// <param name="objects">The objects to be inserted into the database.</param>
        /// <param name="ct">A cancellation token that can be used to cancel the operation</param>
        /// <typeparam name="T">The mapped type.</typeparam> 
        public static Task<IReadOnlyDictionary<TInserted, TResult>> InsertOrReplaceAllAsync<TInserted, TResult>(
            this IAsyncDatabaseConnection This, 
            IEnumerable<TInserted> objects,
            Func<IReadOnlyList<IResultSetValue>,TResult> resultSelector,
            CancellationToken ct)
        {
            Contract.Requires(This != null);
            Contract.Requires(objects != null);
            Contract.Requires(resultSelector != null);

            return This.Use((db, _) => db.InsertOrReplaceAll(objects, resultSelector), ct);
        }

        /// <summary>
        /// Inserts the objects into the database, replacing existing entries if the given primary keys already exist.
        /// </summary>
        /// <returns>A Task that completes with a dictionary mapping the provided objects to the objects that were inserted into the database.</returns>
        /// <param name="This">The database connection.</param>
        /// <param name="tableMapping">The table mapping.</param>
        /// <param name="objects">The objects to be inserted into the database.</param>
        /// <typeparam name="T">The mapped type.</typeparam> 
        public static Task<IReadOnlyDictionary<TInserted, TResult>> InsertOrReplaceAllAsync<TInserted, TResult>(
            this IAsyncDatabaseConnection This, 
            IEnumerable<TInserted> objects,
            Func<IReadOnlyList<IResultSetValue>,TResult> resultSelector)
        {
            Contract.Requires(This != null);
            Contract.Requires(objects != null);
            Contract.Requires(resultSelector != null);

            return This.InsertOrReplaceAllAsync(objects, resultSelector, CancellationToken.None);
        }
    }
}


﻿//
// Copyright (c) 2009-2015 Krueger Systems, Inc.
// Copyright (c) 2015 David Bordoley
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace SQLitePCL.pretty.Orm
{
    public sealed class ColumnMapping
    {
        private readonly Type clrType;
        private readonly PropertyInfo property;
        private readonly TableColumnMetadata metadata;

        internal ColumnMapping(Type clrType, PropertyInfo property, TableColumnMetadata metadata)
        {
            this.clrType = clrType;
            this.property = property;
            this.metadata = metadata;
        }

        public Type ClrType { get { return clrType; } }

        public PropertyInfo Property { get { return property; } }

        public TableColumnMetadata Metadata { get { return metadata; } }
    }

    public sealed class IndexInfo
    {
        private readonly string name;
        private readonly bool unique;
        private readonly IReadOnlyList<string> columns;

        internal IndexInfo(string name, bool unique, IReadOnlyList<string> columns)
        {
            this.name = name;
            this.unique = unique;
            this.columns = columns;
        }

        public string Name { get { return name; } }

        public bool Unique { get { return unique; } }

        public IEnumerable<string> Columns { get { return columns; } }
    }

    public interface ITableMapping<T> : IEnumerable<KeyValuePair<string, ColumnMapping>>
    {
        String TableName { get; }

        CreateFlags CreateFlags { get; }

        ColumnMapping this[string column] { get; }

        IEnumerable<IndexInfo> Indexes { get; }

        bool TryGetColumnMapping(string column, out ColumnMapping mapping);

        T ToObject(IReadOnlyList<IResultSetValue> row);
    }

    public static class TableMapping
    {   
        internal static string PropertyToColumnName(PropertyInfo prop)
        {
            var colAttr = (ColumnAttribute)prop.GetCustomAttributes(typeof(ColumnAttribute), true).FirstOrDefault();
            return colAttr == null ? prop.Name : colAttr.Name;
        }

        // FIXME: Rename this as it actually will create/migrate the table and create indexes
        public static void CreateTable<T>(this IDatabaseConnection This, ITableMapping<T> tableMapping)
        {
            This.Execute(tableMapping.CreateTable());
            if (This.Changes == 0)
            {
                This.MigrateTable(tableMapping);
            }

            foreach (var index in tableMapping.Indexes) 
            {
                This.CreateIndex(index.Name, tableMapping.TableName,index.Columns, index.Unique);
            }
        }
            
        public static void MigrateTable<T>(this IDatabaseConnection This, ITableMapping<T> tableMapping)
        {
            var existingCols = This.GetTableInfo(tableMapping.TableName);
            
            var toBeAdded = new List<Tuple<string, TableColumnMetadata>> ();

            // FIXME: Nasty n^2 search due to case insensitive strings. Number of columns
            // is normally small so not that big of a deal.
            foreach (var p in tableMapping) 
            {
                var found = false;

                foreach (var c in existingCols) 
                {
                    found = (string.Compare (p.Key, c.Key, StringComparison.OrdinalIgnoreCase) == 0);
                    if (found) { break; }
                }

                if (!found) { toBeAdded.Add (Tuple.Create(p.Key, p.Value.Metadata)); }
            }
            
            foreach (var p in toBeAdded) 
            {
                This.Execute (SQLBuilder.AlterTableAddColumn(tableMapping.TableName, p.Item1, p.Item2));
            }
        }

        public static string CreateTable<T>(this ITableMapping<T> This)
        {
            return SQLBuilder.CreateTable(This.TableName, This.CreateFlags, This.Select(x => Tuple.Create(x.Key, x.Value.Metadata)));
        }

        // FIXME: I'm a little dubious of this method. If the table mapping is a representation of the table
        // then all indexes should be defined on the table itself and not out of band using functions like this.
        public static string CreateIndex<T>(this ITableMapping<T> This, Expression<Func<T, object>> property, bool unique)
        {
            MemberExpression mx;
            if (property.Body.NodeType == ExpressionType.Convert)
            {
                mx = ((UnaryExpression) property.Body).Operand as MemberExpression;
            }
            else
            {
                mx = (property.Body as MemberExpression);
            }

            var propertyInfo = mx.Member as PropertyInfo;
            if (propertyInfo == null)
            {
                throw new ArgumentException("The lambda expression 'property' should point to a valid Property");
            }

            var colName = PropertyToColumnName(propertyInfo);

            return SQLBuilder.CreateIndex(This.TableName, colName, unique);
        }

        public static string Insert<T>(this ITableMapping<T> tableMapping)
        {
            return SQLBuilder.Insert(tableMapping.TableName, tableMapping.Select(x => x.Key));
        }

        public static string InsertOrReplace<T>(this ITableMapping<T> tableMapping)
        {
            return SQLBuilder.InsertOrReplace(tableMapping.TableName, tableMapping.Select(x => x.Key));     
        }

        public static ITableMapping<T> Create<T>()
        {
            Func<object> builder = () => Activator.CreateInstance<T>();
            Func<object, T> build = obj => (T) obj;

            return TableMapping.Create(builder, build);
        }
            
        public static ITableMapping<T> Create<T>(Func<object> builder, Func<object, T> build)
        {
            var mappedType = typeof(T);

            var tableAttr = 
                (TableAttribute)CustomAttributeExtensions.GetCustomAttribute(mappedType.GetTypeInfo(), typeof(TableAttribute), true);

            var tableName = tableAttr != null ? tableAttr.Name : mappedType.Name;
            var createFlags = tableAttr != null ? tableAttr.CreateFlags : CreateFlags.None;

            var props = mappedType.GetRuntimeProperties().Where(p => p.GetMethod != null && p.GetMethod.IsPublic && !p.GetMethod.IsStatic);

            // FIXME: I wish this was immutable
            var columnToMapping = new Dictionary<string, ColumnMapping>();

            // map each column to it's index attributes
            var columnToIndexMapping = new Dictionary<string, IEnumerable<IndexedAttribute>>();
            foreach (var prop in props)
            {
                if (prop.GetCustomAttributes(typeof(IgnoreAttribute), true).Count() == 0)
                {
                    var columnType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                    var colAttr = (ColumnAttribute)prop.GetCustomAttributes(typeof(ColumnAttribute), true).FirstOrDefault();
                    var name = colAttr == null ? prop.Name : colAttr.Name;

                    var metadata = CreateColumnMetadata(prop, createFlags);

                    columnToMapping.Add(name, new ColumnMapping(columnType, prop, metadata));

                    // FIXME: Duplicate code. Make a function
                    var isPK = IsPrimaryKey(prop) ||
                               (((createFlags & CreateFlags.ImplicitPK) == CreateFlags.ImplicitPK) &&
                               string.Compare(prop.Name, ImplicitPkName, StringComparison.OrdinalIgnoreCase) == 0);

                    var columnIndexes = GetIndexes(prop);
                               
                    if (!columnIndexes.Any()
                        && !isPK
                        && ((createFlags & CreateFlags.ImplicitIndex) == CreateFlags.ImplicitIndex)
                        && name.EndsWith(ImplicitIndexSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        columnIndexes = new IndexedAttribute[] { new IndexedAttribute() };
                    }

                    columnToIndexMapping.Add(name, columnIndexes);
                }
            }

            // A Map of the index name to its columns and whether its unique or not
            var indexToColumns = new Dictionary<string, Tuple<bool, Dictionary<int, string>>>();

            foreach (var columnIndex in columnToIndexMapping)
            {
                foreach (var indexAttribute in columnIndex.Value)
                {
                    var indexName = indexAttribute.Name ?? SQLBuilder.NameIndex(tableName, columnIndex.Key);

                    Tuple<bool, Dictionary<int, string>> iinfo;
                    if (!indexToColumns.TryGetValue(indexName, out iinfo))
                    {
                        iinfo = Tuple.Create(indexAttribute.Unique, new Dictionary<int,string>());
                        indexToColumns.Add(indexName, iinfo);
                    }

                    if (indexAttribute.Unique != iinfo.Item1)
                    {
                        throw new Exception("All the columns in an index must have the same value for their Unique property");
                    }

                    if (iinfo.Item2.ContainsKey(indexAttribute.Order))
                    {
                        throw new Exception("Ordered columns must have unique values for their Order property.");
                    }

                    iinfo.Item2.Add(indexAttribute.Order, columnIndex.Key);
                }
            }
 
            var indexes = 
                indexToColumns.Select(x => 
                    new IndexInfo(
                        x.Key, 
                        x.Value.Item1, 
                        x.Value.Item2.OrderBy(col => col.Key).Select(col => col.Value).ToList()
                    )
                ).ToList();

            return new TableMapping<T>(builder, build, tableName, createFlags, columnToMapping, indexes);
        }
            
        private static TableColumnMetadata CreateColumnMetadata(PropertyInfo prop, CreateFlags createFlags = CreateFlags.None)
        {
            //If this type is Nullable<T> then Nullable.GetUnderlyingType returns the T, otherwise it returns null, so get the actual type instead
            var columnType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            var collation = Collation(prop);

            // FIXME: Duplicate code. Make a function
            var isPK = IsPrimaryKey(prop) ||
                (((createFlags & CreateFlags.ImplicitPK) == CreateFlags.ImplicitPK) &&
                    string.Compare (prop.Name, ImplicitPkName, StringComparison.OrdinalIgnoreCase) == 0);
            
            var isAuto = IsAutoIncrement(prop) || (isPK && ((createFlags & CreateFlags.AutoIncPK) == CreateFlags.AutoIncPK));
            var isAutoGuid = isAuto && columnType == typeof(Guid);
            var isAutoInc = isAuto && !isAutoGuid;
            var isNullable = !(isPK || IsMarkedNotNull(prop));
            var maxStringLength = MaxStringLength(prop);

            return new TableColumnMetadata(GetSqlType(columnType, maxStringLength), collation, isNullable, isPK, isAutoInc);
        }

        private const int DefaultMaxStringLength = 140;
        private const string ImplicitPkName = "Id";
        private const string ImplicitIndexSuffix = "Id";

        private static string GetSqlType(Type clrType, int? maxStringLen)
        {
            if (clrType == typeof(Boolean) || 
                clrType == typeof(Byte)    || 
                clrType == typeof(UInt16)  || 
                clrType == typeof(SByte)   || 
                clrType == typeof(Int16)   || 
                clrType == typeof(Int32)   ||
                clrType == typeof(UInt32)  || 
                clrType == typeof(Int64))  
            { 
                return "integer"; 
            } 
                
            else if (clrType == typeof(Single) || clrType == typeof(Double) || clrType == typeof(Decimal)) { return "float"; } 
            else if (clrType == typeof(String) && maxStringLen.HasValue)                                   { return "varchar(" + maxStringLen.Value + ")";  }
            else if (clrType == typeof(String))                                                            { return "varchar"; } 
            else if (clrType == typeof(TimeSpan))                                                          { return "bigint"; } 
            else if (clrType == typeof(DateTime))                                                          { return "bigint"; } 
            else if (clrType == typeof(DateTimeOffset))                                                    { return "bigint"; } 
            else if (clrType.GetTypeInfo().IsEnum)                                                         { return "integer"; } 
            else if (clrType == typeof(byte[]))                                                            { return "blob"; } 
            else if (clrType == typeof(Guid))                                                              { return "varchar(36)"; } 
            else 
            {
                throw new NotSupportedException ("Don't know about " + clrType);
            }
        }

        private static bool IsPrimaryKey (MemberInfo p)
        {
            var attrs = p.GetCustomAttributes (typeof(PrimaryKeyAttribute), true);
            return attrs.Count() > 0;
        }

        private static string Collation (MemberInfo p)
        {
            var attrs = p.GetCustomAttributes (typeof(CollationAttribute), true);
            if (attrs.Count() > 0) {
                return ((CollationAttribute)attrs.First()).Value;
            } else {
                return string.Empty;
            }
        }

        private static bool IsAutoIncrement (MemberInfo p)
        {
            var attrs = p.GetCustomAttributes (typeof(AutoIncrementAttribute), true);
            return attrs.Count() > 0;
        }

        private static IEnumerable<IndexedAttribute> GetIndexes(MemberInfo p)
        {
            var attrs = p.GetCustomAttributes(typeof(IndexedAttribute), true);
            return attrs.Cast<IndexedAttribute>();
        }

        private static int? MaxStringLength(PropertyInfo prop)
        {
            var attrs = prop.GetCustomAttributes(typeof(MaxLengthAttribute), true);
            if (attrs.Count() > 0)
            {
                return ((MaxLengthAttribute) attrs.First()).Value;
            }

            return null;
        }

        private static bool IsMarkedNotNull(MemberInfo p)
        {
            var attrs = p.GetCustomAttributes (typeof (NotNullAttribute), true);
            return attrs.Count() > 0;
        }
    }

    internal sealed class TableMapping<T> : ITableMapping<T>
    {
        private readonly Func<object> builder;
        private readonly Func<object,T> build;

        private readonly string tableName;

        private readonly CreateFlags createFlags;

        private readonly IReadOnlyDictionary<string, ColumnMapping> columnToMapping;
        private readonly IReadOnlyList<IndexInfo> indexes;

        internal TableMapping(
            Func<object> builder, 
            Func<object,T> build, 
            string tableName,
            CreateFlags createFlags,
            IReadOnlyDictionary<string, ColumnMapping> columnToMapping,
            IReadOnlyList<IndexInfo> indexes)
        {
            this.builder = builder;
            this.build = build;
            this.tableName = tableName;
            this.createFlags = createFlags;
            this.columnToMapping = columnToMapping;
            this.indexes = indexes;
        }

        public String TableName { get { return tableName; } }

        public CreateFlags CreateFlags { get { return createFlags; } }

        public IEnumerable<IndexInfo> Indexes { get { return indexes.AsEnumerable(); } }

        public ColumnMapping this [string column] { get  { return columnToMapping[column]; } }

        public bool TryGetColumnMapping(string column, out ColumnMapping mapping)
        {
            return this.columnToMapping.TryGetValue(column, out mapping);
        }

        public T ToObject(IReadOnlyList<IResultSetValue> row)
        {
            var builder = this.builder();

            foreach (var resultSetValue in row)
            {
                var columnName = resultSetValue.ColumnInfo.OriginName;
                ColumnMapping columnMapping; 

                if (columnToMapping.TryGetValue(columnName, out columnMapping))
                {
                    var value = resultSetValue.ToObject(columnMapping.ClrType);
                    var prop = columnMapping.Property;
                    prop.SetValue (builder, value, null);
                }
            }

            return build(builder);
        }

        public IEnumerator<KeyValuePair<string, ColumnMapping>> GetEnumerator()
        {
            return this.columnToMapping.GetEnumerator();
        }
            
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }
    }
}


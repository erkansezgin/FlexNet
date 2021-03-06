using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SenseNet.ContentRepository.Storage.ApplicationMessaging;
using SenseNet.ContentRepository.Storage.Schema;
using SenseNet.ContentRepository.Storage.Search;
using SenseNet.ContentRepository.Storage.Search.Internal;
using SenseNet.ContentRepository.Storage.Security;
using SenseNet.Diagnostics;

namespace SenseNet.ContentRepository.Storage.Data.SqlClient
{
    internal static class DataReaderExtension
    {
        internal static int GetSafeInt32(this IDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
                return 0;
            return reader.GetInt32(index);
        }
        internal static string GetSafeString(this IDataReader reader, int index)
        {
            if (reader.IsDBNull(index))
                return null;
            return reader.GetString(index);
        }
    }
    internal class SqlProvider : DataProvider
    {
        //////////////////////////////////////// Internal Constants ////////////////////////////////////////

        internal const int StringPageSize = 80;
        internal const int StringDataTypeSize = 450;
        internal const int IntPageSize = 40;
        internal const int DateTimePageSize = 25;
        internal const int CurrencyPageSize = 15;
        internal const int TextAlternationSizeLimit = 4000; // (Autoloaded)NVarchar -> (Lazy)NText
        internal const int CsvParamSize = 8000;
        internal const int BinaryStreamBufferLength = 32768;

        internal const string StringMappingPrefix = "nvarchar_";
        internal const string DateTimeMappingPrefix = "datetime_";
        internal const string IntMappingPrefix = "int_";
        internal const string CurrencyMappingPrefix = "money_";

        private int _contentListStartPage;
        private Dictionary<DataType, int> _contentListMappingOffsets;

        public SqlProvider()
        {
            _contentListStartPage = 10000000;
            _contentListMappingOffsets = new Dictionary<DataType, int>();
            _contentListMappingOffsets.Add(DataType.String, StringPageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.Int, IntPageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.DateTime, DateTimePageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.Currency, CurrencyPageSize * _contentListStartPage);
            _contentListMappingOffsets.Add(DataType.Binary, 0);
            _contentListMappingOffsets.Add(DataType.Reference, 0);
            _contentListMappingOffsets.Add(DataType.Text, 0);
        }


        public override int PathMaxLength
        {
            get { return StringDataTypeSize; }
        }
        public override DateTime DateTimeMinValue
        {
            get { return SqlDateTime.MinValue.Value; }
        }
        public override DateTime DateTimeMaxValue
        {
            get { return SqlDateTime.MaxValue.Value; }
        }
        public override decimal DecimalMinValue
        {
            get { return SqlMoney.MinValue.Value; }
        }
        public override decimal DecimalMaxValue
        {
            get { return SqlMoney.MaxValue.Value; }
        }

        protected internal override ITransactionProvider CreateTransaction()
        {
            return new Transaction();
        }

        protected internal override INodeWriter CreateNodeWriter()
        {
            return new SqlNodeWriter();
        }

        protected internal override SchemaWriter CreateSchemaWriter()
        {
            return new SqlSchemaWriter();
        }


        //////////////////////////////////////// Schema Members ////////////////////////////////////////

        protected internal override DataSet LoadSchema()
        {
            SqlConnection cn = new SqlConnection(RepositoryConfiguration.ConnectionString);
            SqlCommand cmd = new SqlCommand();
            cmd.Connection = cn;
            cmd.CommandType = CommandType.StoredProcedure;
            cmd.CommandText = "proc_Schema_LoadAll";
            SqlDataAdapter adapter = new SqlDataAdapter(cmd);
            DataSet dataSet = new DataSet();

            try
            {
                cn.Open();
                adapter.Fill(dataSet);
            }
            finally
            {
                cn.Close();
            }

            dataSet.Tables[0].TableName = "SchemaModification";
            dataSet.Tables[1].TableName = "DataTypes";
            dataSet.Tables[2].TableName = "PropertySetTypes";
            dataSet.Tables[3].TableName = "PropertySets";
            dataSet.Tables[4].TableName = "PropertyTypes";
            dataSet.Tables[5].TableName = "PropertySetsPropertyTypes";
            dataSet.Tables[6].TableName = "Permissions";

            return dataSet;
        }

        protected internal override void Reset()
        {
            //TODO: Read the configuration if is exist
        }

        public override Dictionary<DataType, int> ContentListMappingOffsets
        {
            get { return _contentListMappingOffsets; }
        }

        protected internal override int ContentListStartPage
        {
            get { return _contentListStartPage; }
        }

        protected override PropertyMapping GetPropertyMappingInternal(PropertyType propType)
        {
            //internal const string StringMappingPrefix = "nvarchar_";
            //internal const string DateTimeMappingPrefix = "datetime_";
            //internal const string IntMappingPrefix = "int_";
            //internal const string CurrencyMappingPrefix = "money_";

            PropertyStorageSchema storageSchema = PropertyStorageSchema.SingleColumn;
            string tableName;
            string columnName;
            bool usePageIndex = false;
            int page = 0;

            switch (propType.DataType)
            {
                case DataType.String:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.StringMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.Text:
                    usePageIndex = false;
                    tableName = "TextPropertiesNVarchar, TextPropertiesNText";
                    columnName = "Value";
                    storageSchema = PropertyStorageSchema.MultiTable;
                    break;
                case DataType.Int:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.IntMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.Currency:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.CurrencyMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.DateTime:
                    usePageIndex = true;
                    tableName = "FlatProperties";
                    columnName = SqlProvider.DateTimeMappingPrefix + GetColumnIndex(propType.DataType, propType.Mapping, out page);
                    break;
                case DataType.Binary:
                    usePageIndex = false;
                    tableName = "BinaryProperties";
                    columnName = "ContentType, FileNameWithoutExtension, Extension, Size, Stream";
                    storageSchema = PropertyStorageSchema.MultiColumn;
                    break;
                case DataType.Reference:
                    usePageIndex = false;
                    tableName = "ReferenceProperties";
                    columnName = "ReferredNodeId";
                    break;
                default:
                    throw new NotSupportedException("Unknown DataType" + propType.DataType);
            }
            return new PropertyMapping
            {
                StorageSchema = storageSchema,
                TableName = tableName,
                ColumnName = columnName,
                PageIndex = page,
                UsePageIndex = usePageIndex
            };
        }
        private static int GetColumnIndex(DataType dataType, int mapping, out int page)
        {
            //internal const int StringPageSize = 80;
            //internal const int StringDataTypeSize = 450;
            //internal const int IntPageSize = 40;
            //internal const int DateTimePageSize = 25;
            //internal const int CurrencyPageSize = 15;
            //internal const int TextAlternationSizeLimit = 4000; // (Autoloaded)NVarchar -> (Lazy)NText
            //internal const int CsvParamSize = 8000;
            //internal const int BinaryStreamBufferLength = 32768;
            int pageSize;
            switch (dataType)
            {
                case DataType.String: pageSize = SqlProvider.StringPageSize; break;
                case DataType.Int: pageSize = SqlProvider.IntPageSize; break;
                case DataType.DateTime: pageSize = SqlProvider.DateTimePageSize; break;
                case DataType.Currency: pageSize = SqlProvider.CurrencyPageSize; break;
                default:
                    page = 0;
                    return 0;
            }

            page = mapping / pageSize;
            int index = mapping % pageSize;
            return index + 1;
        }

        public override void AssertSchemaTimestampAndWriteModificationDate(long timestamp)
        {
            var script = @"DECLARE @Count INT
                            SELECT @Count = COUNT(*) FROM SchemaModification
                            IF @Count = 0
                                INSERT INTO SchemaModification (ModificationDate) VALUES (GETDATE())
                            ELSE
                            BEGIN
                                UPDATE [SchemaModification] SET [ModificationDate] = GETDATE() WHERE Timestamp = @Timestamp
                                IF @@ROWCOUNT = 0
                                    RAISERROR (N'Storage schema is out of date.', 12, 1);
                            END";

            using (var cmd = (SqlProcedure)DataProvider.CreateDataProcedure(script))
            {
                try
                {
                    cmd.CommandType = CommandType.Text;
                    cmd.Parameters.Add("@Timestamp", SqlDbType.Timestamp).Value = SqlProvider.GetBytesFromLong(timestamp);
                    cmd.ExecuteNonQuery();
                }
                catch (SqlException sex) //rethrow
                {
                    throw new DataException(sex.Message, sex);
                }
            }
        }

        protected internal override IEnumerable<int> QueryNodesByPath(string pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPath(null, pathStart, orderByPath);
        }
        protected internal override IEnumerable<int> QueryNodesByType(int[] nodeTypeIds)
        {
            return QueryNodesByTypeAndPath(nodeTypeIds, null, false);
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPath(int[] nodeTypeIds, string pathStart, bool orderByPath)
        {
            return QueryNodesByTypeAndPathAndName(nodeTypeIds, pathStart, orderByPath, null);
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPathAndName(int[] nodeTypeIds, string pathStart, bool orderByPath, string name)
        {
            var sql = new StringBuilder("SELECT NodeId FROM Nodes WHERE");
            var first = true;

            if (pathStart != null)
            {
                sql.Append(" Path LIKE '");
                sql.Append(pathStart);
                if (!pathStart.EndsWith(RepositoryPath.PathSeparator))
                    sql.Append(RepositoryPath.PathSeparator);
                sql.Append("%'");
                first = false;
            }

            if (name != null)
            {
                if (!first)
                    sql.Append(" AND");
                sql.Append(" Name = '").Append(name).Append("'");
                first = false;
            }

            if (nodeTypeIds != null)
            {
                if (!first)
                    sql.Append(" AND");
                sql.Append(" NodeTypeId");
                if (nodeTypeIds.Length == 1)
                    sql.Append(" = ").Append(nodeTypeIds[0]);
                else
                    sql.Append(" IN (").Append(String.Join(", ", nodeTypeIds)).Append(")");

                first = false;
            }

            if (orderByPath)
                sql.AppendLine().Append("ORDER BY Path");

            var cmd = new SqlProcedure { CommandText = sql.ToString(), CommandType = CommandType.Text };
            SqlDataReader reader = null;
            var result = new List<int>();
            try
            {
                reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetSafeInt32(0));
                return result;
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();
                cmd.Dispose();
            }
        }
        protected internal override IEnumerable<int> QueryNodesByTypeAndPathAndProperty(int[] nodeTypeIds, string pathStart, bool orderByPath, List<QueryPropertyData> properties)
        {
            var sql = new StringBuilder("SELECT NodeId FROM SysSearchWithFlatsView WHERE");
            var first = true;

            if (pathStart != null)
            {
                sql.Append(" Path LIKE '");
                sql.Append(pathStart);
                if (!pathStart.EndsWith(RepositoryPath.PathSeparator))
                    sql.Append(RepositoryPath.PathSeparator);
                sql.Append("%'");
                first = false;
            }

            if (nodeTypeIds != null)
            {
                if (!first)
                    sql.Append(" AND");
                sql.Append(" NodeTypeId");
                if (nodeTypeIds.Length == 1)
                    sql.Append(" = ").Append(nodeTypeIds[0]);
                else
                    sql.Append(" IN (").Append(String.Join(", ", nodeTypeIds)).Append(")");

                first = false;
            }

            if (properties != null)
            {
                foreach (var queryPropVal in properties)
                {
                    if (string.IsNullOrEmpty(queryPropVal.PropertyName))
                        continue;
                    
                    var pt = PropertyType.GetByName(queryPropVal.PropertyName);
                    var pm = pt == null ? null : pt.GetDatabaseInfo();
                    var colName = pm == null ? GetNodeAttributeName(queryPropVal.PropertyName) : pm.ColumnName;
                    var dt = pt == null ? GetNodeAttributeType(queryPropVal.PropertyName) : pt.DataType;

                    if (!first)
                        sql.Append(" AND");

                    if (queryPropVal.Value != null)
                    {
                        switch (dt)
                        {
                            case DataType.DateTime:
                            case DataType.String:
                                switch (queryPropVal.QueryOperator)
                                {
                                    case Operator.Equal:
                                        sql.AppendFormat(" {0} = '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.Contains:
                                        sql.AppendFormat(" {0} LIKE '%{1}%'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.StartsWith:
                                        sql.AppendFormat(" {0} LIKE '{1}%'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.EndsWith:
                                        sql.AppendFormat(" {0} LIKE '%{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.GreaterThan:
                                        sql.AppendFormat(" {0} > '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.GreaterThanOrEqual:
                                        sql.AppendFormat(" {0} >= '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.LessThan:
                                        sql.AppendFormat(" {0} < '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.LessThanOrEqual:
                                        sql.AppendFormat(" {0} <= '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.NotEqual:
                                        sql.AppendFormat(" {0} <> '{1}'", colName, queryPropVal.Value);
                                        break;
                                    default:
                                        throw new InvalidOperationException(string.Format("Direct query not implemented (data type: {0}, operator: {1})", dt, queryPropVal.QueryOperator));
                                }
                                break;
                            case DataType.Int:
                            case DataType.Currency:
                                switch (queryPropVal.QueryOperator)
                                {
                                    case Operator.Equal:
                                        sql.AppendFormat(" {0} = {1}", colName, queryPropVal.Value);
                                        break;
                                    case Operator.GreaterThan:
                                        sql.AppendFormat(" {0} > '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.GreaterThanOrEqual:
                                        sql.AppendFormat(" {0} >= '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.LessThan:
                                        sql.AppendFormat(" {0} < '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.LessThanOrEqual:
                                        sql.AppendFormat(" {0} <= '{1}'", colName, queryPropVal.Value);
                                        break;
                                    case Operator.NotEqual:
                                        sql.AppendFormat(" {0} <> '{1}'", colName, queryPropVal.Value);
                                        break;
                                    default:
                                        throw new InvalidOperationException(string.Format("Direct query not implemented (data type: {0}, operator: {1})", dt, queryPropVal.QueryOperator));
                                }
                                break;
                            default:
                                throw new NotSupportedException("Not supported direct query dataType: " + dt);
                        }
                    }
                    else
                    {
                        sql.Append(" IS NULL");
                    }
                }
            }

            if (orderByPath)
                sql.AppendLine().Append("ORDER BY Path");

            var cmd = new SqlProcedure { CommandText = sql.ToString(), CommandType = CommandType.Text };
            SqlDataReader reader = null;
            var result = new List<int>();
            try
            {
                reader = cmd.ExecuteReader();
                while (reader.Read())
                    result.Add(reader.GetSafeInt32(0));
                return result;
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();
                cmd.Dispose();
            }
        }
        protected internal override IEnumerable<int> QueryNodesByReferenceAndType(string referenceName, int referredNodeId, int[] allowedTypeIds)
        {
            if (referenceName == null)
                throw new ArgumentNullException("referenceName");
            if (referenceName.Length == 0)
                throw new ArgumentException("Argument referenceName cannot be empty.", "referenceName");
            var referenceProperty = ActiveSchema.PropertyTypes[referenceName];
            if (referenceProperty == null)
                throw new ArgumentException("PropertyType is not found: " + referenceName, "referenceName");
            var referencePropertyId = referenceProperty.Id;

            string sql;
            if (allowedTypeIds == null || allowedTypeIds.Length == 0)
            {
                sql = @"SELECT V.NodeId FROM ReferenceProperties R
	JOIN Versions V ON R.VersionId = V.VersionId
	JOIN Nodes N ON V.VersionId = N.LastMinorVersionId
WHERE R.PropertyTypeId = @PropertyTypeId AND R.ReferredNodeId = @ReferredNodeId";
            }
            else
            {
                sql = String.Format(@"SELECT N.NodeId FROM ReferenceProperties R
	JOIN Versions V ON R.VersionId = V.VersionId
	JOIN Nodes N ON V.VersionId = N.LastMinorVersionId
WHERE R.PropertyTypeId = @PropertyTypeId AND R.ReferredNodeId = @ReferredNodeId AND N.NodeTypeId IN ({0})", String.Join(", ", allowedTypeIds));
            }

            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add("@PropertyTypeId", SqlDbType.Int).Value = referencePropertyId;
                cmd.Parameters.Add("@ReferredNodeId", SqlDbType.Int).Value = referredNodeId;
                var result = new List<int>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        result.Add(reader.GetSafeInt32(0));
                    return result;
                }
            }
        }

        private static string GetNodeAttributeName(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw new ArgumentNullException("propertyName");

            switch (propertyName)
            {
                case "Id":
                    return "NodeId";
                case "ParentId":
                case "Parent":
                    return "ParentNodeId";
                case "Locked":
                    return "Locked";
                case "LockedById":
                case "LockedBy":
                    return "LockedById";
                case "MajorVersion":
                    return "MajorNumber";
                case "MinorVersion":
                    return "MinorNumber";
                case "CreatedById":
                case "CreatedBy":
                    return "CreatedById";
                case "ModifiedById":
                case "ModifiedBy":
                    return "ModifiedById";
                default:
                    return propertyName;
            }
        }
        private static DataType GetNodeAttributeType(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName))
                throw new ArgumentNullException("propertyName");

            switch (propertyName)
            {
                case "Id":
                case "IsDeleted":
                case "IsInherited":
                case "ParentId":
                case "Parent":
                case "Index":
                case "Locked":
                case "LockedById":
                case "LockedBy":
                case "LockType":
                case "LockTimeout":
                case "MajorVersion":
                case "MinorVersion":
                case "CreatedById":
                case "CreatedBy":
                case "ModifiedById":
                case "ModifiedBy":
                    return DataType.Int;
                case "Name":
                case "Path":
                case "ETag":
                case "LockToken":
                    return DataType.String;
                case "LockDate":
                case "LastLockUpdate":
                case "CreationDate":
                case "ModificationDate":
                    return DataType.DateTime;
                default:
                    return DataType.String;
            }
        }

        protected internal override int InstanceCount(int[] nodeTypeIds)
        {
            var sql = new StringBuilder("SELECT COUNT(*) FROM Nodes WHERE NodeTypeId");
            if (nodeTypeIds.Length == 1)
                sql.Append(" = ").Append(nodeTypeIds[0]);
            else
                sql.Append(" IN (").Append(String.Join(", ", nodeTypeIds)).Append(")");

            var cmd = new SqlProcedure { CommandText = sql.ToString(), CommandType = CommandType.Text }; ;
            try
            {
                var count = (int)cmd.ExecuteScalar();
                return count;
            }
            finally
            {
                cmd.Dispose();
            }

        }

        //////////////////////////////////////// Node Query ////////////////////////////////////////

        protected internal override VersionNumber[] GetVersionNumbers(int nodeId)
        {
            List<VersionNumber> versions = new List<VersionNumber>();
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_VersionNumbers_GetByNodeId" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                reader = cmd.ExecuteReader();

                int majorNumberIndex = reader.GetOrdinal("MajorNumber");
                int minorNumberIndex = reader.GetOrdinal("MinorNumber");
                int statusIndex = reader.GetOrdinal("Status");

                while (reader.Read())
                {
                    versions.Add(new VersionNumber(
                        reader.GetInt16(majorNumberIndex),
                        reader.GetInt16(minorNumberIndex),
                        (VersionStatus)reader.GetInt16(statusIndex)));
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();

                cmd.Dispose();
            }
            return versions.ToArray();
        }

        protected internal override VersionNumber[] GetVersionNumbers(string path)
        {
            List<VersionNumber> versions = new List<VersionNumber>();
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_VersionNumbers_GetByPath" };
                cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
                reader = cmd.ExecuteReader();

                int majorNumberIndex = reader.GetOrdinal("MajorNumber");
                int minorNumberIndex = reader.GetOrdinal("MinorNumber");
                int statusIndex = reader.GetOrdinal("Status");

                while (reader.Read())
                {
                    versions.Add(new VersionNumber(
                        reader.GetInt32(majorNumberIndex),
                        reader.GetInt32(minorNumberIndex),
                        (VersionStatus)reader.GetInt32(statusIndex)));
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();

                cmd.Dispose();
            }
            return versions.ToArray();
        }

        //protected internal override INodeQueryCompiler CreateNodeQueryCompiler()
        //{
        //    return new SqlCompiler();
        //}
        //protected internal override List<NodeToken> ExecuteQuery(NodeQuery query)
        //{
        //    List<NodeToken> result = new List<NodeToken>();
        //    SqlCompiler compiler = new SqlCompiler();

        //    NodeQueryParameter[] parameters;
        //    string compiledCommandText = compiler.Compile(query, out parameters);

        //    SqlProcedure command = null;
        //    SqlDataReader reader = null;
        //    try
        //    {
        //        command = new SqlProcedure { CommandText = compiledCommandText };
        //        command.CommandType = CommandType.Text;
        //        foreach (var parameter in parameters)
        //            command.Parameters.Add(new SqlParameter(parameter.Name, parameter.Value));

        //        reader = command.ExecuteReader();

        //        ReadNodeTokens(reader, result);
        //    }
        //    finally
        //    {
        //        if (reader != null && !reader.IsClosed)
        //            reader.Close();

        //        command.Dispose();
        //    }

        //    return result;
        //}

        protected internal override void LoadNodes(Dictionary<int, NodeBuilder> buildersByVersionId)
        {
            List<string> versionInfo = new List<string>();
            versionInfo.Add(String.Concat("VersionsId[count: ", buildersByVersionId.Count, "]"));

            if (buildersByVersionId.Keys.Count > 20)
            {
                versionInfo.AddRange(buildersByVersionId.Keys.Take(20).Select(x => x.ToString()));
                versionInfo.Add("...");
            }
            else
                versionInfo.AddRange(buildersByVersionId.Keys.Select(x => x.ToString()).ToArray());
            var operationTitle = String.Join(", ", versionInfo.ToArray());

            using (var traceOperation = Logger.TraceOperation("SqlProvider.LoadNodes" + operationTitle))
            {
                var builders = buildersByVersionId; // Shortcut
                SqlProcedure cmd = null;
                SqlDataReader reader = null;
                try
                {
                    cmd = new SqlProcedure { CommandText = "proc_Node_LoadData_Batch" };
                    string xmlIds = CreateIdXmlForNodeInfoBatchLoad(builders);
                    cmd.Parameters.Add("@IdsInXml", SqlDbType.Xml).Value = xmlIds;
                    reader = cmd.ExecuteReader();

                    //-- #1: FlatProperties
                    //SELECT * FROM FlatProperties
                    //    WHERE VersionId IN (select id from @versionids)
                    var versionIdIndex = reader.GetOrdinal("VersionId");
                    var pageIndex = reader.GetOrdinal("Page");

                    while (reader.Read())
                    {
                        int versionId = reader.GetInt32(versionIdIndex);
                        int page = reader.GetInt32(pageIndex);
                        NodeBuilder builder = builders[versionId];
                        foreach (PropertyType pt in builder.Token.AllPropertyTypes)
                        {
                            string mapping = PropertyMap.GetValidMapping(page, pt);
                            if (mapping.Length != 0)
                            {
                                // Mapped property appears in the given page
                                object val = reader[mapping];
                                builder.AddDynamicProperty(pt, (val == DBNull.Value) ? null : val);
                            }
                        }
                    }

                    reader.NextResult();


                    //-- #2: BinaryProperties
                    //SELECT BinaryPropertyId, VersionId, PropertyTypeId, ContentType, FileNameWithoutExtension,
                    //    Extension, [Size], [Checksum], NULL AS Stream, 0 AS Loaded
                    //FROM dbo.BinaryProperties
                    //WHERE PropertyTypeId IN (select id from @binids) AND VersionId IN (select id from @versionids)
                    var binaryPropertyIdIndex = reader.GetOrdinal("BinaryPropertyId");
                    versionIdIndex = reader.GetOrdinal("VersionId");
                    var checksumPropertyIndex = reader.GetOrdinal("Checksum");
                    var propertyTypeIdIndex = reader.GetOrdinal("PropertyTypeId");
                    var contentTypeIndex = reader.GetOrdinal("ContentType");
                    var fileNameWithoutExtensionIndex = reader.GetOrdinal("FileNameWithoutExtension");
                    var extensionIndex = reader.GetOrdinal("Extension");
                    var sizeIndex = reader.GetOrdinal("Size");

                    while (reader.Read())
                    {
                        string ext = reader.GetString(extensionIndex);
                        if (ext.Length != 0)
                            ext = ext.Remove(0, 1); // Remove dot from the start if extension is not empty

                        string fn = reader.GetSafeString(fileNameWithoutExtensionIndex); // reader.IsDBNull(fileNameWithoutExtensionIndex) ? null : reader.GetString(fileNameWithoutExtensionIndex);

                        var x = new BinaryDataValue
                        {
                            Id = reader.GetInt32(binaryPropertyIdIndex),
                            Checksum = reader.GetSafeString(checksumPropertyIndex), //reader.IsDBNull(checksumPropertyIndex) ? null : reader.GetString(checksumPropertyIndex),
                            FileName = new BinaryFileName(fn, ext),
                            ContentType = reader.GetString(contentTypeIndex),
                            Size = reader.GetInt64(sizeIndex)
                        };

                        var versionId = reader.GetInt32(versionIdIndex);
                        var propertyTypeId = reader.GetInt32(propertyTypeIdIndex);
                        builders[versionId].AddDynamicProperty(propertyTypeId, x);
                    }

                    reader.NextResult();


                    //-- #3: ReferencePropertyInfo + Referred NodeToken
                    //SELECT VersionId, PropertyTypeId, ReferredNodeId
                    //FROM dbo.ReferenceProperties ref
                    //WHERE ref.VersionId IN (select id from @versionids)
                    versionIdIndex = reader.GetOrdinal("VersionId");
                    propertyTypeIdIndex = reader.GetOrdinal("PropertyTypeId");
                    var nodeIdIndex = reader.GetOrdinal("ReferredNodeId");

                    //-- Collect references to Dictionary<versionId, Dictionary<propertyTypeId, List<referredNodeId>>>
                    var referenceCollector = new Dictionary<int, Dictionary<int, List<int>>>();
                    while (reader.Read())
                    {
                        var versionId = reader.GetInt32(versionIdIndex);
                        var propertyTypeId = reader.GetInt32(propertyTypeIdIndex);
                        var referredNodeId = reader.GetInt32(nodeIdIndex);

                        if (!referenceCollector.ContainsKey(versionId))
                            referenceCollector.Add(versionId, new Dictionary<int, List<int>>());
                        var referenceCollectorPerVersion = referenceCollector[versionId];
                        if (!referenceCollectorPerVersion.ContainsKey(propertyTypeId))
                            referenceCollectorPerVersion.Add(propertyTypeId, new List<int>());
                        referenceCollectorPerVersion[propertyTypeId].Add(referredNodeId);
                    }
                    //-- Set references to NodeData
                    foreach (var versionId in referenceCollector.Keys)
                    {
                        var referenceCollectorPerVersion = referenceCollector[versionId];
                        foreach (var propertyTypeId in referenceCollectorPerVersion.Keys)
                            builders[versionId].AddDynamicProperty(propertyTypeId, referenceCollectorPerVersion[propertyTypeId]);
                    }

                    reader.NextResult();


                    //-- #4: TextPropertyInfo (NText:Lazy, NVarchar(4000):loaded)
                    //SELECT VersionId, PropertyTypeId, NULL AS Value, 0 AS Loaded
                    //FROM dbo.TextPropertiesNText
                    //WHERE VersionId IN (select id from @versionids)
                    //UNION ALL
                    //SELECT VersionId, PropertyTypeId, Value, 1 AS Loaded
                    //FROM dbo.TextPropertiesNVarchar
                    //WHERE VersionId IN (select id from @versionids)
                    versionIdIndex = reader.GetOrdinal("VersionID");
                    propertyTypeIdIndex = reader.GetOrdinal("PropertyTypeId");
                    var valueIndex = reader.GetOrdinal("Value");
                    var loadedIndex = reader.GetOrdinal("Loaded");

                    while (reader.Read())
                    {
                        int versionId = reader.GetInt32(versionIdIndex);
                        int propertyTypeId = reader.GetInt32(propertyTypeIdIndex);
                        string value = reader.GetSafeString(valueIndex); // (reader[valueIndex] == DBNull.Value) ? null : reader.GetString(valueIndex);
                        bool loaded = Convert.ToBoolean(reader.GetInt32(loadedIndex));

                        if (loaded)
                            builders[versionId].AddDynamicProperty(propertyTypeId, value);
                    }

                    reader.NextResult();


                    //-- #5: BaseData
                    //SELECT N.NodeId, N.NodeTypeId, N.ContentListTypeId, N.ContentListId, N.IsDeleted, N.IsInherited, 
                    //    N.ParentNodeId, N.[Name], N.[Path], N.[Index], N.Locked, N.LockedById, 
                    //    N.ETag, N.LockType, N.LockTimeout, N.LockDate, N.LockToken, N.LastLockUpdate,
                    //    N.CreationDate AS NodeCreationDate, N.CreatedById AS NodeCreatedById, 
                    //    N.ModificationDate AS NodeModificationDate, N.ModifiedById AS NodeModifiedById,
                    //    V.VersionId, V.MajorNumber, V.MinorNumber, V.CreationDate, V.CreatedById, 
                    //    V.ModificationDate, V.ModifiedById, V.[Status]
                    //FROM dbo.Nodes AS N 
                    //    INNER JOIN dbo.Versions AS V ON N.NodeId = V.NodeId ON N.NodeId = V.NodeId
                    //WHERE V.VersionId IN (select id from @versionids)
                    nodeIdIndex = reader.GetOrdinal("NodeId");
                    var nodeTypeIdIndex = reader.GetOrdinal("NodeTypeId");
                    var contentListTypeIdIndex = reader.GetOrdinal("ContentListTypeId");
                    var contentListIdIndex = reader.GetOrdinal("ContentListId");
                    var isDeletedIndex = reader.GetOrdinal("IsDeleted");
                    var isInheritedIndex = reader.GetOrdinal("IsInherited");
                    var parentNodeIdIndex = reader.GetOrdinal("ParentNodeId");
                    var nameIndex = reader.GetOrdinal("Name");
                    var displayNameIndex = reader.GetOrdinal("DisplayName");
                    var pathIndex = reader.GetOrdinal("Path");
                    var indexIndex = reader.GetOrdinal("Index");
                    var lockedIndex = reader.GetOrdinal("Locked");
                    var lockedByIdIndex = reader.GetOrdinal("LockedById");
                    var eTagIndex = reader.GetOrdinal("ETag");
                    var lockTypeIndex = reader.GetOrdinal("LockType");
                    var lockTimeoutIndex = reader.GetOrdinal("LockTimeout");
                    var lockDateIndex = reader.GetOrdinal("LockDate");
                    var lockTokenIndex = reader.GetOrdinal("LockToken");
                    var lastLockUpdateIndex = reader.GetOrdinal("LastLockUpdate");
                    var nodeCreationDateIndex = reader.GetOrdinal("NodeCreationDate");
                    var nodeCreatedByIdIndex = reader.GetOrdinal("NodeCreatedById");
                    var nodeModificationDateIndex = reader.GetOrdinal("NodeModificationDate");
                    var nodeModifiedByIdIndex = reader.GetOrdinal("NodeModifiedById");
                    var nodeTimestampIndex = reader.GetOrdinal("NodeTimestamp");

                    versionIdIndex = reader.GetOrdinal("VersionId");
                    var majorNumberIndex = reader.GetOrdinal("MajorNumber");
                    var minorNumberIndex = reader.GetOrdinal("MinorNumber");
                    var creationDateIndex = reader.GetOrdinal("CreationDate");
                    var createdByIdIndex = reader.GetOrdinal("CreatedById");
                    var modificationDateIndex = reader.GetOrdinal("ModificationDate");
                    var modifiedByIdIndex = reader.GetOrdinal("ModifiedById");
                    var status = reader.GetOrdinal("Status");
                    var versionTimestampIndex = reader.GetOrdinal("VersionTimestamp");

                    while (reader.Read())
                    {
                        int versionId = reader.GetInt32(versionIdIndex);

                        VersionNumber versionNumber = new VersionNumber(
                            reader.GetInt16(majorNumberIndex),
                            reader.GetInt16(minorNumberIndex),
                            (VersionStatus)reader.GetInt16(status));

                        builders[versionId].SetCoreAttributes(
                            reader.GetInt32(nodeIdIndex),
                            reader.GetInt32(nodeTypeIdIndex),
                            TypeConverter.ToInt32(reader.GetValue(contentListIdIndex)),
                            TypeConverter.ToInt32(reader.GetValue(contentListTypeIdIndex)),
                            Convert.ToBoolean(reader.GetByte(isDeletedIndex)),
                            Convert.ToBoolean(reader.GetByte(isInheritedIndex)),
                            reader.GetSafeInt32(parentNodeIdIndex), // reader.GetValue(parentNodeIdIndex) == DBNull.Value ? 0 : reader.GetInt32(parentNodeIdIndex), //parent,
                            reader.GetString(nameIndex),
                            reader.GetSafeString(displayNameIndex),
                            reader.GetString(pathIndex),
                            reader.GetInt32(indexIndex),
                            Convert.ToBoolean(reader.GetByte(lockedIndex)),
                            reader.GetSafeInt32(lockedByIdIndex), // reader.GetValue(lockedByIdIndex) == DBNull.Value ? 0 : reader.GetInt32(lockedByIdIndex),
                            reader.GetString(eTagIndex),
                            reader.GetInt32(lockTypeIndex),
                            reader.GetInt32(lockTimeoutIndex),
                            reader.GetDateTime(lockDateIndex),
                            reader.GetString(lockTokenIndex),
                            reader.GetDateTime(lastLockUpdateIndex),
                            versionId,
                            versionNumber,
                            reader.GetDateTime(creationDateIndex),
                            reader.GetInt32(createdByIdIndex),
                            reader.GetDateTime(modificationDateIndex),
                            reader.GetInt32(modifiedByIdIndex),
                            reader.GetDateTime(nodeCreationDateIndex),
                            reader.GetInt32(nodeCreatedByIdIndex),
                            reader.GetDateTime(nodeModificationDateIndex),
                            reader.GetInt32(nodeModifiedByIdIndex),
                            GetLongFromBytes((byte[])reader.GetValue(nodeTimestampIndex)),
                            GetLongFromBytes((byte[])reader.GetValue(versionTimestampIndex))
                            );
                    }
                    foreach (var builder in builders.Values)
                        builder.Finish();
                }
                finally
                {
                    if (reader != null && !reader.IsClosed)
                        reader.Close();

                    cmd.Dispose();
                }
                traceOperation.IsSuccessful = true;
            }
        }

        protected internal override bool IsCacheableText(string text)
        {
            if (text == null)
                return false;
            return text.Length < TextAlternationSizeLimit;
        }

        protected internal override string LoadTextPropertyValue(int versionId, int propertyTypeId)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_TextProperty_LoadValue" };
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                cmd.Parameters.Add("@PropertyTypeId", SqlDbType.Int).Value = propertyTypeId;
                var s = (string)cmd.ExecuteScalar();
                return s;
            }
            finally
            {
                cmd.Dispose();
            }
        }

        protected internal override Stream LoadBinaryPropertyValue(int versionId, int propertyTypeId)
        {
            // Retrieve binary pointer for chunk reading
            int length = 0;
            int pointer = 0;

            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_BinaryProperty_GetPointer" };
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                cmd.Parameters.Add("@PropertyTypeId", SqlDbType.Int).Value = propertyTypeId;
                SqlParameter pointerOutParam = cmd.Parameters.Add("@Id", SqlDbType.Int);
                pointerOutParam.Direction = ParameterDirection.Output;
                SqlParameter lengthOutParam = cmd.Parameters.Add("@Length", SqlDbType.Int);
                lengthOutParam.Direction = ParameterDirection.Output;

                cmd.ExecuteNonQuery();

                if (lengthOutParam.Value != DBNull.Value)
                    length = (int)lengthOutParam.Value;
                if (pointerOutParam.Value != DBNull.Value)
                    pointer = Convert.ToInt32(pointerOutParam.Value);
            }
            finally
            {
                cmd.Dispose();
            }

            if (pointer == 0)
                return null;


            // Read the stream by segments
            cmd = null;
            SqlDataReader reader = null;
            Stream stream = null;

            try
            {
                cmd = new SqlProcedure { CommandText = "proc_BinaryProperty_ReadStream" };
                cmd.Parameters.Add("@Id", SqlDbType.Int).Value = pointer;
                SqlParameter offsetParam = cmd.Parameters.Add("@Offset", SqlDbType.Int);
                SqlParameter sizeParam = cmd.Parameters.Add("@Size", SqlDbType.Int);
                int offset = 0;
                offsetParam.Value = offset;
                int size = BinaryStreamBufferLength;
                sizeParam.Value = size;

                byte[] buffer = new byte[BinaryStreamBufferLength];
                stream = new MemoryStream(length);

                if (length > 0)
                {
                    do
                    {
                        // Calculate buffer size - may be less than BinaryStreamBufferLength for last block.
                        if ((offset + BinaryStreamBufferLength) >= length)
                        {
                            size = length - offset;
                            sizeParam.Value = size + 1;
                        }

                        reader = cmd.ExecuteReader(CommandBehavior.SingleResult);
                        reader.Read();
                        reader.GetBytes(0, 0, buffer, 0, size);
                        reader.Close();

                        stream.Write(buffer, 0, size);

                        // Set the new offset
                        offset += size;
                        offsetParam.Value = offset;
                    }
                    while (offset < length);

                    stream.Seek(0, SeekOrigin.Begin);
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();

                cmd.Dispose();
            }

            return stream;
        }

        protected internal override IEnumerable<int> GetChildrenIdentfiers(int nodeId)
        {
            var cmd = new SqlProcedure { CommandText = "SELECT NodeId FROM Nodes WHERE ParentNodeId = @ParentNodeId" };
            cmd.CommandType = CommandType.Text;
            cmd.Parameters.Add("@ParentNodeId", SqlDbType.Int).Value = nodeId;
            SqlDataReader reader = null;
            try
            {
                reader = cmd.ExecuteReader();
                var ids = new List<int>();
                while (reader.Read())
                    ids.Add(reader.GetSafeInt32(0));
                return ids;
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }
        }

        //////////////////////////////////////// Operations ////////////////////////////////////////

        protected internal override IEnumerable<NodeType> LoadChildTypesToAllow(int sourceNodeId)
        {
            var result = new List<NodeType>();
            using (var cmd = new SqlProcedure { CommandText = "proc_LoadChildTypesToAllow" })
            {
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = sourceNodeId;
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var name = (string)reader[0];
                    var nt = ActiveSchema.NodeTypes[name];
                    if(nt != null)
                        result.Add(nt);
                }
            }
            return result;
        }
        protected internal override DataOperationResult MoveNodeTree(int sourceNodeId, int targetNodeId)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_Move" };
                cmd.Parameters.Add("@SourceNodeId", SqlDbType.Int).Value = sourceNodeId;
                cmd.Parameters.Add("@TargetNodeId", SqlDbType.Int).Value = targetNodeId;
                cmd.ExecuteNonQuery();
            }
            catch (SqlException e) //logged //rethrow
            {
                switch (e.State)
                {
                    case 1: //'Invalid operation: moving a contentList / a subtree that contains a contentList under an another contentList.'
                        Logger.WriteException(e);
                        return DataOperationResult.Move_TargetContainsSameName;
                    case 2:
                    default:
                        throw;
                }
            }
            finally
            {
                cmd.Dispose();
            }
            return 0;
        }

        protected internal override DataOperationResult DeleteNodeTree(int nodeId)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_Delete" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Dispose();
            }
            return DataOperationResult.Successful;
        }

        protected internal override DataOperationResult DeleteNodeTreePsychical(int nodeId)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_DeletePhysical" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Dispose();
            }
            return DataOperationResult.Successful;
        }

        protected internal override void DeleteVersion(int versionId, NodeData nodeData, out int lastMajorVersionId, out int lastMinorVersionId)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            lastMajorVersionId = 0;
            lastMinorVersionId = 0;

            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_DeleteVersion" };
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;

                reader = cmd.ExecuteReader();

                //refresh timestamp value from the db
                while (reader.Read())
                {
                    nodeData.NodeTimestamp = DataProvider.GetLongFromBytes((byte[])reader[0]);
                    lastMajorVersionId = reader.GetSafeInt32(1);
                    lastMinorVersionId = reader.GetSafeInt32(2);
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();
                cmd.Dispose();
            }
        }

        protected internal override bool HasChild(int nodeId)
        {
            SqlProcedure cmd = null;
            int result;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_HasChild" };
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                result = (int)cmd.ExecuteScalar();
            }
            finally
            {
                cmd.Dispose();
            }

            if (result == -1)
                throw new ApplicationException();

            return result > 0;
        }

        protected internal override List<ContentListType> GetContentListTypesInTree(string path)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            var result = new List<ContentListType>();

            string commandString = @"SELECT ContentListTypeId FROM Nodes WHERE ContentListId IS NULL AND ContentListTypeId IS NOT NULL AND Path LIKE @Path + '/%'";
            cmd = new SqlProcedure { CommandText = commandString };
            cmd.CommandType = CommandType.Text;
            cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            try
            {
                reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var t = NodeTypeManager.Current.ContentListTypes.GetItemById(id);
                    result.Add(t);
                }
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }
            return result;
        }


        //////////////////////////////////////// Tool Methods ////////////////////////////////////////

        internal static string CreateIdXmlForReferencePropertyUpdate(IEnumerable<int> values)
        {
            StringBuilder xmlBuilder = new StringBuilder(values == null ? 50 : 50 + values.Count() * 10);
            xmlBuilder.AppendLine("<Identifiers>");
            xmlBuilder.AppendLine("<ReferredNodeIds>");
            if (values != null)
                foreach (var value in values)
                    if (value > 0)
                        xmlBuilder.Append("<Id>").Append(value).AppendLine("</Id>");
            xmlBuilder.AppendLine("</ReferredNodeIds>");
            xmlBuilder.AppendLine("</Identifiers>");
            return xmlBuilder.ToString();
        }

        private static string CreateIdXmlForNodeInfoBatchLoad(Dictionary<int, NodeBuilder> builders)
        {
            StringBuilder xmlBuilder = new StringBuilder(500 + builders.Count * 20);

            xmlBuilder.AppendLine("<Identifiers>");
            xmlBuilder.AppendLine("  <VersionIds>");
            foreach (int versionId in builders.Keys)
                xmlBuilder.Append("    <Id>").Append(versionId).AppendLine("</Id>");
            xmlBuilder.AppendLine("  </VersionIds>");
            xmlBuilder.AppendLine("</Identifiers>");

            return xmlBuilder.ToString();
        }

        protected internal override long GetTreeSize(string path, bool includeChildren)
        {
            SqlProcedure cmd = null;
            long result;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Node_GetTreeSize" };
                cmd.Parameters.Add("@NodePath", SqlDbType.NVarChar, 450).Value = path;
                cmd.Parameters.Add("@IncludeChildren", SqlDbType.TinyInt).Value = includeChildren ? 1 : 0;

                var obj = cmd.ExecuteScalar();

                result = (obj == DBNull.Value) ? 0 : (long)obj;
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }

            if (result == -1)
                throw new ApplicationException();

            return result;
        }

        protected override int NodeCount(string path)
        {
            var proc = new SqlProcedure();
            proc.CommandType = CommandType.Text;
            if (String.IsNullOrEmpty(path) || path == RepositoryPath.PathSeparator)
            {
                proc.CommandText = "SELECT COUNT(*) FROM Nodes";
            }
            else
            {
                proc.CommandText = "SELECT COUNT(*) FROM Nodes WHERE Path LIKE @Path + '/%'";
                proc.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            }
            return (int)proc.ExecuteScalar();
        }
        protected override int VersionCount(string path)
        {
            var proc = new SqlProcedure();
            proc.CommandType = CommandType.Text;
            if (String.IsNullOrEmpty(path) || path == RepositoryPath.PathSeparator)
            {
                proc.CommandText = "SELECT COUNT(*) FROM Versions V JOIN Nodes N ON N.NodeId = V.NodeId";
            }
            else
            {
                proc.CommandText = "SELECT COUNT(*) FROM Versions V JOIN Nodes N ON N.NodeId = V.NodeId WHERE N.Path LIKE @Path + '/%'";
                proc.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            }
            return (int)proc.ExecuteScalar();
        }

        //////////////////////////////////////// Security Methods ////////////////////////////////////////

        protected internal override Dictionary<int, List<int>> LoadMemberships()
        {
            SqlProcedure cmd = null;
            Dictionary<int, List<int>> result = new Dictionary<int, List<int>>();
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Security_LoadMemberships" };
                SqlDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    int groupId = (int)reader["ContainerId"];
                    int userId = (int)reader["UserId"];
                    if (!result.ContainsKey(groupId))
                        result.Add(groupId, new List<int>());
                    result[groupId].Add(userId);
                }
            }
            finally
            {
                cmd.Dispose();
            }
            return result;
        }

        private static string GetPermissionCacheKey(int principalID, int nodeID, PermissionType type)
        {
            return string.Format(CultureInfo.InvariantCulture, "SN:NodePermissionCache:{0}:{1}:{2}", principalID, nodeID, type.Id);
        }

        protected internal override void SetPermission(int principalId, int nodeId, PermissionType permissionType, bool isInheritable, PermissionValue permissionValue)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Security_SetPermission" };
                cmd.Parameters.Add("@PrincipalId", SqlDbType.Int).Value = principalId;
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                cmd.Parameters.Add("@PermissionTypeId", SqlDbType.Int).Value = permissionType.Id;
                cmd.Parameters.Add("@IsInheritable", SqlDbType.TinyInt).Value = isInheritable ? (byte)1 : (byte)0;
                cmd.Parameters.Add("@PermissionValue", SqlDbType.TinyInt).Value = (byte)permissionValue;
                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Dispose();
            }
        }
        protected internal override void SetPermission(SecurityEntry entry)
        {
            var sql = new StringBuilder();

            if (entry.AllowBits + entry.DenyBits == 0)
            {
                sql.Append("DELETE FROM SecurityEntries WHERE DefinedOnNodeId = @NodeId AND PrincipalId = @PrincipalId AND IsInheritable = @IsInheritable");
            }
            else
            {
                sql.AppendLine("IF NOT EXISTS (SELECT SecurityEntryId FROM dbo.SecurityEntries WHERE DefinedOnNodeId = @NodeId AND PrincipalId = @PrincipalId AND IsInheritable = @IsInheritable)");
                sql.AppendLine("    INSERT INTO dbo.SecurityEntries (DefinedOnNodeId, PrincipalId, IsInheritable) VALUES (@NodeId, @PrincipalId, @IsInheritable)");
                sql.AppendLine("UPDATE SecurityEntries SET");

                for (int i = 0; i < ActiveSchema.PermissionTypes.Count; i++)
                {
                    var value = (byte)0;
                    var mask = 1<<i;
                    if((entry.DenyBits & mask)!=0)
                        value = (byte)2;
                    else if((entry.AllowBits & mask)!=0)
                        value = (byte)1;
                    if (i > 0)
                        sql.AppendLine(",");
                    sql.Append("    PermissionValue").Append(i + 1).Append(" = ").Append(value);
                }
                sql.AppendLine();

                sql.AppendLine("WHERE DefinedOnNodeId = @NodeId AND PrincipalId = @PrincipalId AND IsInheritable = @IsInheritable");
            }

            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = sql.ToString(), CommandType = CommandType.Text };
                cmd.Parameters.Add("@PrincipalId", SqlDbType.Int).Value = entry.PrincipalId;
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = entry.DefinedOnNodeId;
                cmd.Parameters.Add("@IsInheritable", SqlDbType.TinyInt).Value = entry.Propagates ? (byte)1 : (byte)0;
                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Dispose();
            }

        }

        protected internal override void ExplicateGroupMemberships()
        {
            SqlProcedure cmd = null;

            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Security_ExplicateGroupMemberships" };
                cmd.ExecuteNonQuery();
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }
        }

        protected internal override void ExplicateOrganizationUnitMemberships(IUser user)
        {
            SqlProcedure cmd = null;

            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Security_ExplicateOrgUnitMemberships" };
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = user.Id;
                cmd.ExecuteNonQuery();
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }
        }

        protected internal override void BreakInheritance(int nodeId)
        {
            SetBreakInheritanceFlag(nodeId, true);
        }
        protected internal override void RemoveBreakInheritance(int nodeId)
        {
            SetBreakInheritanceFlag(nodeId, false);
        }
        private void SetBreakInheritanceFlag(int nodeId, bool @break)
        {
            SqlProcedure cmd = null;
            using (cmd = new SqlProcedure { CommandText = "proc_Security_BreakInheritance2" })
            {
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;
                cmd.Parameters.Add("@BreakInheritanceValue", SqlDbType.TinyInt).Value = @break ? (byte)0 : (byte)1;
                cmd.ExecuteNonQuery();
            }
        }

        private const string LOADGROUPMEMBERSHIPSQL = @"DECLARE @GroupTypeId int
SELECT @GroupTypeId = PropertySetId FROM SchemaPropertySets WHERE Name = 'Group'
DECLARE @MembersPropertyTypeId int
SELECT @MembersPropertyTypeId = PropertyTypeId FROM SchemaPropertyTypes WHERE Name = 'Members';

WITH AllMembers (GroupId, MemberId) AS
(
	SELECT NodeId, NodeId
	FROM Nodes
	WHERE NodeId = @GroupId

	UNION ALL

	SELECT THIS.GroupId, RP.ReferredNodeId
	FROM ReferenceProperties RP
		JOIN Nodes N ON RP.VersionId = N.LastMinorVersionId
		JOIN AllMembers THIS ON THIS.MemberId = N.NodeId
	WHERE PropertyTypeId = @MembersPropertyTypeId
)
SELECT  DISTINCT AllMembers.GroupId, AllMembers.MemberId
FROM
	AllMembers
	JOIN Nodes ON Nodes.NodeId = AllMembers.MemberId
WHERE
	Nodes.NodeTypeId IN (SELECT NodeTypeId FROM dbo.udfGetAllDerivatedNodeTypesByNodeTypeId (@GroupTypeId))
	AND AllMembers.GroupId != AllMembers.MemberId
";
        protected internal override List<int> LoadGroupMembership(int groupId)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            var members = new List<int>();
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_Security_LoadGroupMembership" };
                cmd.Parameters.Add("@GroupId", SqlDbType.Int).Value = groupId;
                reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var memberId = (int)reader["MemberId"];
                    members.Add(memberId);
                }
                return members;
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();
                cmd.Dispose();
            }

        }

        internal override int LoadLastModifiersGroupId()
        {
            // SELECT TOP 1 NodeId FROM Nodes WHERE NodeTypeId = 2 AND Name = 'LastModifiers'
            SqlProcedure cmd = null;
            SqlDataReader reader = null;

            string commandString = String.Format("SELECT TOP 1 NodeId FROM Nodes WHERE NodeTypeId = {0} AND Name = 'LastModifiers'", ActiveSchema.NodeTypes["Group"].Id);

            cmd = new SqlProcedure { CommandText = commandString };
            cmd.CommandType = CommandType.Text;

            try
            {
                reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return 0;
                var id = reader.GetSafeInt32(0);
                return id;
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }

        }

        //======================================================

        protected internal override void PersistUploadToken(UploadToken value)
        {
            SqlProcedure cmd = null;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_ApplicationMessaging_PersistUploadToken" };

                cmd.Parameters.Add("@Token", SqlDbType.UniqueIdentifier).Value = value.UploadGuid;
                cmd.Parameters.Add("@UserId", SqlDbType.Int).Value = value.UserId;
                cmd.Parameters.Add("@CreatedOn", SqlDbType.DateTime).Value = DateTime.Now;

                cmd.ExecuteNonQuery();
            }
            finally
            {
                cmd.Dispose();
            }
        }
        protected internal override int GetUserIdByUploadGuid(Guid uploadGuid)
        {
            SqlProcedure cmd = null;
            int result;
            try
            {
                cmd = new SqlProcedure { CommandText = "proc_ApplicationMessaging_GetUserIdByUploadGuid" };
                cmd.Parameters.Add("@Token", SqlDbType.UniqueIdentifier).Value = uploadGuid;
                result = (int)cmd.ExecuteScalar();
            }
            finally
            {
                cmd.Dispose();
            }

            return result;
        }

        protected internal override NodeHead LoadNodeHead(string path)
        {
            return LoadNodeHead(0, path, 0);
        }
        protected internal override NodeHead LoadNodeHead(int nodeId)
        {
            return LoadNodeHead(nodeId, null, 0);
        }
        protected internal override NodeHead LoadNodeHeadByVersionId(int versionId)
        {
            return LoadNodeHead(0, null, versionId);
        }
        private NodeHead LoadNodeHead(int nodeId, string path, int versionId)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;

            //command string sceleton. When using this, WHERE clause needs to be completed!
            string commandString = @"
                    SELECT
                        Node.NodeId,             -- 0
	                    Node.Name,               -- 1
	                    Node.DisplayName,        -- 2
                        Node.Path,               -- 3
                        Node.ParentNodeId,       -- 4
                        Node.NodeTypeId,         -- 5
	                    Node.ContentListTypeId,  -- 6
	                    Node.ContentListId,      -- 7
                        Node.CreationDate,       -- 8
                        Node.ModificationDate,   -- 9
                        Node.LastMinorVersionId, -- 10
                        Node.LastMajorVersionId, -- 11
                        Node.CreatedById,        -- 12
                        Node.ModifiedById,       -- 13
  		                Node.[Index],            -- 14
		                Node.LockedById,         -- 15
                        Node.Timestamp           -- 16
                    FROM
	                    Nodes Node  
                    WHERE ";
            if (path != null)
            {
                commandString = string.Concat(commandString, "Node.Path = @Path");
            }
            else if (versionId > 0)
            {
                commandString = string.Concat(@"DECLARE @NodeId int
                    SELECT @NodeId = NodeId FROM Versions WHERE VersionId = @VersionId
                ", 
                 commandString, 
                 "Node.NodeId = @NodeId");
            }
            else
            {
                commandString = string.Concat(commandString, "Node.NodeId = @NodeId");
            }

            cmd = new SqlProcedure { CommandText = commandString };
            cmd.CommandType = CommandType.Text;
            if (path != null)
                cmd.Parameters.Add("@Path", SqlDbType.NVarChar, 450).Value = path;
            else if (versionId > 0)
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
            else
                cmd.Parameters.Add("@NodeId", SqlDbType.Int).Value = nodeId;

            try
            {
                reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return null;

                return new NodeHead(
                    reader.GetInt32(0),      // nodeId,
                    reader.GetString(1),     // name,
                    reader.GetSafeString(2), // displayName,
                    reader.GetString(3),     // pathInDb,
                    reader.GetSafeInt32(4),  // parentNodeId,
                    reader.GetInt32(5),      // nodeTypeId,
                    reader.GetSafeInt32(6),  // contentListTypeId,
                    reader.GetSafeInt32(7),  // contentListId,
                    reader.GetDateTime(8),   // creationDate,
                    reader.GetDateTime(9),   // modificationDate,
                    reader.GetSafeInt32(10), // lastMinorVersionId,
                    reader.GetSafeInt32(11), // lastMajorVersionId,
                    reader.GetSafeInt32(12), // creatorId,
                    reader.GetSafeInt32(13), // modifierId,
                    reader.GetSafeInt32(14), // index,
                    reader.GetSafeInt32(15), // lockerId
                    GetLongFromBytes((byte[])reader.GetValue(16))     // timestamp
                );

            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }
        }
        protected internal override IEnumerable<NodeHead> LoadNodeHeads(IEnumerable<int> heads)
        {
            var nodeHeads = new List<NodeHead>();


            var cn = new SqlConnection(RepositoryConfiguration.ConnectionString);
            var cmd = new SqlCommand
            {
                Connection = cn,
                CommandType = CommandType.StoredProcedure,
                CommandText = "proc_NodeHead_Load_Batch"
            };

            var sb = new StringBuilder();
            sb.Append("<NodeHeads>");
            foreach (var id in heads)
                sb.Append("<id>").Append(id).Append("</id>");
            sb.Append("</NodeHeads>");

            cmd.Parameters.Add("@IdsInXml", SqlDbType.Xml).Value = sb.ToString();
            var adapter = new SqlDataAdapter(cmd);
            var dataSet = new DataSet();

            try
            {
                cn.Open();
                adapter.Fill(dataSet);
            }
            finally
            {
                cn.Close();
            }

            if (dataSet.Tables[0].Rows.Count > 0)
                foreach (DataRow currentRow in dataSet.Tables[0].Rows)
                {
                    if (currentRow["NodeID"] == DBNull.Value)
                        nodeHeads.Add(null);
                    else
                        nodeHeads.Add(new NodeHead(
                            TypeConverter.ToInt32(currentRow["NodeID"]),  //  0 - NodeId
                            TypeConverter.ToString(currentRow[1]),        //  1 - Name
                            TypeConverter.ToString(currentRow[2]),        //  2 - DisplayName
                            TypeConverter.ToString(currentRow[3]),        //  3 - Path
                            TypeConverter.ToInt32(currentRow[4]),         //  4 - ParentNodeId
                            TypeConverter.ToInt32(currentRow[5]),         //  5 - NodeTypeId
                            TypeConverter.ToInt32(currentRow[6]),         //  6 - ContentListTypeId 
                            TypeConverter.ToInt32(currentRow[7]),         //  7 - ContentListId
                            TypeConverter.ToDateTime(currentRow[8]),      //  8 - CreationDate
                            TypeConverter.ToDateTime(currentRow[9]),      //  9 - ModificationDate
                            TypeConverter.ToInt32(currentRow[10]),        // 10 - LastMinorVersionId
                            TypeConverter.ToInt32(currentRow[11]),        // 11 - LastMajorVersionId
                            TypeConverter.ToInt32(currentRow[12]),        // 12 - CreatedById
                            TypeConverter.ToInt32(currentRow[13]),        // 13 - ModifiedById
                            TypeConverter.ToInt32(currentRow[14]),        // 14 - Index
                            TypeConverter.ToInt32(currentRow[15]),        // 15 - LockedById
                            GetLongFromBytes((byte[])currentRow[16])
                            ));

                }
            return nodeHeads;
        }

        protected internal override NodeHead.NodeVersion[] GetNodeVersions(int nodeId)
        {
            SqlProcedure cmd = null;
            SqlDataReader reader = null;
            try
            {
                string commandString = @"
                    SELECT VersionId, MajorNumber, MinorNumber, Status
                    FROM Versions
                    WHERE NodeId = @NodeId
                    ORDER BY MajorNumber, MinorNumber
                ";
                cmd = new SqlProcedure { CommandText = commandString };
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.Add("@NodeId", SqlDbType.NVarChar, 450).Value = nodeId;
                reader = cmd.ExecuteReader();

                List<NodeHead.NodeVersion> versionList = new List<NodeHead.NodeVersion>();

                while (reader.Read())
                {
                    var versionId = reader.GetInt32(0);
                    var major = reader.GetInt16(1);
                    var minor = reader.GetInt16(2);
                    var statusCode = reader.GetInt16(3);

                    var versionNumber = new VersionNumber(major, minor, (VersionStatus)statusCode);

                    versionList.Add(new NodeHead.NodeVersion(versionNumber, versionId));
                }

                return versionList.ToArray();

            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (cmd != null)
                    cmd.Dispose();
            }


        }

        protected internal override BinaryCacheEntity LoadBinaryCacheEntity(int nodeVersionId, int propertyTypeId)
        {
            var commandText = string.Format(@"
            SELECT
	            Size,
	            CASE
		            WHEN Size < {0} THEN Stream
		            ELSE null
	            END AS Stream,
                BinaryPropertyId
            FROM
	            dbo.BinaryProperties
            WHERE
                VersionId = @VersionId
                AND
                PropertyTypeId = @PropertyTypeId
            ", RepositoryConfiguration.CachedBinarySize);

            using (var cmd = new SqlProcedure { CommandText = commandText })
            {
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = nodeVersionId;
                cmd.Parameters.Add("@PropertyTypeId", SqlDbType.Int).Value = propertyTypeId;
                cmd.CommandType = CommandType.Text;
                var reader = cmd.ExecuteReader(CommandBehavior.SingleRow | CommandBehavior.SingleResult);
                if (reader.HasRows && reader.Read())
                {
                    long length = reader.GetInt64(0);
                    byte[] rawData;
                    if (reader.IsDBNull(1))
                        rawData = null;
                    else
                        rawData = (byte[])reader.GetValue(1);
                    int binaryPropertyId = reader.GetInt32(2);
                    reader.Close();

                    return new BinaryCacheEntity()
                    {
                        Length = length,
                        RawData = rawData,
                        BinaryPropertyId = binaryPropertyId
                    };
                }
                else
                {
                    reader.Close();
                    return null;
                }
            }
        }
        protected internal override byte[] LoadBinaryFragment(int binaryPropertyId, long position, int count)
        {
            var commandText = @"
            SELECT
                SUBSTRING(Stream, @Position, @Count)
            FROM
	            dbo.BinaryProperties
            WHERE
                BinaryPropertyId = @BinaryPropertyId
            ";

            byte[] result;

            using (var cmd = new SqlProcedure { CommandText = commandText })
            {
                cmd.Parameters.Add("@BinaryPropertyId", SqlDbType.Int).Value = binaryPropertyId;
                cmd.Parameters.Add("@Position", SqlDbType.BigInt).Value = position + 1;
                cmd.Parameters.Add("@Count", SqlDbType.Int).Value = count;
                cmd.CommandType = CommandType.Text;

                result = (byte[])cmd.ExecuteScalar();
            }

            return result;
        }

        protected override bool NodeExistsInDatabase(string path)
        {
            var cmd = new SqlProcedure { CommandText = "SELECT COUNT(*) FROM Nodes WHERE Path = @Path", CommandType = CommandType.Text };
            cmd.Parameters.Add("@Path", SqlDbType.NVarChar, PathMaxLength).Value = path;
            try
            {
                var count = (int)cmd.ExecuteScalar();
                return count > 0;
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }
        }
        public override string GetNameOfLastNodeWithNameBase(int parentId, string namebase, string extension)
        {
            var cmd = new SqlProcedure { CommandText = "SELECT TOP 1 Name FROM Nodes WHERE ParentNodeId=@ParentId AND Name LIKE @Name+'(%)' + @Extension ORDER BY LEN(Name) DESC, Name DESC", CommandType = CommandType.Text };
            cmd.Parameters.Add("@ParentId", SqlDbType.Int).Value = parentId;
            cmd.Parameters.Add("@Name", SqlDbType.NVarChar).Value = namebase;
            cmd.Parameters.Add("@Extension", SqlDbType.NVarChar).Value = extension;
            try
            {
                var lastName = (string)cmd.ExecuteScalar();
                return lastName;
            }
            finally
            {
                if (cmd != null)
                    cmd.Dispose();
            }
        }

        //====================================================== AppModel script generator

        #region AppModel script generator constants
        private const string AppModelQ0 = "DECLARE @availablePaths AS TABLE([Id] INT IDENTITY (1, 1), [Path] NVARCHAR(900))";
        private const string AppModelQ1 = "INSERT @availablePaths ([Path]) VALUES ('{0}')";

        private const string AppModelQ2 = @"SELECT TOP 1 N.NodeId FROM @availablePaths C
LEFT OUTER JOIN Nodes N ON C.[Path] = N.[Path]
WHERE N.[Path] IS NOT NULL
ORDER BY C.Id";

        private const string AppModelQ3 = @"SELECT N.NodeId FROM @availablePaths C
LEFT OUTER JOIN Nodes N ON C.[Path] = N.[Path]
WHERE N.[Path] IS NOT NULL
ORDER BY C.Id";

        private const string AppModelQ4 = @"SELECT N.NodeId, N.[Path] FROM Nodes N
WHERE N.ParentNodeId IN
(
    SELECT N.NodeId FROM @availablePaths C
    LEFT OUTER JOIN Nodes N ON C.[Path] = N.[Path]
    WHERE N.[Path] IS NOT NULL
)";
        #endregion

        protected override string GetAppModelScriptPrivate(IEnumerable<string> paths, bool resolveAll, bool resolveChildren)
        {
            var script = new StringBuilder();
            script.AppendLine(AppModelQ0);
            foreach (var path in paths)
            {
                script.AppendFormat(AppModelQ1, SecureSqlStringValue(path));
                script.AppendLine();
            }

            if (resolveAll)
            {
                if (resolveChildren)
                    script.AppendLine(AppModelQ4);
                else
                    script.AppendLine(AppModelQ3);
            }
            else
            {
                script.Append(AppModelQ2);
            }
            return script.ToString();
        }

        /// <summary>
        /// SQL injection prevention.
        /// </summary>
        /// <param name="value">String value that will changed to.</param>
        /// <returns>Safe string value.</returns>
        public static string SecureSqlStringValue(string value)
        {
            return value.Replace(@"'", @"''").Replace("/*", "**").Replace("--", "**");
        }

        //====================================================== Custom database script support

        protected internal override IDataProcedure CreateDataProcedureInternal(string commandText)
        {
            var proc = new SqlProcedure();
            proc.CommandText = commandText;
            return proc;
        }
        protected override IDbDataParameter CreateParameterInternal()
        {
            return new SqlParameter();
        }

        protected internal override void CheckScriptInternal(string commandText)
        {
            // c:\Program Files\Microsoft SQL Server\90\SDK\Assemblies\Microsoft.SqlServer.Smo.dll
            // c:\Program Files\Microsoft SQL Server\90\SDK\Assemblies\Microsoft.SqlServer.ConnectionInfo.dll

            //--- The code is equivalent to this script:
            // SET NOEXEC ON
            // GO
            // SELECT * FROM Nodes
            // GO
            // SET NOEXEC OFF
            // GO

            //var c = new Microsoft.SqlServer.Management.Common.ServerConnection(new SqlConnection(RepositoryConfiguration.ConnectionString));
            //var server = new Microsoft.SqlServer.Management.Smo.Server(c);
            //server.ConnectionContext.ExecuteNonQuery(commandText, Microsoft.SqlServer.Management.Common.ExecutionTypes.NoExec);
        }

        //====================================================== Index document save / load operations

        const string LOADINDEXDOCUMENTSCRIPT = @"
            SELECT N.NodeTypeId, V.VersionId, V.NodeId, N.ParentNodeId, N.Path, N.LastMinorVersionId, N.LastMajorVersionId, V.Status, 
                V.IndexDocument, N.Timestamp, V.Timestamp
            FROM Nodes N INNER JOIN Versions V ON N.NodeId = V.NodeId
            ";

        private const int DOCSFRAGMENTSIZE = 100;

        protected internal override void UpdateIndexDocument(NodeData nodeData, byte[] indexDocumentBytes)
        {
            var cmd = (SqlProcedure)CreateDataProcedure("UPDATE Versions SET [IndexDocument] = @IndexDocument WHERE VersionId = @VersionId\nSELECT Timestamp FROM Versions WHERE VersionId = @VersionId");
            cmd.CommandType = CommandType.Text;

            cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = nodeData.VersionId;
            cmd.Parameters.Add("@IndexDocument", SqlDbType.VarBinary).Value = indexDocumentBytes;

            SqlDataReader reader = null;
            try
            {
                reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    // SELECT Timestamp FROM Versions WHERE VersionId = @VersionId
                    nodeData.VersionTimestamp = SqlProvider.GetLongFromBytes((byte[])reader[0]);
                }
            }
            finally
            {
                if (reader != null && !reader.IsClosed)
                    reader.Close();
                cmd.Dispose();
            }
        }
        protected internal override IndexDocumentData LoadIndexDocumentByVersionId(int versionId)
        {
            using (var cmd = new SqlProcedure { CommandText = LOADINDEXDOCUMENTSCRIPT + "WHERE V.VersionId = @VersionId" })
            {
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.Add("@VersionId", SqlDbType.Int).Value = versionId;
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                        return GetIndexDocumentDataFromReader(reader);
                    return null;
                }
            }
        }
        protected internal override IEnumerable<IndexDocumentData> LoadIndexDocumentByVersionId(IEnumerable<int> versionId)
        {
            var fi = 0;
            var listCount = versionId.Count();
            var result = new List<IndexDocumentData>();

            while (fi * DOCSFRAGMENTSIZE < listCount)
            {
                var docsSegment = versionId.Skip(fi * DOCSFRAGMENTSIZE).Take(DOCSFRAGMENTSIZE).ToArray();
                var paramNames = docsSegment.Select((s, i) => "@vi" + i.ToString()).ToArray();
                var where = String.Concat("WHERE V.VersionId IN (", string.Join(", ", paramNames), ")");

                SqlProcedure cmd = null;
                var retry = 0;
                while (retry < 15)
                {
                    try
                    {
                        cmd = new SqlProcedure { CommandText = LOADINDEXDOCUMENTSCRIPT + where };
                        cmd.CommandType = CommandType.Text;
                        for (var i = 0; i < paramNames.Length; i++)
                        {
                            cmd.Parameters.AddWithValue(paramNames[i], docsSegment[i]);
                        }

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                                result.Add(GetIndexDocumentDataFromReader(reader));
                        }
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteException(ex);
                        retry++;
                        System.Threading.Thread.Sleep(1000);
                    }
                    finally
                    {
                        if (cmd != null)
                            cmd.Dispose();
                    }
                }

                fi++;
            }

            return result;
        }
        protected internal override IDataProcedure CreateLoadIndexDocumentCollectionByPathProcedure(string path)
        {
            var proc = CreateDataProcedure(LOADINDEXDOCUMENTSCRIPT + "WHERE N.Path = @Path OR N.Path LIKE @Path + '/%' ORDER BY N.Path");
            proc.CommandType = CommandType.Text;
            var pathParam = new SqlParameter("@Path", SqlDbType.NVarChar, PathMaxLength);
            pathParam.Value = path;
            proc.Parameters.Add(pathParam);
            return proc;
        }
        protected internal override IndexDocumentData GetIndexDocumentDataFromReader(System.Data.Common.DbDataReader reader)
        {
            // 0           1          2       3             4     5                   6                   7       8              9            10
            // NodeTypeId, VersionId, NodeId, ParentNodeId, Path, LastMinorVersionId, LastMajorVersionId, Status, IndexDocument, N.Timestamp, V.Timestamp
            var versionId = reader.GetSafeInt32(1);
            var approved = Convert.ToInt32(reader.GetInt16(7)) == (int)VersionStatus.Approved;
            var isLastMajor = reader.GetSafeInt32(6) == versionId;

            var bytesData = reader.GetValue(8);
            var bytes = (bytesData == DBNull.Value) ? new byte[0] : (byte[])bytesData;

            return new IndexDocumentData
            {
                NodeTypeId = reader.GetSafeInt32(0),
                VersionId = versionId,
                NodeId = reader.GetSafeInt32(2),
                ParentId = reader.GetSafeInt32(3),
                Path = reader.GetSafeString(4),
                IsLastDraft = reader.GetSafeInt32(5) == versionId,
                IsLastPublic = approved && isLastMajor,
                IndexDocumentInfoBytes = bytes,
                NodeTimestamp = GetLongFromBytes((byte[])reader[9]),
                VersionTimestamp = GetLongFromBytes((byte[])reader[10]),
            };
        }
        protected internal override IEnumerable<int> GetIdsOfNodesThatDoNotHaveIndexDocument()
        {
            var proc = CreateDataProcedure("SELECT NodeId FROM Versions WHERE IndexDocument IS NULL");
            proc.CommandType = CommandType.Text;
            System.Data.Common.DbDataReader reader = null;
            try
            {
                reader = proc.ExecuteReader();
                var idSet = new List<int>();
                while(reader.Read())
                    idSet.Add(reader.GetSafeInt32(0));
                return idSet;
            }
            finally
            {
                if (reader != null)
                    reader.Dispose();
                if (proc != null)
                    proc.Dispose();
            }
        }

        //====================================================== Index backup / restore operations

        const int BUFFERSIZE = 1024 * 128; // * 512; // * 64; // * 8;

        protected internal override IndexBackup LoadLastBackup()
        {
            var sql = @"
SELECT [IndexBackupId], [BackupNumber], [BackupDate], [ComputerName], [AppDomain],
        DATALENGTH([BackupFile]) AS [BackupFileLength], [RowGuid], [Timestamp]
    FROM [IndexBackup] WHERE IsActive != 0
SELECT [IndexBackupId], [BackupNumber], [BackupDate], [ComputerName], [AppDomain],
        DATALENGTH([BackupFile]) AS [BackupFileLength], [RowGuid], [Timestamp]
    FROM [IndexBackup2] WHERE IsActive != 0
";
            IndexBackup result = null;
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                using (var reader = cmd.ExecuteReader())
                {
                    do
                    {
                        while (reader.Read())
                            result = GetBackupFromReader(reader);
                    } while (reader.NextResult());
                }
            }
            return result;
        }
        protected internal override IndexBackup CreateBackup(int backupNumber)
        {
            var backup = new IndexBackup
            {
                BackupNumber = backupNumber,
                AppDomainName = AppDomain.CurrentDomain.FriendlyName,
                BackupDate = DateTime.Now,
                ComputerName = Environment.MachineName,
            };

            var sql = String.Format(@"INSERT INTO {0} (BackupNumber, IsActive, BackupDate, ComputerName, [AppDomain]) VALUES
                (@BackupNumber, 0, @BackupDate, @ComputerName, @AppDomain)", backup.TableName);

            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add("@BackupNumber", SqlDbType.Int).Value = backup.BackupNumber;
                cmd.Parameters.Add("@BackupDate", SqlDbType.DateTime).Value = backup.BackupDate;
                cmd.Parameters.Add("@ComputerName", SqlDbType.NVarChar, 100).Value = backup.ComputerName;
                cmd.Parameters.Add("@AppDomain", SqlDbType.NVarChar, 500).Value = backup.AppDomainName;

                cmd.ExecuteNonQuery();
            }
            return backup;
        }
        private IndexBackup GetBackupFromReader(SqlDataReader reader)
        {
            var result = new IndexBackup();
            result.IndexBackupId = reader.GetInt32(0);              // IndexBackupId
            result.BackupNumber = reader.GetInt32(1);               // BackupNumber
            result.BackupDate = reader.GetDateTime(2);              // BackupDate
            result.ComputerName = reader.GetSafeString(3);          // ComputerName
            result.AppDomainName = reader.GetSafeString(4);         // AppDomain
            result.BackupFileLength = reader.GetInt64(5);           // BackupFileLength
            result.RowGuid = reader.GetGuid(6);                     // RowGuid
            result.Timestamp = GetLongFromBytes((byte[])reader[7]); // Timestamp
            return result;
        }
        protected internal override void StoreBackupStream(string backupFilePath, IndexBackup backup, IndexBackupProgress progress)
        {
            var fileLength = new FileInfo(backupFilePath).Length;

            using (var writeCommand = CreateWriteCommand(backup))
            {
                using (var stream = new FileStream(backupFilePath, FileMode.Open))
                {
                    using (var reader = new BinaryReader(stream))
                    {
                        InitializeNewStream(backup);

                        progress.Type = IndexBackupProgressType.Storing;
                        progress.Message = "Storing backup";
                        progress.MaxValue = fileLength;

                        var timer = Stopwatch.StartNew();

                        var offset = 0L;
                        while (offset < fileLength)
                        {
                            progress.Value = offset;
                            progress.NotifyChanged();

                            var remnant = fileLength - offset;
                            var length = remnant < BUFFERSIZE ? Convert.ToInt32(remnant) : BUFFERSIZE;
                            var buffer = reader.ReadBytes(length);
                            writeCommand.Parameters["@Buffer"].Value = buffer;
                            writeCommand.Parameters["@Offset"].Value = offset;
                            writeCommand.Parameters["@Length"].Value = length;
                            writeCommand.ExecuteNonQuery();
                            offset += BUFFERSIZE;
                        }
                        //progress.FinishStoreIndexBackupToDb();
                        ////progress.Value = fileLength;
                        ////progress.NotifyChanged();
                    }
                }
            }
        }
        //protected internal override void StoreBackupStream(string backupFilePath, IndexBackup2 backup, BackupProgress progress)
        //{
        //    var fileLength = new FileInfo(backupFilePath).Length;

        //    using (var stream = new FileStream(backupFilePath, FileMode.Open))
        //    {
        //        using (var reader = new BinaryReader(stream))
        //        {
        //            InitializeNewStream(backup);

        //            progress.Type = BackupProgressType.Storing;
        //            progress.Message = "Storing backup";
        //            progress.MaxValue = fileLength;

        //            var offset = 0L;
        //            while (offset < fileLength)
        //            {
        //                using (var writeCommand = CreateWriteCommand(backup))
        //                {
        //                    progress.Value = offset;
        //                    progress.NotifyChanged();

        //                    var remnant = fileLength - offset;
        //                    var length = remnant < BUFFERSIZE ? Convert.ToInt32(remnant) : BUFFERSIZE;
        //                    var buffer = reader.ReadBytes(length);
        //                    writeCommand.Parameters["@Buffer"].Value = buffer;
        //                    writeCommand.Parameters["@Offset"].Value = offset;
        //                    writeCommand.Parameters["@Length"].Value = length;
        //                    writeCommand.ExecuteNonQuery();
        //                    offset += BUFFERSIZE;
        //                }
        //            }
        //            progress.Value = fileLength;
        //            progress.NotifyChanged();
        //        }
        //    }
        //}
        private SqlProcedure CreateWriteCommand(IndexBackup backup)
        {
            var sql = String.Format("UPDATE {0} SET [BackupFile].WRITE(@Buffer, @Offset, @Length) WHERE BackupNumber = @BackupNumber", backup.TableName);
            var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text };
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@BackupNumber", SqlDbType.Int)).Value = backup.BackupNumber;
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Offset", SqlDbType.BigInt));
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Length", SqlDbType.BigInt));
            cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Buffer", SqlDbType.VarBinary));
            return cmd;
        }
        private void InitializeNewStream(IndexBackup backup)
        {
            var sql = String.Format("UPDATE {0} SET [BackupFile] = @InitialStream WHERE BackupNumber = @BackupNumber", backup.TableName);
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.CommandType = CommandType.Text;
                cmd.Parameters.Add(new SqlParameter("@BackupNumber", SqlDbType.Int));
                cmd.Parameters["@BackupNumber"].Value = backup.BackupNumber;
                cmd.Parameters.Add(new SqlParameter("@InitialStream", SqlDbType.VarBinary));
                cmd.Parameters["@InitialStream"].Value = new byte[0];
                cmd.ExecuteNonQuery();
            }
        }
        protected internal override void SetActiveBackup(IndexBackup backup, IndexBackup lastBackup)
        {
            var sql = (lastBackup == null) ?
                String.Format("UPDATE {0} SET IsActive = 1 WHERE BackupNumber = @ActiveBackupNumber", backup.TableName)
                :
                String.Format(@"UPDATE {0} SET IsActive = 1 WHERE BackupNumber = @ActiveBackupNumber
                    UPDATE {1} SET IsActive = 0 WHERE BackupNumber = @InactiveBackupNumber", backup.TableName, lastBackup.TableName);
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new SqlParameter("@ActiveBackupNumber", SqlDbType.Int)).Value = backup.BackupNumber;
                if(lastBackup!=null)
                    cmd.Parameters.Add(new SqlParameter("@InactiveBackupNumber", SqlDbType.Int)).Value = lastBackup.BackupNumber;
                cmd.ExecuteNonQuery();
            }
        }
        protected override void KeepOnlyLastIndexBackup()
        {
            var backup = LoadLastBackup();
            if (backup == null)
                return;

            backup = new IndexBackup { BackupNumber = backup.BackupNumber - 1 };
            var sql = "TRUNCATE TABLE " + backup.TableName;
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
                cmd.ExecuteNonQuery();
        }

        protected override Guid GetLastIndexBackupNumber()
        {
            var backup = LoadLastBackup();
            if(backup == null)
                throw GetNoBackupException();
            return backup.RowGuid;
        }
        private Exception GetNoBackupException()
        {
            return new InvalidOperationException("Last index backup does not exist in the database.");
        }

        /*------------------------------------------------------*/

        protected override IndexBackup RecoverIndexBackup(string backupFilePath)
        {
            var backup = LoadLastBackup();
            if (backup == null)
                throw GetNoBackupException();

            if (File.Exists(backupFilePath))
                File.Delete(backupFilePath);

            var dbFileLength = backup.BackupFileLength;

            using (var readCommand = CreateReadCommand(backup))
            {
                using (var stream = new FileStream(backupFilePath, FileMode.Create))
                {
                    BinaryWriter writer = new BinaryWriter(stream);
                    var offset = 0L;
                    while (offset < dbFileLength)
                    {
                        var remnant = dbFileLength - offset;
                        var length = remnant < BUFFERSIZE ? Convert.ToInt32(remnant) : BUFFERSIZE;
                        readCommand.Parameters["@Offset"].Value = offset;
                        readCommand.Parameters["@Length"].Value = length;
                        readCommand.ExecuteNonQuery();
                        var buffer = (byte[])readCommand.ExecuteScalar();
                        writer.Write(buffer, 0, buffer.Length);
                        offset += BUFFERSIZE;
                    }
                }
            }
            return backup;
        }
        private IDataProcedure CreateReadCommand(IndexBackup backup)
        {
            var sql = String.Format("SELECT SUBSTRING([BackupFile], @Offset, @Length) FROM {0} WHERE BackupNumber = @BackupNumber", backup.TableName);
            var cmd = new SqlProcedure { CommandText = sql, CommandType = System.Data.CommandType.Text };
            cmd.CommandType = CommandType.Text;
            cmd.Parameters.Add(new SqlParameter("@BackupNumber", SqlDbType.Int)).Value = backup.BackupNumber;
            cmd.Parameters.Add(new SqlParameter("@Offset", SqlDbType.BigInt));
            cmd.Parameters.Add(new SqlParameter("@Length", SqlDbType.BigInt));
            return cmd;
        }

        private const string GETLASTACTIVITYIDSCRIPT = "SELECT CASE WHEN i.last_value IS NULL THEN 0 ELSE CONVERT(int, i.last_value) END last_value FROM sys.identity_columns i JOIN sys.tables t ON i.object_id = t.object_id WHERE t.name = 'IndexingActivity'";
        public override int GetLastActivityId()
        {
            using (var cmd = new SqlProcedure { CommandText = GETLASTACTIVITYIDSCRIPT, CommandType = CommandType.Text })
            {
                var x = cmd.ExecuteScalar();
                if (x == DBNull.Value)
                    return 0;
                return Convert.ToInt32(x);
            }
        }

        //====================================================== Checking  index integrity

        public override IDataProcedure GetTimestampDataForOneNodeIntegrityCheck(string path)
        {
            string checkNodeSql = "SELECT V.VersionId, CONVERT(bigint, n.timestamp) NodeTimestamp, CONVERT(bigint, v.timestamp) VersionTimestamp from Versions V join Nodes N on V.NodeId = N.NodeId WHERE N.Path = '{0}'";
            var sql = String.Format(checkNodeSql, path);
            var proc = SenseNet.ContentRepository.Storage.Data.DataProvider.CreateDataProcedure(sql);
            proc.CommandType = System.Data.CommandType.Text;
            return proc;
        }
        public override IDataProcedure GetTimestampDataForRecursiveIntegrityCheck(string path)
        {
            string sql;
            if (path == null)
                sql = "SELECT V.VersionId, CONVERT(bigint, n.timestamp) NodeTimestamp, CONVERT(bigint, v.timestamp) VersionTimestamp from Versions V join Nodes N on V.NodeId = N.NodeId";
            else
                sql = String.Format("SELECT V.VersionId, CONVERT(bigint, n.timestamp) NodeTimestamp, CONVERT(bigint, v.timestamp) VersionTimestamp from Versions V join Nodes N on V.NodeId = N.NodeId WHERE N.Path = '{0}' OR N.Path LIKE '{0}/%'", path);
            var proc = SenseNet.ContentRepository.Storage.Data.DataProvider.CreateDataProcedure(sql);
            proc.CommandType = System.Data.CommandType.Text;
            return proc;
        }

        //====================================================== Database backup / restore operations

        private string _databaseName;
        public override string DatabaseName
        {
            get
            {
                if (_databaseName == null)
                {
                    var cnstr = new SqlConnectionStringBuilder(RepositoryConfiguration.ConnectionString);
                    _databaseName = cnstr.InitialCatalog;
                }
                return _databaseName;
            }
        }

        public override IEnumerable<string> GetScriptsForDatabaseBackup()
        {
            return new[]
            {
                "USE [Master]",
                @"BACKUP DATABASE [{DatabaseName}] TO DISK = N'{BackupFilePath}' WITH NOFORMAT, INIT, NAME = N'SenseNetContentRepository-Full Database Backup', SKIP, NOREWIND, NOUNLOAD, STATS = 10"
            };
        }

        //====================================================== Powershell provider

        protected internal override int InitializeStagingBinaryData(int versionId, int propertyTypeId, string fileName, long fileSize)
        {
            var sql = @"
                INSERT INTO StagingBinaryProperties ( VersionId,  PropertyTypeId,  ContentType,  FileNameWithoutExtension,  Extension,  Size,  Stream) VALUES
                                                    (@VersionId, @PropertyTypeId, @ContentType, @FileNameWithoutExtension, @Extension, @Size,    0x00)
                SELECT @@IDENTITY";
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                var fName = Path.GetFileNameWithoutExtension(fileName);
                var ext = Path.GetExtension(fileName);
                var mime =  MimeTable.GetMimeType(ext.ToLower(CultureInfo.InvariantCulture));
                cmd.Parameters.Add(new SqlParameter("@VersionId", SqlDbType.Int)).Value = versionId;
                cmd.Parameters.Add(new SqlParameter("@PropertyTypeId", SqlDbType.Int)).Value = propertyTypeId;
                cmd.Parameters.Add(new SqlParameter("@ContentType", SqlDbType.VarChar, 50)).Value = mime;
                cmd.Parameters.Add(new SqlParameter("@FileNameWithoutExtension", SqlDbType.NVarChar, 450)).Value = fName;
                cmd.Parameters.Add(new SqlParameter("@Extension", SqlDbType.NVarChar, 450)).Value = ext;
                cmd.Parameters.Add(new SqlParameter("@Size", SqlDbType.BigInt)).Value = fileSize;
                var result = cmd.ExecuteScalar();
                return Convert.ToInt32(result);
            }
        }
        protected internal override void SaveChunk(int stagingBinaryDataId, byte[] bytes, int offset)
        {
            var sql = "UPDATE StagingBinaryProperties SET [Stream].WRITE(@Buffer, @Offset, @Length) WHERE Id = @Id";
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Id", SqlDbType.Int)).Value = stagingBinaryDataId;
                cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Buffer", SqlDbType.VarBinary)).Value = bytes;
                cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Offset", SqlDbType.BigInt)).Value = offset;
                cmd.Parameters.Add(new System.Data.SqlClient.SqlParameter("@Length", SqlDbType.BigInt)).Value = bytes.LongLength;
                cmd.ExecuteNonQuery();
            }
        }
        protected internal override void CopyStagingToBinaryData(int versionId, int propertyTypeId, int stagingBinaryDataId, string checksum)
        {
            var sql = @"
UPDATE BinaryProperties
	SET ContentType = S.ContentType,
		FileNameWithoutExtension = S.FileNameWithoutExtension,
		Extension = S.Extension,
		Size = S.Size,
		[Checksum] = @Checksum,
		Stream = S.Stream
	FROM BinaryProperties B
		JOIN StagingBinaryProperties S ON S.VersionId = B.VersionId AND S.PropertyTypeId = B.PropertyTypeId
WHERE B.VersionId = @VersionId AND B.PropertyTypeId = @PropertyTypeId
";
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new SqlParameter("@VersionId", SqlDbType.Int)).Value = versionId;
                cmd.Parameters.Add(new SqlParameter("@PropertyTypeId", SqlDbType.Int)).Value = propertyTypeId;
                cmd.Parameters.Add(new SqlParameter("@Checksum", SqlDbType.VarChar, 200)).Value = checksum;
                cmd.ExecuteNonQuery();
            }
        }
        protected internal override void DeleteStagingBinaryData(int stagingBinaryDataId)
        {
            var sql = "DELETE from StagingBinaryProperties WHERE Id = @Id";
            using (var cmd = new SqlProcedure { CommandText = sql, CommandType = CommandType.Text })
            {
                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int)).Value = stagingBinaryDataId;
                cmd.ExecuteNonQuery();
            }
        }
    }
}

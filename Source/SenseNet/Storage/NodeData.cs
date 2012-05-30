﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SenseNet.ContentRepository.Storage.Schema;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace SenseNet.ContentRepository.Storage
{
	internal interface IDynamicDataAccessor
	{
		Node OwnerNode { get; set; }
		PropertyType PropertyType { get; set; }
		object RawData { get; set; }
		object GetDefaultRawData();
	}
    [DebuggerDisplay("{Name}: {Original} -> {Value}")]
    public class ChangedData
    {
        public string Name { get; set; }
        public object Value { get; set; }
        public object Original { get; set; }
    }

	public class NodeData
	{
		private class SnapshotData
		{
			public int Id;
			public string Path;
			public int VersionId;
			public VersionNumber Version;
			public Dictionary<int, int> BinaryIds;
		}
		internal enum StaticDataSlot
		{
			Id, NodeTypeId, ContentListId, ContentListTypeId,
            ParentId, Name, DisplayName, Path, Index, IsDeleted, IsInherited,
			NodeCreationDate, NodeModificationDate, NodeCreatedById, NodeModifiedById,
			VersionId, Version, CreationDate, ModificationDate, CreatedById, ModifiedById,
			Locked, LockedById, ETag, LockType, LockTimeout, LockDate, LockToken, LastLockUpdate,
            NodeTimestamp, VersionTimestamp
		}
        private static readonly object LockObject = new Object();
        private object _readPropertySync = new object();

		//=========================================================== Data content

		private bool _isShared;
		private NodeData _sharedData;
		private object[] staticData;
		private bool[] staticDataIsModified;
		private Dictionary<int, object> dynamicData;

		internal TypeCollection<PropertyType> PropertyTypes { get; private set; }

		//----------------------------------------------------------- Structure

		internal NodeData SharedData
		{
			get { return _sharedData; }
			set { _sharedData = value; }
		}
		internal bool IsShared
		{
			get { return _isShared; }
			set { _isShared = value; }
		}

		//----------------------------------------------------------- Information Properties

		internal bool PathChanged
		{
			get { return staticDataIsModified[(int)StaticDataSlot.Path]; }
		}
		internal string OriginalPath
		{
			get
			{
				int index = (int)StaticDataSlot.Path;
				if (SharedData == null)
					return (string)staticData[index];
				return (string)SharedData.staticData[index];
			}
		}
		internal bool AnyDataModified
		{
			get
			{
				if (IsShared)
					return false;
				//var modified = false;
				foreach (var b in staticDataIsModified)
					if(b)
                        return true;
				//return dynamicData.Count > 0;

                var sharedDynamicData = this._sharedData.dynamicData;
                foreach (var propId in dynamicData.Keys)
                {
                    var propType = ActiveSchema.PropertyTypes.GetItemById(propId);
                    var origData = sharedDynamicData[propId];
                    var privData = dynamicData[propId];
                    if (RelevantChange(origData, privData, propType.DataType))
                        return true;
                }
                return false;
            }
		}
		internal bool IsModified(PropertyType propertyType)
		{
			if (IsShared)
				return false;
            bool result = false;
            lock (_readPropertySync)
            {
                result = dynamicData.ContainsKey(propertyType.Id);
            }
            return result;
        }
        internal string DefaultName { get; private set; }

        //----------------------------------------------------------- Cached properties

        private string ContentFieldXml { get; set; }

		//=========================================================== Construction

		public NodeData(int nodeTypeId, int contentListTypeId)
			: this(NodeTypeManager.Current.NodeTypes.GetItemById(nodeTypeId), NodeTypeManager.Current.ContentListTypes.GetItemById(contentListTypeId)) { }
		public NodeData(NodeType nodeType, ContentListType contentListType)
		{
			staticDataIsModified = new bool[31];
			staticData = new object[31];

			PropertyTypes = NodeTypeManager.GetDynamicSignature(nodeType.Id, contentListType == null ? 0 : contentListType.Id);

			dynamicData = new Dictionary<int, object>();
		}

		#region //===========================================================  Static data slot shortcuts and accessors

		private T Get<T>(StaticDataSlot slot)
		{
            var value = Get(slot);
            return (value == null) ? default(T) : (T)value;
		}
		private void Set<T>(StaticDataSlot slot, T value) where T : IComparable
		{
            if (SharedData != null)
            {
                var index = (int)slot;
                var sharedValue = (T)SharedData.staticData[index];
                if (value.CompareTo(sharedValue) == 0)
                {
                    Reset(index);
                    return;
                }
            }
            Set(slot, (object)value);
		}

		private object Get(StaticDataSlot slot)
		{
			int index = (int)slot;
			if (SharedData == null || staticDataIsModified[index])
				return staticData[index];
			return SharedData.staticData[index];
		}
		private void Set(StaticDataSlot slot, object value)
		{
			if (IsShared)
				throw Exception_SharedIsReadOnly();
			int index = (int)slot;
			staticData[index] = value;
			staticDataIsModified[index] = true;
		}
        private void Reset(int index)
        {
			if (IsShared)
				throw Exception_SharedIsReadOnly();
            staticData[index] = null;
            staticDataIsModified[index] = false;
        }

		internal int Id
		{
			get { return Get<int>(StaticDataSlot.Id); }
			set { Set<int>(StaticDataSlot.Id, value); }
		}
		internal int NodeTypeId
		{
			get { return Get<int>(StaticDataSlot.NodeTypeId); }
			set { Set<int>(StaticDataSlot.NodeTypeId, value); }
		}
		internal int ContentListId
		{
			get { return Get<int>(StaticDataSlot.ContentListId); }
			set { Set<int>(StaticDataSlot.ContentListId, value); }
		}
		internal int ContentListTypeId
		{
			get { return Get<int>(StaticDataSlot.ContentListTypeId); }
			set { Set<int>(StaticDataSlot.ContentListTypeId, value); }
		}

		internal int ParentId
		{
			get { return Get<int>(StaticDataSlot.ParentId); }
			set { Set<int>(StaticDataSlot.ParentId, value); }
		}
        internal string Name
        {
            get { return Get<string>(StaticDataSlot.Name); }
            set
            {
                if (Get<string>(StaticDataSlot.Name) == null)
                    DefaultName = value;
                Set<string>(StaticDataSlot.Name, value);
            }
        }
        internal string DisplayName
        {
            get { return Get<string>(StaticDataSlot.DisplayName); }
            set { Set<string>(StaticDataSlot.DisplayName, value); }
        }
        internal string Path
		{
			get { return Get<string>(StaticDataSlot.Path); }
			set { Set<string>(StaticDataSlot.Path, value); }
		}
		internal int Index
		{
			get { return Get<int>(StaticDataSlot.Index); }
			set { Set<int>(StaticDataSlot.Index, value); }
		}
        internal bool IsDeleted
		{
            get { return Get<bool>(StaticDataSlot.IsDeleted); }
            set { Set<bool>(StaticDataSlot.IsDeleted, value); }
		}
        internal bool IsInherited
		{
            get { return Get<bool>(StaticDataSlot.IsInherited); }
            set { Set<bool>(StaticDataSlot.IsInherited, value); }
		}

		internal DateTime NodeCreationDate
		{
			get { return Get<DateTime>(StaticDataSlot.NodeCreationDate); }
			set { Set<DateTime>(StaticDataSlot.NodeCreationDate, value); }
		}
		internal DateTime NodeModificationDate
		{
			get { return Get<DateTime>(StaticDataSlot.NodeModificationDate); }
            set { Set<DateTime>(StaticDataSlot.NodeModificationDate, value); NodeModificationDateChanged = true; }
		}
        internal int NodeCreatedById
        {
            get { return Get<int>(StaticDataSlot.NodeCreatedById); }
            set { Set<int>(StaticDataSlot.NodeCreatedById, value); }
        }
        internal int NodeModifiedById
        {
            get { return Get<int>(StaticDataSlot.NodeModifiedById); }
            set { Set<int>(StaticDataSlot.NodeModifiedById, value); NodeModifiedByIdChanged = true; }
        }

		internal int VersionId
		{
			get { return Get<int>(StaticDataSlot.VersionId); }
			set { Set<int>(StaticDataSlot.VersionId, value); }
		}
		internal VersionNumber Version
		{
			get { return Get<VersionNumber>(StaticDataSlot.Version); }
			set { Set<VersionNumber>(StaticDataSlot.Version, value); }
		}
		internal DateTime CreationDate
		{
			get { return Get<DateTime>(StaticDataSlot.CreationDate); }
			set { Set<DateTime>(StaticDataSlot.CreationDate, value); }
		}
		internal DateTime ModificationDate
		{
			get { return Get<DateTime>(StaticDataSlot.ModificationDate); }
            set { Set<DateTime>(StaticDataSlot.ModificationDate, value); ModificationDateChanged = true; }
		}
        internal int CreatedById
        {
            get { return Get<int>(StaticDataSlot.CreatedById); }
            set { Set<int>(StaticDataSlot.CreatedById, value); }
        }
        internal int ModifiedById
        {
            get { return Get<int>(StaticDataSlot.ModifiedById); }
            set { Set<int>(StaticDataSlot.ModifiedById, value); ModifiedByIdChanged = true; }
        }

		internal bool Locked
		{
			get { return Get<bool>(StaticDataSlot.Locked); }
			set { Set<bool>(StaticDataSlot.Locked, value); }
		}
		internal int LockedById
		{
			get { return Get<int>(StaticDataSlot.LockedById); }
			set { Set<int>(StaticDataSlot.LockedById, value); }
		}
		internal string ETag
		{
			get { return Get<string>(StaticDataSlot.ETag); }
			set { Set<string>(StaticDataSlot.ETag, value); }
		}
		internal int LockType
		{
			get { return Get<int>(StaticDataSlot.LockType); }
			set { Set<int>(StaticDataSlot.LockType, value); }
		}
		internal int LockTimeout
		{
			get { return Get<int>(StaticDataSlot.LockTimeout); }
			set { Set<int>(StaticDataSlot.LockTimeout, value); }
		}
		internal DateTime LockDate
		{
			get { return Get<DateTime>(StaticDataSlot.LockDate); }
			set { Set<DateTime>(StaticDataSlot.LockDate, value); }
		}
		internal string LockToken
		{
			get { return Get<string>(StaticDataSlot.LockToken); }
			set { Set<string>(StaticDataSlot.LockToken, value); }
		}
		internal DateTime LastLockUpdate
		{
			get { return Get<DateTime>(StaticDataSlot.LastLockUpdate); }
			set { Set<DateTime>(StaticDataSlot.LastLockUpdate, value); }
		}

        internal long NodeTimestamp
        {
            get { return Get<long>(StaticDataSlot.NodeTimestamp); }
            set { Set<long>(StaticDataSlot.NodeTimestamp, value); }
        }
        internal long VersionTimestamp
        {
            get { return Get<long>(StaticDataSlot.VersionTimestamp); }
            set { Set<long>(StaticDataSlot.VersionTimestamp, value); }
        }

		#endregion

        internal bool ModificationDateChanged { get; set; }
        internal bool ModifiedByIdChanged { get; set; }
        internal bool NodeModificationDateChanged { get; set; }
        internal bool NodeModifiedByIdChanged { get; set; }

		//=========================================================== Dynamic raw data accessors

		internal object GetDynamicRawData(int propertyTypeId)
		{
			var propType = this.PropertyTypes.GetItemById(propertyTypeId);
			if (propType == null)
				throw Exception_PropertyNotFound(propertyTypeId);
			return GetDynamicRawData(propType);
		}
		internal object GetDynamicRawData(PropertyType propertyType)
		{
			var id = propertyType.Id;
			object value;

			//-- if modified
            lock (_readPropertySync)
            {
                if (dynamicData.TryGetValue(id, out value))
                    return value;
            }
            
			if (SharedData != null)
			{
				//-- if loaded
                lock (_readPropertySync)
                {
                    if (SharedData.dynamicData.TryGetValue(id, out value))
                        return value;
                } 
                return SharedData.LoadProperty(propertyType);
            }
            
            if (this.IsShared)
                return LoadProperty(propertyType);

			return null;
		}

        internal void SetDynamicRawData(int propertyTypeId, object data)
        {
            SetDynamicRawData(propertyTypeId, data, true);
        }
        internal void SetDynamicRawData(int propertyTypeId, object data, bool withCheckModifying)
        {
            var propType = this.PropertyTypes.GetItemById(propertyTypeId);
            SetDynamicRawData(propType, data, withCheckModifying);
        }
        internal void SetDynamicRawData(PropertyType propertyType, object data)
        {
            SetDynamicRawData(propertyType, data, true);
        }
        internal void SetDynamicRawData(PropertyType propertyType, object data, bool withCheckModifying)
        {
            if (IsShared)
                throw Exception_SharedIsReadOnly();
            var id = propertyType.Id;
            if (!withCheckModifying || IsDynamicPropertyChanged(propertyType, data))
            {
                lock (_readPropertySync)
                {
                    if (dynamicData.ContainsKey(id))
                        dynamicData[propertyType.Id] = data;
                    else
                        dynamicData.Add(id, data);
                }
            }
            else
            {
                ResetDynamicRawData(propertyType);
            }
        }

        internal void CheckChanges(PropertyType propertyType)
        {
            lock (_readPropertySync)
            {
                if (!dynamicData.ContainsKey(propertyType.Id))
                    return;
                if (!IsDynamicPropertyChanged(propertyType, dynamicData[propertyType.Id]))
                    ResetDynamicRawData(propertyType);
            }
        }
        private void ResetDynamicRawData(PropertyType propertyType)
        {
            lock (_readPropertySync)
            {
                if (dynamicData.ContainsKey(propertyType.Id))
                    dynamicData.Remove(propertyType.Id);
            }
        }
        internal bool IsPropertyChanged(string propertyName)
        {
            if (IsShared)
                return false;
            if (SharedData == null)
                return true;

            StaticDataSlot slot;
            if (Enum.TryParse<StaticDataSlot>(propertyName, out slot))
                return staticDataIsModified[(int)slot];

            var propType = ActiveSchema.PropertyTypes[propertyName];
            if (propType == null)
                throw Exception_PropertyNotFound(propertyName);

            object currentValue;
            if(dynamicData.TryGetValue(propType.Id, out currentValue))
                return IsDynamicPropertyChanged(propType, currentValue);
            return false;
        }
        private bool IsPropertyChanged(PropertyType propertyType)
        {
            throw new NotImplementedException();
        }
        internal bool IsDynamicPropertyChanged(PropertyType propertyType, object data)
        {
            if (IsShared)
                return false;
            if (SharedData == null)
                return true;
            var containsPropertyType = false;
            lock (_readPropertySync)
            {
                containsPropertyType = SharedData.dynamicData.ContainsKey(propertyType.Id);
            }
            if (!containsPropertyType) 
                SharedData.LoadProperty(propertyType);
            var propId = propertyType.Id;
            object sharedDynamicData = null;
            lock (_readPropertySync)
            {
                sharedDynamicData = SharedData.dynamicData[propId];
            }

            if (data == null && sharedDynamicData == null)
                return false;
            if (data == null || sharedDynamicData == null)
                return true;

            switch (propertyType.DataType)
            {
                case DataType.String:
                case DataType.Text:
                    return ((string)data != (string)sharedDynamicData);
                case DataType.Int:
                    return ((int)data != (int)sharedDynamicData);
                case DataType.Currency:
                    return ((decimal)data != (decimal)sharedDynamicData);
                case DataType.DateTime:
                    return ((DateTime)data != (DateTime)sharedDynamicData);
                case DataType.Binary:
                    return IsBinaryChanged(data, sharedDynamicData);
                case DataType.Reference:
                    return IsIdListsChanged(data, sharedDynamicData);
                default:
                    throw new NotImplementedException(propertyType.DataType.ToString());
            }
        }
        private bool IsBinaryChanged(object value, object original)
        {
            var a = (BinaryDataValue)value;
            var b = (BinaryDataValue)original;
            if (a == null && b == null)
                return false;
            if (!(a != null && b != null))
                return true;
            if (a.Id != b.Id)
                return true;
            if (a.ContentType != b.ContentType)
                return true;
            if (a.FileName != b.FileName)
                return true;
            if (a.Size != b.Size)
                return true;
            if (a.Checksum != b.Checksum)
                return true;
            return false;
        }
        private static bool IsIdListsChanged(object value, object original)
        {
            var a = (List<int>)value;
            var b = (List<int>)original;
            if (a == null && b == null)
                return false;
            if (!(a != null && b != null))
                return true;
            if (a.Count != b.Count)
                return true;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i])
                    return true;
            return false;
        }

        private object LoadProperty(PropertyType propertyType)
        {
            var propId = propertyType.Id;
            lock (_readPropertySync)
            {
                if (dynamicData.ContainsKey(propId))
                    return dynamicData[propId];
            }
            object data = DataBackingStore.LoadProperty(this.VersionId, propertyType);
            lock (_readPropertySync)
            {
                if (!dynamicData.ContainsKey(propId))
                    dynamicData.Add(propId, data);
            }
            return data;
        }


		//---------------------------------------------------------- static structure builder

		internal static NodeData CreatePrivateData(NodeData asSharedData)
		{
			if (!asSharedData.IsShared)
				MakeSharedData(asSharedData);
			var privateData = new NodeData(asSharedData.NodeTypeId, asSharedData.ContentListTypeId) { SharedData = asSharedData };
            //if (privateData.PropertyTypes.Count != privateData.SharedData.PropertyTypes.Count)
            //    ExtendSharedDataWithListProperties(privateData);
			return privateData;
		}
		private static void MakeSharedData(NodeData data)
		{
			if (data.SharedData != null)
			{
				MergeData(data.SharedData, data);
				data.SharedData = null;
			}
			data.IsShared = true;
		}
		private static void MergeData(NodeData shared, NodeData target)
		{
		    for (int i = 0; i < target.staticData.Length; i++)
		    {
		        if (!target.staticDataIsModified[i])
		            target.staticData[i] = shared.staticData[i];
		        target.staticDataIsModified[i] = false;
		    }
		    foreach (var propType in target.PropertyTypes)
		    {
		        var id = propType.Id;
		        object sharedData;
                lock (LockObject)
                {
                    if (shared.dynamicData.TryGetValue(id, out sharedData))
                        if (!target.dynamicData.ContainsKey(id))
                            target.dynamicData.Add(id, sharedData);
                }
            }
		}

        //---------------------------------------------------------- copy

        private static readonly StaticDataSlot[] slotsToCopy = {
            //do not copy:
            // StaticDataSlot.Id, StaticDataSlot.VersionId, StaticDataSlot.ParentId, StaticDataSlot.Path, StaticDataSlot.Name,
            StaticDataSlot.DisplayName,
            // StaticDataSlot.Locked, StaticDataSlot.LockedById,
            // StaticDataSlot.ETag, StaticDataSlot.LockType, StaticDataSlot.LockTimeout, StaticDataSlot.LockDate, StaticDataSlot.LockToken, StaticDataSlot.LastLockUpdate
            StaticDataSlot.NodeTypeId,
            //StaticDataSlot.ContentListId, StaticDataSlot.ContentListTypeId,
            StaticDataSlot.Index, StaticDataSlot.IsDeleted, StaticDataSlot.IsInherited,
            StaticDataSlot.NodeCreationDate, StaticDataSlot.NodeModificationDate, StaticDataSlot.NodeCreatedById, StaticDataSlot.NodeModifiedById,
            StaticDataSlot.Version,
            StaticDataSlot.CreationDate, StaticDataSlot.ModificationDate, StaticDataSlot.CreatedById, StaticDataSlot.ModifiedById,
        };

        internal void CopyGeneralPropertiesTo(NodeData target)
        {
            foreach (var slot in slotsToCopy)
                target.Set(slot, Get(slot));
        }
        internal void CopyDynamicPropertiesTo(NodeData target)
        {
            foreach (var propType in PropertyTypes)
            {
                if (Node.EXCLUDED_COPY_PROPERTIES.Contains(propType.Name)) continue;

                if (!propType.IsContentListProperty || target.PropertyTypes[propType.Name] != null)
                    target.SetDynamicRawData(propType, GetDynamicRawData(propType));
            }
        }

		//---------------------------------------------------------- exception helpers

		internal static Exception Exception_SharedIsReadOnly()
		{
			return new NotSupportedException("#### Storage2: shared data is read only.");
		}
		internal static Exception Exception_PropertyNotFound(string name)
		{
			var propType = NodeTypeManager.Current.PropertyTypes[name];
			if (propType == null)
				return new ApplicationException("PropertyType not found. Name: " + name);
			return new ApplicationException(String.Concat("Unknown property. Id: ", propType.Id, ", Name: ", name));
		}
		internal static Exception Exception_PropertyNotFound(int propTypeId)
		{
			var propType = NodeTypeManager.Current.PropertyTypes.GetItemById(propTypeId);
			if (propType == null)
				return new ApplicationException("PropertyType not found. Id: " + propTypeId);
			return new ApplicationException(String.Concat("Unknown property. Id: ", propType.Id, ", Name: ", propType.Name));
		}

		//---------------------------------------------------------- transaction

		private SnapshotData _snapshotData;
		internal void CreateSnapshotData()
		{
			var binIds = new Dictionary<int, int>();
			foreach (var propType in PropertyTypes)
			{
				if (propType.DataType == DataType.Binary)
				{
					var binValue = GetDynamicRawData(propType) as BinaryDataValue;
					if (binValue != null)
						binIds.Add(propType.Id, binValue.Id);
				}
			}
			_snapshotData = new SnapshotData
			{
				Id = this.Id,
				Path = this.Path,
				VersionId = this.VersionId,
				Version = this.Version,
				BinaryIds = binIds
			};
		}
		internal void Rollback()
		{
			if (IsShared)
				throw Exception_SharedIsReadOnly();

			this.Id = _snapshotData.Id;
			this.Path = _snapshotData.Path;
			this.VersionId = _snapshotData.VersionId;
			this.Version = _snapshotData.Version;
			foreach (var propTypeId in _snapshotData.BinaryIds.Keys)
			{
				var binValue = GetDynamicRawData(propTypeId) as BinaryDataValue;
				if (binValue != null)
					binValue.Id = _snapshotData.BinaryIds[propTypeId];
			}
		}

        internal IEnumerable<ChangedData> GetChangedValues()
        {
            var changes = new List<ChangedData>();
            if (this._sharedData == null)
                return changes;
            var sharedStaticData = this._sharedData.staticData;

            for (int i = 0; i < staticData.Length; i++)
            {
                if (staticDataIsModified[i])
                {
                    var slot = (StaticDataSlot)i;
                    changes.Add(new ChangedData
                    {
                        Name = slot.ToString(),
                        Original = FormatStaticData(sharedStaticData[i], slot),
                        Value = FormatStaticData(staticData[i], slot)
                    });
                    var spec = FormatSpecialStaticChangedValue(slot, sharedStaticData[i], staticData[i]);
                    if (spec != null)
                        changes.Add(spec);
                }
            }

            var sharedDynamicData = this._sharedData.dynamicData;
            foreach (var propId in dynamicData.Keys)
            {
                var propType = ActiveSchema.PropertyTypes.GetItemById(propId);
                var origData = sharedDynamicData[propId];
                var privData = dynamicData[propId];
                if (RelevantChange(origData, privData, propType.DataType))
                {
                    changes.Add(new ChangedData
                    {
                        Name = propType.Name,
                        Original = FormatDynamicData(origData, propType.DataType),
                        Value = FormatDynamicData(privData, propType.DataType)
                    });
                }
            }

            return changes;
        }
        private bool RelevantChange(object origData, object privData, DataType dataType)
        {
            if (dataType == DataType.Reference)
            {
                if (origData == null)
                {
                    if (privData == null)
                        return false;

                    var list = privData as List<int>;
                    if (list != null)
                        return list.Count > 0;
                }
            }
            return true;
        }

        internal IDictionary<string, object> GetAllValues()
        {
            var values = new Dictionary<string, object>();

            values.Add("Id", Id);
            values.Add("NodeTypeId", NodeTypeId);
            values.Add("NodeType", FormatNodeType(NodeTypeId));
            values.Add("ContentListId", ContentListId);
            values.Add("ContentListTypeId", ContentListTypeId);
            values.Add("ParentId", ParentId);
            values.Add("Name", Name);
            values.Add("DisplayName", DisplayName);
            values.Add("Path", Path);
            values.Add("Index", Index);
            values.Add("IsDeleted", IsDeleted.ToString().ToLower());
            values.Add("IsInherited", IsInherited.ToString().ToLower());
            values.Add("NodeCreationDate", FormatDate(NodeCreationDate));
            values.Add("NodeModificationDate", FormatDate(NodeModificationDate));
            values.Add("NodeCreatedById", NodeCreatedById);
            values.Add("NodeCreatedBy", FormatUser(NodeCreatedById));
            values.Add("NodeModifiedById", NodeModifiedById);
            values.Add("NodeModifiedBy", FormatUser(NodeModifiedById));
            values.Add("VersionId", VersionId);
            values.Add("Version", Version.ToString());
            values.Add("CreationDate", FormatDate(CreationDate));
            values.Add("ModificationDate", FormatDate(ModificationDate));
            values.Add("CreatedById", CreatedById);
            values.Add("CreatedBy", FormatUser(CreatedById));
            values.Add("ModifiedById", ModifiedById);
            values.Add("ModifiedBy", FormatUser(ModifiedById));
            values.Add("Locked", Locked.ToString().ToLower());
            values.Add("LockedById", LockedById);
            values.Add("LockedBy", FormatUser(LockedById));
            values.Add("ETag", ETag);
            values.Add("LockType", LockType);
            values.Add("LockTimeout", LockTimeout);
            values.Add("LockDate", FormatDate(LockDate));
            values.Add("LockToken", LockToken);
            values.Add("LastLockUpdate", FormatDate(LastLockUpdate));

            foreach (var key in dynamicData.Keys)
            {
                var propType = ActiveSchema.PropertyTypes.GetItemById(key);
                if (propType != null)
                    values.Add(propType.Name.Replace("#", "_"), FormatDynamicData(dynamicData[key] ?? string.Empty, propType.DataType));
            }
            return values;
        }

        private string FormatStaticData(object data, StaticDataSlot slot)
        {
            switch (slot)
            {
                case StaticDataSlot.Id:
                case StaticDataSlot.NodeTypeId:
                case StaticDataSlot.ContentListId:
                case StaticDataSlot.ContentListTypeId:
                case StaticDataSlot.ParentId:
                case StaticDataSlot.Name:
                case StaticDataSlot.DisplayName:
                case StaticDataSlot.Path:
                case StaticDataSlot.Index:
                case StaticDataSlot.VersionId:
                case StaticDataSlot.Version:
                case StaticDataSlot.ETag:
                case StaticDataSlot.LockType:
                case StaticDataSlot.LockTimeout:
                case StaticDataSlot.LockToken:
                    return data == null ? String.Empty : data.ToString();

                case StaticDataSlot.IsDeleted:
                case StaticDataSlot.IsInherited:
                case StaticDataSlot.Locked:
                    return data.ToString().ToLower();

                case StaticDataSlot.NodeCreatedById:
                case StaticDataSlot.NodeModifiedById:
                case StaticDataSlot.CreatedById:
                case StaticDataSlot.ModifiedById:
                case StaticDataSlot.LockedById:
                    return data.ToString();

                case StaticDataSlot.NodeCreationDate:
                case StaticDataSlot.NodeModificationDate:
                case StaticDataSlot.CreationDate:
                case StaticDataSlot.ModificationDate:
                case StaticDataSlot.LockDate:
                case StaticDataSlot.LastLockUpdate:
                    return FormatDate((DateTime)data);

                default:
                    return string.Empty;
            }
        }
        private ChangedData FormatSpecialStaticChangedValue(StaticDataSlot slot, object oldValue, object newValue)
        {
            string name = null;
            switch (slot)
            {
                case StaticDataSlot.NodeCreatedById: name = "NodeCreatedBy"; break;
                case StaticDataSlot.NodeModifiedById: name = "NodeModifiedBy"; break;
                case StaticDataSlot.CreatedById: name = "CreatedBy"; break;
                case StaticDataSlot.ModifiedById: name = "ModifiedBy"; break;
                case StaticDataSlot.LockedById: name = "LockedBy"; break;
            }
            if (name == null)
                return null;
            return new ChangedData
            {
                Name = name,
                Original = FormatUser((int)oldValue),
                Value = FormatUser((int)newValue)
            };
        }
        private string FormatDynamicData(object data, DataType dataType)
        {
            if (data == null)
                return string.Empty;
            
            switch (dataType)
            {
                case DataType.Text:
                    var text = Convert.ToString(data, CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(text))
                        return string.Empty;
                    if (text.StartsWith("<![CDATA["))
                        return text;
                    return String.Concat("<![CDATA[", text, "]]>");
                case DataType.String:
                case DataType.Int:
                case DataType.Currency:
                    return Convert.ToString(data, CultureInfo.InvariantCulture);
                case DataType.DateTime:
                    //cast cannot be used here
                    if (!(data is DateTime))
                        return string.Empty;
                    return FormatDate((DateTime)data);
                case DataType.Binary:
                    var bin = data as BinaryDataValue;
                    if (bin == null)
                        return string.Empty;
                    return String.Format(CultureInfo.InvariantCulture,
                              "Id: {0}, MimeType: {1}, FileName: {2}, IsEmpty: {3}, Size: {4}, Checksum {5}",
                              bin.Id, bin.ContentType, bin.FileName, bin.IsEmpty, bin.Size, bin.Checksum);
                case DataType.Reference:
                    var refs = data as NodeList<Node>;
                    if (refs == null)
                        return string.Empty;
                    return String.Join(", ", (from id in refs.GetIdentifiers() select id.ToString()).ToArray());
            }
            return string.Empty;
        }

        private string FormatDate(DateTime dateTime)
        {
            return dateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffK");
        }
        private string FormatNodeType(int id)
        {
            var nt = ActiveSchema.NodeTypes.GetItemById(id);
            if (nt == null)
                return string.Empty;
            return nt.Name;
        }
        private string FormatUser(int id)
        {
            var n = Node.LoadNode(id);
            var u = n as SenseNet.ContentRepository.Storage.Security.IUser;
            if (u == null)
                return string.Empty;
            return u.Username;
        }

        //=========================================================== Shared extension

        //System.Collections.Concurrent.ConcurrentDictionary<string, object> _extendedSharedData;
        Dictionary<string, object> _extendedSharedData;
        ReaderWriterLock _extendedSharedDataLock;

        internal object GetExtendedSharedData(string name)
        {
            if (name == null)
                throw new ArgumentNullException("name");

            var shared = IsShared ? this : SharedData;
            if (shared == null)
                //throw new NotSupportedException("Cannot set extended data if there is no shared data.");
                return null;

            switch (name)
            {
                case "ContentFieldXml":
                    return shared.ContentFieldXml;
            }

            var dict = shared._extendedSharedData;
            if (dict == null)
                return null;

            shared._extendedSharedDataLock.AcquireReaderLock(Timeout.Infinite);
            try
            {
                object value;
                if (dict.TryGetValue(name, out value))
                    return value;
                return null;
            }
            finally
            {
                shared._extendedSharedDataLock.ReleaseReaderLock();
            }
        }
        internal void SetExtendedSharedData(string name, object value)
        {
            if (name == null)
                throw new ArgumentNullException("name");

            var shared = IsShared ? this : SharedData;
            if (shared == null)
                //throw new NotSupportedException("Cannot set extended data if there is no shared data.");
                return;

            switch (name)
            {
                case "ContentFieldXml":
                    shared.ContentFieldXml = value as string;
                    return;
            }

            var dict = shared._extendedSharedData;
            if (dict == null)
            {
                dict = new Dictionary<string, object>();
                shared._extendedSharedData = dict;
                shared._extendedSharedDataLock = new ReaderWriterLock();
            }

            shared._extendedSharedDataLock.AcquireWriterLock(Timeout.Infinite);
            try
            {
                if (!dict.ContainsKey(name))
                    dict.Add(name, value);
                else
                    dict[name] = value;
            }
            finally
            {
                shared._extendedSharedDataLock.ReleaseWriterLock();
            }
        }
        internal void ResetExtendedSharedData(string name)
        {
            if (name == null)
                throw new ArgumentNullException("name");

            if (SharedData == null)
                return;

            switch (name)
            {
                case "ContentFieldXml":
                    SharedData.ContentFieldXml = null;
                    return;
            }

            var dict = SharedData._extendedSharedData;
            if (dict == null)
                return;

            SharedData._extendedSharedDataLock.AcquireReaderLock(Timeout.Infinite);
            try
            {
                if (dict.ContainsKey(name))
                    dict.Remove(name);
            }
            finally
            {
                SharedData._extendedSharedDataLock.ReleaseReaderLock();
            }
        }
    }
}
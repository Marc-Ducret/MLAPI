using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MLAPI.Serialization.Pooled;

namespace MLAPI.NetworkedVar.Collections
{
    /// <summary>
    ///     Event based networkedVar container for syncing Lists
    /// </summary>
    /// <typeparam name="T">The type for the list</typeparam>
    public class NetworkedList<T> : IList<T>, INetworkedVar
    {
        /// <summary>
        ///     Delegate type for list changed event
        /// </summary>
        /// <param name="changeEvent">Struct containing information about the change event</param>
        public delegate void OnListChangedDelegate(NetworkedListEvent<T> changeEvent);

        private readonly List<NetworkedListEvent<T>> dirtyEvents = new List<NetworkedListEvent<T>>();
        private readonly IList<T>                    list        = new List<T>();

        /// <summary>
        ///     The settings for this container
        /// </summary>
        public readonly NetworkedVarSettings Settings = new NetworkedVarSettings();
        private NetworkedBehaviour networkedBehaviour;

        /// <summary>
        ///     Creates a NetworkedList with the default value and settings
        /// </summary>
        public NetworkedList()
        { }

        /// <summary>
        ///     Creates a NetworkedList with the default value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedList</param>
        public NetworkedList(NetworkedVarSettings settings)
        {
            Settings = settings;
        }

        /// <summary>
        ///     Creates a NetworkedList with a custom value and custom settings
        /// </summary>
        /// <param name="settings">The settings to use for the NetworkedList</param>
        /// <param name="value">The initial value to use for the NetworkedList</param>
        public NetworkedList(NetworkedVarSettings settings, IList<T> value)
        {
            Settings = settings;
            list     = value;
        }

        /// <summary>
        ///     Creates a NetworkedList with a custom value and the default settings
        /// </summary>
        /// <param name="value">The initial value to use for the NetworkedList</param>
        public NetworkedList(IList<T> value)
        {
            list = value;
        }

        /// <summary>
        ///     Gets the last time the variable was synced
        /// </summary>
        public float LastSyncedTime { get; internal set; }

        /// <inheritdoc />
        public IEnumerator<T> GetEnumerator()
        {
            return list.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)list).GetEnumerator();
        }

        /// <inheritdoc />
        public void Add(T item)
        {
            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T> {eventType = NetworkedListEvent<T>.EventType.Add, value = item, index = list.Count};
            dirtyEvents.Add(listEvent);

            if (NetworkingManager.Singleton.IsServer)
            {
                list.Add(item);
            }

            if (NetworkingManager.Singleton.IsServer && OnListChanged != null)
            {
                OnListChanged(listEvent);
            }
        }

        /// <inheritdoc />
        public void Clear()
        {
            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T> {eventType = NetworkedListEvent<T>.EventType.Clear};
            dirtyEvents.Add(listEvent);

            if (NetworkingManager.Singleton.IsServer)
            {
                list.Clear();
            }

            if (NetworkingManager.Singleton.IsServer && OnListChanged != null)
            {
                OnListChanged(listEvent);
            }
        }

        /// <inheritdoc />
        public bool Contains(T item)
        {
            return list.Contains(item);
        }

        /// <inheritdoc />
        public void CopyTo(T[] array, int arrayIndex)
        {
            list.CopyTo(array, arrayIndex);
        }

        /// <inheritdoc />
        public bool Remove(T item)
        {
            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T> {eventType = NetworkedListEvent<T>.EventType.Remove, value = item};
            dirtyEvents.Add(listEvent);

            if (NetworkingManager.Singleton.IsServer)
            {
                list.Remove(item);
            }

            if (NetworkingManager.Singleton.IsServer && OnListChanged != null)
            {
                OnListChanged(listEvent);
            }

            return true;
        }

        /// <inheritdoc />
        public int Count => list.Count;

        /// <inheritdoc />
        public bool IsReadOnly => list.IsReadOnly;

        /// <inheritdoc />
        public int IndexOf(T item)
        {
            return list.IndexOf(item);
        }

        /// <inheritdoc />
        public void Insert(int index, T item)
        {
            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T> {eventType = NetworkedListEvent<T>.EventType.Insert, index = index, value = item};
            dirtyEvents.Add(listEvent);

            if (NetworkingManager.Singleton.IsServer)
            {
                list.Insert(index, item);
            }

            if (NetworkingManager.Singleton.IsServer && OnListChanged != null)
            {
                OnListChanged(listEvent);
            }
        }

        /// <inheritdoc />
        public void RemoveAt(int index)
        {
            NetworkedListEvent<T> listEvent = new NetworkedListEvent<T> {eventType = NetworkedListEvent<T>.EventType.RemoveAt, index = index};
            dirtyEvents.Add(listEvent);

            if (NetworkingManager.Singleton.IsServer)
            {
                list.RemoveAt(index);
            }

            if (NetworkingManager.Singleton.IsServer && OnListChanged != null)
            {
                OnListChanged(listEvent);
            }
        }


        /// <inheritdoc />
        public T this[int index]
        {
            get => list[index];
            set
            {
                NetworkedListEvent<T> listEvent = new NetworkedListEvent<T> {eventType = NetworkedListEvent<T>.EventType.Value, index = index, value = value};
                dirtyEvents.Add(listEvent);

                if (NetworkingManager.Singleton.IsServer)
                {
                    list[index] = value;
                }

                if (NetworkingManager.Singleton.IsServer && OnListChanged != null)
                {
                    OnListChanged(listEvent);
                }
            }
        }

        /// <inheritdoc />
        public void ResetDirty()
        {
            dirtyEvents.Clear();
            LastSyncedTime = NetworkingManager.Singleton.NetworkTime;
        }

        /// <inheritdoc />
        public bool IsDirty()
        {
            if (dirtyEvents.Count == 0)
            {
                return false;
            }

            if (Settings.SendTickrate == 0)
            {
                return true;
            }

            if (Settings.SendTickrate < 0)
            {
                return false;
            }

            if (NetworkingManager.Singleton.NetworkTime - LastSyncedTime >= 1f / Settings.SendTickrate)
            {
                return true;
            }

            return false;
        }

        /// <inheritdoc />
        public string GetChannel()
        {
            return Settings.SendChannel;
        }

        /// <inheritdoc />
        public bool CanClientWrite(ulong clientId)
        {
            switch (Settings.WritePermission)
            {
                case NetworkedVarPermission.Everyone:
                    return true;
                case NetworkedVarPermission.ServerOnly:
                    return false;
                case NetworkedVarPermission.OwnerOnly:
                    return networkedBehaviour.OwnerClientId == clientId;
                case NetworkedVarPermission.Custom:
                    {
                        if (Settings.WritePermissionCallback == null)
                        {
                            return false;
                        }

                        return Settings.WritePermissionCallback(clientId);
                    }
            }

            return true;
        }

        /// <inheritdoc />
        public bool CanClientRead(ulong clientId)
        {
            switch (Settings.ReadPermission)
            {
                case NetworkedVarPermission.Everyone:
                    return true;
                case NetworkedVarPermission.ServerOnly:
                    return false;
                case NetworkedVarPermission.OwnerOnly:
                    return networkedBehaviour.OwnerClientId == clientId;
                case NetworkedVarPermission.Custom:
                    {
                        if (Settings.ReadPermissionCallback == null)
                        {
                            return false;
                        }

                        return Settings.ReadPermissionCallback(clientId);
                    }
            }

            return true;
        }

        /// <inheritdoc />
        public void WriteDelta(Stream stream)
        {
            using (PooledBitWriter writer = PooledBitWriter.Get(stream))
            {
                writer.WriteUInt16Packed((ushort)dirtyEvents.Count);
                for (int i = 0; i < dirtyEvents.Count; i++)
                {
                    writer.WriteBits((byte)dirtyEvents[i].eventType, 3);
                    switch (dirtyEvents[i].eventType)
                    {
                        case NetworkedListEvent<T>.EventType.Add:
                            {
                                writer.WriteInt32Packed(dirtyEvents[i].index);
                                writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.Insert:
                            {
                                writer.WriteInt32Packed(dirtyEvents[i].index);
                                writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.Remove:
                            {
                                writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.RemoveAt:
                            {
                                writer.WriteInt32Packed(dirtyEvents[i].index);
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.Value:
                            {
                                writer.WriteInt32Packed(dirtyEvents[i].index);
                                writer.WriteObjectPacked(dirtyEvents[i].value); //BOX
                            }

                            break;
                        case NetworkedListEvent<T>.EventType.Clear:
                            {
                                //Nothing has to be written
                            }
                            break;
                    }
                }
            }
        }

        /// <inheritdoc />
        public void WriteField(Stream stream)
        {
            using (PooledBitWriter writer = PooledBitWriter.Get(stream))
            {
                writer.WriteUInt16Packed((ushort)list.Count);
                for (int i = 0; i < list.Count; i++)
                {
                    writer.WriteObjectPacked(list[i]); //BOX
                }
            }
        }

        /// <inheritdoc />
        public void ReadField(Stream stream)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                list.Clear();
                ushort count = reader.ReadUInt16Packed();
                for (int i = 0; i < count; i++)
                {
                    list.Add((T)reader.ReadObjectPacked(typeof(T))); //BOX
                }
            }
        }

        /// <inheritdoc />
        public void ReadDelta(Stream stream, bool keepDirtyDelta)
        {
            using (PooledBitReader reader = PooledBitReader.Get(stream))
            {
                ushort deltaCount = reader.ReadUInt16Packed();
                for (int i = 0; i < deltaCount; i++)
                {
                    NetworkedListEvent<T>.EventType eventType = (NetworkedListEvent<T>.EventType)reader.ReadBits(3);
                    switch (eventType)
                    {
                        case NetworkedListEvent<T>.EventType.Add:
                            {
                                int index = reader.ReadInt32Packed();
                                T   value = (T)reader.ReadObjectPacked(typeof(T)); //BOX
                                if (index < list.Count)
                                {
                                    if (NetworkingManager.Singleton.IsServer)
                                    {
                                        index = list.Count;
                                        list.Add(value);
                                    }
                                    else
                                    {
                                        list[index] = value;
                                    }
                                }
                                else if (index == list.Count)
                                {
                                    list.Add(value);
                                }
                                else
                                {
                                    throw new Exception($"De-synchronized Networked List: trying to add an element at index {index} while list is of size {list.Count}");
                                }

                                if (OnListChanged != null)
                                {
                                    OnListChanged(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }

                                if (keepDirtyDelta)
                                {
                                    dirtyEvents.Add(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.Insert:
                            {
                                int index = reader.ReadInt32Packed();
                                list.Insert(index, (T)reader.ReadObjectPacked(typeof(T))); //BOX

                                if (OnListChanged != null)
                                {
                                    OnListChanged(new NetworkedListEvent<T> {eventType = eventType, index = index, value = list[index]});
                                }

                                if (keepDirtyDelta)
                                {
                                    dirtyEvents.Add(new NetworkedListEvent<T> {eventType = eventType, index = index, value = list[index]});
                                }
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.Remove:
                            {
                                T   value = (T)reader.ReadObjectPacked(typeof(T)); //BOX
                                int index = list.IndexOf(value);
                                list.RemoveAt(index);

                                if (OnListChanged != null)
                                {
                                    OnListChanged(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }

                                if (keepDirtyDelta)
                                {
                                    dirtyEvents.Add(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.RemoveAt:
                            {
                                int index = reader.ReadInt32Packed();
                                T   value = list[index];
                                list.RemoveAt(index);

                                if (OnListChanged != null)
                                {
                                    OnListChanged(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }

                                if (keepDirtyDelta)
                                {
                                    dirtyEvents.Add(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.Value:
                            {
                                int index = reader.ReadInt32Packed();
                                T   value = (T)reader.ReadObjectPacked(typeof(T)); //BOX
                                if (index < list.Count)
                                {
                                    list[index] = value;
                                }

                                if (OnListChanged != null)
                                {
                                    OnListChanged(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }

                                if (keepDirtyDelta)
                                {
                                    dirtyEvents.Add(new NetworkedListEvent<T> {eventType = eventType, index = index, value = value});
                                }
                            }
                            break;
                        case NetworkedListEvent<T>.EventType.Clear:
                            {
                                //Read nothing
                                list.Clear();

                                if (OnListChanged != null)
                                {
                                    OnListChanged(new NetworkedListEvent<T> {eventType = eventType});
                                }

                                if (keepDirtyDelta)
                                {
                                    dirtyEvents.Add(new NetworkedListEvent<T> {eventType = eventType});
                                }
                            }
                            break;
                    }
                }
            }
        }

        /// <inheritdoc />
        public void SetNetworkedBehaviour(NetworkedBehaviour behaviour)
        {
            networkedBehaviour = behaviour;
        }

        /// <summary>
        ///     The callback to be invoked when the list gets changed
        /// </summary>
        public event OnListChangedDelegate OnListChanged;
    }

    /// <summary>
    ///     Struct containing event information about changes to a NetworkedList.
    /// </summary>
    /// <typeparam name="T">The type for the list that the event is about</typeparam>
    public struct NetworkedListEvent<T>
    {
        /// <summary>
        ///     Enum representing the different operations available for triggering an event.
        /// </summary>
        public enum EventType
        {
            /// <summary>
            ///     Add
            /// </summary>
            Add,

            /// <summary>
            ///     Insert
            /// </summary>
            Insert,

            /// <summary>
            ///     Remove
            /// </summary>
            Remove,

            /// <summary>
            ///     Remove at
            /// </summary>
            RemoveAt,

            /// <summary>
            ///     Value changed
            /// </summary>
            Value,

            /// <summary>
            ///     Clear
            /// </summary>
            Clear
        }

        /// <summary>
        ///     Enum representing the operation made to the list.
        /// </summary>
        public EventType eventType;

        /// <summary>
        ///     The value changed, added or removed if available.
        /// </summary>
        public T value;

        /// <summary>
        ///     the index changed, added or removed if available
        /// </summary>
        public int index;
    }
}

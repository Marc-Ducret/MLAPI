using System.IO;
using MLAPI.Serialization.Pooled;
using MLAPI.Spawning;

namespace MLAPI.Serialization
{
    public class LazyNetworkedBehaviour<T> : IBitWritable where T : NetworkedBehaviour
    {
        private ulong  networkedID;
        private ushort behaviourID;
        private T      cachedValue;

        public T Value
        {
            get
            {
                if (cachedValue == null)
                {
                    if (SpawnManager.SpawnedObjects.ContainsKey(networkedID))
                    {
                        cachedValue = (T)SpawnManager.SpawnedObjects[networkedID].GetBehaviourAtOrderIndex(behaviourID);
                    }
                    else
                    {
                        return null;
                    }
                }

                return cachedValue;
            }
            set
            {
                cachedValue = value;
                networkedID = value.NetworkId;
                behaviourID = value.GetBehaviourId();
            }
        }

        public void Read(Stream stream)
        {
            using (PooledBitReader pooledBitReader = PooledBitReader.Get(stream))
            {
                networkedID = pooledBitReader.ReadUInt64Packed();
                behaviourID = pooledBitReader.ReadUInt16Packed();
                cachedValue = null;
            }
        }

        public void Write(Stream stream)
        {
            using (PooledBitWriter pooledBitWriter = PooledBitWriter.Get(stream))
            {
                pooledBitWriter.WriteUInt64Packed(networkedID);
                pooledBitWriter.WriteUInt16Packed(behaviourID);
            }
        }
    }
}

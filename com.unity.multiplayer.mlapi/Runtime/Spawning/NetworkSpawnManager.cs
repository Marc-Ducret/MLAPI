using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MLAPI.Configuration;
using MLAPI.Exceptions;
using MLAPI.Logging;
using MLAPI.Messaging;
using MLAPI.SceneManagement;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;
using UnityEngine;

namespace MLAPI.Spawning
{
    /// <summary>
    /// Class that handles object spawning
    /// </summary>
    public class NetworkSpawnManager
    {
        /// <summary>
        /// The currently spawned objects
        /// </summary>
        public readonly Dictionary<ulong, NetworkObject> SpawnedObjects = new Dictionary<ulong, NetworkObject>();

        // Pending SoftSync objects
        internal readonly Dictionary<ulong, NetworkObject> PendingSoftSyncObjects = new Dictionary<ulong, NetworkObject>();

        /// <summary>
        /// A list of the spawned objects
        /// </summary>
        public readonly HashSet<NetworkObject> SpawnedObjectsList = new HashSet<NetworkObject>();


        /// <summary>
        /// Gets the NetworkManager associated with this SpawnManager.
        /// </summary>
        public NetworkManager NetworkManager { get; }

        internal NetworkSpawnManager(NetworkManager networkManager)
        {
            NetworkManager = networkManager;
        }

        internal readonly Queue<ReleasedNetworkId> ReleasedNetworkObjectIds = new Queue<ReleasedNetworkId>();
        private ulong m_NetworkObjectIdCounter;

        internal ulong GetNetworkObjectId()
        {
            if (ReleasedNetworkObjectIds.Count > 0 && NetworkManager.NetworkConfig.RecycleNetworkIds && (Time.unscaledTime - ReleasedNetworkObjectIds.Peek().ReleaseTime) >= NetworkManager.NetworkConfig.NetworkIdRecycleDelay)
            {
                return ReleasedNetworkObjectIds.Dequeue().NetworkId;
            }

            m_NetworkObjectIdCounter++;

            return m_NetworkObjectIdCounter;
        }

        /// <summary>
        /// Gets the prefab index of a given prefab hash
        /// </summary>
        /// <param name="hash">The hash of the prefab</param>
        /// <returns>The index of the prefab</returns>
        public int GetNetworkPrefabIndexOfHash(uint hash)
        {
            for (int i = 0; i < NetworkManager.NetworkConfig.NetworkPrefabs.Count; i++)
            {
                if (NetworkManager.NetworkConfig.NetworkPrefabs[i].Hash == hash)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Returns the prefab hash for the NetworkPrefab with a given index
        /// </summary>
        /// <param name="index">The NetworkPrefab index</param>
        /// <returns>The prefab hash for the given prefab index</returns>
        public uint GetPrefabHashFromIndex(int index)
        {
            return NetworkManager.NetworkConfig.NetworkPrefabs[index].Hash;
        }

        /// <summary>
        /// Returns the local player object or null if one does not exist
        /// </summary>
        /// <returns>The local player object or null if one does not exist</returns>
        public NetworkObject GetLocalPlayerObject()
        {
            if (!NetworkManager.ConnectedClients.ContainsKey(NetworkManager.LocalClientId))
            {
                return null;
            }

            return NetworkManager.ConnectedClients[NetworkManager.LocalClientId].PlayerObject;
        }

        /// <summary>
        /// Returns the player object with a given clientId or null if one does not exist
        /// </summary>
        /// <returns>The player object with a given clientId or null if one does not exist</returns>
        public NetworkObject GetPlayerNetworkObject(ulong clientId)
        {
            if (!NetworkManager.ConnectedClients.ContainsKey(clientId))
            {
                return null;
            }

            return NetworkManager.ConnectedClients[clientId].PlayerObject;
        }

        internal void RemoveOwnership(NetworkObject networkObject)
        {
            if (!NetworkManager.IsServer)
            {
                throw new NotServerException("Only the server can change ownership");
            }

            if (!networkObject.IsSpawned)
            {
                throw new SpawnStateException("Object is not spawned");
            }

            for (int i = NetworkManager.ConnectedClients[networkObject.OwnerClientId].OwnedObjects.Count - 1; i > -1; i--)
            {
                if (NetworkManager.ConnectedClients[networkObject.OwnerClientId].OwnedObjects[i] == networkObject)
                {
                    NetworkManager.ConnectedClients[networkObject.OwnerClientId].OwnedObjects.RemoveAt(i);
                }
            }

            networkObject.OwnerClientIdInternal = null;

            using (var buffer = PooledNetworkBuffer.Get())
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteUInt64Packed(networkObject.NetworkObjectId);
                writer.WriteUInt64Packed(networkObject.OwnerClientId);

                InternalMessageSender.Send(NetworkConstants.CHANGE_OWNER, NetworkChannel.Internal, buffer);
            }
        }

        internal void ChangeOwnership(NetworkObject networkObject, ulong clientId)
        {
            if (!NetworkManager.IsServer)
            {
                throw new NotServerException("Only the server can change ownership");
            }

            if (!networkObject.IsSpawned)
            {
                throw new SpawnStateException("Object is not spawned");
            }

            if (NetworkManager.ConnectedClients.ContainsKey(networkObject.OwnerClientId))
            {
                for (int i = NetworkManager.ConnectedClients[networkObject.OwnerClientId].OwnedObjects.Count - 1; i >= 0; i--)
                {
                    if (NetworkManager.ConnectedClients[networkObject.OwnerClientId].OwnedObjects[i] == networkObject)
                    {
                        NetworkManager.ConnectedClients[networkObject.OwnerClientId].OwnedObjects.RemoveAt(i);
                    }
                }
            }

            NetworkManager.ConnectedClients[clientId].OwnedObjects.Add(networkObject);
            networkObject.OwnerClientId = clientId;

            using (var buffer = PooledNetworkBuffer.Get())
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteUInt64Packed(networkObject.NetworkObjectId);
                writer.WriteUInt64Packed(clientId);

                InternalMessageSender.Send(NetworkConstants.CHANGE_OWNER, NetworkChannel.Internal, buffer);
            }
        }

        // Only ran on Client
        internal NetworkObject CreateLocalNetworkObject(bool softCreate, uint prefabHash, ulong ownerClientId, ulong? parentNetworkId, Vector3? position, Quaternion? rotation)
        {
            NetworkObject parentNetworkObject = null;

            if (parentNetworkId != null && SpawnedObjects.ContainsKey(parentNetworkId.Value))
            {
                parentNetworkObject = SpawnedObjects[parentNetworkId.Value];
            }
            else if (parentNetworkId != null)
            {
                if (NetworkLog.CurrentLogLevel <= LogLevel.Normal)
                {
                    NetworkLog.LogWarning("Cannot find parent. Parent objects always have to be spawned and replicated BEFORE the child");
                }
            }


            if (!NetworkManager.NetworkConfig.EnableSceneManagement || !softCreate)
            {
                // Create the object
                if (NetworkManager.PrefabHandler.ContainsHandler(prefabHash))
                {
                    var networkObject = NetworkManager.PrefabHandler.HandleNetworkPrefabSpawn(prefabHash, ownerClientId, position.GetValueOrDefault(Vector3.zero), rotation.GetValueOrDefault(Quaternion.identity));

                    if (parentNetworkObject != null)
                    {
                        networkObject.transform.SetParent(parentNetworkObject.transform, true);
                    }

                    if (NetworkSceneManager.IsSpawnedObjectsPendingInDontDestroyOnLoad)
                    {
                        UnityEngine.Object.DontDestroyOnLoad(networkObject.gameObject);
                    }

                    return networkObject;
                }
                else
                {
                    GameObject networkPrefabReference = null;
                    if(NetworkManager.NetworkConfig.HashedNetworkPrefabs.ContainsKey(prefabHash))
                    {
                        switch (NetworkManager.NetworkConfig.HashedNetworkPrefabs[prefabHash].Override)
                        {
                            default:
                            case NetworkPrefabOverride.Unset:
                                networkPrefabReference = NetworkManager.NetworkConfig.HashedNetworkPrefabs[prefabHash].Prefab;
                                break;
                            case NetworkPrefabOverride.Hash:
                            case NetworkPrefabOverride.Prefab:
                                networkPrefabReference = NetworkManager.NetworkConfig.HashedNetworkPrefabs[prefabHash].OverridingTargetPrefab;
                                break;
                        }
                    }

                    //for (int i = 0; networkPrefabReference == null && i < NetworkManager.NetworkConfig.NetworkPrefabs.Count; i++)
                    //{
                    //    var element = NetworkManager.NetworkConfig.NetworkPrefabs[i];
                        
                    //    switch (element.Override)
                    //    {
                    //        default:
                    //        case NetworkPrefabOverride.Unset:
                    //            if (element.Hash == prefabHash)
                    //            {
                    //                networkPrefabReference = element.Prefab;
                    //            }
                    //            break;
                    //        case NetworkPrefabOverride.Prefab:
                    //            if (element.OverridingSourcePrefab.GetComponent<NetworkObject>().GlobalObjectIdHash == prefabHash)
                    //            {
                    //                networkPrefabReference = element.OverridingTargetPrefab;
                    //            }
                    //            break;
                    //        case NetworkPrefabOverride.Hash:
                    //            if (element.OverridingSourceHash == prefabHash)
                    //            {
                    //                networkPrefabReference = element.OverridingTargetPrefab;
                    //            }
                    //            break;
                    //    }
                    //}

                    if (networkPrefabReference == null)
                    {
                        if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                        {
                            NetworkLog.LogError($"Failed to create object locally. [{nameof(prefabHash)}={prefabHash}]. {nameof(NetworkPrefab)} could not be found. Is the prefab registered with {nameof(NetworkManager)}?");
                        }

                        return null;
                    }

                    var networkObject = ((position == null && rotation == null) ? UnityEngine.Object.Instantiate(networkPrefabReference) : UnityEngine.Object.Instantiate(networkPrefabReference, position.GetValueOrDefault(Vector3.zero), rotation.GetValueOrDefault(Quaternion.identity))).GetComponent<NetworkObject>();

                    if (parentNetworkObject != null)
                    {
                        networkObject.transform.SetParent(parentNetworkObject.transform, true);
                    }

                    if (NetworkSceneManager.IsSpawnedObjectsPendingInDontDestroyOnLoad)
                    {
                        UnityEngine.Object.DontDestroyOnLoad(networkObject.gameObject);
                    }

                    return networkObject;
                }
            }
            else
            {
                // SoftSync them by mapping
                if (!PendingSoftSyncObjects.ContainsKey(prefabHash))
                {
                    // TODO: Fix this message
                    if (NetworkLog.CurrentLogLevel <= LogLevel.Error)
                    {
                        NetworkLog.LogError("Cannot find pending soft sync object. Is the projects the same?");
                    }
                    return null;
                }

                var networkObject = PendingSoftSyncObjects[prefabHash];
                PendingSoftSyncObjects.Remove(prefabHash);

                if (parentNetworkObject != null)
                {
                    networkObject.transform.SetParent(parentNetworkObject.transform, true);
                }

                return networkObject;
            }
        }

        // Ran on both server and client
        internal void SpawnNetworkObjectLocally(NetworkObject networkObject, ulong networkId, bool sceneObject, bool playerObject, ulong? ownerClientId, Stream dataStream, bool readPayload, int payloadLength, bool readNetworkVariable, bool destroyWithScene)
        {
            if (networkObject == null)
            {
                throw new ArgumentNullException(nameof(networkObject), "Cannot spawn null object");
            }

            if (networkObject.IsSpawned)
            {
                throw new SpawnStateException("Object is already spawned");
            }

            if (readNetworkVariable && NetworkManager.NetworkConfig.EnableNetworkVariable)
            {
                networkObject.SetNetworkVariableData(dataStream);
            }

            if (SpawnedObjects.ContainsKey(networkObject.NetworkObjectId))
            {
                return;
            }

            networkObject.IsSpawned = true;

            networkObject.IsSceneObject = sceneObject;
            networkObject.NetworkObjectId = networkId;

            networkObject.DestroyWithScene = sceneObject || destroyWithScene;

            networkObject.OwnerClientIdInternal = ownerClientId;
            networkObject.IsPlayerObject = playerObject;

            SpawnedObjects.Add(networkObject.NetworkObjectId, networkObject);
            SpawnedObjectsList.Add(networkObject);

            if (ownerClientId != null)
            {
                if (NetworkManager.IsServer)
                {
                    if (playerObject)
                    {
                        NetworkManager.ConnectedClients[ownerClientId.Value].PlayerObject = networkObject;
                    }
                    else
                    {
                        NetworkManager.ConnectedClients[ownerClientId.Value].OwnedObjects.Add(networkObject);
                    }
                }
                else if (playerObject && ownerClientId.Value == NetworkManager.LocalClientId)
                {
                    NetworkManager.ConnectedClients[ownerClientId.Value].PlayerObject = networkObject;
                }
            }

            if (NetworkManager.IsServer)
            {
                for (int i = 0; i < NetworkManager.ConnectedClientsList.Count; i++)
                {
                    if (networkObject.CheckObjectVisibility == null || networkObject.CheckObjectVisibility(NetworkManager.ConnectedClientsList[i].ClientId))
                    {
                        networkObject.Observers.Add(NetworkManager.ConnectedClientsList[i].ClientId);
                    }
                }
            }

            networkObject.ResetNetworkStartInvoked();

            if (readPayload)
            {
                using (var payloadBuffer = PooledNetworkBuffer.Get())
                {
                    payloadBuffer.CopyUnreadFrom(dataStream, payloadLength);
                    dataStream.Position += payloadLength;
                    payloadBuffer.Position = 0;
                    networkObject.InvokeBehaviourNetworkSpawn(payloadBuffer);
                }
            }
            else
            {
                networkObject.InvokeBehaviourNetworkSpawn(null);
            }
        }

        internal void SendSpawnCallForObject(ulong clientId, NetworkObject networkObject, Stream payload)
        {
            //Currently, if this is called and the clientId (destination) is the server's client Id, this case
            //will be checked within the below Send function.  To avoid unwarranted allocation of a PooledNetworkBuffer
            //placing this check here. [NSS]
            if (NetworkManager.IsServer && clientId == NetworkManager.ServerClientId)
            {
                return;
            }

            var rpcQueueContainer = NetworkManager.RpcQueueContainer;

            var buffer = PooledNetworkBuffer.Get();
            WriteSpawnCallForObject(buffer, clientId, networkObject, payload);

            var queueItem = new RpcFrameQueueItem
            {
                UpdateStage = NetworkUpdateStage.Update,
                QueueItemType = RpcQueueContainer.QueueItemType.CreateObject,
                NetworkId = 0,
                NetworkBuffer = buffer,
                NetworkChannel = NetworkChannel.Internal,
                ClientNetworkIds = new[] { clientId }
            };
            rpcQueueContainer.AddToInternalMLAPISendQueue(queueItem);
        }

        internal void WriteSpawnCallForObject(Serialization.NetworkBuffer buffer, ulong clientId, NetworkObject networkObject, Stream payload)
        {
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteBool(networkObject.IsPlayerObject);
                writer.WriteUInt64Packed(networkObject.NetworkObjectId);
                writer.WriteUInt64Packed(networkObject.OwnerClientId);

                NetworkObject parentNetworkObject = null;

                if (!networkObject.AlwaysReplicateAsRoot && networkObject.transform.parent != null)
                {
                    parentNetworkObject = networkObject.transform.parent.GetComponent<NetworkObject>();
                }

                if (parentNetworkObject == null)
                {
                    writer.WriteBool(false);
                }
                else
                {
                    writer.WriteBool(true);
                    writer.WriteUInt64Packed(parentNetworkObject.NetworkObjectId);
                }

                writer.WriteBool(networkObject.IsSceneObject ?? true);
                writer.WriteUInt32Packed(networkObject.GlobalObjectIdHash);

                if (networkObject.IncludeTransformWhenSpawning == null || networkObject.IncludeTransformWhenSpawning(clientId))
                {
                    writer.WriteBool(true);
                    writer.WriteSinglePacked(networkObject.transform.position.x);
                    writer.WriteSinglePacked(networkObject.transform.position.y);
                    writer.WriteSinglePacked(networkObject.transform.position.z);

                    writer.WriteSinglePacked(networkObject.transform.rotation.eulerAngles.x);
                    writer.WriteSinglePacked(networkObject.transform.rotation.eulerAngles.y);
                    writer.WriteSinglePacked(networkObject.transform.rotation.eulerAngles.z);
                }
                else
                {
                    writer.WriteBool(false);
                }

                writer.WriteBool(payload != null);

                if (payload != null)
                {
                    writer.WriteInt32Packed((int)payload.Length);
                }

                if (NetworkManager.NetworkConfig.EnableNetworkVariable)
                {
                    networkObject.WriteNetworkVariableData(buffer, clientId);
                }

                if (payload != null)
                {
                    buffer.CopyFrom(payload);
                }
            }
        }

        internal void DespawnObject(NetworkObject networkObject, bool destroyObject = false)
        {
            if (!networkObject.IsSpawned)
            {
                throw new SpawnStateException("Object is not spawned");
            }

            if (!NetworkManager.IsServer)
            {
                throw new NotServerException("Only server can despawn objects");
            }

            OnDestroyObject(networkObject.NetworkObjectId, destroyObject);
        }

        // Makes scene objects ready to be reused
        internal void ServerResetShudownStateForSceneObjects()
        {
            foreach (var sobj in SpawnedObjectsList)
            {
                if ((sobj.IsSceneObject != null && sobj.IsSceneObject == true) || sobj.DestroyWithScene)
                {
                    sobj.IsSpawned = false;
                    sobj.DestroyWithScene = false;
                    sobj.IsSceneObject = null;
                }
            }
        }

        /// <summary>
        /// Gets called only by NetworkSceneManager.SwitchScene
        /// </summary>
        internal void ServerDestroySpawnedSceneObjects()
        {
            //This Allocation is "ok" for now because this code only executes when a new scene is switched to
            //We need to create a new copy the HashSet of NetworkObjects (SpawnedObjectsList) so we can remove
            //objects from the HashSet (SpawnedObjectsList) without causing a list has been modified exception to occur.
            var spawnedObjects = SpawnedObjectsList.ToList();
            foreach (var sobj in spawnedObjects)
            {
                if ((sobj.IsSceneObject != null && sobj.IsSceneObject == true) || sobj.DestroyWithScene)
                {
                    if (NetworkManager.PrefabHandler.ContainsHandler(sobj))
                    {
                        SpawnedObjectsList.Remove(sobj);
                        NetworkManager.PrefabHandler.HandleNetworkPrefabDestroy(sobj);
                        OnDestroyObject(sobj.NetworkObjectId, false);
                    }
                    else
                    {
                        SpawnedObjectsList.Remove(sobj);
                        UnityEngine.Object.Destroy(sobj.gameObject);
                    }
                }
            }
        }

        internal void DestroyNonSceneObjects()
        {
            var networkObjects = UnityEngine.Object.FindObjectsOfType<NetworkObject>();

            for (int i = 0; i < networkObjects.Length; i++)
            {
                if (networkObjects[i].IsSceneObject != null && networkObjects[i].IsSceneObject.Value == false)
                {
                    if (NetworkManager.PrefabHandler.ContainsHandler(networkObjects[i]))
                    {
                        NetworkManager.PrefabHandler.HandleNetworkPrefabDestroy(networkObjects[i]);

                        OnDestroyObject(networkObjects[i].NetworkObjectId, false);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(networkObjects[i].gameObject);
                    }
                }
            }
        }

        internal void DestroySceneObjects()
        {
            var networkObjects = UnityEngine.Object.FindObjectsOfType<NetworkObject>();

            for (int i = 0; i < networkObjects.Length; i++)
            {
                if (networkObjects[i].IsSceneObject == null || networkObjects[i].IsSceneObject.Value == true)
                {
                    if (NetworkManager.PrefabHandler.ContainsHandler(networkObjects[i]))
                    {
                        NetworkManager.PrefabHandler.HandleNetworkPrefabDestroy(networkObjects[i]);
                        OnDestroyObject(networkObjects[i].NetworkObjectId, false);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(networkObjects[i].gameObject);
                    }
                }
            }
        }

        internal void CleanDiffedSceneObjects()
        {
            // Clean up the diffed scene objects. I.E scene objects that have been destroyed
            if (PendingSoftSyncObjects.Count > 0)
            {
                var networkObjectsToDestroy = new List<NetworkObject>();

                foreach (var pair in PendingSoftSyncObjects)
                {
                    networkObjectsToDestroy.Add(pair.Value);
                }

                for (int i = 0; i < networkObjectsToDestroy.Count; i++)
                {
                    UnityEngine.Object.Destroy(networkObjectsToDestroy[i].gameObject);
                }
            }
        }

        internal void ServerSpawnSceneObjectsOnStartSweep()
        {
            var networkObjects = UnityEngine.Object.FindObjectsOfType<NetworkObject>();

            for (int i = 0; i < networkObjects.Length; i++)
            {
                if (networkObjects[i].IsSceneObject == null)
                {
                    SpawnNetworkObjectLocally(networkObjects[i], GetNetworkObjectId(), true, false, null, null, false, 0, false, true);
                }
            }
        }

        internal void ClientCollectSoftSyncSceneObjectSweep(NetworkObject[] networkObjects)
        {
            if (networkObjects == null)
            {
                networkObjects = UnityEngine.Object.FindObjectsOfType<NetworkObject>();
            }

            for (int i = 0; i < networkObjects.Length; i++)
            {
                if (networkObjects[i].IsSceneObject == null)
                {
                    PendingSoftSyncObjects.Add(networkObjects[i].GlobalObjectIdHash, networkObjects[i]);
                }
            }
        }

        internal void OnDestroyObject(ulong networkId, bool destroyGameObject)
        {
            if (NetworkManager == null)
            {
                return;
            }

            //Removal of spawned object
            if (!SpawnedObjects.ContainsKey(networkId))
            {
                Debug.LogWarning($"Trying to destroy object {networkId} but it doesn't seem to exist anymore!");
                return;
            }

            var sobj = SpawnedObjects[networkId];

            if (!sobj.IsOwnedByServer && !sobj.IsPlayerObject && NetworkManager.ConnectedClients.ContainsKey(sobj.OwnerClientId))
            {
                //Someone owns it.
                for (int i = NetworkManager.ConnectedClients[sobj.OwnerClientId].OwnedObjects.Count - 1; i > -1; i--)
                {
                    if (NetworkManager.ConnectedClients[sobj.OwnerClientId].OwnedObjects[i].NetworkObjectId == networkId)
                    {
                        NetworkManager.ConnectedClients[sobj.OwnerClientId].OwnedObjects.RemoveAt(i);
                    }
                }
            }

            sobj.IsSpawned = false;

            if (NetworkManager != null && NetworkManager.IsServer)
            {
                if (NetworkManager.NetworkConfig.RecycleNetworkIds)
                {
                    ReleasedNetworkObjectIds.Enqueue(new ReleasedNetworkId()
                    {
                        NetworkId = networkId,
                        ReleaseTime = Time.unscaledTime
                    });
                }

                var rpcQueueContainer = NetworkManager.RpcQueueContainer;
                if (rpcQueueContainer != null)
                {
                    if (sobj != null)
                    {
                        // As long as we have any remaining clients, then notify of the object being destroy.
                        if (NetworkManager.ConnectedClientsList.Count > 0)
                        {
                            var buffer = PooledNetworkBuffer.Get();
                            using (var writer = PooledNetworkWriter.Get(buffer))
                            {
                                writer.WriteUInt64Packed(networkId);

                                var queueItem = new RpcFrameQueueItem
                                {
                                    UpdateStage = NetworkUpdateStage.PostLateUpdate,
                                    QueueItemType = RpcQueueContainer.QueueItemType.DestroyObject,
                                    NetworkId = networkId,
                                    NetworkBuffer = buffer,
                                    NetworkChannel = NetworkChannel.Internal,
                                    ClientNetworkIds = NetworkManager.ConnectedClientsList.Select(c => c.ClientId).ToArray()
                                };

                                rpcQueueContainer.AddToInternalMLAPISendQueue(queueItem);
                            }
                        }
                    }
                }
            }

            var gobj = sobj.gameObject;

            if (destroyGameObject && gobj != null)
            {
                if (NetworkManager.PrefabHandler.ContainsHandler(sobj))
                {
                    NetworkManager.PrefabHandler.HandleNetworkPrefabDestroy(sobj);
                    OnDestroyObject(networkId, false);
                }
                else
                {
                    UnityEngine.Object.Destroy(gobj);
                }
            }

            // for some reason, we can get down here and SpawnedObjects for this
            //  networkId will no longer be here, even as we check this at the start
            //  of the function
            if (SpawnedObjects.ContainsKey(networkId))
            {
                SpawnedObjectsList.Remove(sobj);
                SpawnedObjects.Remove(networkId);
            }
        }
    }
}
